using System.Buffers;
using System.IO.Pipes;
using System.Threading.Channels;

namespace SpireReplay.Media;

/// <summary>Bounded handoff between the real-time capture thread and FFmpeg's second input.</summary>
internal sealed class PcmPipe : IDisposable
{
    private const int ChunkSize = 4096;
    private readonly string name = "ReplayTheSpire-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly Channel<ArraySegment<byte>> chunks = Channel.CreateBounded<ArraySegment<byte>>(
        new BoundedChannelOptions(512) { SingleWriter = true, SingleReader = true });
    internal string InputPath => @"\\.\pipe\" + name;

    internal PcmPipe()
    {
        pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 64 * 1024);
    }

    internal void Write(byte[] source, int offset, int count)
    {
        while (count > 0)
        {
            int size = Math.Min(count, ChunkSize);
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            Buffer.BlockCopy(source, offset, buffer, 0, size);
            if (!chunks.Writer.TryWrite(new ArraySegment<byte>(buffer, 0, size)))
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw new IOException("编码器未能及时接收游戏音轨。");
            }
            offset += size;
            count -= size;
        }
    }

    internal async Task PumpAsync(CancellationToken token)
    {
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            await foreach (var chunk in chunks.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try { await pipe.WriteAsync(chunk.AsMemory(), token).ConfigureAwait(false); }
                finally { ArrayPool<byte>.Shared.Return(chunk.Array!); }
            }
        }
        finally
        {
            chunks.Writer.TryComplete();
            while (chunks.Reader.TryRead(out var chunk)) ArrayPool<byte>.Shared.Return(chunk.Array!);
            pipe.Dispose(); // The encoder receives EOF on the PCM input.
        }
    }

    internal void Complete() => chunks.Writer.TryComplete();
    public void Dispose() => pipe.Dispose();
}
