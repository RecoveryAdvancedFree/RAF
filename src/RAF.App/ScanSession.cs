using RAF.Core.Model;
using RAF.Core.Recovery;

namespace RAF.App;

/// <summary>
/// מה מוצג ברשימת הקבצים: תיקייה, או חיפוש בשם על פני כל התוצאות —
/// ועליהם סינון לפי קטגוריה, "רק ניתנים לשחזור" ומיון.
/// </summary>
internal sealed record ViewQuery(
    string Path, string? Search, bool Evidence,
    string Category, bool RecoverableOnly, ViewSort Sort, bool Descending)
{
    /// <summary>החלק שקובע אילו קבצים נכנסים לרשימה, לפני סינון לפי קטגוריה ומיון.</summary>
    internal (string, string?, bool, bool) Source => (Path, Search, Evidence, RecoverableOnly);
}

internal enum ViewSort { Name, Size, Date, Quality }

/// <summary>מצב הבחירה: כמה נבחרו בסך הכול, ומה מזה ברשימה המוצגת — לתיבת "הכל".</summary>
internal readonly record struct SelectionSummary(int Count, long Bytes, int ViewSelectable, int ViewSelected);

/// <summary>
/// תוצאות סריקה שהושלמה, מאורגנות לעץ תיקיות שניתן לדפדף בו מהממשק.
/// העץ נבנה פעם אחת בסיום הסריקה, והממשק מושך ממנו רמה אחת בכל פעם
/// כדי שגם מאות אלפי קבצים יוצגו במהירות.
/// </summary>
internal sealed class ScanSession
{
    /// <summary>הקבצים לפי תיקיית האב שלהם.</summary>
    private readonly Dictionary<string, List<RecoveredFile>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// תיקיות המשנה הישירות של כל תיקייה, בשני עותקים.
    ///
    /// רשומות שמקורן ביומנים מספקות שם בלבד וללא מיקום תוכן, והן נפוצות
    /// בהרבה מהקבצים הניתנים לשחזור בפועל. הצגתן כברירת מחדל מטביעה
    /// את התוצאות המשמעותיות, ולכן העץ נבנה גם בגרסה שאינה כוללת אותן.
    /// </summary>
    private readonly Dictionary<string, SortedSet<string>> _subFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _subFoldersReal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>גישה מהירה לקובץ לפי מזההו, לצורך תצוגה מקדימה ושחזור.</summary>
    private readonly Dictionary<long, RecoveredFile> _byId = new();

    internal ScanResult Result { get; }
    internal int DiskNumber { get; }
    internal long PartitionOffset { get; }
    internal long PartitionSize { get; }
    internal int SectorSize { get; }
    internal string PartitionTitle { get; }

    /// <summary>מערכת הקבצים של המחיצה, לבחירת המנוע בעת חילוץ.</summary>
    internal FileSystemKind FileSystem { get; }

    /// <summary>הכונן שנסרק — לזיהויו מחדש כשהסריקה נפתחת מקובץ.</summary>
    internal DiskIdentity Disk { get; }

    /// <summary>המחיצה נקראה דרך עותק הגיבוי של מגזר האתחול.</summary>
    internal bool ReadThrough { get; }

    /// <summary>נקודת ביניים שנשמרה באמצע סריקה — לא כל המחיצה נסרקה.</summary>
    internal bool Partial { get; init; }

    /// <summary>
    /// סריקה שנפתחה מקובץ, כשהכונן שלה אינו מחובר: אפשר לעיין בתוצאות,
    /// אבל לא לקרוא תוכן — לא לתצוגה מקדימה ולא לשחזור.
    /// </summary>
    internal bool Offline => DiskNumber < 0;

    /// <summary>הקובץ שממנו נפתחה הסריקה, או שאליו נשמרה אוטומטית.</summary>
    internal string? SavedPath { get; set; }

    internal ScanSession(
        ScanResult result, int diskNumber, long partitionOffset,
        long partitionSize, int sectorSize, string partitionTitle,
        FileSystemKind fileSystem, DiskIdentity disk, bool readThrough)
    {
        FileSystem = fileSystem;
        Result = result;
        DiskNumber = diskNumber;
        PartitionOffset = partitionOffset;
        PartitionSize = partitionSize;
        SectorSize = sectorSize;
        PartitionTitle = partitionTitle;
        Disk = disk;
        ReadThrough = readThrough;

        BuildIndex();
    }

