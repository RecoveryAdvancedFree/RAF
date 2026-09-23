using RAF.Core.FileSystems;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Recovery;

/// <summary>
/// קריאה מכל מקום בתוך קובץ שנמצא בסריקה — לא רק מתחילתו. משמש את נגן
/// התצוגה המקדימה: סרטון של כמה ג'יגה אינו נקרא כולו, אלא רק הקטעים שהנגן
/// מבקש, כשמנגנים או מדלגים קדימה.
///
/// קריאה בלבד, כמו כל גישה לכונן המקור. אזור שאינו נקרא מוחזר כאפסים —
/// לנגן עדיף קטע משובש על פני שגיאה שעוצרת את כל הניגון.
/// </summary>
public sealed class FileContentSource : IDisposable
{
    private readonly VolumeReader? _reader;
    private readonly IClusterVolume? _volume;
    private readonly byte[]? _resident;
    private readonly List<(long StartVcn, DataExtent Extent)> _map = new();
    private readonly object _gate = new();

    public long Length { get; }

    private FileContentSource(long length, byte[]? resident, VolumeReader? reader, IClusterVolume? volume,
        IReadOnlyList<DataExtent> extents)
    {
        Length = length;
        _resident = resident;
        _reader = reader;
        _volume = volume;

        long vcn = 0;
        foreach (var extent in extents)
        {
            _map.Add((vcn, extent));
            vcn += extent.ClusterCount;
        }
    }

    /// <summary>
    /// פתיחת הקובץ לקריאה חופשית. null כשאי אפשר: אין מיקום ידוע, הכונן אינו
    /// נקרא, או קובץ דחוס של NTFS — שבו היסט בקובץ אינו היסט על הכונן.
    /// </summary>
    public static FileContentSource? Open(
        FileSystemKind fileSystem, int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        RecoveredFile file)
    {
        if (file.Size <= 0) return null;

        if (file.ResidentData is not null)
            return new FileContentSource(Math.Min(file.Size, file.ResidentData.Length), file.ResidentData,
                null, null, Array.Empty<DataExtent>());

        if (file.IsCompressed || file.Extents.Count == 0) return null;

        var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false);
        if (reader is null) return null;

        var volume = VolumeScanner.Open(reader, fileSystem, sectorSize);
        if (volume is null)
        {
            reader.Dispose();
            return null;
        }

        return new FileContentSource(file.Size, null, reader, volume, file.Extents);
    }

    /// <summary>
    /// קריאת עד destination.Length בתים מהיסט בקובץ. מחזיר כמה נכתבו —
    /// פחות מהמבוקש רק בסוף הקובץ.
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length) return 0;
        int total = (int)Math.Min(destination.Length, Length - offset);
        destination = destination[..total];

        if (_resident is not null)
        {
            _resident.AsSpan((int)offset, total).CopyTo(destination);
            return total;
        }

        lock (_gate)
        {
            int cluster = _volume!.BytesPerCluster;
            int done = 0;

            while (done < total)
            {
                long position = offset + done;
                long vcn = position / cluster;
                int inner = (int)(position % cluster);

                var run = RunAt(vcn);
                if (run is null)
                {
                    // מעבר לסוף המקטעים הידועים — אין מה לקרוא.
                    destination[done..].Clear();
                    return total;
                }

                var (startVcn, extent) = run.Value;
                long runBytes = (startVcn + extent.ClusterCount - vcn) * cluster - inner;
                int take = (int)Math.Min(total - done, runBytes);
                var slice = destination.Slice(done, take);

                if (extent.IsSparse)
                {
                    slice.Clear();
                }
                else
                {
                    long at = _volume.ClusterToOffset(extent.StartCluster + (vcn - startVcn)) + inner;
                    int read = _volume.ReadRaw(at, slice);
                    if (read < take) slice[Math.Max(0, read)..].Clear();
                }

                done += take;
            }

            return total;
        }
    }

    private (long StartVcn, DataExtent Extent)? RunAt(long vcn)
    {
        foreach (var entry in _map)
            if (vcn >= entry.StartVcn && vcn < entry.StartVcn + entry.Extent.ClusterCount)
                return entry;
        return null;
    }

    public void Dispose()
    {
        _volume?.Dispose();
        _reader?.Dispose();
    }
}
