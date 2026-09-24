namespace PEIS.Report.Api.Printing;

public sealed class SequenceStream : Stream
{
    private readonly Stream[] _streams;
    private int _currentIndex;

    public SequenceStream(params Stream[] streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _streams.Sum(s => s.Length);

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_currentIndex < _streams.Length)
        {
            var bytesRead = _streams[_currentIndex].Read(buffer, offset, count);
            if (bytesRead > 0)
                return bytesRead;
            _currentIndex++;
        }
        return 0;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (_currentIndex < _streams.Length)
        {
            var bytesRead = await _streams[_currentIndex].ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (bytesRead > 0)
                return bytesRead;
            _currentIndex++;
        }
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_currentIndex < _streams.Length)
        {
            var bytesRead = await _streams[_currentIndex].ReadAsync(buffer, cancellationToken);
            if (bytesRead > 0)
                return bytesRead;
            _currentIndex++;
        }
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
