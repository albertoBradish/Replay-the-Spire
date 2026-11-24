using System.IO.Pipes;
using SpireReplay.Media;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Media pipe checks require Windows.");
    return;
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
