using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
        // מחיצת BitLocker שנפתחה במפתח: קוראים מהדיסק שמתחתיה, ומפענחים.
        if (DevicePaths.DecryptedSourceOf(devicePath) is { } source)
        {
            string? below = DevicePaths.PathOf(source.Disk);
            if (below is null)
            {
                NotFound();
                return null;
            }
            var inner = TryOpen(below, sectorSize, sequential);
            return inner is null ? null : new RawDevice(IntPtr.Zero, devicePath, sectorSize) { _inner = inner, _decrypted = source };
        }
        if (DevicePaths.IsDecryptedPath(devicePath))
        {
            NotFound();
            return null;
        }

        // מערך RAID שהורכב בתוכנה: פותחים כל כונן שלו, והקריאה עוברת דרך הגאומטריה.
        if (DevicePaths.IsRaidPath(devicePath))
        {
            if (DevicePaths.RaidSourceOf(devicePath) is not { } raid)
            {
                NotFound();
                return null;
            }
            var members = new RawDevice?[raid.Members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                if (raid.Members[i] is not { } m) continue;
                if (DevicePaths.PathOf(m.Disk) is not { } path || TryOpen(path, sectorSize, sequential: false) is not { } opened)
                {
                    foreach (var done in members) done?.Dispose();
                    return null;
                }
                members[i] = opened;
            }
            return new RawDevice(IntPtr.Zero, devicePath, sectorSize) { _raid = raid, _members = members };
        }

        // במק ובלינוקס: קובץ או התקן (/dev/rdisk…) נפתחים כמו כל קובץ. הקריאות ממילא
        // מיושרות לסקטור, כפי שהתקן גולמי במק דורש.
        if (!OperatingSystem.IsWindows())
        {
            SafeFileHandle? file;
            try
            {
                // כונן במק דורש הרשאת מנהל: authopen מבקש סיסמה ופותח רק אותו (ראו MacAuthOpen).
                file = MacDevice(devicePath)
                    ? MacAuthOpen.Open(devicePath, write: false) ?? throw new UnauthorizedAccessException()
                    : File.OpenHandle(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _openError = ex is UnauthorizedAccessException ? 5 : ex is FileNotFoundException or DirectoryNotFoundException ? 2 : 1;
                return null;
            }
            var opened = new RawDevice(IntPtr.Zero, devicePath, sectorSize) { _file = file };
            if (!DevicePaths.IsDevicePath(devicePath))
            {
                try
                {
                    long length = RandomAccess.GetLength(file);
                    opened.Virtual = VirtualDisk.TryOpen((at, count) => opened.ReadBlockPhysical(at, count), length, devicePath);
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
            }
            return opened;
        }

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

        // קובץ כונן וירטואלי (VHD/VHDX/VMDK): הקריאות מתורגמות למקום בקובץ. כונן פגום או
        // מסוג "הפרשים" — ההסבר עולה למשתמש, במקום "לא ניתן לפתוח".
        if (!DevicePaths.IsDevicePath(devicePath))
        {
            try
            {
                long length = new FileInfo(devicePath).Length;
                device.Virtual = VirtualDisk.TryOpen((at, count) => device.ReadBlockPhysical(at, count), length, devicePath);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        return device;
    }

    /// <summary>
    /// כונן במק שהמשתמש אינו רשאי לפתוח ישירות — דרך authopen. RAF_MAC_AUTHOPEN=1 מכריח
    /// את הדרך הזו (לבדיקה אוטומטית, שרצה כמנהל ולכן פותחת ישירות).
    /// </summary>
    internal static bool MacDevice(string path)
    {
        if (!OperatingSystem.IsMacOS() || !path.StartsWith("/dev/", StringComparison.Ordinal)) return false;
        if (Environment.GetEnvironmentVariable("RAF_MAC_AUTHOPEN") == "1") return true;
        try
        {
            using var direct = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; }
    }

    /// <summary>הכונן הווירטואלי שהקובץ מכיל, או null — תמונה רגילה או התקן.</summary>
    public VirtualDisk? Virtual { get; private set; }

    /// <summary>מחיצת BitLocker שהתוכנה מפענחת: הדיסק שמתחתיה, ואיך לפענח.</summary>
    private RawDevice? _inner;
    private DevicePaths.DecryptedSource? _decrypted;

    /// <summary>מערך RAID: הגאומטריה, והכוננים שלו לפי מקומם במערך (null — חסר).</summary>
    private DevicePaths.RaidSource? _raid;
    private RawDevice?[]? _members;

    [ThreadStatic] private static int _openError;

    /// <summary>הכונן לא נמצא כלל (אין לו נתיב) — כמו "הקובץ לא נמצא".</summary>
    internal static void NotFound() => _openError = 2;

    /// <summary>
    /// למה נכשלה הפתיחה האחרונה בחוט הזה, במילים פשוטות: כונן שאינו מחובר אינו
    /// "חוסר הרשאות" — ההודעה הישנה שלחה אנשים לחפש בעיה שאינה קיימת.
    /// </summary>
    public static string OpenFailure(string? what = null) => OpenFailureText(what ?? L.T("הדיסק"));

    private static string OpenFailureText(string what) => _openError switch
    {
        2 or 3 or 21 or 55 or 433 or 1167 or 1617 =>
            L.T("הכונן אינו מחובר. חברו אותו שוב, רעננו את רשימת הכוננים ונסו שוב."),
        5 => L.T("אין הרשאה לקרוא את {0}. ודאו שהתוכנה פועלת בהרשאות מנהל.", what),
        32 => L.T("{0} בשימוש של תוכנה אחרת שנעלה אותו. סגרו אותה ונסו שוב.", what),
        0 => L.T("לא ניתן לפתוח את {0} לקריאה. ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל.", what),
        var e => L.T("לא ניתן לפתוח את {0} לקריאה (שגיאה {1}). ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל.", what, e),
    };

    /// <summary>שגיאת Win32 האחרונה, לצורך הודעות שגיאה מדויקות למשתמש.</summary>
    public static int LastError => Marshal.GetLastWin32Error();

    public bool IsValid => _members is not null || (_inner?.IsValid ?? (_file is not null ? !_file.IsClosed
        : _handle != IntPtr.Zero && _handle != Win32.INVALID_HANDLE_VALUE));

    /// <summary>הקובץ או ההתקן, במק ובלינוקס (ב-Windows — _handle).</summary>
    private SafeFileHandle? _file;

    /// <summary>
    /// ההתקן נותק (נשלף, או שהחיבור נפל) — ולא סקטור פגום. Windows מבחין בין השניים
    /// בקוד השגיאה: סקטור פגום מחזיר שגיאת CRC או שגיאת קלט/פלט, וניתוק — "ההתקן אינו מחובר".
    /// מי שעובר על הכונן עוצר אז ושומר נקודת המשך, במקום לסמן את כל ההמשך כפגום.
    /// </summary>
    public bool Disconnected
    {
        get => _inner?.Disconnected ?? _disconnected;
        private set => _disconnected = value;
    }
    private bool _disconnected;

    /// <summary>לבדיקות: מכאן והלאה ההתקן מתנהג כמו כונן שנשלף.</summary>
    internal void SimulateDisconnect()
    {
        _gone = true;
        _inner?.SimulateDisconnect();
    }
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
        if (_decrypted is { } s)
        {
            var inner = _inner!;
            return s.Volume.Read(offset, destination, (at, buffer) => inner.Read(s.Offset + at, buffer));
        }
        if (_raid is { } raid)
        {
            var members = _members!;
            return raid.Volume.Read(offset, destination, (role, at, buffer) =>
                members[role] is { } m ? m.Read(raid.Members[role]!.Value.Offset + at, buffer) : 0);
        }

        if (Virtual is not { } disk) return ReadPhysical(offset, destination);
        if (disk.IsComposite) return disk.ReadComposite(offset, destination);

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
                if (_file is not null)
                {
                    try
                    {
                        int got = 0;
                        while (got < alignedLength)
                        {
                            int n = RandomAccess.Read(_file, buffer.AsSpan(got), alignedStart + got);
                            if (n <= 0) break;
                            got += n;
                        }
                        read = (uint)got;
                    }
                    catch (IOException)
                    {
                        return 0;
                    }
                }
                else if (!Win32.SetFilePointerEx(_handle, alignedStart, out _position, 0 /* FILE_BEGIN */))
                {
                    Failed();
                    return 0;
                }

                else if (!Win32.ReadFile(_handle, pin.AddrOfPinnedObject(), (uint)alignedLength, out read, IntPtr.Zero))
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
            if (_file is not null) _file.Dispose();
            else if (IsValid)
            {
                Win32.CloseHandle(_handle);
                _handle = Win32.INVALID_HANDLE_VALUE;
            }
        }
        Virtual?.Close();
        _inner?.Dispose();
        if (_members is not null) foreach (var m in _members) m?.Dispose();
        GC.SuppressFinalize(this);
    }

    ~RawDevice() => Dispose();
}
