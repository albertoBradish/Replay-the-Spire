using System.IO.Pipes;
using SpireReplay.Media;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Media pipe checks require Windows.");
    return;
}

if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
{
    AudioInterfaces.CheckProcessLoopbackActivation(0);
    foreach (uint code in new uint[] { 0x80004001, 0x80004002, 0x80070032, 0x80070490, 0x80070005, 0x80070057 })
    {
        try { AudioInterfaces.CheckProcessLoopbackActivation(unchecked((int)code)); }
        catch (Exception error) when ((uint)error.HResult == code &&
            error.Message.Contains("游戏进程音轨接口") == (code is not (0x80070005 or 0x80070057))) { continue; }
        throw new InvalidDataException($"Process audio activation lost error 0x{code:X8}.");
    }
}

// Optional live activation check for Windows 10 machines. Requires no game or audible test output.
if (args.Contains("--probe-audio"))
{
    for (int attempt = 1; attempt <= 2; attempt++)
    {
        long bytes = 0;
        using var capture = GameAudioCapture.Start((_, _, size) => bytes += size);
        await Task.Delay(300);
        capture.Stop(14400);
        if (bytes < 14400 * 4 || bytes != capture.FramesWritten * 4)
            throw new InvalidDataException("Unexpected 48 kHz stereo PCM length.");
        Console.WriteLine($"Windows {Environment.OSVersion.Version} process audio activation {attempt}: {bytes} PCM bytes.");
    }
}

using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = limit.Token;
static NamedPipeClientStream Client(PcmPipe pipe) => new(".", pipe.InputPath.Split('\\').Last(),
    PipeDirection.In, PipeOptions.Asynchronous);

static async Task MustCancel(Task pump)
{
    try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); }
    catch (OperationCanceledException) { return; }
    throw new InvalidDataException("The audio pipe did not signal cancellation.");
}

// A partial final PCM chunk must arrive before EOF when a normal export completes.
using (var pipe = new PcmPipe())
using (var client = Client(pipe))
{
    var pump = pipe.PumpAsync(token);
    await client.ConnectAsync(token);
    byte[] sent = new byte[24003];
    new Random(7239).NextBytes(sent);
    pipe.Write(sent, 5, sent.Length - 17);
    pipe.Complete();
    using var received = new MemoryStream();
    await client.CopyToAsync(received, token);
    await pump.WaitAsync(token);
    if (!sent.AsSpan(5, sent.Length - 17).SequenceEqual(received.ToArray()))
        throw new InvalidDataException("PCM write changed bytes at a chunk boundary.");
}

// Failure to launch an encoder must not hang a worker waiting for its client.
using (var pipe = new PcmPipe())
using (var stop = new CancellationTokenSource())
{
    var pump = pipe.PumpAsync(stop.Token);
    stop.Cancel();
    await MustCancel(pump);
}

// A stalled encoder must not block cancellation while the OS pipe buffer is full.
using (var pipe = new PcmPipe())
using (var stop = new CancellationTokenSource())
using (var client = Client(pipe))
{
    var pump = pipe.PumpAsync(stop.Token);
    await client.ConnectAsync(token);
    pipe.Write(new byte[512 * 1024], 0, 512 * 1024);
    stop.Cancel();
    await MustCancel(pump);
}

// A dead encoder must surface a failed write.
using (var pipe = new PcmPipe())
{
    var pump = pipe.PumpAsync(token);
    using (var client = Client(pipe)) await client.ConnectAsync(token);
    pipe.Write(new byte[4096], 0, 4096);
    pipe.Complete();
    try { await pump.WaitAsync(token); throw new InvalidDataException("Encoder disconnect was ignored."); }
    catch (IOException) { }
}
Console.WriteLine("Media pipe round trip, cancellation and encoder disconnect passed.");
