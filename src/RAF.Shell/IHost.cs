namespace RAF.App;

/// <summary>
/// מה שהגשר צריך מהחלון שמציג את הממשק — ב-Windows (WinForms ו-WebView2) או במק.
/// כל השאר — המנוע, הסריקה והממשק עצמו — משותף.
/// </summary>
internal interface IHost
{
    /// <summary>הרצה על תהליכון הממשק, בלי המתנה. מחזיר null (לשימוש בתשובות הגשר).</summary>
    object? InvokeOnUi(Action action);

    /// <summary>הרצה על תהליכון הממשק והמתנה לסיום — לחלונות בחירה.</summary>
    void InvokeOnUiSync(Action action);

    /// <summary>הודעה יזומה לממשק (התקדמות, אירועים).</summary>
    void PostToWeb(string payload);

    void ApplyTheme(bool dark);
    void Minimize();
    void ToTray();
    void ToggleMaximize();
    void Close();

    /// <summary>גרירת החלון או שינוי גודלו מתוך הממשק (hit — קוד האזור של Windows).</summary>
    void BeginDrag(int hit);

    void WriteDiagnostics(string pageMetrics);
    (int Width, int Height, int ClientWidth, int ClientHeight, int Dpi) Metrics();

    /// <summary>התקדמות על סמל התוכנה (שורת המשימות ב-Windows, ה-Dock במק).</summary>
    ITaskbar Taskbar { get; }

    /// <summary>חלון בחירת קובץ (פתיחה או שמירה). true — נבחר; הנתיבים ב-dialog.</summary>
    bool ShowFileDialog(Dialogs.FileDialog dialog);

    /// <summary>חלון בחירת תיקייה. true — נבחרה; הנתיב ב-dialog.SelectedPath.</summary>
    bool ShowFolderDialog(Dialogs.FolderBrowserDialog dialog);

    /// <summary>תמונה ממוזערת (JPEG) בגודל עד box. null — המערכת לא מקטינה, או שהנתונים אינם תמונה.</summary>
    byte[]? Thumbnail(byte[] data, int box);

    /// <summary>פתיחת תיקייה בסייר הקבצים של המערכת.</summary>
    void OpenFolder(string path);
}

internal interface ITaskbar
{
    void Start();
    void Report(double? percent);
    void Finish(bool failed);
    void Clear();
}
