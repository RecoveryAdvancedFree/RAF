using Microsoft.Web.WebView2.Core;
using RAF.Core.Recovery;

namespace RAF.App;

/// <summary>
/// נגן התצוגה המקדימה: הנגן שבממשק מבקש מהכתובת media/&lt;מספר קובץ&gt; קטעים
/// מהקובץ (בקשות Range), והם נקראים ישירות מהכונן — רק מה שמנגנים, לא הקובץ כולו.
/// </summary>
internal sealed partial class Bridge
{
    /// <summary>קטע מרבי לתשובה אחת. הנגן מבקש את הבא בעצמו.</summary>
    private const int MediaChunk = 2 * 1024 * 1024;

    private readonly object _mediaGate = new();
    private (ScanSession Session, long Id, FileContentSource Source)? _media;

    /// <summary>סוג התוכן לפי הסיומת — רק מה שהנגן המובנה יודע לנגן.</summary>
    internal static (string Mime, bool Video)? MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        "mp4" or "m4v" or "mov" => ("video/mp4", true),
        "webm" or "mkv" => ("video/webm", true),
        "mp3" => ("audio/mpeg", false),
        "wav" => ("audio/wav", false),
        "flac" => ("audio/flac", false),
        "m4a" => ("audio/mp4", false),
        "ogg" or "opus" => ("audio/ogg", false),
        "aac" => ("audio/aac", false),
        _ => null,
    };

    /// <summary>מענה לבקשת קטע מהנגן. הקריאה מהכונן ברקע, והתשובה על תהליכון הממשק.</summary>
    internal async void ServeMedia(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env, string idText)
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
            var chunk = await Task.Run(() => ReadMediaChunk(idText, range));

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

    private (byte[] Data, long Start, long Total, string Mime)? ReadMediaChunk(string idText, string range)
    {
        if (!long.TryParse(idText, out long id) || _session is not { Offline: false } session) return null;
        var file = session.ById(id);
        if (file is null || MediaType(file.Extension) is not { } type) return null;

        lock (_mediaGate)
        {
            // קובץ אחד פתוח בכל רגע: מעבר לקובץ אחר בתצוגה סוגר את הקודם.
            if (_media is not { } open || open.Session != session || open.Id != id)
            {
                _media?.Source.Dispose();
                _media = null;

                var source = FileContentSource.Open(
                    session.FileSystem, session.DiskNumber, session.PartitionOffset,
                    session.PartitionSize, session.SectorSize, file);
                if (source is null) return null;
                _media = (session, id, source);
            }

            var media = _media.Value.Source;
            long total = media.Length;

            // "bytes=START-" או "bytes=START-END"; בלי כותרת — מההתחלה.
            long start = 0, end = total - 1;
            if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = range[6..].Split('-', 2);
                if (long.TryParse(parts[0], out long s)) start = s;
                if (parts.Length > 1 && long.TryParse(parts[1], out long en)) end = Math.Min(en, total - 1);
            }

            if (start < 0 || start >= total) return null;
            int length = (int)Math.Min(MediaChunk, end - start + 1);

            byte[] data = new byte[length];
            int read = media.Read(start, data);
            if (read < length) Array.Resize(ref data, read);
            return (data, start, total, type.Mime);
        }
    }
}
