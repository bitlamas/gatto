using Gatto.Core.Models;

namespace Gatto.Core.Acquire;

//a seekable view of a remote file for the header parser: a seek costs nothing and a read fetches a window, growing while the reads run on
internal sealed class RangeStream(RangeFetch fetch, long length, CancellationToken ct, long budget) : Stream
{
    //the first window after a jump is small, since a jump lands near what it needs, and a run of reads doubles it up to the cap
    private const int FirstWindow = 64 << 10;
    private const int MaxWindow = 2 << 20;

    private byte[] _window = [];
    private long _windowAt;
    private int _size = FirstWindow;
    private long _position;

    //the bytes fetched so far, which a lying header cannot push past the budget
    public long Fetched { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0 || _position >= length) return 0;
        if (_position < _windowAt || _position >= _windowAt + _window.Length) Fill();
        var n = (int)Math.Min(count, _windowAt + _window.Length - _position);
        Array.Copy(_window, _position - _windowAt, buffer, offset, n);
        _position += n;
        return n;
    }

    //a read just past the window continues the run, since a skipped string lands a few bytes beyond it. the parser is synchronous, so the fetch is waited on here
    private void Fill()
    {
        var end = _windowAt + _window.Length;
        var onward = _window.Length > 0 && _position >= end && _position - end < 1 << 20;
        _size = onward ? Math.Min(_size * 2, MaxWindow) : FirstWindow;
        var want = (int)Math.Min(_size, length - _position);
        if (Fetched + want > budget) throw new IOException("the header read passed its budget");
        var got = fetch(_position, want, ct).GetAwaiter().GetResult();
        if (got.Length == 0) throw new IOException("the file ended before its header did");
        Fetched += got.Length;
        _window = got;
        _windowAt = _position;
    }

    public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
