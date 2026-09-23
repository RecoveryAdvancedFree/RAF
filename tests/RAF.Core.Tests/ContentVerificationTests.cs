using RAF.Core.FileSystems;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות אימות התוכן מול הדיסק.
///
/// אלה הבדיקות שתופסות את הכשל החמור ביותר בתוכנת שחזור: קובץ שרשומת
/// המטא-דאטה שלו שלמה — שם, גודל ומיקום אשכולות — אך הנתונים עצמם כבר
/// אינם קיימים. בכונן SSD עם TRIM זהו המצב הרגיל לאחר מחיקה, והתוצאה
/// היא קובץ בגודל הנכון המלא כולו באפסים. ללא אימות, התוכנה מבטיחה
/// שחזור שאינו אפשרי.
/// </summary>
public class ContentVerificationTests
{
    private const long DataCluster = 200;
    private const int DataClusters = 4;
    private static readonly int DataBytes = DataClusters * NtfsImageBuilder.BytesPerCluster;

    /// <summary>מקטעי הנתונים של קובץ הבדיקה.</summary>
    private static List<DataExtent> Extents() => new()
    {
        new DataExtent(DataCluster, DataClusters, IsSparse: false),
    };

    private static byte[] RealContent()
    {
        byte[] data = new byte[DataBytes];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251 + 1); // ללא אפסים
        return data;
    }

    // ------------------------------------------------------------ דגימה

    [Fact]
    public void Sampling_reports_data_when_the_content_is_still_there()
    {
        using var image = new NtfsImageBuilder();
        image.WriteClusters(DataCluster, RealContent());

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), DataBytes, 0);

        Assert.Equal(ContentCheck.HasData, stream.SampleContent());
    }

    [Fact]
    public void Sampling_reports_empty_when_the_clusters_were_erased()
    {
        using var image = new NtfsImageBuilder();
        image.ZeroClusters(DataCluster, DataClusters);

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), DataBytes, 0);

        // זהו בדיוק המצב שאחרי TRIM: המטא-דאטה שלמה, הנתונים אינם.
        Assert.Equal(ContentCheck.Empty, stream.SampleContent());
    }

    [Fact]
    public void Sampling_detects_content_even_when_only_the_tail_survives()
    {
        using var image = new NtfsImageBuilder();
        image.ZeroClusters(DataCluster, DataClusters);

        // רק האשכול האחרון מכיל נתונים.
        byte[] tail = new byte[NtfsImageBuilder.BytesPerCluster];
        Array.Fill(tail, (byte)0xAB);
        image.WriteClusters(DataCluster + DataClusters - 1, tail);

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), DataBytes, 0);

        Assert.Equal(ContentCheck.HasData, stream.SampleContent());
    }

    // ------------------------------------------------------------ חילוץ

    [Fact]
    public void Extraction_returns_the_exact_bytes_and_reports_content()
    {
        byte[] expected = RealContent();

        using var image = new NtfsImageBuilder();
        image.WriteClusters(DataCluster, expected);

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), DataBytes, 0);

        using var output = new MemoryStream();
        var outcome = stream.CopyTo(output, CancellationToken.None);

        Assert.True(outcome.SawContent);
        Assert.Equal(0, outcome.UnreadableBytes);
        Assert.Equal(DataBytes, outcome.BytesWritten);
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public void Extraction_of_erased_clusters_reports_that_no_content_was_found()
    {
        using var image = new NtfsImageBuilder();
        image.ZeroClusters(DataCluster, DataClusters);

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), DataBytes, 0);

        using var output = new MemoryStream();
        var outcome = stream.CopyTo(output, CancellationToken.None);

        // הגודל נכון והכתיבה הצליחה — ולכן דווקא הדגל הזה הוא ההגנה היחידה
        // מפני הצגת קובץ אפסים כשחזור מוצלח.
        Assert.Equal(DataBytes, outcome.BytesWritten);
        Assert.False(outcome.SawContent);
        Assert.All(output.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Partial_size_is_honoured_rather_than_padded_to_the_cluster()
    {
        byte[] expected = RealContent();
        const int realSize = 1300; // אינו כפולה של גודל אשכול

        using var image = new NtfsImageBuilder();
        image.WriteClusters(DataCluster, expected);

        using var volume = image.OpenVolume();
        var stream = new ClusterStream(volume, Extents(), realSize, 0);

        using var output = new MemoryStream();
        var outcome = stream.CopyTo(output, CancellationToken.None);

        Assert.Equal(realSize, outcome.BytesWritten);
        Assert.Equal(expected.AsSpan(0, realSize).ToArray(), output.ToArray());
    }

    [Fact]
    public void Sparse_regions_are_written_as_zeros_without_reading_the_disk()
    {
        byte[] real = RealContent();

        using var image = new NtfsImageBuilder();
        image.WriteClusters(DataCluster, real);

        using var volume = image.OpenVolume();

        var extents = new List<DataExtent>
        {
            new(0, 2, IsSparse: true),                       // חור בתחילת הקובץ
            new(DataCluster, DataClusters, IsSparse: false),  // ואז נתונים אמיתיים
        };

        int total = (2 + DataClusters) * NtfsImageBuilder.BytesPerCluster;
        var stream = new ClusterStream(volume, extents, total, 0);

        using var output = new MemoryStream();
        var outcome = stream.CopyTo(output, CancellationToken.None);

        byte[] result = output.ToArray();
        Assert.True(outcome.SawContent);
        Assert.Equal(total, outcome.BytesWritten);

        // האזור הדליל הוא אפסים לגיטימיים, והנתונים מגיעים אחריו.
        Assert.All(result.AsSpan(0, 2 * NtfsImageBuilder.BytesPerCluster).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(real, result.AsSpan(2 * NtfsImageBuilder.BytesPerCluster).ToArray());
    }
}
