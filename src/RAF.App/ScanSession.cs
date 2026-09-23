using RAF.Core.Model;

namespace RAF.App;

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
    /// בהרבה מהקבצים הניתנים לשיחזור בפועל. הצגתן כברירת מחדל מטביעה
    /// את התוצאות המשמעותיות, ולכן העץ נבנה גם בגרסה שאינה כוללת אותן.
    /// </summary>
    private readonly Dictionary<string, SortedSet<string>> _subFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _subFoldersReal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>גישה מהירה לקובץ לפי מזההו, לצורך תצוגה מקדימה ושיחזור.</summary>
    private readonly Dictionary<long, RecoveredFile> _byId = new();

    internal ScanResult Result { get; }
    internal int DiskNumber { get; }
    internal long PartitionOffset { get; }
    internal long PartitionSize { get; }
    internal int SectorSize { get; }
    internal string PartitionTitle { get; }

    /// <summary>מערכת הקבצים של המחיצה, לבחירת המנוע בעת חילוץ.</summary>
    internal FileSystemKind FileSystem { get; }

    internal ScanSession(
        ScanResult result, int diskNumber, long partitionOffset,
        long partitionSize, int sectorSize, string partitionTitle,
        FileSystemKind fileSystem)
    {
        FileSystem = fileSystem;
        Result = result;
        DiskNumber = diskNumber;
        PartitionOffset = partitionOffset;
        PartitionSize = partitionSize;
        SectorSize = sectorSize;
        PartitionTitle = partitionTitle;

        BuildIndex();
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
        }

        foreach (var list in _byFolder.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// רשומה שמקורה ביומן מעידה שהקובץ היה קיים, אך אינה נושאת
    /// את מיקום תוכנו ולכן לעולם לא תניב שיחזור.
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

    internal IEnumerable<RecoveredFile> ByIds(IEnumerable<long> ids)
        => ids.Select(ById).Where(f => f is not null)!;

    /// <summary>
    /// חיפוש בשם הקובץ על פני כל התוצאות.
    /// מוגבל במספר התוצאות כדי שהממשק יישאר מגיב.
    /// </summary>
    internal List<RecoveredFile> Search(string query, int limit, bool includeEvidence)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<RecoveredFile>();

        return Result.Files
            .Where(f => !f.IsDirectory &&
                        (includeEvidence || !IsEvidence(f)) &&
                        f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    /// <summary>כל הקבצים תחת נתיב נתון, כולל תיקיות משנה — לבחירת תיקייה שלמה.</summary>
    internal List<RecoveredFile> AllUnder(string path)
    {
        string prefix = path ?? "";

        // רשומות יומן לעולם לא יניבו שיחזור, ולכן אינן נכללות בבחירת תיקייה.
        return Result.Files
            .Where(f => !f.IsDirectory && !IsEvidence(f) && IsUnder(f.Path ?? "", prefix))
            .ToList();
    }

    private static bool IsUnder(string filePath, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return true;
        if (filePath.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return filePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
