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
        {
            _openError = Marshal.GetLastPInvokeError();
            return null;
        }

        var device = new RawDevice(h, devicePath, sectorSize);

        // קובץ כונן וירטואלי (VHD/VHDX): הקריאות מתורגמות למקום בקובץ. כונן פגום או
        // מסוג "הפרשים" — ההסבר עולה למשתמש, במקום "לא ניתן לפתוח".
        if (!DevicePaths.IsDevicePath(devicePath))
        {
            try
            {
                long length = new FileInfo(devicePath).Length;
                device.Virtual = VirtualDisk.TryOpen((at, count) => device.ReadBlockPhysical(at, count), length);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        return device;
    }

    /// <summary>הכונן הווירטואלי שהקובץ מכיל, או null — תמונה רגילה או התקן.</summary>
    public VirtualDisk? Virtual { get; private set; }

    [ThreadStatic] private static int _openError;

    /// <summary>הכונן לא נמצא כלל (אין לו נתיב) — כמו "הקובץ לא נמצא".</summary>
    internal static void NotFound() => _openError = 2;

    /// <summary>
    /// למה נכשלה הפתיחה האחרונה בחוט הזה, במילים פשוטות: כונן שאינו מחובר אינו
    /// "חוסר הרשאות" — ההודעה הישנה שלחה אנשים לחפש בעיה שאינה קיימת.
    /// </summary>
    public static string OpenFailure(string what = "הדיסק") => _openError switch
    {
        2 or 3 or 21 or 55 or 433 or 1167 or 1617 =>
            "הכונן אינו מחובר. חברו אותו שוב, רעננו את רשימת הכוננים ונסו שוב.",
        5 => $"אין הרשאה לקרוא את {what}. ודאו שהתוכנה פועלת בהרשאות מנהל.",
        32 => $"{what} בשימוש של תוכנה אחרת שנעלה אותו. סגרו אותה ונסו שוב.",
        0 => $"לא ניתן לפתוח את {what} לקריאה. ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל.",
        var e => $"לא ניתן לפתוח את {what} לקריאה (שגיאה {e}). ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל.",
    };

    /// <summary>שגיאת Win32 האחרונה, לצורך הודעות שגיאה מדויקות למשתמש.</summary>
    public static int LastError => Marshal.GetLastWin32Error();

    public bool IsValid => _handle != IntPtr.Zero && _handle != Win32.INVALID_HANDLE_VALUE;

    /// <summary>
    /// ההתקן נותק (נשלף, או שהחיבור נפל) — ולא סקטור פגום. Windows מבחין בין השניים
    /// בקוד השגיאה: סקטור פגום מחזיר שגיאת CRC או שגיאת קלט/פלט, וניתוק — "ההתקן אינו מחובר".
    /// מי שעובר על הכונן עוצר אז ושומר נקודת המשך, במקום לסמן את כל ההמשך כפגום.
    /// </summary>
    public bool Disconnected { get; private set; }

    /// <summary>לבדיקות: מכאן והלאה ההתקן מתנהג כמו כונן שנשלף.</summary>
    internal void SimulateDisconnect() => _gone = true;
    private volatile bool _gone;

    private void Failed()
    {
        int error = Marshal.GetLastPInvokeError();
        // NOT_READY, DEV_NOT_EXIST, NO_SUCH_DEVICE, DEVICE_NOT_CONNECTED, DEVICE_REMOVED, INVALID_HANDLE, FILE_NOT_FOUND
        if (error is 21 or 55 or 433 or 1167 or 1617 or 6 or 2) Disconnected = true;
    }

    /// <summary>
    /// קריאת בתים מהיסט מוחלט. ההיסט והאורך מיושרים אוטומטית לגבול סקטור
    /// (דרישת FILE_FLAG_NO_BUFFERING), והתוצאה נחתכת חזרה לטווח המבוקש.
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (Virtual is not { } disk) return ReadPhysical(offset, destination);

        // כונן וירטואלי: קטע אחרי קטע, כל אחד בתוך בלוק אחד. בלוק שלא הוקצה — אפסים.
        if (offset >= disk.Size) return 0;
        int total = (int)Math.Min(destination.Length, disk.Size - offset);
        for (int done = 0; done < total; )
        {
            long? at = disk.Locate(offset + done, out long contiguous);
            int part = (int)Math.Min(total - done, contiguous);
            var target = destination.Slice(done, part);
            if (at is null) target.Clear();
            else
            {
                int read = ReadPhysical(at.Value, target);
                if (read < part) return done + read;
            }
            done += part;
        }
        return total;
    }

    private byte[] ReadBlockPhysical(long offset, int length)
    {
        byte[] result = new byte[length];
        int read = ReadPhysical(offset, result);
        return read == length ? result : result.AsSpan(0, Math.Max(0, read)).ToArray();
    }

    private int ReadPhysical(long offset, Span<byte> destination)
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
                if (!IsValid) return 0;
                if (_gone)
                {
                    Disconnected = true;
                    return 0;
                }
                if (!Win32.SetFilePointerEx(_handle, alignedStart, out _position, 0 /* FILE_BEGIN */))
                {
                    Failed();
                    return 0;
                }

                if (!Win32.ReadFile(_handle, pin.AddrOfPinnedObject(), (uint)alignedLength, out read, IntPtr.Zero))
                {
                    Failed();
                    return 0;
                }
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
