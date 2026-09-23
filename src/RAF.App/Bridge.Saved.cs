using System.Text.Json.Nodes;
using RAF.Core.Disks;
using RAF.Core.Imaging;
using RAF.Core.Native;
using RAF.Core.Model;
using RAF.Core.Recovery;
using RAF.Core.Repair;

namespace RAF.App;

/// <summary>
/// שמירת סריקות לקובץ ופתיחתן: שמירה אוטומטית בסוף כל סריקה ובנקודות ביניים
/// בסריקה מתקדמת, שמירה ידנית ל"שמור בשם", ורשימת הסריקות האחרונות.
///
/// הכלל הראשון של שחזור מידע חל גם כאן: קובץ סריקה לעולם אינו נכתב לדיסק
/// שממנו משחזרים. תיקיית הזמניים יושבת לרוב על C:, ומחיקה בטעות מ-C: היא
/// בדיוק המקרה שבו כתיבה אליו דורסת את מה שמנסים להציל.
/// </summary>
internal sealed partial class Bridge
{
    private static string AutosaveFolder => Path.Combine(Path.GetTempPath(), "RAF", "scans");

    /// <summary>כמה שמירות אוטומטיות נשמרות; הישנות נמחקות.</summary>
    private const int KeepAutosaves = 10;

    private static string AppVersion => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>
    /// הסבר למה אסור לכתוב לתיקייה, או null אם מותר. כשלא ידוע על איזה דיסק
    /// התיקייה יושבת — אסור: עדיף לוותר על שמירה מאשר להסתכן בדריסה.
    /// </summary>
    private static string? UnsafeFolder(string folder, int sourceDisk)
    {
        if (sourceDisk < 0) return null;
        int target = DiskEnumerator.GetDiskNumberForPath(folder);
        if (target == sourceDisk)
            return "התיקייה נמצאת על הדיסק שממנו משחזרים — כתיבה אליו עלולה לדרוס קבצים שעוד לא שוחזרו.";
        if (target < 0 && !DevicePaths.IsImage(sourceDisk))
            return "לא ניתן לוודא שהתיקייה אינה על הדיסק שממנו משחזרים.";
        return null;
    }

    private static string NewAutosavePath(string title)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
        return Path.Combine(AutosaveFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{title.Trim()}{ScanArchive.Extension}");
    }

    /// <summary>
    /// שמירה אוטומטית. רצה ברקע אחרי הסריקה — סריקה של מאות אלפי קבצים
    /// נשמרת בשניות, והתוצאות מוצגות בינתיים. מודיעה לממשק כשהסתיימה.
    /// </summary>
    private void Autosave(ScanSession session, bool partial)
    {
        if (session.Offline || session.SavedPath is null) return;

        string? unsafeReason = UnsafeFolder(AutosaveFolder, session.DiskNumber);
        if (unsafeReason is not null)
        {
            PushEvent("scan.saved", new { skipped = unsafeReason, partial });
            return;
        }

        try
        {
            ScanArchive.Save(session.ToArchive(AppVersion, partial), session.SavedPath);
            if (!partial) PruneAutosaves();
            PushEvent("scan.saved", new { path = session.SavedPath, partial });
        }
        catch (Exception ex)
        {
            PushEvent("scan.saved", new { skipped = "השמירה האוטומטית נכשלה: " + ex.Message, partial });
        }
    }

