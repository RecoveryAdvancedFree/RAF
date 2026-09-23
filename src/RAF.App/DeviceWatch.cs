using System.Runtime.InteropServices;

namespace RAF.App;

/// <summary>
/// התראה כשכונן מתחבר או מתנתק, כדי שרשימת הכוננים תתעדכן בלי "רענון".
///
/// Windows משדר לכל החלונות רק שינויים באמצעי אחסון עם אות כונן. כונן בלי
/// מחיצה קריאה — בדיוק מה שמגיעים איתו לתוכנת שחזור — לא היה מדווח; לכן
/// החלון נרשם במפורש להודעות על כל דיסק.
/// </summary>
internal static partial class DeviceWatch
{
    internal const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    // GUID_DEVINTERFACE_DISK
    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW")]
    private static partial IntPtr RegisterDeviceNotification(IntPtr recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);

    /// <summary>רישום החלון להודעות על חיבור וניתוק של דיסקים.</summary>
    internal static void Register(IntPtr hWnd)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            Size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            DeviceType = DBT_DEVTYP_DEVICEINTERFACE,
            ClassGuid = DiskInterface,
        };
        RegisterDeviceNotification(hWnd, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    /// <summary>האם ההודעה מדווחת על כונן שחובר או נותק.</summary>
    internal static bool IsDiskChange(Message m)
        => m.Msg == WM_DEVICECHANGE && (int)m.WParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE;
}
