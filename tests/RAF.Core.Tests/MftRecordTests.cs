using System.Text;
using RAF.Core.FileSystems.Ntfs;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>בדיקות מפענח רשומות ה-MFT מול רשומות סינתטיות תקינות במבנן.</summary>
public class MftRecordTests
{
    private static readonly DateTime Created = new(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2025, 1, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Accessed = new(2025, 6, 9, 21, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_name_parent_and_timestamps()
    {
        byte[] raw = new MftRecordBuilder()
            .WithStandardInformation(Created, Modified, Accessed)
            .WithFileName("דוח שנתי.docx", parentRecord: 77, parentSequence: 4)
            .Build();

        var record = MftRecord.Parse(raw, 512);

        Assert.NotNull(record);
        Assert.Equal(42, record!.RecordNumber);

        var name = record.PreferredName();
        Assert.NotNull(name);
        Assert.Equal("דוח שנתי.docx", name!.Value.Name);
        Assert.Equal(77, name.Value.ParentRecord);
        Assert.Equal(4, name.Value.ParentSequence);

        Assert.Equal(Created.ToLocalTime(), record.Created);
        Assert.Equal(Modified.ToLocalTime(), record.Modified);
        Assert.Equal(Accessed.ToLocalTime(), record.Accessed);
    }

    [Fact]
    public void Deleted_record_is_reported_as_not_in_use()
    {
        byte[] raw = new MftRecordBuilder { InUse = false }
            .WithFileName("נמחק.txt", parentRecord: 5)
            .Build();

        var record = MftRecord.Parse(raw, 512);

        Assert.NotNull(record);
        Assert.False(record!.InUse);
        Assert.False(record.IsDirectory);
    }

    [Fact]
    public void Directory_flag_is_detected()
    {
        byte[] raw = new MftRecordBuilder { IsDirectory = true }
            .WithFileName("תמונות", parentRecord: 5)
            .Build();

        var record = MftRecord.Parse(raw, 512);

        Assert.NotNull(record);
        Assert.True(record!.IsDirectory);
    }

    [Fact]
    public void Resident_data_survives_the_fixup_across_a_sector_boundary()
    {
        // תוכן ארוך מספיק כדי לחצות את גבול הסקטור הראשון (בית 510–511),
        // שם NTFS דורס את הבתים במונה. אם ה-fixup שגוי, התוכן יחזור פגום.
        byte[] content = new byte[800];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);

        byte[] raw = new MftRecordBuilder(recordSize: 2048)
            .WithFileName("קטן.bin", parentRecord: 5)
            .WithResidentData(content)
            .Build();

        var record = MftRecord.Parse(raw, 512);
        var data = record?.PrimaryData();

        Assert.NotNull(data);
        Assert.False(data!.IsNonResident);
        Assert.Equal(content, data.ResidentValue);
    }

    [Fact]
    public void Record_with_broken_fixup_marker_is_rejected()
    {
        byte[] raw = new MftRecordBuilder()
            .WithFileName("פגום.txt", parentRecord: 5)
            .Build();

        // שינוי המונה בסוף הסקטור הראשון מדמה רשומה שנכתבה חלקית.
        raw[510] = 0x00;
        raw[511] = 0x00;

        Assert.Null(MftRecord.Parse(raw, 512));
    }

    [Fact]
    public void Long_name_is_preferred_over_dos_short_name()
    {
        // NTFS שומר לקובץ גם שם 8.3 (מרחב שמות 2) וגם שם ארוך (מרחב שמות 1).
        byte[] raw = new MftRecordBuilder()
            .WithFileName("DOCUME~1.DOC", parentRecord: 5, nameSpace: 2)
            .WithFileName("מסמך ארוך מאוד.doc", parentRecord: 5, nameSpace: 1)
            .Build();

        var record = MftRecord.Parse(raw, 512);

        Assert.Equal(2, record!.FileNames.Count);
        Assert.Equal("מסמך ארוך מאוד.doc", record.PreferredName()!.Value.Name);
    }

    [Fact]
    public void Non_resident_data_exposes_its_extents()
    {
        byte[] runList = { 0x21, 0x10, 0x00, 0x10, 0x00 }; // 16 אשכולות מאשכול 0x1000
        byte[] raw = new MftRecordBuilder()
            .WithFileName("גדול.mp4", parentRecord: 9)
            .WithNonResidentData(runList, realSize: 65536)
            .Build();

        var record = MftRecord.Parse(raw, 512);
        var data = record?.PrimaryData();

        Assert.NotNull(data);
        Assert.True(data!.IsNonResident);
        Assert.Equal(65536, data.RealSize);
        Assert.Single(data.Extents);
        Assert.Equal(0x1000, data.Extents[0].StartCluster);
        Assert.Equal(16, data.Extents[0].ClusterCount);
    }

    [Fact]
    public void Alternate_stream_is_kept_separate_from_the_main_stream()
    {
        byte[] main = Encoding.UTF8.GetBytes("תוכן ראשי");
        byte[] alternate = Encoding.UTF8.GetBytes("Zone.Identifier");

        byte[] raw = new MftRecordBuilder()
            .WithFileName("הורדה.exe", parentRecord: 5)
            .WithResidentData(main)
            .WithResidentData(alternate, streamName: "Zone.Identifier")
            .Build();

        var record = MftRecord.Parse(raw, 512);

        Assert.Equal(main, record!.PrimaryData()!.ResidentValue);
        Assert.Single(record.AlternateStreams());
        Assert.Equal("Zone.Identifier", record.AlternateStreams().First().Name);
    }

    [Fact]
    public void Non_file_signature_is_rejected()
    {
        byte[] raw = new byte[1024];
        Encoding.ASCII.GetBytes("BAAD").CopyTo(raw, 0);

        Assert.Null(MftRecord.Parse(raw, 512));
    }

    [Fact]
    public void Impossible_timestamps_are_discarded_rather_than_surfaced()
    {
        byte[] raw = new MftRecordBuilder()
            .WithStandardInformation(Created, Modified, Accessed)
            .WithFileName("קובץ.txt", parentRecord: 5)
            .Build();

        var record = MftRecord.Parse(raw, 512);
        Assert.NotNull(record!.Created);

        // רשומה פגומה עם חותמת זמן אבסורדית לא אמורה להציג תאריך שגוי למשתמש.
        byte[] corrupt = new MftRecordBuilder()
            .WithStandardInformation(
                new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Modified, Accessed)
            .WithFileName("קובץ.txt", parentRecord: 5)
            .Build();

        var corruptRecord = MftRecord.Parse(corrupt, 512);
        Assert.Null(corruptRecord!.Created);
    }
}
