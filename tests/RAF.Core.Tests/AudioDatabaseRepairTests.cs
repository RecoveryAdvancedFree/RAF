using System.Buffers.Binary;
using System.Text;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// תיקון הקלטות WAV, שירי MP3 ומסדי נתונים SQLite.
///
/// שלושתם נפגעים בדרך אחרת: הקלטה שנקטעה נשארת עם כותרת שאומרת שאין בה שמע;
/// ל-MP3 אין חתימה קבועה, ונתונים זרים לפניו מסתירים אותו; ומסד נתונים סומך
/// על כמה בתים בכותרת. בכל מקרה העותק המתוקן נבדק שוב — ומסד הנתונים גם נפתח
/// בפועל ב-SQLite של Windows.
/// </summary>
public sealed class AudioDatabaseRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-audio-{Guid.NewGuid():N}");

    public AudioDatabaseRepairTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Save(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private (FileRepairResult Result, byte[] Output) Repair(string path)
    {
        var result = FileDoctor.Repair(path, Path.Combine(_dir, "out"));
        Assert.True(result.Succeeded, result.Message);
        return (result, File.ReadAllBytes(result.OutputPath!));
    }

    private static FileIssueKind[] Kinds(FileDiagnosis d) => d.Issues.Select(i => i.Kind).ToArray();

    // ================================================================ WAV

    /// <summary>הקלטת סטריאו של 16 ביט: 4 בתים לכל דגימה, 176,400 בתים לשנייה.</summary>
    private static byte[] Wav(int dataBytes, uint riffField, uint dataField, byte[]? after = null)
    {
        after ??= [];
        byte[] b = new byte[44 + dataBytes + after.Length];
        new Random(1).NextBytes(b.AsSpan(44, dataBytes));
        after.CopyTo(b, 44 + dataBytes);

        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), riffField);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), dataField);
        return b;
    }

    private static uint Field(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    [Theory]
    [InlineData(0u)]                  // המקליט לא הספיק לכתוב גודל
    [InlineData(0xFFFFFFFFu)]         // "גודל לא ידוע" — כמו בהקלטה בזרימה
    [InlineData(5_000_000u)]          // הגודל שתוכנן, וההקלטה נקטעה לפניו
    public void An_unfinished_recording_keeps_all_its_audio(uint declared)
    {
        const int audio = 441_000;                                            // 2.5 שניות
        byte[] original = Wav(audio, declared == 0 ? 0 : declared + 36, declared);
        string path = Save("recording.wav", original);

        var diagnosis = FileDoctor.Diagnose(path);
        Assert.Equal([FileIssueKind.SizeFieldsWrong], Kinds(diagnosis));
        Assert.Contains("2 שניות", diagnosis.Issues[0].Description);

        var (result, output) = Repair(path);
        Assert.True(result.After!.IsHealthy);
        Assert.Equal(original.Length, output.Length);                          // שום שמע לא נחתך
        Assert.Equal((uint)audio, Field(output, 40));
        Assert.Equal((uint)(output.Length - 8), Field(output, 4));
        Assert.Equal(original.AsSpan(44).ToArray(), output.AsSpan(44).ToArray());
    }

    [Fact]
    public void A_recording_cut_in_the_middle_of_a_sample_ends_on_a_whole_sample()
    {
        byte[] original = Wav(100_002, 0, 0);                                  // שני בתים מדגימה שנקטעה
        var (_, output) = Repair(Save("cut.wav", original));

        Assert.Equal(44 + 100_000, output.Length);
        Assert.Equal(100_000u, Field(output, 40));
    }

    [Fact]
    public void Junk_after_a_complete_recording_is_removed()
    {
        byte[] junk = new byte[5000];
        new Random(9).NextBytes(junk);
        byte[] original = Wav(100_000, 36 + 100_000, 100_000, junk);

        var diagnosis = FileDoctor.Diagnose(Save("junk.wav", original));
        Assert.Equal([FileIssueKind.TrailingData], Kinds(diagnosis));

        var (_, output) = Repair(diagnosis.Path);
        Assert.Equal(original.AsSpan(0, 44 + 100_000).ToArray(), output);
    }

    [Fact]
    public void Details_saved_after_the_audio_are_part_of_the_recording()
    {
        // מקליטים ותוכנות עריכה כותבים אחרי השמע מקטע פרטים (LIST) — הוא לא "זבל".
        byte[] list = [.. "LIST"u8, 12, 0, 0, 0, .. "INFOISFT"u8, 0, 0, 0, 0];
        byte[] original = Wav(100_000, (uint)(36 + 100_000 + list.Length), 100_000, list);

        Assert.True(FileDoctor.Diagnose(Save("list.wav", original)).IsHealthy);
    }

    [Fact]
    public void Zeros_after_a_recording_are_left_alone()
    {
        byte[] original = Wav(100_000, 36 + 100_000, 100_000, new byte[4096]);
        Assert.True(FileDoctor.Diagnose(Save("zeros.wav", original)).IsHealthy);
    }

    // ================================================================ MP3

    /// <summary>מסגרות שמע של MPEG-1 שכבה 3, ‏128 קילו-ביט בשנייה, ‏44.1 קילו-הרץ — 417 בתים כל אחת.</summary>
    private static byte[] Frames(int count)
    {
        var random = new Random(2);
        byte[] data = new byte[count * 417];
        random.NextBytes(data);
        for (int i = 0; i < count; i++)
        {
            data[i * 417] = 0xFF; data[i * 417 + 1] = 0xFB; data[i * 417 + 2] = 0x90; data[i * 417 + 3] = 0x00;
        }
        return data;
    }

    /// <summary>תג פרטים (ID3) עם גוף של body בתים.</summary>
    private static byte[] Id3(int body)
    {
        byte[] tag = new byte[10 + body];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3;
        tag[6] = (byte)((body >> 21) & 0x7F); tag[7] = (byte)((body >> 14) & 0x7F);
        tag[8] = (byte)((body >> 7) & 0x7F); tag[9] = (byte)(body & 0x7F);
        return tag;
    }

    private static byte[] Junk(int length, int seed)
    {
        byte[] junk = new byte[length];
        new Random(seed).NextBytes(junk);
        for (int i = 0; i < junk.Length; i++) if (junk[i] == 0xFF) junk[i] = 0x7F;     // בלי מסגרות מקריות
        return junk;
    }

    [Fact]
    public void A_song_without_a_details_tag_is_recognised_as_healthy()
    {
        var diagnosis = FileDoctor.Diagnose(Save("plain.mp3", Frames(60)));

        Assert.True(diagnosis.IsHealthy);
        Assert.Equal("שמע MP3", diagnosis.DetectedFormat);
    }

    [Fact]
    public void Junk_before_a_song_is_removed()
    {
        byte[] song = Frames(60);
        string path = Save("junk-start.mp3", [.. Junk(3000, 3), .. song]);

        Assert.Equal([FileIssueKind.LeadingData], Kinds(FileDoctor.Diagnose(path)));

        var (result, output) = Repair(path);
        Assert.Equal(song, output);
        Assert.True(result.After!.IsHealthy);
    }

    [Fact]
    public void Junk_before_the_details_tag_is_removed_and_the_tag_is_kept()
    {
        byte[] song = [.. Id3(500), .. Frames(60)];
        var (_, output) = Repair(Save("junk-tag.mp3", [.. Junk(777, 4), .. song]));

        Assert.Equal(song, output);
    }

    [Fact]
    public void Junk_after_a_song_is_removed_but_the_closing_tag_stays()
    {
        byte[] closing = [.. "TAG"u8, .. new byte[125]];
        byte[] song = [.. Id3(200), .. Frames(60), .. closing];
        string path = Save("junk-end.mp3", [.. song, .. Junk(3000, 5)]);

        Assert.Equal([FileIssueKind.TrailingData], Kinds(FileDoctor.Diagnose(path)));
        Assert.Equal(song, Repair(path).Output);
    }

    [Fact]
    public void A_song_whose_last_frame_was_cut_is_not_trimmed()
    {
        byte[] song = Frames(60);
        Assert.True(FileDoctor.Diagnose(Save("cut.mp3", song.AsSpan(0, song.Length - 200).ToArray())).IsHealthy);
    }

    [Fact]
    public void Random_data_named_mp3_is_not_mistaken_for_a_song()
    {
        byte[] data = new byte[200_000];
        new Random(6).NextBytes(data);

        var diagnosis = FileDoctor.Diagnose(Save("random.mp3", data));
        Assert.Equal([FileIssueKind.Unrecognized], Kinds(diagnosis));
    }

    // ================================================================ SQLite

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(65536)]                 // נשמר בכותרת כ-1
    public void A_database_whose_page_size_was_lost_opens_again(int pageSize)
    {
        byte[] db = RealSqlite.Create(pageSize, 3000);
        Assert.True(FileDoctor.Diagnose(Save("healthy.db", db)).IsHealthy);

        byte[] damaged = (byte[])db.Clone();
        Array.Clear(damaged, 16, 8);                                           // גודל הדף והשדות הקבועים
        string path = Save("damaged.db", damaged);
        Assert.NotEqual("ok", RealSqlite.Open(path).Integrity);

        var diagnosis = FileDoctor.Diagnose(path);
        var issue = Assert.Single(diagnosis.Issues);
        Assert.Equal(FileIssueKind.HeaderDamaged, issue.Kind);
        Assert.True(issue.Fixable, issue.Description);

        var (result, output) = Repair(path);
        Assert.Equal(db, output);
        Assert.Equal(("ok", 3000L), RealSqlite.Open(result.OutputPath!));
    }

    [Fact]
    public void A_database_whose_whole_header_start_was_zeroed_opens_again()
    {
        byte[] db = RealSqlite.Create(4096, 2000);
        byte[] damaged = (byte[])db.Clone();
        Array.Clear(damaged, 0, 24);                                           // החתימה, גודל הדף והשדות הקבועים

        var (result, output) = Repair(Save("zeroed.db", damaged));
        Assert.Equal(db, output);
        Assert.Equal(("ok", 2000L), RealSqlite.Open(result.OutputPath!));
    }

    [Fact]
    public void An_outdated_page_count_does_not_cut_real_pages()
    {
        byte[] db = RealSqlite.Create(4096, 3000);

        // מספר דפים ישן בכותרת, ומונה הגרסה שלצידו לא תואם — SQLite מתעלמת ממנו.
        byte[] stale = (byte[])db.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(stale.AsSpan(28), 2);
        BinaryPrimitives.WriteUInt32BigEndian(stale.AsSpan(92), BinaryPrimitives.ReadUInt32BigEndian(stale.AsSpan(24)) - 1);
        string path = Save("stale.db", stale);

        Assert.Equal(("ok", 3000L), RealSqlite.Open(path));
        Assert.True(FileDoctor.Diagnose(path).IsHealthy);
    }
}
