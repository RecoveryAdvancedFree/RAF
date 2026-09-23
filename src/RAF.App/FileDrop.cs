using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RAF.App;

/// <summary>
/// גרירת קבצים מסייר הקבצים אל החלון.
///
/// התוכנה פועלת תמיד בהרשאות מנהל, ו-Windows חוסם גרירת OLE מתהליך רגיל
/// (הסייר) אל תהליך מורם (UIPI). WebView2 מכסה את כל החלון ורשום כיעד OLE,
/// ולכן גרירה אליו נענית ב-🚫 — והמנגנון הישן, WM_DROPFILES, לעולם אינו
/// מגיע לתורו. נבדק על המחשב: פתיחת WM_DROPFILES לחלון הראשי לבדה לא עזרה.
///
/// הפתרון: כשגרירה מבחוץ נכנסת לחלון, מוצג מעל הממשק אזור שחרור — חלון
/// שלנו, בלי רישום OLE, ופתוח ל-WM_DROPFILES גם מתהליך בהרשאות נמוכות.
/// </summary>
internal static partial class FileDrop
{
    internal const int WM_DROPFILES = 0x0233;
    private const int WM_COPYDATA = 0x004A;
    private const int WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int SM_SWAPBUTTON = 23;
    private const uint GA_ROOT = 2;

    [LibraryImport("shell32.dll")]
    private static partial void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static unsafe partial uint DragQueryFile(IntPtr hDrop, uint index, char* file, uint size);

    [LibraryImport("shell32.dll")]
    private static partial void DragFinish(IntPtr hDrop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr filterStatus);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    // POINT מועבר בערך: ב-x64 שמונה הבתים שלו הם ארגומנט אחד, x בחצי התחתון.
    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(long point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>פתיחת חלון לגרירה, גם מתהליך בהרשאות נמוכות יותר.</summary>
    internal static void Enable(IntPtr hWnd)
    {
        ChangeWindowMessageFilterEx(hWnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hWnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hWnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(hWnd, true);
    }

    /// <summary>הנתיבים שבגרירה. משחרר את הגרירה בסיום.</summary>
    internal static unsafe List<string> Read(IntPtr hDrop)
    {
        var paths = new List<string>();
        try
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(hDrop, i, null, 0);
                char[] buffer = new char[length + 1];
                fixed (char* p = buffer)
                {
                    uint copied = DragQueryFile(hDrop, i, p, (uint)buffer.Length);
                    if (copied > 0) paths.Add(new string(buffer, 0, (int)copied));
                }
            }
        }
        finally
        {
            DragFinish(hDrop);
        }

        return paths;
    }

    /// <summary>האם הכפתור הראשי של העכבר לחוץ — בכל תוכנה, לא רק בשלנו.</summary>
    internal static bool PrimaryButtonDown()
    {
        int key = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    /// <summary>האם הנקודה על חלון שלנו ולא מוסתרת בחלון אחר (כולל WebView2, שהוא חלון-בן שלנו).</summary>
    internal static bool IsOver(IntPtr root, Point screenPoint)
    {
        long packed = (uint)screenPoint.X | ((long)screenPoint.Y << 32);
        IntPtr hwnd = WindowFromPoint(packed);
        return hwnd != IntPtr.Zero && GetAncestor(hwnd, GA_ROOT) == root;
    }
}

/// <summary>
/// אזור השחרור שמופיע מעל הממשק בזמן גרירה מבחוץ. זהו חלון של התוכנה עצמה,
/// ולכן Windows מוסר לו את הקבצים; כשהגרירה מסתיימת הוא נעלם.
/// </summary>
internal sealed class DropZone : Control
{
    internal event Action<List<string>>? Dropped;

    private bool _dark = true;

    internal DropZone()
    {
        Visible = false;
        Dock = DockStyle.Fill;
        RightToLeft = RightToLeft.Yes;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal void SetTheme(bool dark)
    {
        _dark = dark;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FileDrop.Enable(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == FileDrop.WM_DROPFILES)
        {
            var paths = FileDrop.Read(m.WParam);
            Visible = false;
            Dropped?.Invoke(paths);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // אותם צבעים כמו בממשק: רקע, משטח ומבטא, בערכה הכהה ובבהירה.
        var back = _dark ? Color.FromArgb(11, 13, 18) : Color.FromArgb(243, 245, 249);
        var surface = _dark ? Color.FromArgb(18, 21, 29) : Color.White;
        var accent = _dark ? Color.FromArgb(61, 159, 255) : Color.FromArgb(10, 111, 224);
        var dim = _dark ? Color.FromArgb(154, 164, 184) : Color.FromArgb(77, 89, 112);

        g.Clear(back);

        float scale = DeviceDpi / 96f;
        int inset = (int)(28 * scale);
        var box = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
        if (box.Width <= 0 || box.Height <= 0) return;

        using (var path = RoundedRect(box, 18 * scale))
        using (var fill = new SolidBrush(surface))
        using (var pen = new Pen(accent, 2.5f * scale) { DashStyle = DashStyle.Dash })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.RightToLeft | TextFormatFlags.WordBreak;

        using var title = new Font("Segoe UI", 20f, FontStyle.Bold);
        using var sub = new Font("Segoe UI", 12f);

        int mid = box.Top + box.Height / 2;
        var titleBox = new Rectangle(box.Left, mid - (int)(52 * scale), box.Width, (int)(56 * scale));
        var subBox = new Rectangle(box.Left, mid + (int)(8 * scale), box.Width, (int)(44 * scale));

        TextRenderer.DrawText(g, "שחררו כאן כדי לבדוק את הקבצים", title, titleBox, accent, flags);
        TextRenderer.DrawText(g, "קבצים ותיקיות · הקבצים המקוריים לא ישתנו", sub, subBox, dim, flags);
    }

    private static GraphicsPath RoundedRect(Rectangle r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
