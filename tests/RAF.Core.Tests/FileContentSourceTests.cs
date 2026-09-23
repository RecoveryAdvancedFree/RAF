using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// קריאה מכל מקום בקובץ — הבסיס של נגן התצוגה המקדימה. הנגן מבקש קטעים
/// באמצע הקובץ (דילוג קדימה), ובקובץ מפוצל כל קטע יושב במקום אחר בכונן;
/// קטע שחוצה את הגבול בין שני חלקים חייב לחזור רציף ומדויק.
/// </summary>
public class FileContentSourceTests : IDisposable
{
    private const int Sector = 512;
    private readonly string _image = Path.Combine(Path.GetTempPath(), $"raf-source-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        foreach (var p in new[] { _image, ImageMap.PathFor(_image) })
            try { File.Delete(p); } catch { }
        GC.SuppressFinalize(this);
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 31 / 7 + i / 512);
        return data;
    }

    private T WithFile<T>(byte[] image, RecoveredFile file, Func<FileContentSource, T> test)
    {
        File.WriteAllBytes(_image, image);
        new ImageMap { Kind = "partition", Size = image.Length, Complete = true }.Save(ImageMap.PathFor(_image));
        var disk = ImageDisk.Open(_image);
        try
        {
            using var source = FileContentSource.Open(FileSystemKind.Raw, disk.DiskNumber, 0, image.Length, Sector, file)
                               ?? throw new InvalidOperationException("the file could not be opened");
            return test(source);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void Any_range_of_a_fragmented_file_comes_back_exact()
    {
        // קובץ של 10 סקטורים, בשלושה חלקים לא רציפים ובסדר הפוך על הכונן.
        byte[] content = Pattern(10 * Sector - 100);
        byte[] image = new byte[80 * Sector];
        content.AsSpan(0, 4 * Sector).CopyTo(image.AsSpan(60 * Sector));
        content.AsSpan(4 * Sector, 3 * Sector).CopyTo(image.AsSpan(30 * Sector));
        content.AsSpan(7 * Sector).CopyTo(image.AsSpan(10 * Sector));

        var file = new RecoveredFile
        {
            Id = 1, Name = "clip.mp4", Size = content.Length,
            Extents = new List<DataExtent> { new(60, 4, false), new(30, 3, false), new(10, 3, false) },
        };

        WithFile(image, file, source =>
        {
            Assert.Equal(content.Length, source.Length);

            // קטעים שחוצים את שני הגבולות, קטע באמצע חלק, וקטע עד סוף הקובץ.
            foreach (var (offset, length) in new[] { (0, 700), (4 * Sector - 50, 100), (5 * Sector + 3, 1500), (content.Length - 300, 1000) })
            {
                byte[] buffer = new byte[length];
                int read = source.Read(offset, buffer);
                int expected = Math.Min(length, content.Length - offset);
                Assert.Equal(expected, read);
                Assert.Equal(content.AsSpan(offset, expected).ToArray(), buffer.AsSpan(0, read).ToArray());
            }

            Assert.Equal(0, source.Read(content.Length, new byte[10]));
            return 0;
        });
    }

    [Fact]
    public void A_compressed_file_is_not_offered_for_random_access()
    {
        var file = new RecoveredFile
        {
            Id = 2, Name = "clip.mp4", Size = 4096, IsCompressed = true,
            Extents = new List<DataExtent> { new(1, 8, false) },
        };

        Assert.Null(FileContentSource.Open(FileSystemKind.Raw, 0, 0, 4096, Sector, file));
    }
}
