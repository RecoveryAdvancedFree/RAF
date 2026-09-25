using System.Diagnostics;
using System.Security.Cryptography;
using RAF.Core.Update;

namespace RAF.App;

/// <summary>
/// עדכון התוכנה: בדיקה מול הגרסה האחרונה בגיטהאב, הורדה, והחלפה.
///
/// תוכנה שהותקנה (יש לידה את קובץ ההסרה) מתעדכנת דרך קובץ ההתקנה, בהתקנה שקטה שסוגרת
/// אותה ופותחת אותה מחדש בסיום. תוכנה ניידת מחליפה את קובץ ה-EXE שלה במקום: קובץ שרץ
/// אי אפשר למחוק, אבל אפשר לשנות את שמו — הישן נשאר בצד עד ההפעלה הבאה ונמחק בה.
///
/// העדכון לעולם לא מתחיל באמצע פעולה ארוכה (סריקה, שחזור, יצירת תמונה): הוא סוגר את
/// התוכנה, והפעולה הייתה נעצרת באמצע. רק ב-Windows — גרסת המק מופצת בנפרד.
/// </summary>
internal sealed partial class Bridge
{
    private static readonly HttpClient Http = CreateHttp();
    private ReleaseInfo? _latest;
    private int _updating;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // גיטהאב דוחה בקשות בלי זיהוי.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RAF/" + AppVersion);
        return http;
    }

    private static bool UpdatesSupported => OperatingSystem.IsWindows();

    /// <summary>קובץ ה-EXE שרץ עכשיו.</summary>
    private static string? ExePath => Environment.ProcessPath;

    /// <summary>הותקנה בקובץ ההתקנה — לידה יושב קובץ ההסרה שההתקנה יוצרת.</summary>
    private static bool IsInstalled =>
        ExePath is { } exe && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "unins000.exe"));

    private static string UpdateTempFolder => Path.Combine(Path.GetTempPath(), "RAF", "update");

    /// <summary>"update.check" — האם יש גרסה חדשה יותר.</summary>
    private async Task<object> CheckForUpdate()
    {
        if (!UpdatesSupported) return new { available = false, current = AppVersion };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseInfo.LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(L.T("לא ניתן לבדוק אם יש עדכון. בדקו את החיבור לאינטרנט ונסו שוב."), ex);
        }

        var latest = ReleaseInfo.Parse(json);
        var current = ReleaseInfo.ParseVersion(AppVersion);
        bool available = latest is not null && current is not null && latest.IsNewerThan(current)
                         && (IsInstalled ? latest.Setup : latest.Portable) is not null;
        if (available) _latest = latest;

        return new { available, current = AppVersion, version = latest?.Version.ToString(3) };
    }

    /// <summary>
    /// "update.prepare" — מה יקרה אם נעדכן עכשיו, לתצוגה לפני האישור: לאיזה כונן העדכון
    /// כותב, ומה יקרה לתוצאות הסריקה הפתוחה.
    /// </summary>
    private object PrepareUpdate()
    {
        var latest = _latest ?? throw new InvalidOperationException(L.T("לא נמצא עדכון. בדקו שוב אם יש גרסה חדשה."));
        bool installed = IsInstalled;
        var asset = (installed ? latest.Setup : latest.Portable)!;

        // ההתקנה נכתבת לתיקיית ההתקנה, והקובץ שיורד — לתיקיית הזמניים; קובץ נייד מוחלף במקומו.
        var drives = new List<string> { DriveOf(ExePath) };
        if (installed) drives.Add(DriveOf(UpdateTempFolder));

        var session = _session;
        return new
        {
            version = latest.Version.ToString(3),
            installed,
            size = asset.Size,
            drives = drives.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            busy = RunningOperation is not null,
            sessionOpen = session is not null,
            // סריקה שנפתחה מקובץ, או שנשמרה אוטומטית — תחכה ב"סריקות אחרונות" אחרי העדכון.
            sessionSaved = session is not null && session.SavedPath is { } saved && File.Exists(saved),
        };
    }

    private static string DriveOf(string? path)
    {
        try { return path is null ? "" : (Path.GetPathRoot(Path.GetFullPath(path)) ?? "").TrimEnd('\\'); }
        catch { return ""; }
    }

    /// <summary>
    /// "update.install" — הורדה, אימות והפעלה של העדכון, ואז סגירת התוכנה.
    /// ההתקדמות נשלחת באירוע update.progress.
    /// </summary>
    private async Task<object?> InstallUpdate()
    {
        if (!UpdatesSupported) throw new InvalidOperationException(L.T("עדכון אוטומטי אינו זמין במערכת הזו."));
        if (RunningOperation is not null)
            throw new InvalidOperationException(L.T("אי אפשר לעדכן באמצע פעולה: העדכון סוגר את התוכנה, והפעולה הייתה נעצרת. " +
                                                    "אפשר לעדכן כשהיא תסתיים."));
        var latest = _latest ?? throw new InvalidOperationException(L.T("לא נמצא עדכון. בדקו שוב אם יש גרסה חדשה."));
        string exe = ExePath ?? throw new InvalidOperationException(L.T("לא ניתן לאתר את קובץ התוכנה."));
        if (Interlocked.Exchange(ref _updating, 1) == 1) return null;

        try
        {
            if (IsInstalled)
            {
                Directory.CreateDirectory(UpdateTempFolder);
                string setup = Path.Combine(UpdateTempFolder, $"RAF-Setup-{latest.Version.ToString(3)}.exe");
                await DownloadVerified(latest.Setup!, setup);

                // התוכנה רצה כמנהל, ולכן ההתקנה נפתחת בלי בקשת הרשאה נוספת. היא ממתינה
                // שהתוכנה תיסגר (וסוגרת אותה אם צריך), ופותחת אותה מחדש בסיום.
                Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH")
                {
                    UseShellExecute = false,
                });
            }
            else
            {
                string fresh = exe + ".new";
                string old = exe + ".old";
                await DownloadVerified(latest.Portable!, fresh);

                // הקובץ שרץ עובר הצידה, והחדש נכנס במקומו. אם ההחלפה נכשלת באמצע — הישן חוזר.
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
                Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _updating, 0);
            if (ex is UnauthorizedAccessException or IOException)
                throw new InvalidOperationException(
                    L.T("העדכון לא הושלם: לא ניתן לכתוב לתיקייה של התוכנה. אפשר להוריד את הגרסה החדשה ידנית מדף השחרורים בגיטהאב."), ex);
            throw;
        }

        _host.InvokeOnUi(_host.Close);
        return null;
    }

    /// <summary>הורדה לקובץ, עם בדיקת הגודל וטביעת האצבע. קובץ שלא עבר את הבדיקה נמחק.</summary>
    private async Task DownloadVerified(ReleaseAsset asset, string target)
    {
        string partial = target + ".part";
        try
        {
            using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                byte[] buffer = new byte[256 * 1024];
                long done = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (done > asset.Size) break;

                    int percent = (int)(done * 100 / asset.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        PushEvent("update.progress", new { percent });
                    }
                }

                byte[] hash = sha.GetHashAndReset();
                if (done != asset.Size || (asset.Sha256 is not null && !hash.AsSpan().SequenceEqual(asset.Sha256)))
                    throw new InvalidDataException(L.T("הקובץ שהורד אינו תואם לקובץ שפורסם. העדכון בוטל; נסו שוב מאוחר יותר."));
            }

            File.Move(partial, target, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partial);
            throw new InvalidOperationException(L.T("ההורדה נכשלה. בדקו את החיבור לאינטרנט ונסו שוב."), ex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ניקוי בלבד */ }
    }

    /// <summary>
    /// ניקוי אחרי עדכון: הקובץ הישן של גרסה ניידת, וקובצי ההתקנה שהורדו. התהליך הקודם
    /// עוד עשוי להיסגר ברגעים אלה, ולכן יש כמה ניסיונות.
    /// </summary>
    private static void CleanupAfterUpdate()
    {
        if (!UpdatesSupported || ExePath is not { } exe) return;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool pending = false;
                string old = exe + ".old";
                if (File.Exists(old)) { TryDelete(old); pending |= File.Exists(old); }
                TryDelete(exe + ".new");
                try
                {
                    if (Directory.Exists(UpdateTempFolder))
                        foreach (var f in Directory.GetFiles(UpdateTempFolder)) { TryDelete(f); pending |= File.Exists(f); }
                }
                catch { /* ניקוי בלבד */ }
                if (!pending) return;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        });
    }
}
