using System.Runtime.InteropServices;

namespace RAF.Core.Native;

/// <summary>
/// כתיבה גולמית להתקן אחסון.
///
/// זהו המקום היחיד בתוכנה שכותב לדיסק מקור, והוא מופרד בכוונה מ-<see cref="RawDevice"/>
/// שהוא לקריאה בלבד. הפרדה זו שומרת על כך שכל מסלול הסריקה והשחזור אינו
/// יכול לכתוב, גם לא בטעות.
/// </summary>
internal sealed class RawWriter : IDisposable
{
    private IntPtr _handle;

    internal int SectorSize { get; }
    internal string Path { get; }

    private RawWriter(IntPtr handle, string path, int sectorSize)
    {
        _handle = handle;
        Path = path;
        SectorSize = sectorSize;
    }

    /// <summary>פתיחת התקן לכתיבה. מחזיר null אם אין הרשאה או שההתקן נעול.</summary>
    internal static RawWriter? TryOpen(string devicePath, int sectorSize)
    {
        // כונן BitLocker פתוח נקרא דרך האות שלו (\\.\E:) — לעולם לא כותבים אליו:
        // כתיבה שם הייתה עוברת הצפנה ונוחתת על הנתונים שמנסים להציל.
        if (DevicePaths.IsVolumePath(devicePath))
            throw new InvalidOperationException("כונן שנפתח דרך Windows הוא לקריאה בלבד — התוכנה אינה כותבת אליו.");

        IntPtr handle = Win32.CreateFile(
            devicePath,
            Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            // קובץ תמונה נכתב דרך מטמון המערכת: NO_BUFFERING היה מחייב יישור
            // לגודל הסקטור של הכונן שעליו הקובץ יושב, שאינו בהכרח זה של התמונה.
            DevicePaths.IsDevicePath(devicePath) ? Win32.FILE_FLAG_NO_BUFFERING : Win32.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        return handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero
            ? null
            : new RawWriter(handle, devicePath, sectorSize);
    }

    internal static int LastError => Marshal.GetLastWin32Error();

    internal bool IsValid => _handle != IntPtr.Zero && _handle != Win32.INVALID_HANDLE_VALUE;

    /// <summary>
    /// כתיבת בתים בהיסט מוחלט.
    /// ההיסט והאורך חייבים להיות מיושרים לגודל סקטור.
    /// </summary>
    internal bool Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!IsValid) return false;
        if (offset % SectorSize != 0) return false;
        if (data.Length % SectorSize != 0) return false;

        byte[] buffer = data.ToArray();
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            if (!Win32.SetFilePointerEx(_handle, offset, out _, 0 /* FILE_BEGIN */))
                return false;

            if (!Win32.WriteFile(_handle, pin.AddrOfPinnedObject(), (uint)buffer.Length,
                    out uint written, IntPtr.Zero))
                return false;

            // כתיבה חלקית מותירה את הדיסק במצב ביניים ולכן נחשבת לכישלון.
            if (written != buffer.Length) return false;

            return Win32.FlushFileBuffers(_handle);
        }
        finally
        {
            pin.Free();
        }
    }

    public void Dispose()
    {
        if (IsValid)
        {
            Win32.CloseHandle(_handle);
            _handle = Win32.INVALID_HANDLE_VALUE;
        }

        GC.SuppressFinalize(this);
    }

    ~RawWriter() => Dispose();
}
