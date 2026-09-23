namespace RAF.Core.Model;

/// <summary>תוצאת סריקה שהושלמה או בוטלה.</summary>
public sealed class ScanResult
{
    public List<RecoveredFile> Files { get; init; } = new();

    public ScanMode Mode { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>שם מערכת הקבצים שנסרקה בפועל.</summary>
    public string FileSystem { get; init; } = "";

    /// <summary>מספר רשומות המטא-דאטה שנבדקו.</summary>
    public long RecordsExamined { get; init; }

    /// <summary>סך הבתים שנקראו מהדיסק.</summary>
    public long BytesRead { get; init; }

    /// <summary>אזהרות שנאספו במהלך הסריקה, בעברית.</summary>
    public List<string> Warnings { get; init; } = new();

    public int DeletedCount => Files.Count(f => f.IsDeleted);
    public int RecoverableCount => Files.Count(f => f.HasContent && !f.IsDirectory);
}
