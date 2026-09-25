using System.Runtime.InteropServices;

namespace RAF.App;

/// <summary>
/// התקדמות בסמל שבשורת המשימות, והבהוב בסיום. סריקה מתקדמת נמשכת שעות,
/// והמשתמש עובד בינתיים בחלונות אחרים — כך הוא רואה מה המצב בלי לחזור לתוכנה.
///
/// שגיאה נשארת אדומה עד שחוזרים לחלון, כדי שלא תיעלם בלי שמישהו ראה אותה.
/// </summary>
internal sealed partial class TaskbarProgress : ITaskbar
{
    private readonly Form _form;
    private readonly ITaskbarList3? _taskbar;
    private bool _showingError;

    /// <summary>הסמל באזור ההודעות — מקבל את אותה התקדמות, כשהתוכנה נשלחה לשם.</summary>
    internal TrayIcon? Tray { get; set; }

    internal TaskbarProgress(Form form)
    {
        _form = form;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();
        }
        catch
        {
            _taskbar = null; // בלי שורת משימות (למשל Windows Server Core) — פשוט אין חיווי.
        }

        _form.Activated += (_, _) =>
        {
            if (_showingError) Clear();
        };
    }

    /// <summary>תחילת פעולה ארוכה: פס "עובד" עד שמגיע אחוז ראשון.</summary>
    public void Start() => OnUi(() =>
    {
        _showingError = false;
        Tray?.Progress(null);
        _taskbar?.SetProgressState(_form.Handle, TaskbarState.Indeterminate);
    });

    /// <summary>אחוז ההתקדמות; null משאיר פס "עובד".</summary>
    public void Report(double? percent) => OnUi(() =>
    {
        Tray?.Progress(percent);
        if (_taskbar is null || _showingError) return;

        if (percent is not { } p || double.IsNaN(p))
        {
            _taskbar.SetProgressState(_form.Handle, TaskbarState.Indeterminate);
            return;
        }

        _taskbar.SetProgressState(_form.Handle, TaskbarState.Normal);
        _taskbar.SetProgressValue(_form.Handle, (ulong)Math.Clamp(p * 10, 0, 1000), 1000);
    });

    /// <summary>סיום הפעולה. בהצלחה הפס נעלם; בשגיאה ברקע הוא נשאר אדום. בשני המקרים — הבהוב, אם החלון ברקע.</summary>
    public void Finish(bool failed) => OnUi(() =>
    {
        Tray?.Finished(failed, cancelled: false);
        bool watching = Form.ActiveForm == _form;

        if (_taskbar is not null)
        {
            // מי שמסתכל על החלון רואה את השגיאה בממשק; הפס האדום הוא למי שלא.
            if (failed && !watching)
            {
                _showingError = true;
                _taskbar.SetProgressState(_form.Handle, TaskbarState.Error);
                _taskbar.SetProgressValue(_form.Handle, 1000, 1000);
            }
            else
            {
                _taskbar.SetProgressState(_form.Handle, TaskbarState.NoProgress);
            }
        }

        // הבהוב עד שחוזרים לחלון — ורק אם המשתמש אינו מסתכל עליו כבר.
        if (!watching)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = _form.Handle,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            };
            FlashWindowEx(ref info);
        }
    });

    /// <summary>ביטול: הפס נעלם בלי הבהוב.</summary>
    public void Clear() => OnUi(() =>
    {
        _showingError = false;
        Tray?.Finished(failed: false, cancelled: true);
        _taskbar?.SetProgressState(_form.Handle, TaskbarState.NoProgress);
    });

    private void OnUi(Action action)
    {
        if (_form.IsDisposed || !_form.IsHandleCreated) return;
        try
        {
            if (_form.InvokeRequired) _form.BeginInvoke(action);
            else action();
        }
        catch
        {
            // חיווי בלבד — לעולם לא יפיל פעולה.
        }
    }

    // ------------------------------------------------------------ Win32 / COM

    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO info);

    private enum TaskbarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    // ITaskbarList3 יורש מ-ITaskbarList ו-ITaskbarList2: כל השיטות שלהם חייבות להופיע כאן,
    // לפי הסדר, כדי שהטבלה הווירטואלית תתאים. בשימוש בפועל רק שלוש.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarState state);
    }
}
