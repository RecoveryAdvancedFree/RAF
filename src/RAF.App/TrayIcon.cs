using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RAF.App;

/// <summary>
/// שליחת התוכנה לאזור ההודעות (הסמלים הקטנים שליד השעון) — כמו ב-PDFConvert.
/// בזמן פעולה ארוכה הסמל מצויר עם אחוז ההתקדמות, כך שרואים כמה נשאר בלי לפתוח
/// את החלון. בסיום מופיעה הודעה והחלון חוזר. לחיצה כפולה — חזרה לחלון.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const string Name = "שחזור מתקדם חינם";

    private readonly Form _form;
    private readonly NotifyIcon _icon;
    private Icon? _drawn;
    private int _shownPercent = -1;
    private int? _percent;              // ההתקדמות של הפעולה הנוכחית; null — אין פעולה, או שאין אחוז

    public TrayIcon(Form form)
    {
        _form = form;
        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add("פתיחת החלון", null, (_, _) => Restore());
        menu.Items.Add("יציאה", null, (_, _) => { Restore(); _form.Close(); });

        _icon = new NotifyIcon { Text = Name, Icon = form.Icon, Visible = false, ContextMenuStrip = menu };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        _icon.BalloonTipClicked += (_, _) => Restore();
    }

    public bool InTray => _icon.Visible;

    /// <summary>הסתרת החלון והצגת הסמל באזור ההודעות.</summary>
    public void Send()
    {
        Draw(force: true);
        _icon.Visible = true;
        _form.Hide();
    }

    public void Restore()
    {
        _icon.Visible = false;
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    /// <summary>התקדמות הפעולה הנוכחית (מאותו מקום שמעדכן את שורת המשימות).</summary>
    public void Progress(double? percent)
    {
        _percent = percent is { } p && !double.IsNaN(p) ? (int)Math.Clamp(Math.Floor(p), 0, 100) : null;
        if (InTray) Draw(force: false);
    }

    /// <summary>סוף הפעולה: הודעה, וחזרה לחלון — כדי שהתוצאה לא תחכה בלי שמישהו ראה אותה.</summary>
    public void Finished(bool failed, bool cancelled)
    {
        _percent = null;
        if (!InTray) return;
        Draw(force: true);
        if (!cancelled)
            _icon.ShowBalloonTip(6000, Name,
                failed ? "הפעולה נעצרה בגלל שגיאה — הפרטים בחלון." : "הפעולה הסתיימה.",
                failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
        Restore();
    }

    private void Draw(bool force)
    {
        int shown = _percent ?? -1;
        if (!force && shown == _shownPercent) return;
        _shownPercent = shown;

        if (_percent is not { } p)
        {
            _icon.Icon = _form.Icon;
            _icon.Text = Name;
            ReplaceDrawn(null);
            return;
        }

        var icon = PercentIcon(p);
        _icon.Icon = icon;
        _icon.Text = $"{Name} — {p}%";
        ReplaceDrawn(icon);
    }

    /// <summary>עיגול בצבע התוכנה עם המספר בלבן — קריא גם בגודל 16 פיקסלים.</summary>
    private static Icon PercentIcon(int percent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0x1A, 0x6F, 0xE0));
            g.FillEllipse(fill, 0, 0, 31, 31);

            float size = percent >= 100 ? 12f : percent >= 10 ? 16f : 19f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(percent.ToString(), font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        }

        // Icon.FromHandle אינו משחרר את ה-HICON — עותק מנוהל, ושחרור המקורי מיד.
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private void ReplaceDrawn(Icon? next)
    {
        var old = _drawn;
        _drawn = next;
        old?.Dispose();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn?.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
