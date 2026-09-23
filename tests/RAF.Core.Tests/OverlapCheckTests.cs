using RAF.Core.FileSystems;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// קבצים מחוקים שנדרסו על ידי קובץ מחוק מאוחר יותר.
///
/// הכשל: שני הקבצים נמחקו, ולכן כל האשכולות "פנויים" — והקובץ הישן נראה שלם
/// ומקבל "מצוין", אף שהתוכן באשכולות שלו הוא של הקובץ החדש. המקרה נמצא בבדיקה
/// על כונן אמיתי; כאן הוא נבדק בכל הווריאציות.
/// </summary>
public class OverlapCheckTests
{
    private static readonly DateTime Monday = new(2026, 9, 21, 10, 0, 0);

    private static RecoveredFile Deleted(string name, DateTime? created, params (long Start, long Count)[] extents)
        => File_(name, created, deleted: true, extents);

    private static RecoveredFile File_(string name, DateTime? created, bool deleted, params (long Start, long Count)[] extents) => new()
    {
        Name = name,
        Size = extents.Sum(e => e.Count) * 4096,
        IsDeleted = deleted,
        Created = created,
        Modified = created,
        Quality = RecoveryQuality.Excellent,
        QualityReason = "נמצאו נתונים וכל האשכולות פנויים.",
        Content = ContentCheck.HasData,
        Extents = extents.Select(e => new DataExtent(e.Start, e.Count, false)).ToList(),
    };

    [Fact]
    public void A_file_fully_overwritten_by_a_later_deleted_file_is_unrecoverable_and_names_it()
    {
        var photo = Deleted("photo.jpg", Monday, (154, 32), (250, 72));
        var newcomer = Deleted("newcomer.bin", Monday.AddHours(1), (10, 384));

        OverlapCheck.Apply(new List<RecoveredFile> { photo, newcomer });

        Assert.Equal(RecoveryQuality.Unrecoverable, photo.Quality);
        Assert.Contains("newcomer.bin", photo.QualityReason);
        Assert.Equal(RecoveryQuality.Excellent, newcomer.Quality);   // הקובץ המאוחר — שלו התוכן
    }

    [Theory]
    [InlineData(5, RecoveryQuality.Good)]           // 5% מהקובץ
    [InlineData(30, RecoveryQuality.Poor)]          // 30%
    [InlineData(95, RecoveryQuality.Unrecoverable)] // 95%
    public void The_grade_follows_the_share_that_was_overwritten(int percent, RecoveryQuality expected)
    {
        var old = Deleted("old.doc", Monday, (1000, 100));
        var later = Deleted("later.tmp", Monday.AddDays(1), (1000 + 100 - percent, percent));

        OverlapCheck.Apply(new List<RecoveredFile> { old, later });

        Assert.Equal(expected, old.Quality);
        Assert.Contains($"{percent}%", old.QualityReason);
    }

    [Fact]
    public void The_file_written_first_is_the_victim_regardless_of_list_order()
    {
        var older = Deleted("a.jpg", Monday, (500, 10));
        var newer = Deleted("b.jpg", Monday.AddMinutes(5), (500, 10));

        OverlapCheck.Apply(new List<RecoveredFile> { newer, older });

        Assert.Equal(RecoveryQuality.Unrecoverable, older.Quality);
        Assert.Equal(RecoveryQuality.Excellent, newer.Quality);
    }

    [Fact]
    public void Without_dates_neither_file_is_trusted_fully()
    {
        var a = Deleted("a.bin", null, (300, 20));
        var b = Deleted("b.bin", null, (310, 20));

        OverlapCheck.Apply(new List<RecoveredFile> { a, b });

        Assert.Equal(RecoveryQuality.Good, a.Quality);
        Assert.Equal(RecoveryQuality.Good, b.Quality);
        Assert.Contains("לא ידוע מי", a.QualityReason);
    }

    [Fact]
    public void The_same_file_seen_twice_is_not_an_overlap()
    {
        // סריקה עמוקה עשויה למצוא את אותה רשומה גם בספרייה וגם באשכול יתום.
        var a = Deleted("report.pdf", Monday, (700, 50));
        var b = Deleted("report.pdf", Monday, (700, 50));

        Assert.Equal(0, OverlapCheck.Apply(new List<RecoveredFile> { a, b }));
        Assert.Equal(RecoveryQuality.Excellent, a.Quality);
    }

    [Fact]
    public void Existing_files_and_separate_files_are_left_alone()
    {
        var deleted = Deleted("gone.txt", Monday, (100, 10));
        var elsewhere = Deleted("other.txt", Monday.AddDays(1), (200, 10));
        // קובץ קיים — התפיסה שלו מטופלת במפת ההקצאה, לא כאן
        var existing = File_("live.txt", Monday.AddDays(2), deleted: false, (100, 10));

        Assert.Equal(0, OverlapCheck.Apply(new List<RecoveredFile> { deleted, elsewhere, existing }));
        Assert.Equal(RecoveryQuality.Excellent, deleted.Quality);
    }
}
