using System.Runtime.InteropServices;

namespace RAF.Core.Native;

/// <summary>
/// גישת קריאה גולמית להתקן אחסון (דיסק פיזי או מחיצה).
/// קריאה בלבד — התוכנה לעולם אינה כותבת לדיסק המקור.
/// </summary>
internal sealed class RawDevice : IDisposable
{
    private IntPtr _handle;
    private long _position;

    /// <summary>הזזת המצביע והקריאה הן שתי קריאות מערכת — ביחד, כדי שקריאה מוקדמת ברקע לא תתערב באחרת.</summary>
    private readonly object _gate = new();

    /// <summary>גודל סקטור לוגי. כל קריאה גולמית חייבת להיות מיושרת אליו.</summary>
    public int SectorSize { get; }

    public string Path { get; }

    private RawDevice(IntPtr handle, string path, int sectorSize)
    {
        _handle = handle;
        Path = path;
        SectorSize = sectorSize;
    }

    /// <summary>פתיחת התקן לקריאה גולמית. מחזיר null אם הפתיחה נכשלה (בד"כ חוסר הרשאות אדמין).</summary>
    public static RawDevice? TryOpen(string devicePath, int sectorSize = 512, bool sequential = true)
    {
        // NO_BUFFERING רק בהתקן: בקובץ תמונה הוא היה מחייב יישור לסקטור של
        // הכונן המארח, שעשוי להיות 4096 גם כשהתמונה עצמה בסקטורים של 512.
        uint flags = (DevicePaths.IsDevicePath(devicePath) ? Win32.FILE_FLAG_NO_BUFFERING : 0) |
                     (sequential ? Win32.FILE_FLAG_SEQUENTIAL_SCAN : Win32.FILE_FLAG_RANDOM_ACCESS);

        IntPtr h = Win32.CreateFile(
            devicePath,
            Win32.GENERIC_READ,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (h == Win32.INVALID_HANDLE_VALUE || h == IntPtr.Zero)
            return null;

        return new RawDevice(h, devicePath, sectorSize);
    }

    /// <summary>שגיאת Win32 האחרונה, לצורך הודעות שגיאה מדויקות למשתמש.</summary>
    public static int LastError => Marshal.GetLastWin32Error();

    public bool IsValid => _handle != IntPtr.Zero && _handle != Win32.INVALID_HANDLE_VALUE;

    /// <summary>
    /// קריאת בתים מהיסט מוחלט. ההיסט והאורך מיושרים אוטומטית לגבול סקטור
    /// (דרישת FILE_FLAG_NO_BUFFERING), והתוצאה נחתכת חזרה לטווח המבוקש.
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (!IsValid || destination.Length == 0) return 0;

        long alignedStart = offset / SectorSize * SectorSize;
        int skew = (int)(offset - alignedStart);
        int alignedLength = Align(skew + destination.Length, SectorSize);

        byte[] buffer = new byte[alignedLength];
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            uint read;
            lock (_gate)
            {
                if (!IsValid || !Win32.SetFilePointerEx(_handle, alignedStart, out _position, 0 /* FILE_BEGIN */))
                    return 0;

                if (!Win32.ReadFile(_handle, pin.AddrOfPinnedObject(), (uint)alignedLength, out read, IntPtr.Zero))
                    return 0;
            }

            int usable = Math.Max(0, Math.Min(destination.Length, (int)read - skew));
            buffer.AsSpan(skew, usable).CopyTo(destination);
            return usable;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>קריאת בלוק בגודל נתון והחזרתו כמערך. מחזיר מערך קצר יותר אם נקרא פחות.</summary>
    public byte[] ReadBlock(long offset, int length)
    {
        byte[] result = new byte[length];
        int read = Read(offset, result);
        return read == length ? result : result.AsSpan(0, Math.Max(0, read)).ToArray();
    }

    private static int Align(int value, int alignment)
        => (value + alignment - 1) / alignment * alignment;

    public void Dispose()
    {
        lock (_gate)
        {
            if (IsValid)
            {
                Win32.CloseHandle(_handle);
                _handle = Win32.INVALID_HANDLE_VALUE;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~RawDevice() => Dispose();
}
