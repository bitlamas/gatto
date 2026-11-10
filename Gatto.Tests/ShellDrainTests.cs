using System.Text;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//a pipe read blocks, so a drain that borrows the pool can wait behind a busy pool past the grace and report a child holding the pipe when none does
public class ShellDrainTests
{
    //a stream that reads as a redirected pipe does: Read blocks, and the base ReadAsync hands Read to the pool
    private sealed class PipeLike(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public readonly List<bool> OnPool = [];

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (OnPool) OnPool.Add(Thread.CurrentThread.IsThreadPoolThread);
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            lock (OnPool) OnPool.Add(Thread.CurrentThread.IsThreadPoolThread);
            return _inner.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task THE_DRAIN_READS_ON_A_THREAD_OF_ITS_OWN_SO_A_BUSY_POOL_CANNOT_HOLD_IT()
    {
        var pipe = new PipeLike(Encoding.UTF8.GetBytes(new string('x', 20_000)));
        var into = new HeadBuffer();

        await ShellTool.Drain(new StreamReader(pipe, Encoding.UTF8), into, new object());

        Assert.Equal(20_000, into.Received);
        Assert.NotEmpty(pipe.OnPool);
        Assert.DoesNotContain(true, pipe.OnPool);
    }

    //a read that throws ends the drain with that exception, as the awaited read did, so the caller does not read it as a held pipe
    [Fact]
    public async Task A_READ_THAT_THROWS_FAULTS_THE_DRAIN()
    {
        var reader = new StreamReader(new PipeLike([]), Encoding.UTF8);
        reader.Dispose();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => ShellTool.Drain(reader, new HeadBuffer(), new object()));
    }
}
