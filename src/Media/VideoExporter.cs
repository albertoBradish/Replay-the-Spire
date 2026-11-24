using System.Diagnostics;
using System.Threading.Channels;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using IOFile = System.IO.File;

namespace SpireReplay.Media;

/// <summary>Encode the post-game viewer on demand. Only the latest pending frame is held by the producer.</summary>
public static class VideoExporter
{
    const int FramesPerSecond = 30;
    static int running;
    public static bool IsRecording => Volatile.Read(ref running) != 0;
    public static bool EncoderAvailable => FindEncoder() != null;
    static string Config => Path.Combine(ReplayStore.LibraryDirectory, "encoder.path");

    public static void SetEncoder(string path)
    {
        path = Path.GetFullPath(path);
        if (!IOFile.Exists(path) || !Path.GetFileName(path).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("请选择 ffmpeg.exe。", path);
        IOFile.WriteAllText(Config, path);
    }

    static string? FindEncoder()
    {
        var configured = IOFile.Exists(Config) ? IOFile.ReadAllText(Config).Trim() : "";
        var candidates = new List<string?>
        {
            configured, System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_FFMPEG"), EncoderInstaller.EncoderPath,
            Path.Combine(Path.GetDirectoryName(typeof(VideoExporter).Assembly.Location)!, "tools", "ffmpeg.exe"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe")
        };
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
        return candidates.FirstOrDefault(IOFile.Exists);
    }

    sealed record Frame(int Index, byte[] Pixels);

    static Process StartEncoder(string encoder, bool inputPipe, params string[] arguments)
    {
        var start = new ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = inputPipe, RedirectStandardError = true };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("FFmpeg 未能启动。");
    }

    static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    static async Task ExpectSuccess(Process process, Task<string> errors, CancellationToken token)
    {
        await process.WaitForExitAsync(token);
        var message = await errors;
        if (process.ExitCode != 0) throw new IOException("FFmpeg: " + message.Trim());
    }

    static byte[] Pixels(Viewport view, int width, int height)
    {
        using var image = view.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new IOException("读取游戏画面失败。");
        if (image.GetWidth() != width || image.GetHeight() != height)
            image.Resize(width, height, Image.Interpolation.Bilinear);
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        return image.GetData();
    }

    static async Task StreamFrames(ChannelReader<Frame> frames, Stream pipe, Func<int> length, CancellationToken token)
    {
        byte[]? last = null;
        int written = 0;
        async Task WriteUntil(int end)
        {
            while (written < end)
            {
                await pipe.WriteAsync(last!, token);
                written++;
            }
        }
        await foreach (var frame in frames.ReadAllAsync(token))
        {
            last ??= frame.Pixels;
            await WriteUntil(frame.Index);
            last = frame.Pixels;
        }
        if (last != null) await WriteUntil(length());
        pipe.Close();
    }

    public static Task<string> RecordAsync(string outputPath, Func<CancellationToken, Task> play, CancellationToken ct, int targetHeight = 720)
        => RecordAsync(outputPath, play, ct, targetHeight, null);

    public static async Task<string> RecordAsync(string outputPath, Func<CancellationToken, Task> play, CancellationToken ct, int targetHeight, float? audioGain)
    {
        if (targetHeight is not (720 or 1080)) throw new ArgumentOutOfRangeException(nameof(targetHeight));
        ct.ThrowIfCancellationRequested();
        var encoder = FindEncoder() ?? throw new FileNotFoundException("请选择 FFmpeg 编码器。");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("视频正在导出。");
        try { return await Capture(encoder, Path.GetFullPath(outputPath), play, ct, targetHeight, audioGain); }
        finally { Volatile.Write(ref running, 0); }
    }

    static async Task<string> Capture(string encoder, string outputPath, Func<CancellationToken, Task> play, CancellationToken ct, int targetHeight, float? audioGain)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("游戏尚未就绪。");
        var view = game.GetViewport();
        using var renderTarget = await VideoRenderTarget.EnsureAsync(game.GetTree().Root, targetHeight, ct);
        var size = renderTarget.SourceSize;
        if (size.X <= 0 || size.Y <= 0) throw new InvalidOperationException("需要图形窗口。");
        int targetWidth = targetHeight * 16 / 9;
        double scale = Math.Min((double)targetWidth / size.X, (double)targetHeight / size.Y);
        int width = Math.Clamp((int)(size.X * scale / 2) * 2, 2, targetWidth);
        int height = Math.Clamp((int)(size.Y * scale / 2) * 2, 2, targetHeight);
        var dir = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(dir);
        string stage = Path.Combine(dir, ".video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string video = Path.Combine(stage, "frames.mp4");
        string audio = Path.Combine(stage, "mix.wav");
        string final = Path.Combine(stage, "ready.mp4");
        int frames = 1, captured = 1;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stop.Token;
            using (var gameMix = GameMixScope.Open(audioGain))
            using (var writer = StartEncoder(encoder, true, "-f", "rawvideo", "-pixel_format", "rgba", "-video_size",
                $"{width}x{height}", "-framerate", FramesPerSecond.ToString(), "-i", "pipe:0", "-an", "-vf",
                $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1", "-c:v", "libx264", "-preset", "veryfast",
                "-crf", "18", "-pix_fmt", "yuv420p", video))
            {
                using var cancel = token.Register(() => Terminate(writer));
                var stderr = writer.StandardError.ReadToEndAsync();
                var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2)
                    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                var pump = Task.Run(() => StreamFrames(channel.Reader, writer.StandardInput.BaseStream,
                    () => Volatile.Read(ref frames), token), CancellationToken.None);
                byte[] first;
                GameAudioCapture capture;
                try { first = Pixels(view, width, height); capture = GameAudioCapture.Start(audio); }
                catch
                {
                    stop.Cancel();
                    channel.Writer.TryComplete();
                    try { await pump; } catch (Exception) when (token.IsCancellationRequested) { }
                    await writer.WaitForExitAsync();
                    throw;
                }
                using var mix = capture;
                var start = mix.StartedTimestamp;
                channel.Writer.TryWrite(new Frame(0, first));
                int latest = 0;
                Exception? failure = null;
                void OnDraw()
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        int wanted = (int)(Stopwatch.GetElapsedTime(start).TotalSeconds * FramesPerSecond);
                        if (pump.IsFaulted) throw pump.Exception!.GetBaseException();
                        if (wanted <= latest || channel.Reader.Count >= 2) return;
                        var next = new Frame(wanted, Pixels(view, width, height));
                        if (channel.Writer.TryWrite(next))
                        {
                            latest = wanted;
                            captured++;
                        }
                    }
                    catch (Exception ex) { failure = ex; stop.Cancel(); }
                }
                RenderingServer.FramePostDraw += OnDraw;
                try
                {
                    await play(token);
                    ct.ThrowIfCancellationRequested();
                    if (failure != null) throw failure;
                }
                catch (OperationCanceledException) when (failure != null) { throw new IOException("视频帧写入失败。", failure); }
                catch { stop.Cancel(); throw; }
                finally
                {
                    RenderingServer.FramePostDraw -= OnDraw;
                    Volatile.Write(ref frames, Math.Max(1, (int)Math.Ceiling(Stopwatch.GetElapsedTime(start).TotalSeconds * FramesPerSecond)));
                    channel.Writer.TryComplete();
                    try { mix.Stop(); }
                    finally { try { await pump; } catch when (token.IsCancellationRequested) { } }
                }
                await ExpectSuccess(writer, stderr, ct);
            }
            using (var mux = StartEncoder(encoder, false, "-nostdin", "-i", video, "-i", audio,
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
                "-af", "apad", "-shortest", "-movflags", "+faststart", final))
            {
                using var cancel = ct.Register(() => Terminate(mux));
                await ExpectSuccess(mux, mux.StandardError.ReadToEndAsync(), ct);
            }
            ct.ThrowIfCancellationRequested();
            IOFile.Move(final, outputPath);
            GD.Print($"[ReplayTheSpire] VIDEO_FRAMES captured={captured} total={frames} source={size.X}x{size.Y} output={targetWidth}x{targetHeight}");
            return outputPath;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