    /// <summary>הסריקה כקובץ, כולל הבחירה הנוכחית.</summary>
    internal ScanArchive ToArchive(string appVersion, bool partial) => new()
    {
        Header = new ScanArchiveHeader
        {
            SavedAt = DateTime.Now,
            AppVersion = appVersion,
            Disk = Disk,
            PartitionTitle = PartitionTitle,
            Mode = Result.Mode,
            FileCount = Result.Files.Count(f => !f.IsDirectory),
            RecoverableCount = _recoverableUnder.GetValueOrDefault(""),
            Partial = partial || Partial,
            ResumePercent = Result.Resume?.Percent,
        },
        PartitionOffset = PartitionOffset,
        PartitionSize = PartitionSize,
        SectorSize = SectorSize,
        FileSystem = FileSystem,
        ReadThrough = ReadThrough,
        Result = Result,
        Selected = SelectedIds(),
    };

    /// <summary>
    /// השמירה האוטומטית רצה ברקע, בזמן שהממשק עשוי לסמן קבצים — ולכן
    /// הבחירה נקראת ומשתנה רק תחת נעילה.
    /// </summary>
    private readonly object _selectionGate = new();

    private List<long> SelectedIds()
    {
        lock (_selectionGate) return _selected.ToList();
    }

    /// <summary>שחזור סריקה מקובץ. diskNumber שלילי — הכונן אינו מחובר.</summary>
    internal static ScanSession FromArchive(ScanArchive archive, int diskNumber, string path)
    {
        var session = new ScanSession(
            archive.Result, diskNumber, archive.PartitionOffset, archive.PartitionSize,
            archive.SectorSize, archive.Header.PartitionTitle, archive.FileSystem,
            archive.Header.Disk, archive.ReadThrough)
        {
            Partial = archive.Header.Partial,
            SavedPath = path,
        };

        session.Select(archive.Selected.Select(session.ById).OfType<RecoveredFile>(), true);
        return session;
    }

