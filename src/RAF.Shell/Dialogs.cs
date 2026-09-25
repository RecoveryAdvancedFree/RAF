namespace RAF.App.Dialogs;

/// <summary>
/// חלונות הבחירה של הגשר — באותם שמות ומאפיינים כמו של WinForms, כדי שקוד הגשר יישאר
/// כמו שהוא. החלון (IHost) מציג אותם בדרך של המערכת שלו.
/// </summary>
internal enum DialogResult { OK, Cancel }

internal abstract class FileDialog : IDisposable
{
    public string Title { get; set; } = "";
    /// <summary>בפורמט של Windows: "תיאור|*.a;*.b|תיאור|*.*".</summary>
    public string Filter { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DefaultExt { get; set; } = "";
    public bool Multiselect { get; set; }
    public bool CheckFileExists { get; set; }
    public bool OverwritePrompt { get; set; }
    public string[] FileNames { get; set; } = [];
    public abstract bool Save { get; }

    public DialogResult ShowDialog(IHost owner) => owner.ShowFileDialog(this) ? DialogResult.OK : DialogResult.Cancel;

    /// <summary>הסיומות שהמסנן מתיר (בלי "*.*"), לחלונות שאינם מבינים את הפורמט של Windows.</summary>
    public IEnumerable<string> Extensions()
        => Filter.Split('|').Where((_, i) => i % 2 == 1)
                 .SelectMany(p => p.Split(';'))
                 .Select(p => p.Trim())
                 .Where(p => p.StartsWith("*.") && p != "*.*" && p.IndexOf('*', 2) < 0)
                 .Select(p => p[2..])
                 .Distinct();

    public void Dispose() { }
}

internal sealed class OpenFileDialog : FileDialog { public override bool Save => false; }

internal sealed class SaveFileDialog : FileDialog { public override bool Save => true; }

internal sealed class FolderBrowserDialog : IDisposable
{
    public string Description { get; set; } = "";
    public bool UseDescriptionForTitle { get; set; }
    public bool ShowNewFolderButton { get; set; }
    public string SelectedPath { get; set; } = "";

    public DialogResult ShowDialog(IHost owner) => owner.ShowFolderDialog(this) ? DialogResult.OK : DialogResult.Cancel;

    public void Dispose() { }
}
