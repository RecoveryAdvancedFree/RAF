using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Web.WebView2.Core;
using Win = System.Windows.Forms;

namespace RAF.App;

/// <summary>החלון של Windows כמארח של הגשר (IHost): חלונות בחירה, שורת המשימות, תמונות ממוזערות.</summary>
internal sealed partial class MainForm : IHost
{
    ITaskbar IHost.Taskbar => Taskbar;

    void IHost.Minimize() => WindowState = FormWindowState.Minimized;
    void IHost.ToTray() => Tray.Send();
    void IHost.BeginDrag(int hit) => NativeChrome.BeginWindowAction(Handle, hit);

    bool IHost.ShowFileDialog(Dialogs.FileDialog request)
    {
        using Win.FileDialog dialog = request.Save
            ? new Win.SaveFileDialog { OverwritePrompt = request.OverwritePrompt }
            : new Win.OpenFileDialog { Multiselect = request.Multiselect, CheckFileExists = request.CheckFileExists };
        dialog.Title = request.Title;
        if (request.Filter.Length > 0) dialog.Filter = request.Filter;
        dialog.FileName = request.FileName;
        dialog.DefaultExt = request.DefaultExt;
        if (dialog.ShowDialog(this) != Win.DialogResult.OK) return false;
        request.FileName = dialog.FileName;
        request.FileNames = dialog.FileNames;
        return true;
    }

    bool IHost.ShowFolderDialog(Dialogs.FolderBrowserDialog request)
    {
        using var dialog = new Win.FolderBrowserDialog
        {
            Description = request.Description,
            UseDescriptionForTitle = request.UseDescriptionForTitle,
            ShowNewFolderButton = request.ShowNewFolderButton,
        };
        if (dialog.ShowDialog(this) != Win.DialogResult.OK) return false;
        request.SelectedPath = dialog.SelectedPath;
        return true;
    }

    /// <summary>
    /// פתיחת תיקייה בסייר הקבצים. הנתיב מגיע מהממשק, ולכן הוא מועבר כארגומנט יחיד
    /// ל-explorer ולא כפקודה.
    /// </summary>
    void IHost.OpenFolder(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        System.Diagnostics.Process.Start(start);
    }

    byte[]? IHost.Thumbnail(byte[] data, int box)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var image = Image.FromStream(input, false, false);

            double scale = Math.Min(1.0, (double)box / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)(image.Width * scale));
            int h = Math.Max(1, (int)(image.Height * scale));

            using var thumb = new Bitmap(w, h);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(image, 0, 0, w, h);
            }

            using var output = new MemoryStream();
            thumb.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }
        catch
        {
            // נתונים שאינם מפוענחים כתמונה — קובץ פגום או זיהוי שגוי.
            return [];
        }
    }

    /// <summary>מענה לבקשת קטע מהנגן. הקריאה מהכונן ברקע, והתשובה על תהליכון הממשק.</summary>
    private async void ServeMedia(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env, string idText)
    {
        // הדחייה מסתיימת פעם אחת בלבד, ב-finally. בלי using: Dispose היה מסיים
        // אותה שוב, ו-WebView2 זורק על סיום כפול ("A method was called at an unexpected time").
        //
        // הפונקציה רצה ברקע (async void): שגיאה שיוצאת ממנה מפילה חלון שגיאה למשתמש.
        // לכן כל שלב עטוף — גם מתן התשובה, כי הנגן מבטל בקשות כשמדלגים בסרטון,
        // ותשובה לבקשה שבוטלה נדחית.
        CoreWebView2Deferral deferral;
        try { deferral = e.GetDeferral(); }
        catch { return; }

        CoreWebView2WebResourceResponse response;
        try
        {
            string range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : "";
            var chunk = await Task.Run(() => _bridge.ReadMediaChunk(idText, range));

            if (chunk is null)
            {
                response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
            else
            {
                var (data, start, total, mime) = chunk.Value;
                long end = start + data.Length - 1;
                string headers =
                    $"Content-Type: {mime}\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    $"Content-Range: bytes {start}-{end}/{total}\r\n" +
                    $"Content-Length: {data.Length}\r\n" +
                    "Cache-Control: no-store\r\n";
                response = env.CreateWebResourceResponse(new MemoryStream(data), 206, "Partial Content", headers);
            }
        }
        catch
        {
            try { response = env.CreateWebResourceResponse(null, 500, "Error", ""); }
            catch { response = null!; }
        }

        try { if (response is not null) e.Response = response; } catch { /* הבקשה בוטלה בינתיים */ }
        try { deferral.Complete(); } catch { /* הבקשה בוטלה בינתיים */ }
    }

}