    private void BuildIndex()
    {
        foreach (var file in Result.Files)
        {
            _byId[file.Id] = file;

            // תיקיות אינן נרשמות כפריטים; הן נגזרות מנתיבי הקבצים,
            // כך שגם תיקייה שרשומתה נדרסה עדיין מופיעה בעץ.
            if (file.IsDirectory) continue;

            string folder = file.Path ?? "";
            if (!_byFolder.TryGetValue(folder, out var list))
                _byFolder[folder] = list = new List<RecoveredFile>();
            list.Add(file);

            RegisterFolderChain(_subFolders, folder);
            if (!IsEvidence(file)) RegisterFolderChain(_subFoldersReal, folder);
            if (file.IsWorthRecovering) CountRecoverable(file);
        }

        foreach (var list in _byFolder.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// רשומה שמקורה ביומן מעידה שהקובץ היה קיים, אך אינה נושאת
    /// את מיקום תוכנו ולכן לעולם לא תניב שחזור.
    /// </summary>
    internal static bool IsEvidence(RecoveredFile file)
        => file.Source is DiscoverySource.UsnJournal or DiscoverySource.LogFile;

    /// <summary>רישום כל מקטעי הנתיב, כדי שניתן יהיה לרדת בעץ שלב אחר שלב.</summary>
    private static void RegisterFolderChain(Dictionary<string, SortedSet<string>> map, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;

        var segments = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string parent = "";

        foreach (string segment in segments)
        {
            if (!map.TryGetValue(parent, out var children))
                map[parent] = children = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            children.Add(segment);
            parent = string.IsNullOrEmpty(parent) ? segment : parent + "\\" + segment;
        }
    }

    /// <summary>תיקיות המשנה הישירות של נתיב נתון.</summary>
    internal IReadOnlyCollection<string> SubFolders(string path, bool includeEvidence)
    {
        var map = includeEvidence ? _subFolders : _subFoldersReal;
        return map.TryGetValue(path ?? "", out var set) ? set : Array.Empty<string>();
    }

    /// <summary>הקבצים היושבים ישירות בנתיב נתון.</summary>
    internal IReadOnlyList<RecoveredFile> FilesIn(string path, bool includeEvidence)
    {
        if (!_byFolder.TryGetValue(path ?? "", out var list)) return Array.Empty<RecoveredFile>();
        return includeEvidence ? list : list.Where(f => !IsEvidence(f)).ToList();
    }

    /// <summary>מספר הרשומות שמקורן ביומנים, להצגה במתג הסינון.</summary>
    internal int EvidenceCount => Result.Files.Count(f => !f.IsDirectory && IsEvidence(f));

    internal RecoveredFile? ById(long id) => _byId.GetValueOrDefault(id);

    // ------------------------------------------------------------ הרשימה המוצגת

    /// <summary>
    /// הרשימה שהממשק מציג כרגע. הממשק מושך ממנה טווחים לפי הגלילה, ולכן היא
    /// נשמרת בין בקשה לבקשה — אחרת כל גלילה בתיקייה של עשרות אלפי קבצים
    /// הייתה מסננת וממיינת מחדש. המטמון בשתי שכבות: החלפת קטגוריה או מיון
    /// אינה מחייבת לאסוף מחדש את קבצי התיקייה או את תוצאות החיפוש.
    /// </summary>
    internal IReadOnlyList<RecoveredFile> View(ViewQuery query)
    {
        if (_view is { } cached && cached.Query == query) return cached.Files;

        var source = Source(query);
        IEnumerable<RecoveredFile> files = query.Category == FileCategories.All
            ? source
            : source.Where(f => FileCategories.Of(f.Extension) == query.Category);

        var sorted = Sorted(files, query.Sort, query.Descending).ToList();
        _view = (query, sorted);
        return sorted;
    }

    private (ViewQuery Query, List<RecoveredFile> Files)? _view;
    private ((string, string?, bool, bool) Key, List<RecoveredFile> Files)? _source;

    /// <summary>הקבצים שנכנסים לרשימה לפני סינון הקטגוריה — גם הבסיס לספירות שבשבבים.</summary>
    private List<RecoveredFile> Source(ViewQuery query)
    {
        if (_source is { } cached && cached.Key == query.Source) return cached.Files;

        IEnumerable<RecoveredFile> files = string.IsNullOrWhiteSpace(query.Search)
            ? FilesIn(query.Path, query.Evidence)
            : Result.Files.Where(f => !f.IsDirectory &&
                                      (query.Evidence || !IsEvidence(f)) &&
                                      f.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

        if (query.RecoverableOnly) files = files.Where(f => f.IsWorthRecovering);

        var list = files.ToList();
        _source = (query.Source, list);
        return list;
    }

    /// <summary>מספר הקבצים בכל קטגוריה, לשבבי הסינון.</summary>
    internal Dictionary<string, int> CategoryCounts(ViewQuery query)
    {
        var source = Source(query);
        var counts = FileCategories.Ordered.ToDictionary(c => c.Id, _ => 0);
        counts[FileCategories.Other] = 0;

        foreach (var f in source) counts[FileCategories.Of(f.Extension)]++;
        counts[FileCategories.All] = source.Count;
        return counts;
    }

    /// <summary>
    /// מיון לפי המפתח שנבחר. בשוויון — לפי השם, כדי שהסדר יהיה יציב בין בקשות.
    /// קובץ ללא תאריך תמיד בסוף, בשני הכיוונים: "לא ידוע" אינו ישן ואינו חדש.
    /// </summary>
    private static IEnumerable<RecoveredFile> Sorted(IEnumerable<RecoveredFile> files, ViewSort sort, bool desc)
    {
        var byName = StringComparer.OrdinalIgnoreCase;

        IOrderedEnumerable<RecoveredFile> ordered = sort switch
        {
            ViewSort.Size => desc ? files.OrderByDescending(f => f.Size) : files.OrderBy(f => f.Size),
            ViewSort.Date => desc
                ? files.OrderBy(f => f.Modified is null).ThenByDescending(f => f.Modified)
                : files.OrderBy(f => f.Modified is null).ThenBy(f => f.Modified),
            // האיכות בסדר עולה מהטובה לגרועה, ולכן "יורד" מתחיל בגרועה.
            ViewSort.Quality => desc ? files.OrderByDescending(f => f.Quality) : files.OrderBy(f => f.Quality),
            _ => desc ? files.OrderByDescending(f => f.Name, byName) : files.OrderBy(f => f.Name, byName),
        };

        return sort == ViewSort.Name ? ordered : ordered.ThenBy(f => f.Name, byName);
    }

    // ------------------------------------------------------------------ בחירה

    /// <summary>
    /// הבחירה נשמרת כאן ולא בממשק: סימון תיקייה בעץ יכול לכלול מאות אלפי קבצים,
    /// וכל ענף בעץ צריך לדעת אם הוא מסומן כולו, בחלקו או בכלל לא.
    /// </summary>
    private readonly HashSet<long> _selected = new();
    private long _selectedBytes;

    /// <summary>קבצים ניתנים לשחזור, ומתוכם מסומנים, תחת כל תיקייה — כולל תיקיות משנה.</summary>
    private readonly Dictionary<string, int> _recoverableUnder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _selectedUnder = new(StringComparer.OrdinalIgnoreCase);

    private void CountRecoverable(RecoveredFile file)
    {
        foreach (string folder in Ancestors(file.Path ?? ""))
            _recoverableUnder[folder] = _recoverableUnder.GetValueOrDefault(folder) + 1;
    }

    /// <summary>התיקייה עצמה וכל התיקיות שמעליה, כולל השורש ("").</summary>
    private static IEnumerable<string> Ancestors(string folder)
    {
        yield return "";
        int at = -1;
        while (folder.Length > 0 && (at = folder.IndexOf('\\', at + 1)) >= 0)
            yield return folder[..at];
        if (folder.Length > 0) yield return folder;
    }

    internal bool IsSelected(long id) => _selected.Contains(id);

    /// <summary>סימון או ביטול של קבצים. קובץ שאינו ניתן לשחזור לעולם אינו מסומן.</summary>
    internal void Select(IEnumerable<RecoveredFile> files, bool on)
    {
        lock (_selectionGate)
        foreach (var file in files)
        {
            if (!file.IsWorthRecovering) continue;
            if (on ? !_selected.Add(file.Id) : !_selected.Remove(file.Id)) continue;

            _selectedBytes += on ? file.Size : -file.Size;
            foreach (string folder in Ancestors(file.Path ?? ""))
                _selectedUnder[folder] = _selectedUnder.GetValueOrDefault(folder) + (on ? 1 : -1);
        }
    }

    /// <summary>כל הקבצים תחת תיקייה, כולל תיקיות משנה.</summary>
    internal IEnumerable<RecoveredFile> AllUnder(string folder)
        => Result.Files.Where(f => !f.IsDirectory && IsUnder(f.Path ?? "", folder));

    private static bool IsUnder(string filePath, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return true;
        if (filePath.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return filePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>מצב תיקייה בעץ: 0 — לא מסומנת, 1 — חלקית, 2 — כולה. ‎-1 — אין בה מה לסמן.</summary>
    internal int FolderState(string folder)
    {
        int total = _recoverableUnder.GetValueOrDefault(folder);
        int marked = _selectedUnder.GetValueOrDefault(folder);
        return total == 0 ? -1 : marked == 0 ? 0 : marked == total ? 2 : 1;
    }

    internal SelectionSummary Summary(ViewQuery? view)
    {
        int selectable = 0, selected = 0;
        if (view is not null)
        {
            foreach (var f in View(view))
            {
                if (!f.IsWorthRecovering) continue;
                selectable++;
                if (_selected.Contains(f.Id)) selected++;
            }
        }
        return new SelectionSummary(_selected.Count, _selectedBytes, selectable, selected);
    }

    /// <summary>הקבצים שנבחרו, בסדר הופעתם בתוצאות הסריקה — לשחזור.</summary>
    internal List<RecoveredFile> SelectedFiles()
        => Result.Files.Where(f => _selected.Contains(f.Id)).ToList();
}