    private static void PruneAutosaves()
    {
        try
        {
            foreach (var old in new DirectoryInfo(AutosaveFolder).GetFiles("*" + ScanArchive.Extension)
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepAutosaves))
                old.Delete();
        }
        catch { /* ניקוי בלבד */ }
    }

    /// <summary>"שמור בשם" — לכל מקום שאינו על הדיסק שממנו משחזרים.</summary>
    private async Task<object> SaveScanAs()
    {
        var session = RequireSession();

        string title = session.PartitionTitle;
        foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');

        string? path = null;
        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "שמירת הסריקה — בחרו כונן אחר מהכונן שנסרק",
                FileName = $"RAF-{title}-{DateTime.Now:yyyyMMdd-HHmm}{ScanArchive.Extension}",
                Filter = $"סריקת RAF (*{ScanArchive.Extension})|*{ScanArchive.Extension}",
                DefaultExt = ScanArchive.Extension.TrimStart('.'),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK) path = dialog.FileName;
        });

        if (path is null) return new { path = (string?)null };

        string? unsafeReason = UnsafeFolder(Path.GetDirectoryName(path)!, session.DiskNumber);
        if (unsafeReason is not null) throw new InvalidOperationException(unsafeReason + " בחרו כונן אחר.");

        await Task.Run(() => ScanArchive.Save(session.ToArchive(AppVersion, partial: false), path));
        return new { path };
    }

    /// <summary>הסריקות האחרונות שנשמרו אוטומטית, מהחדשה לישנה.</summary>
    private object RecentScans()
    {
        if (!Directory.Exists(AutosaveFolder)) return Array.Empty<object>();

        var list = new List<object>();
        foreach (var file in new DirectoryInfo(AutosaveFolder).GetFiles("*" + ScanArchive.Extension)
                     .OrderByDescending(f => f.LastWriteTimeUtc).Take(KeepAutosaves))
        {
            ScanArchiveHeader header;
            try { header = ScanArchive.ReadHeader(file.FullName); }
            catch { continue; }   // קובץ פגום או שמירה שנקטעה — פשוט לא מוצג

            var disk = MatchDisk(header.Disk);
            list.Add(new
            {
                path = file.FullName,
                title = header.PartitionTitle,
                mode = Display.Mode(header.Mode),
                savedAt = header.SavedAt.ToString("yyyy-MM-dd HH:mm"),
                files = header.FileCount,
                recoverable = header.RecoverableCount,
                partial = header.Partial,
                connected = disk is not null || (header.Disk.ImagePath is { } img && File.Exists(img)),
                diskName = header.Disk.ImagePath is { } image ? Path.GetFileName(image) : header.Disk.Model,
            });
        }
        return list;
    }

    private PhysicalDiskInfo? MatchDisk(DiskIdentity identity)
        => _disks.FirstOrDefault(identity.Matches);

    /// <summary>
    /// פתיחת סריקה שמורה. הכונן מזוהה לפי מספר סידורי וגודל, כי מספרו ב-Windows
    /// משתנה בין חיבורים; תמונת דיסק נפתחת מחדש לפי הנתיב שלה. כונן שאינו
    /// מחובר — התוצאות מוצגות לעיון בלבד.
    /// </summary>
    private async Task<object> LoadScan(JsonObject? p)
    {
        string? path = p?["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
        {
            _form.InvokeOnUiSync(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "פתיחת סריקה שמורה",
                    Filter = $"סריקת RAF (*{ScanArchive.Extension})|*{ScanArchive.Extension}",
                    CheckFileExists = true,
                };
                if (dialog.ShowDialog(_form) == DialogResult.OK) path = dialog.FileName;
            });
            if (string.IsNullOrEmpty(path)) return new { cancelled = true };
        }

        var archive = await Task.Run(() => ScanArchive.Load(path));
        var disk = MatchDisk(archive.Header.Disk);

        // תמונת דיסק שעוד לא נפתחה בהפעלה הזו — נפתחת אוטומטית.
        if (disk is null && archive.Header.Disk.ImagePath is { } image && File.Exists(image))
        {
            disk = ImageDisk.Open(image);
            _images.RemoveAll(d => d.DiskNumber == disk.DiskNumber);
            _images.Add(disk);
            _disks.Add(disk);
        }

        // מחיצה שנקראה דרך עותק הגיבוי: התיקון בזיכרון אבד עם סגירת התוכנה, ומופעל שוב.
        if (disk is not null && archive.ReadThrough)
        {
            var diagnosis = VirtualRepair.Apply(
                disk.DiskNumber, archive.PartitionOffset, archive.PartitionSize, archive.SectorSize);
            if (diagnosis.CanRepair)
            {
                _readThrough[(disk.DiskNumber, archive.PartitionOffset)] = diagnosis.DetectedFileSystem;
                ApplyReadThrough(disk);
            }
        }

        _session = ScanSession.FromArchive(archive, disk?.DiskNumber ?? -1, path);
        return Summary();
    }

    /// <summary>סריקה שהכונן שלה מחובר — לתצוגה מקדימה ולשחזור.</summary>
    private ScanSession RequireOnlineSession()
    {
        var session = RequireSession();
        if (session.Offline)
            throw new InvalidOperationException(
                "הכונן שנסרק אינו מחובר. חברו אותו, חזרו לרשימת הכוננים ופתחו את הסריקה שוב.");
        return session;
    }
}
