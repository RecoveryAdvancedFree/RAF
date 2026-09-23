namespace RAF.Core.Native;

/// <summary>
/// נעילה וניתוק של אמצעי אחסון לפני כתיבה גולמית.
///
/// מאז Windows Vista, המערכת חוסמת כתיבה לסקטורים השייכים לאמצעי אחסון
/// מחובר — גם בהרשאות מנהל. כדי לתקן מגזר אתחול יש לנעול את אמצעי האחסון
/// ולנתקו תחילה. שחרור הנעילה מתבצע בסגירת המאחז.
///
/// אם המחיצה כלל אינה מחוברת (מצב RAW טיפוסי), אין מה לנעול והכתיבה
/// מותרת ממילא.
/// </summary>
internal sealed class VolumeLock : IDisposable
{
    private IntPtr _handle;

    internal bool IsHeld => _handle != IntPtr.Zero && _handle != Win32.INVALID_HANDLE_VALUE;

    /// <summary>הסבר בעברית על מה שקרה, להצגה למשתמש כשנכשל.</summary>
    internal string Status { get; private init; } = "";

    private VolumeLock(IntPtr handle, string status)
    {
        _handle = handle;
        Status = status;
    }

    /// <summary>
    /// ניסיון נעילה וניתוק של אמצעי אחסון לפי אות הכונן.
    /// אות ריקה פירושה שהמחיצה אינה מחוברת, ואז אין צורך בנעילה.
    /// </summary>
    internal static VolumeLock Acquire(string driveLetter)
    {
        string letter = driveLetter.TrimEnd(':', '\\');

        if (string.IsNullOrEmpty(letter))
            return new VolumeLock(IntPtr.Zero, "המחיצה אינה מחוברת; אין צורך בנעילה.");

        IntPtr handle = Win32.CreateFile(
            $@"\\.\{letter}:",
            Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            Win32.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            return new VolumeLock(IntPtr.Zero,
                $"לא ניתן לפתוח את אמצעי האחסון {letter}: לנעילה.");

        // נעילה נכשלת אם קובץ כלשהו על אמצעי האחסון פתוח.
        if (!Win32.DeviceIoControl(handle, Win32.FSCTL_LOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            Win32.CloseHandle(handle);
            return new VolumeLock(IntPtr.Zero,
                $"לא ניתן לנעול את כונן {letter}: — ככל הנראה קובץ כלשהו עליו פתוח. " +
                "סגור את כל החלונות והתוכנות שמשתמשות בכונן ונסה שוב.");
        }

        // ניתוק מסיר את מערכת הקבצים מהזיכרון, כך שהכתיבה לא תידרס על ידה.
        Win32.DeviceIoControl(handle, Win32.FSCTL_DISMOUNT_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        return new VolumeLock(handle, $"כונן {letter}: ננעל ונותק לצורך הכתיבה.");
    }

    public void Dispose()
    {
        if (!IsHeld) return;

        Win32.DeviceIoControl(_handle, Win32.FSCTL_UNLOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        Win32.CloseHandle(_handle);
        _handle = Win32.INVALID_HANDLE_VALUE;
    }
}
