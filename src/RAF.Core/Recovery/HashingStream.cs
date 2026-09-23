using System.Security.Cryptography;

namespace RAF.Core.Recovery;

/// <summary>
/// זרם כתיבה שמעדכן גיבוב בכל בית שעובר דרכו. כך הגיבוב של קובץ משוחזר
/// מחושב תוך כדי הכתיבה, ולא בקריאה נוספת של הקובץ מהיעד.
/// </summary>
internal sealed class HashingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;

    internal HashingStream(Stream inner, IncrementalHash hash)
    {
        _inner = inner;
        _hash = hash;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        _hash.AppendData(buffer);
    }

    public override void WriteByte(byte value) => Write(stackalloc byte[] { value });

    public override void Flush() => _inner.Flush();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;

    // הגיבוב מניח כתיבה רציפה מההתחלה; קפיצה במיקום הייתה משבשת אותו.
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
