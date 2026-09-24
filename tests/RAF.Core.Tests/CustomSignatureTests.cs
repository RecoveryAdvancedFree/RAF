using System.Text;
using RAF.Core.Carving;
using RAF.Core.FileSystems;
using RAF.Core.Native;
using RAF.Core.Signatures;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>הסוגים שהמשתמש מוסיף משותפים לכל התוכנה — הבדיקות שלהם לא רצות במקביל לאחרות.</summary>
[CollectionDefinition("סוגים שהמשתמש הוסיף", DisableParallelization = true)]
public sealed class CustomSignatureCollection;

/// <summary>
/// לימוד סוג קובץ מדוגמאות: הבתים שזהים בתחילת כל הדוגמאות הופכים לחתימה,
/// והסריקה המתקדמת מוצאת לפיה קבצים מהסוג הזה — גם כשהתוכנה לא הכירה אותו.
/// </summary>
[Collection("סוגים שהמשתמש הוסיף")]
public sealed class CustomSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-custom-{Guid.NewGuid():N}");

    public CustomSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CustomSignatures.Activate([]);
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// קובץ של תוכנת הנהלת חשבונות מדומה: "ACCT", גרסה שמשתנה, "\x01\x00BOOK", גוף אקראי,
    /// ובסוף "END-ACCT".
    /// </summary>
    private static byte[] Ledger(int body, int seed, bool footer = true)
    {
        byte[] data = RealFormats.Random(body, seed);
        byte[] head = [.. "ACCT"u8, (byte)(seed % 7 + 1), 0x01, 0x00, .. "BOOK"u8];
        head.CopyTo(data, 0);
        return footer ? [.. data, .. "END-ACCT"u8] : data;
    }

    private string Sample(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void The_bytes_shared_by_all_samples_become_the_signature()
    {
        var result = CustomSignatures.Learn([
            Sample("a.acc", Ledger(5000, 1)), Sample("b.acc", Ledger(9000, 2)), Sample("c.acc", Ledger(7000, 3))]);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("acc", result.Type!.Extension);
        Assert.StartsWith("41 43 43 54 ?? 01 00 42 4F 4F 4B", result.Type.Header);    // הגרסה פתוחה
        Assert.Equal(CustomSignatures.ToHex(Encoding.ASCII.GetBytes("END-ACCT").Select(b => (byte?)b).ToArray()),
            result.Type.Footer);
    }

    [Fact]
    public void An_advanced_scan_finds_files_of_a_learned_type_with_their_exact_length()
    {
        var learned = CustomSignatures.Learn([
            Sample("a.acc", Ledger(5000, 1)), Sample("b.acc", Ledger(9000, 2)), Sample("c.acc", Ledger(7000, 3))]);
        CustomSignatures.Activate([learned.Type! with { Name = "קבצי הנהלת חשבונות" }]);

        // כונן: נתונים אקראיים, קובץ חדש מהסוג (שלא היה בדוגמאות), ועוד נתונים אחריו.
        byte[] lost = Ledger(20_000, 4);
        var image = new MemoryStream();
        image.Write(RealFormats.Random(64 * 1024, 5).Select(b => b == (byte)'A' ? (byte)'B' : b).ToArray());
        long offset = image.Length;
        image.Write(lost);
        image.Write(new byte[512 - lost.Length % 512 + 64 * 1024]);

        string path = Path.Combine(_dir, "disk.img");
        File.WriteAllBytes(path, image.ToArray());
        using var device = RawDevice.TryOpen(path, 512)!;
        var volume = RawVolume.Open(VolumeReader.Wrap(device, 0, image.Length), 512);

        var found = new FileCarver().Sweep(volume, image.Length, 512, null, CancellationToken.None).Files;

        var file = Assert.Single(found, f => f.Extension == "acc");
        Assert.Equal(lost.Length, file.Size);
        Assert.Equal(offset / 512, file.Extents[0].StartCluster);
        Assert.Contains("קבצי הנהלת חשבונות", file.Path);
    }

    [Fact]
    public void A_learned_type_never_hides_a_type_the_program_already_knows()
    {
        // חתימה "נלמדת" שמתחילה כמו PNG — עדיין מזוהה כ-PNG.
        CustomSignatures.Activate([new CustomType("מתחזה", "fake", "89 50 4E 47 0D 0A 1A 0A", null, 1024 * 1024, 3)]);

        Assert.Equal("png", FileSignatures.Identify(RealFormats.Png(500, 6))!.Extensions[0]);
    }

    [Fact]
    public void Files_the_program_already_knows_are_not_learned_again()
    {
        var result = CustomSignatures.Learn([Sample("a.png", RealFormats.Png(500, 7)), Sample("b.png", RealFormats.Png(900, 8))]);

        Assert.False(result.Ok);
        Assert.Contains("כבר מכירה", result.Message);
    }

    [Fact]
    public void Files_with_nothing_in_common_are_refused()
    {
        var result = CustomSignatures.Learn([
            Sample("a.dat", RealFormats.Random(4000, 9)), Sample("b.dat", RealFormats.Random(4000, 10))]);

        Assert.False(result.Ok);
        Assert.Contains("לא נמצא מספיק משותף", result.Message);
    }

    [Fact]
    public void A_single_sample_is_not_enough()
        => Assert.False(CustomSignatures.Learn([Sample("a.acc", Ledger(5000, 1))]).Ok);

    [Fact]
    public void A_type_without_a_shared_ending_is_still_found_and_the_limit_is_explained()
    {
        var result = CustomSignatures.Learn([
            Sample("a.acc", Ledger(5000, 1, footer: false)), Sample("b.acc", Ledger(9000, 2, footer: false))]);

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Type!.Footer);
        Assert.Contains("אין סוף קבוע", result.Message);
    }

    [Fact]
    public void Saved_types_come_back_after_a_restart()
    {
        string path = Path.Combine(_dir, "settings", "custom-types.json");
        var type = new CustomType("קבצי הנהלת חשבונות", "acc", "41 43 43 54 ?? 01 00 42 4F 4F 4B", "45 4E 44", 40_000, 3);

        CustomSignatures.Save(path, [type]);
        Assert.Equal([type], CustomSignatures.Load(path));

        // קובץ הגדרות פגום לא מפיל את התוכנה.
        File.WriteAllText(path, "{ לא json");
        Assert.Empty(CustomSignatures.Load(path));
    }
}
