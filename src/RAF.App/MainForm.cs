using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RAF.App;

/// <summary>
/// חלון ראשי ללא מסגרת, המארח את הממשק כולו ב-WebView2.
/// שורת הכותרת, הכפתורים והגרירה ממומשים בממשק עצמו לצורך מראה אחיד ומודרני.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly Bridge _bridge;
    private bool _webViewReady;

    // גרירה מבחוץ: אזור שחרור שמופיע מעל הממשק, ושעון שמזהה מתי להציג אותו.
    private readonly DropZone _dropZone = new();
    private readonly System.Windows.Forms.Timer _dragWatch = new() { Interval = 100 };
    private bool _buttonWasDown;
    private bool _pressedOutside;

    // חיבור כונן מייצר כמה הודעות ברצף (דיסק, מחיצות, אמצעי אחסון). ממתינים שיירגע, ומרעננים פעם אחת.
    private readonly System.Windows.Forms.Timer _devicesSettled = new() { Interval = 1500 };

    /// <summary>התקדמות פעולות ארוכות בסמל שבשורת המשימות.</summary>
    internal TaskbarProgress Taskbar { get; }

    /// <summary>הסמל באזור ההודעות — כשהחלון נשלח לשם (הכפתור שליד "מזעור").</summary>
    internal TrayIcon Tray { get; }

    internal MainForm()
    {
        _bridge = new Bridge(this);
        Taskbar = new TaskbarProgress(this);

        Text = "שחזור מתקדם חינם — RAF";
        // הסמל שנצרב ב-EXE מוצג גם בשורת המשימות ובמעבר בין חלונות.
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Tray = new TrayIcon(this);
        Taskbar.Tray = Tray;

        // קנה המידה נקבע לפי DPI ולא לפי גופן. ללא קביעה מפורשת,
        // WinForms מחשב את גודל החלון מול הגופן ומקבל תוצאה שגויה
        // במסכים שאינם בקנה מידה 100%.
        AutoScaleMode = AutoScaleMode.Dpi;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1280, 820);

        // פתיחה במסך מלא: מבטלת לגמרי את תלות הפריסה בגודל שנקבע מראש.
        WindowState = FormWindowState.Maximized;
        DoubleBuffered = true;

        _webView = new WebView2 { Dock = DockStyle.Fill };

        // צבע הרקע לפני שהממשק נטען — לפי ערכת Windows, כדי שלא יהבהב
        // חלון כהה במחשב בהיר. הממשק מעדכן אותו כשהמשתמש בוחר ערכה.
        ApplyTheme(dark: !SystemUsesLightTheme());
        Controls.Add(_webView);
        _webView.BringToFront();

        Controls.Add(_dropZone);
        _dropZone.Dropped += paths => _bridge.FilesDropped(paths);
        _dragWatch.Tick += (_, _) => WatchDrag();

        _devicesSettled.Tick += (_, _) =>
        {
            _devicesSettled.Stop();
            _bridge.DisksChanged();
        };

        Load += async (_, _) => await InitializeWebViewAsync();
    }

    private bool _dark = true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyModernFrame(Handle, _dark);
        DeviceWatch.Register(Handle);
    }

    /// <summary>התאמת צבעי החלון עצמו לערכת הנושא של הממשק.</summary>
    internal void ApplyTheme(bool dark)
    {
        _dark = dark;
        var color = dark ? Color.FromArgb(11, 13, 18) : Color.FromArgb(243, 245, 249);

        BackColor = color;
        _webView.DefaultBackgroundColor = color;
        _dropZone.SetTheme(dark);
        if (IsHandleCreated) NativeChrome.ApplyModernFrame(Handle, dark);
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
    }

    private async Task InitializeWebViewAsync()
    {
        // תיקיית נתוני WebView2 בתיקיית הזמניים — התוכנה אינה מלכלכת את מיקום ההרצה.
        string userDataFolder = Path.Combine(Path.GetTempPath(), "RAF.WebView2");

        var options = new CoreWebView2EnvironmentOptions
        {
            // העדפת שפה עברית לתפריטי ההקשר ולהודעות המובנות.
            Language = "he-IL",
            AdditionalBrowserArguments = "--disable-features=msWebOOUI,msPdfOOUI --allow-file-access-from-files",
        };

#if DEBUG || REMOTE_UI_TEST
        // בדיקות ממשק אוטומטיות: שליטה בדף דרך Chrome DevTools Protocol. רק בבנייה
        // לפיתוח, או בבניית בדיקה של גרסת ההפצה (RafUiTest) — פורט דיבאג פתוח נותן
        // לכל תהליך מקומי שליטה בתוכנה שרצה כמנהל, ולכן לעולם לא בקובץ שמופץ.
        if (int.TryParse(Environment.GetEnvironmentVariable("RAF_REMOTE_DEBUG_PORT"), out int debugPort))
            options.AdditionalBrowserArguments += $" --remote-debugging-port={debugPort}";
#endif

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        catch (Exception ex)
        {
            ShowFatalError(
                "לא ניתן לאתחל את מנוע התצוגה WebView2.\n\n" +
                "ב-Windows 11 המנוע מותקן מראש. אם המחשב מריץ Windows 10 ישן, " +
                "יש להתקין את WebView2 Runtime מאתר Microsoft.\n\n" +
                "פירוט: " + ex.Message);
            return;
        }

        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;

        // --- הקשחה: הממשק הוא מקומי בלבד ואינו אמור לנווט לרשת ---
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttachedOrDebugBuild;

        // --- הגשת הממשק מתוך ה-EXE ---
        core.AddWebResourceRequestedFilter(WebAssets.BaseUrl + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += (_, e) => e.Handled = true;

        // גרירת קבצים מתקבלת באזור השחרור (ראו FileDrop). WebView2 מוותר על שלו,
        // אחרת קובץ שנגרר היה נפתח בתוכו במקום הממשק.
        _webView.AllowExternalDrop = false;
        _dragWatch.Start();

        core.Navigate(WebAssets.BaseUrl + "index.html");
        _webViewReady = true;

        // ה-WebView נוצר לעיתים לפני שהחלון קיבל את גודלו הסופי,
        // ואז אזור הציור נשאר קטן מהחלון. אילוץ פריסה מחדש מיישר אותם.
        SyncWebViewBounds();
    }

    /// <summary>מענה לבקשות משאבים — מחזיר את הנכס המוטמע המתאים.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        string fileName = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(fileName)) fileName = "index.html";

        var env = _webView.CoreWebView2.Environment;

        // נגן התצוגה המקדימה: קטעים מקובץ שנמצא בסריקה, נקראים מהכונן לפי בקשה.
        if (fileName.StartsWith("media/", StringComparison.Ordinal))
        {
            _bridge.ServeMedia(e, env, fileName["media/".Length..]);
            return;
        }

        byte[]? content = WebAssets.Read(fileName);

        if (content is null)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        string headers =
            $"Content-Type: {WebAssets.ContentType(fileName)}\r\n" +
            "Cache-Control: no-store\r\n";

        e.Response = env.CreateWebResourceResponse(new MemoryStream(content), 200, "OK", headers);
    }

    /// <summary>קבלת בקשה מהממשק, העברתה לגשר והחזרת התשובה.</summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        string response = await _bridge.HandleAsync(raw);

        if (_webViewReady && !IsDisposed)
            _webView.CoreWebView2.PostWebMessageAsString(response);
    }

    /// <summary>התאמת אזור הציור לגודל החלון בפועל.</summary>
    private void SyncWebViewBounds()
    {
        if (IsDisposed) return;

        _webView.Bounds = new Rectangle(Point.Empty, ClientSize);
        PerformLayout();
        _webView.Refresh();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_webViewReady) SyncWebViewBounds();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (_webViewReady) SyncWebViewBounds();
    }

    /// <summary>
    /// כתיבת קובץ אבחון לתיקיית הזמניים. משמש לאיתור בעיות פריסה
    /// ללא צורך שהמשתמש יקרא מספרים מהמסך.
    /// </summary>
    internal void WriteDiagnostics(string pageMetrics)
    {
        try
        {
            var screen = Screen.FromControl(this);
            string path = Path.Combine(Path.GetTempPath(), "RAF-diagnostics.txt");

            var text = new StringBuilder();
            text.AppendLine("RAF diagnostics — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine(new string('-', 60));
            text.AppendLine($"WindowState        : {WindowState}");
            text.AppendLine($"Form.Bounds        : {Bounds}");
            text.AppendLine($"Form.ClientSize    : {ClientSize}");
            text.AppendLine($"Form.DeviceDpi     : {DeviceDpi}  ({DeviceDpi / 96.0 * 100:F0}%)");
            text.AppendLine($"Form.AutoScaleMode : {AutoScaleMode}");
            text.AppendLine($"Form.CurrentAutoScaleDimensions : {CurrentAutoScaleDimensions}");
            text.AppendLine($"Form.RightToLeft   : {RightToLeft}");
            text.AppendLine($"WebView.Bounds     : {_webView.Bounds}");
            text.AppendLine($"WebView.Dock       : {_webView.Dock}");
            text.AppendLine($"WebView.Visible    : {_webView.Visible}");
            text.AppendLine($"Screen.Bounds      : {screen.Bounds}");
            text.AppendLine($"Screen.WorkingArea : {screen.WorkingArea}");
            text.AppendLine($"Screen.Primary     : {screen.Primary}");
            text.AppendLine();
            text.AppendLine("Page metrics:");
            text.AppendLine(pageMetrics);

            File.WriteAllText(path, text.ToString());
        }
        catch
        {
            // אבחון אינו קריטי לפעולת התוכנה.
        }
    }

    /// <summary>נתוני אבחון על החלון ועל קנה המידה, להצגה בשורת המצב.</summary>
    internal (int Width, int Height, int ClientWidth, int ClientHeight, int Dpi) Metrics()
        => (Width, Height, ClientSize.Width, ClientSize.Height, DeviceDpi);

    // ------------------------------------------------------------ שליטת חלון

    /// <summary>הרצת פעולה על תהליכון הממשק והחזרת null, לשימוש הגשר.</summary>
    internal object? InvokeOnUi(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
        return null;
    }

    /// <summary>
    /// הרצת פעולה על תהליכון הממשק והמתנה לסיומה.
    /// נדרש לתיבות דו-שיח, שחייבות לרוץ על תהליכון הממשק ולהחזיר תוצאה.
    /// </summary>
    internal void InvokeOnUiSync(Action action)
    {
        if (InvokeRequired) Invoke(action);
        else action();
    }

    /// <summary>שליחת הודעה יזומה לממשק, לדיווחי התקדמות.</summary>
    internal void PostToWeb(string payload)
    {
        if (!_webViewReady || IsDisposed) return;

        InvokeOnUi(() =>
        {
            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(payload);
            }
            catch
            {
                // החלון נסגר באמצע פעולה ארוכה — אין למי לדווח.
            }
        });
    }

    internal void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    protected override void WndProc(ref Message m)
    {
        // חלון ללא מסגרת חייב לחשב בעצמו את גבולות מצב "מוגדל",
        // אחרת הוא מכסה גם את שורת המשימות.
        if (m.Msg == NativeChrome.WM_GETMINMAXINFO)
        {
            NativeChrome.ApplyMaximizedBounds(Handle, m.LParam);
            return;
        }

        if (DeviceWatch.IsDiskChange(m))
        {
            _devicesSettled.Stop();
            _devicesSettled.Start();
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// זיהוי גרירה מבחוץ: הכפתור נלחץ מחוץ לחלון, והסמן נמצא עכשיו מעליו.
    /// לחיצה בתוך החלון עצמו — בחירה, גלילה, גרירת החלון — אינה מציגה דבר.
    /// </summary>
    private void WatchDrag()
    {
        bool down = FileDrop.PrimaryButtonDown();
        var point = Cursor.Position;

        if (down && !_buttonWasDown) _pressedOutside = !FileDrop.IsOver(Handle, point);
        _buttonWasDown = down;

        bool show = down && _pressedOutside && FileDrop.IsOver(Handle, point);
        if (show == _dropZone.Visible) return;

        _dropZone.Visible = show;
        if (show) _dropZone.BringToFront();
    }

    /// <summary>סגירה באמצע פעולה ארוכה מפסיקה אותה — ולכן שואלים קודם.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _bridge.RunningOperation is { } running)
        {
            string message = running switch
            {
                LongOperation.Scan =>
                    "סריקה פועלת כעת. אם תסגרו את התוכנה, הסריקה תיעצר באמצע והתוצאות שלה יאבדו.\n\n" +
                    "בסריקה מתקדמת נשמרת נקודת ביניים כל 5 דקות, ואפשר לפתוח אותה אחר כך מ\"סריקות אחרונות\".",
                LongOperation.Recovery =>
                    "שחזור פועל כעת. אם תסגרו את התוכנה, השחזור ייעצר וחלק מהקבצים לא ישוחזרו.",
                LongOperation.Hunt =>
                    "סריקת כונן פועלת כעת. אם תסגרו את התוכנה, היא תיעצר ולא יוצגו המחיצות שנמצאו.",
                _ =>
                    "יצירת תמונת דיסק פועלת כעת. אם תסגרו את התוכנה, היא תיעצר. " +
                    "מה שכבר הועתק נשמר, ואפשר להמשיך מאותה נקודה בפעם הבאה.",
            };

            var answer = MessageBox.Show(this, message + "\n\nלסגור בכל זאת?", "שחזור מתקדם חינם",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnFormClosing(e);
        if (!e.Cancel) Tray.Dispose();                   // בלי סמל "רפאים" באזור ההודעות אחרי היציאה
    }

    private static void ShowFatalError(string message) =>
        MessageBox.Show(message, "שחזור מתקדם חינם — שגיאה",
            MessageBoxButtons.OK, MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
}

/// <summary>עזר קטן לקביעה האם לאפשר כלי מפתחים.</summary>
internal static class Debugger
{
    internal static bool IsAttachedOrDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
