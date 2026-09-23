using RAF.Core.FileSystems;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Recovery;

/// <summary>
/// קריאת תחילת תוכנו של קובץ שנמצא בסריקה, ללא כתיבה לדיסק.
/// משמש לתצוגה מקדימה ולזיהוי חתימת HEX לפני שהמשתמש מחליט לשחזר.
/// </summary>
public static class FileContentReader
{
    /// <summary>
    /// קריאת עד <paramref name="maxBytes"/> בתים מתחילת הקובץ.
    /// מחזיר מערך ריק אם לא ניתן לקרוא את התוכן.
    /// </summary>
    public static byte[] ReadHead(
        FileSystemKind fileSystem,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        RecoveredFile file, int maxBytes)
    {
        if (file.ResidentData is not null)
        {
            int take = (int)Math.Min(Math.Min(file.ResidentData.Length, file.Size), maxBytes);
            return file.ResidentData.AsSpan(0, Math.Max(0, take)).ToArray();
        }

        if (file.Extents.Count == 0 || file.Size <= 0) return Array.Empty<byte>();

        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false);
        if (reader is null) return Array.Empty<byte>();

        using var volume = VolumeScanner.Open(reader, fileSystem, sectorSize);
        if (volume is null) return Array.Empty<byte>();

        long wanted = Math.Min(file.Size, maxBytes);

        var stream = new ClusterStream(
            volume, file.Extents, wanted,
            file.IsCompressed ? file.CompressionUnitClusters : 0);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer, CancellationToken.None);
        return buffer.ToArray();
    }
}
