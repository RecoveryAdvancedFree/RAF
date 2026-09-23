namespace RAF.Core.Native;

/// <summary>
/// קריאה מתוך מחיצה מסוימת על דיסק פיזי.
/// כל ההיסטים יחסיים לתחילת המחיצה, כך שמפענחי מערכות הקבצים
/// אינם צריכים לדעת היכן המחיצה יושבת על הדיסק.
/// </summary>
internal sealed class VolumeReader : IDisposable
{
    private readonly RawDevice _device;
    private readonly bool _ownsDevice;

    /// <summary>תיקון בזיכרון שחל על המחיצה הזו, אם הוגדר (ראה <see cref="ReadOverlays"/>).</summary>
    private ReadOverlays.Overlay? _overlay;

    /// <summary>היסט תחילת המחיצה מתחילת הדיסק הפיזי.</summary>
    internal long BaseOffset { get; }

    /// <summary>גודל המחיצה בבתים.</summary>
    internal long Length { get; }

    internal int SectorSize => _device.SectorSize;

    private VolumeReader(RawDevice device, long baseOffset, long length, bool ownsDevice)
    {
        _device = device;
        BaseOffset = baseOffset;
        Length = length;
        _ownsDevice = ownsDevice;
    }

    /// <summary>פתיחת מחיצה לקריאה, דרך הדיסק הפיזי שעליו היא יושבת.</summary>
    /// <param name="applyOverlay">
    /// false — קריאת מה שכתוב בדיסק בפועל, גם אם הוגדר למחיצה תיקון בזיכרון.
    /// חובה באבחון, בתיקון ובהעתקה לתמונה: הם חייבים לראות את האמת.
    /// </param>
    internal static VolumeReader? TryOpen(
        int diskNumber, long offset, long length, int sectorSize, bool sequential = true, bool applyOverlay = true)
    {
        string? path = DevicePaths.PathOf(diskNumber);
        if (path is null) return null;

        var device = RawDevice.TryOpen(path, sectorSize, sequential);
        if (device is null) return null;

        return new VolumeReader(device, offset, length, ownsDevice: true)
        {
            _overlay = applyOverlay ? ReadOverlays.Get(diskNumber, offset) : null,
        };
    }

    /// <summary>עטיפת התקן פתוח קיים, ללא בעלות עליו.</summary>
    internal static VolumeReader Wrap(RawDevice device, long offset, long length)
        => new(device, offset, length, ownsDevice: false);

    /// <summary>קריאה מהיסט יחסי לתחילת המחיצה.</summary>
    internal int Read(long relativeOffset, Span<byte> destination)
    {
        if (relativeOffset < 0) return 0;

        // מניעת קריאה מעבר לגבול המחיצה — שם מתחילים נתונים של מחיצה אחרת.
        if (Length > 0 && relativeOffset >= Length) return 0;

        int read = _device.Read(BaseOffset + relativeOffset, destination);

        if (_overlay is { } o && relativeOffset < o.End && relativeOffset + destination.Length > o.Offset)
        {
            // סקטור אתחול פגום עלול גם להיכשל בקריאה; בתוך אזור התיקון
            // התוצאה ידועה מראש, ולכן קריאה כזו מוחזרת כמוצלחת.
            if (read <= 0 && relativeOffset >= o.Offset && relativeOffset + destination.Length <= o.End)
            {
                destination.Clear();
                read = destination.Length;
            }

            long from = Math.Max(relativeOffset, o.Offset);
            long to = Math.Min(relativeOffset + read, o.End);
            if (to > from)
                o.Data.AsSpan((int)(from - o.Offset), (int)(to - from))
                    .CopyTo(destination[(int)(from - relativeOffset)..]);
        }

        return read;
    }

    internal byte[] ReadBlock(long relativeOffset, int length)
    {
        byte[] buffer = new byte[length];
        int read = Read(relativeOffset, buffer);
        return read == length ? buffer : buffer.AsSpan(0, Math.Max(0, read)).ToArray();
    }

    public void Dispose()
    {
        if (_ownsDevice) _device.Dispose();
    }
}
