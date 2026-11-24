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

    static Process StartEncoder(string encoder, params string[] arguments)
    {
        var start = new ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true };
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
            if (last != null) await WriteUntil(frame.Index);
            last = frame.Pixels;
            await WriteUntil(frame.Index + 1);
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
        string final = Path.Combine(stage, "ready.mp4");
        int frames = 1, captured = 1;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stop.Token;
            token.ThrowIfCancellationRequested();
            using var pcm = new PcmPipe();
            using (var gameMix = GameMixScope.Open(audioGain))
            using (var writer = StartEncoder(encoder,
                "-thread_queue_size", "2", "-probesize", "32", "-analyzeduration", "1", "-f", "rawvideo",
                "-pixel_format", "rgba", "-video_size", $"{width}x{height}", "-framerate", FramesPerSecond.ToString(), "-i", "pipe:0",
                "-thread_queue_size", "16", "-probesize", "32", "-analyzeduration", "1", "-f", "s16le", "-ar", "48000",
                "-ac", "2", "-i", pcm.InputPath, "-map", "0:v:0", "-map", "1:a:0", "-vf",
                $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1", "-c:v", "libx264", "-preset", "veryfast",
                "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", final))
            {
                using var cancel = token.Register(() => Terminate(writer));
                var stderr = writer.StandardError.ReadToEndAsync();
                var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2)
                    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                Exception? failure = null;
                void Fail(Exception reason)
                {
                    if (token.IsCancellationRequested) return;
                    Interlocked.CompareExchange(ref failure, reason, null);
                    stop.Cancel();
                }
                async Task Guard(Task work)
                {
                    try { await work.ConfigureAwait(false); }
                    catch (Exception error) { Fail(error); }
                }
                var audioPump = Guard(pcm.PumpAsync(token));
                var videoPump = Guard(Task.Run(() => StreamFrames(channel.Reader, writer.StandardInput.BaseStream,
                    () => Volatile.Read(ref frames), token), CancellationToken.None));
                GameAudioCapture? mix = null;
                bool drawing = false;
                long start = 0;
                int latest = 0;
                void OnDraw()
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        int wanted = (int)(Stopwatch.GetElapsedTime(start).TotalSeconds * FramesPerSecond);
                        if (writer.HasExited) throw new IOException("视频编码器提前退出。");
                        if (wanted <= latest || channel.Reader.Count >= 2) return;
                        var next = new Frame(wanted, Pixels(view, width, height));
                        if (channel.Writer.TryWrite(next))
                        {
                            latest = wanted;
                            captured++;
                        }
                    }
                    catch (Exception error) { Fail(error); }
                }
                try
                {
                    token.ThrowIfCancellationRequested();
                    var first = Pixels(view, width, height);
                    mix = GameAudioCapture.Start(pcm.Write, Fail);
                    start = mix.StartedTimestamp;
                    channel.Writer.TryWrite(new Frame(0, first));
                    RenderingServer.FramePostDraw += OnDraw;
                    drawing = true;
                    await play(token);
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (failure != null) { throw new IOException("写入视频失败。", failure); }
                catch { stop.Cancel(); throw; }
                finally
                {
                    if (drawing) RenderingServer.FramePostDraw -= OnDraw;
                    if (start != 0)
                        Volatile.Write(ref frames, Math.Max(1, (int)Math.Ceiling(Stopwatch.GetElapsedTime(start).TotalSeconds * FramesPerSecond)));
                    stop.CancelAfter(TimeSpan.FromSeconds(60));
                    channel.Writer.TryComplete();
                    try { mix?.Stop((long)frames * 48000 / FramesPerSecond); }
                    catch (Exception error) { Fail(error); }
                    pcm.Complete();
                    await Task.WhenAll(audioPump, videoPump);
                    if (token.IsCancellationRequested) await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                }
                if (failure != null) throw new IOException("写入视频失败。", failure);
                try { await ExpectSuccess(writer, stderr, token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("编码器未在限定时间内写完视频。");
                }
            }
            ct.ThrowIfCancellationRequested();
            IOFile.Move(final, outputPath);
            GD.Print($"[ReplayTheSpire] VIDEO_FRAMES captured={captured} total={frames} source={size.X}x{size.Y} output={targetWidth}x{targetHeight}");
            return outputPath;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
