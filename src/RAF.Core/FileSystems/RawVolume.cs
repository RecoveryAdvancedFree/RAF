using RAF.Core.Native;

namespace RAF.Core.FileSystems;

/// <summary>
/// גישה למחיצה ללא מערכת קבצים כלל.
///
/// הסריקה המתקדמת עובדת על סקטורים גולמיים ואינה תלויה במבנה כלשהו,
/// ולכן היחידה שלה היא סקטור ולא אשכול. כך אותו מנגנון חילוץ שמשמש
/// את NTFS ואת FAT משמש גם קבצים שזוהו לפי חתימה בלבד.
/// </summary>
internal sealed class RawVolume : IClusterVolume
{
    private readonly VolumeReader _reader;
    private readonly bool _ownsReader;

    /// <summary>ביחידות של מחיצה גולמית, "אשכול" הוא סקטור יחיד.</summary>
    public int BytesPerCluster { get; }

    internal long Length => _reader.Length;

    private RawVolume(VolumeReader reader, int sectorSize, bool ownsReader)
    {
        _reader = reader;
        BytesPerCluster = sectorSize;
        _ownsReader = ownsReader;
    }

    internal static RawVolume Open(VolumeReader reader, int sectorSize, bool ownsReader = false)
        => new(reader, sectorSize, ownsReader);

    public long ClusterToOffset(long cluster) => cluster * BytesPerCluster;

    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    /// <summary>
    /// במחיצה גולמית אין מפת הקצאה, ולכן אין דרך לדעת אם אזור תפוס.
    /// החזרת null מונעת מהדירוג להסיק מסקנה שאין לה בסיס.
    /// </summary>
    public bool? IsClusterAllocated(long cluster) => null;

    public void Dispose()
    {
        if (_ownsReader) _reader.Dispose();
    }
}
