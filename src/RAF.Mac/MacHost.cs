using System.Diagnostics;
using Photino.NET;

namespace RAF.App;

/// <summary>
/// החלון של מק: Photino מציג את הממשק ב-WKWebView של המערכת, והגשר (Bridge) הוא אותו גשר
/// של Windows. לחלון יש שורת כותרת של מק — כפתורי החלון שבממשק מוסתרים בו (ראו core.js).
/// </summary>
internal sealed class MacHost : IHost
{
    private const string Scheme = "raf";
    private readonly PhotinoWindow _window;
    private readonly Bridge _bridge;

    public MacHost()
    {
        _bridge = new Bridge(this);
        _window = new PhotinoWindow()
            .SetTitle(L.T("שחזור מתקדם חינם"))
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 820)
            .SetMinSize(900, 600)
            .Center()
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(Environment.GetEnvironmentVariable("RAF_DEVTOOLS") == "1")
            .RegisterCustomSchemeHandler(Scheme, Serve)
            .RegisterWebMessageReceivedHandler(OnMessage);
    }

    public void Run()
    {
        _window.Load($"{Scheme}://app/index.html");
        _window.WaitForClose();
    }

    private void OnMessage(object? sender, string raw)
    {
        _ = Task.Run(async () =>
        {
            string response = await _bridge.HandleAsync(raw);
            try { _window.SendWebMessage(response); } catch { /* החלון נסגר */ }
        });
    }

    /// <summary>הממשק מתוך הקובץ, ונגן התצוגה המקדימה — קובץ מהכונן (עד 256MB, בלי בקשות Range).</summary>
    private Stream Serve(object sender, string scheme, string url, out string contentType)
    {
        string file = new Uri(url).AbsolutePath.TrimStart('/');
        if (file.Length == 0) file = "index.html";

        if (file.StartsWith("media/", StringComparison.Ordinal))
        {
            var output = new MemoryStream();
            contentType = "application/octet-stream";
            for (long at = 0; output.Length < 256L * 1024 * 1024; )
            {
                if (_bridge.ReadMediaChunk(file["media/".Length..], $"bytes={at}-") is not { } chunk || chunk.Data.Length == 0) break;
                output.Write(chunk.Data);
                contentType = chunk.Mime;
                at += chunk.Data.Length;
                if (at >= chunk.Total) break;
            }
            output.Position = 0;
            return output;
        }

        contentType = WebAssets.ContentType(file);
        return new MemoryStream(WebAssets.Read(file) ?? []);
    }

    // ------------------------------------------------------------ IHost

    public object? InvokeOnUi(Action action)
    {
        ThreadPool.QueueUserWorkItem(_ => { try { _window.Invoke(action); } catch { } });
        return null;
    }

    public void InvokeOnUiSync(Action action) => _window.Invoke(action);

    public void PostToWeb(string payload)
    {
        try { _window.SendWebMessage(payload); } catch { /* החלון נסגר */ }
    }

    public void ApplyTheme(bool dark) { }                 // החלון של מק עוקב אחרי המראה של המערכת
    public void Minimize() => _window.SetMinimized(true);
    public void ToTray() => _window.SetMinimized(true);
    public void ToggleMaximize() => _window.SetMaximized(!_window.Maximized);
    public void Close() => _window.Close();
    public void BeginDrag(int hit) { }                    // שורת הכותרת של מק גוררת בעצמה

    public void WriteDiagnostics(string pageMetrics)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "RAF-diagnostics.txt"), pageMetrics); }
        catch { }
    }

    public (int Width, int Height, int ClientWidth, int ClientHeight, int Dpi) Metrics()
    {
        var size = _window.Size;
        return (size.Width, size.Height, size.Width, size.Height, 96);
    }

    public ITaskbar Taskbar { get; } = new NoTaskbar();

    public bool ShowFileDialog(Dialogs.FileDialog dialog)
    {
        var extensions = dialog.Extensions().Select(e => "*." + e).ToArray();
        (string, string[])[] filters = extensions.Length > 0 ? [(dialog.Title, extensions)] : [];
        if (dialog.Save)
        {
            string? path = _window.ShowSaveFile(dialog.Title, dialog.FileName, filters);
            if (string.IsNullOrEmpty(path)) return false;
            if (Path.GetExtension(path).Length == 0 && dialog.DefaultExt.Length > 0) path += "." + dialog.DefaultExt;
            dialog.FileName = path;
            dialog.FileNames = [path];
            return true;
        }
        string[] paths = _window.ShowOpenFile(dialog.Title, null, dialog.Multiselect, filters);
        if (paths.Length == 0) return false;
        dialog.FileNames = paths;
        dialog.FileName = paths[0];
        return true;
    }

    public bool ShowFolderDialog(Dialogs.FolderBrowserDialog dialog)
    {
        string[] paths = _window.ShowOpenFolder(dialog.Description, null, false);
        if (paths.Length == 0) return false;
        dialog.SelectedPath = paths[0];
        return true;
    }

    public void OpenFolder(string path)
    {
        var start = new ProcessStartInfo("open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    public byte[]? Thumbnail(byte[] data, int box) => null;     // הדפדפן מקטין בעצמו

    private sealed class NoTaskbar : ITaskbar
    {
        public void Start() { }
        public void Report(double? percent) { }
        public void Finish(bool failed) { }
        public void Clear() { }
    }
}
