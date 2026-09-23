using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// מחיצת NTFS פתוחה לקריאה. אחראית על אתחול ה-MFT,
/// על קריאת רשומות לפי מספר ועל שליפת תוכן קבצים לפי מיקומם.
/// </summary>
internal sealed class NtfsVolume : IClusterVolume
{
    // מספרי הרשומות הקבועות של קובצי המערכת ב-NTFS.
    internal const long RecordMft = 0;
    internal const long RecordLogFile = 2;
    internal const long RecordRoot = 5;
    internal const long RecordBitmap = 6;
    internal const long RecordExtend = 11;

    private readonly VolumeReader _reader;
    private readonly bool _ownsReader;

    /// <summary>מיפוי VCN ל-LCN של ה-MFT עצמו — נקודת הפתיחה לכל קריאה.</summary>
    private readonly List<DataExtent> _mftExtents = new();
    private long _mftClusterCount;

    /// <summary>מפת האשכולות התפוסים, לצורך הערכת איכות השחזור.</summary>
    private byte[]? _clusterBitmap;

    internal NtfsBootSector Boot { get; }

    public int BytesPerCluster => Boot.BytesPerCluster;

    /// <summary>מספר הרשומות הכולל ב-MFT, כפי שנגזר מגודלו.</summary>
    internal long RecordCount { get; private set; }

    private NtfsVolume(VolumeReader reader, NtfsBootSector boot, bool ownsReader)
    {
        _reader = reader;
        Boot = boot;
        _ownsReader = ownsReader;
    }

    /// <summary>פתיחת מחיצת NTFS. מחזיר null אם המחיצה אינה NTFS תקין.</summary>
    internal static NtfsVolume? Open(VolumeReader reader, bool ownsReader = false)
    {
        byte[] sector = reader.ReadBlock(0, 512);
        var boot = NtfsBootSector.Parse(sector);
        if (boot is null) return null;

        var volume = new NtfsVolume(reader, boot, ownsReader);
        return volume.Bootstrap() ? volume : null;
    }

    /// <summary>
    /// אתחול: קריאת רשומה 0 ($MFT) מהמיקום שבמגזר האתחול,
    /// ומתוכה גזירת הפריסה המלאה של ה-MFT על המחיצה.
    /// </summary>
    private bool Bootstrap()
    {
        byte[] first = _reader.ReadBlock(Boot.MftOffset, Boot.MftRecordSize);
        if (first.Length < Boot.MftRecordSize) return false;

        var mftRecord = MftRecord.Parse(first, Boot.BytesPerSector, RecordMft);
        var data = mftRecord?.PrimaryData();

        if (data is null || !data.IsNonResident || data.Extents.Count == 0)
        {
            // גיבוי: אם רשומה 0 פגומה, מניחים MFT רציף מהמיקום שבמגזר האתחול.
            // הדבר מאפשר סריקה חלקית גם במחיצה פגועה.
            _mftExtents.Add(new DataExtent(Boot.MftStartCluster, Boot.TotalClusters / 8, false));
            _mftClusterCount = Boot.TotalClusters / 8;
            RecordCount = _mftClusterCount * Boot.BytesPerCluster / Boot.MftRecordSize;
            return true;
        }

        _mftExtents.AddRange(data.Extents);
        _mftClusterCount = DataRuns.AllocatedClusters(_mftExtents);
        RecordCount = _mftClusterCount * Boot.BytesPerCluster / Boot.MftRecordSize;

        return RecordCount > 0;
    }

    /// <summary>טעינת מפת האשכולות ($Bitmap) — משמשת להערכת סיכויי שחזור.</summary>
    internal void LoadClusterBitmap()
    {
        if (_clusterBitmap is not null) return;

        try
        {
            byte[]? record = ReadRecord(RecordBitmap);
            if (record is null) return;

            var parsed = MftRecord.Parse(record, Boot.BytesPerSector, RecordBitmap);
            var data = parsed?.PrimaryData();
            if (data is null || !data.IsNonResident) return;

            // מפת ביט אחד לכל אשכול. במחיצה של 1TB עם אשכול 4KB זה כ-32MB.
            long sizeBytes = Math.Min(data.RealSize, 256L * 1024 * 1024);
            _clusterBitmap = ReadExtents(data.Extents, 0, (int)sizeBytes);
        }
        catch
        {
            _clusterBitmap = null;
        }
    }

    /// <summary>האם האשכול מסומן כתפוס. null כשמפת האשכולות לא נטענה.</summary>
    public bool? IsClusterAllocated(long lcn)
    {
        if (_clusterBitmap is null || lcn < 0) return null;

        long byteIndex = lcn / 8;
        if (byteIndex >= _clusterBitmap.Length) return null;

        return (_clusterBitmap[byteIndex] & (1 << (int)(lcn % 8))) != 0;
    }

    /// <summary>המרת מספר אשכול להיסט בבתים מתחילת המחיצה.</summary>
    public long ClusterToOffset(long lcn) => lcn * Boot.BytesPerCluster;

    /// <summary>קריאת רשומת MFT לפי מספרה. מחזיר null אם היא מחוץ לטווח.</summary>
    internal byte[]? ReadRecord(long recordNumber)
    {
        if (recordNumber < 0 || recordNumber >= RecordCount) return null;

        long byteOffset = recordNumber * Boot.MftRecordSize;
        byte[] buffer = ReadExtents(_mftExtents, byteOffset, Boot.MftRecordSize);
        return buffer.Length == Boot.MftRecordSize ? buffer : null;
    }

    /// <summary>קריאת רשומת MFT מפוענחת לפי מספרה.</summary>
    internal MftRecord? ReadParsedRecord(long recordNumber)
    {
        byte[]? raw = ReadRecord(recordNumber);
        return raw is null ? null : MftRecord.Parse(raw, Boot.BytesPerSector, recordNumber);
    }

    /// <summary>
    /// קריאת טווח בתים מתוך רשימת מקטעי אשכולות.
    /// זו הפעולה שמרכיבה קובץ מפוצל בחזרה לרצף אחד.
    /// </summary>
    internal byte[] ReadExtents(IReadOnlyList<DataExtent> extents, long byteOffset, int count)
    {
        if (count <= 0 || byteOffset < 0) return Array.Empty<byte>();

        var result = new byte[count];
        int written = 0;
        long cursor = 0; // היסט לוגי בתוך הזרם המתואר על ידי המקטעים

        foreach (var extent in extents)
        {
            long extentBytes = extent.ClusterCount * Boot.BytesPerCluster;

            // דילוג על מקטעים שלפני נקודת ההתחלה המבוקשת.
            if (cursor + extentBytes <= byteOffset)
            {
                cursor += extentBytes;
                continue;
            }

            long skipInExtent = Math.Max(0, byteOffset - cursor);
            long availableHere = extentBytes - skipInExtent;
            int take = (int)Math.Min(availableHere, count - written);

            if (extent.IsSparse)
            {
                // מקטע דליל מייצג אפסים; המאגר כבר מאותחל באפסים.
                written += take;
            }
            else
            {
                long diskOffset = ClusterToOffset(extent.StartCluster) + skipInExtent;
                int read = _reader.Read(diskOffset, result.AsSpan(written, take));
                written += read;

                // קריאה חלקית מעידה על סקטור פגום או על חריגה מגבול המחיצה.
                if (read < take) break;
            }

            cursor += extentBytes;
            if (written >= count) break;
        }

        return written == count ? result : result.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// קריאת טווח בתים מתוך ה-MFT עצמו, לפי ההיסט הלוגי בטבלה.
    /// משמש למעבר סדרתי באצוות על הרשומות.
    /// </summary>
    internal byte[] ReadExtentsForMft(long byteOffset, int count)
        => ReadExtents(_mftExtents, byteOffset, count);

    /// <summary>קריאה ישירה מהיסט על המחיצה — לשימוש סורק הרשומות היתומות.</summary>
    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    internal long VolumeLength => _reader.Length;

    public void Dispose()
    {
        if (_ownsReader) _reader.Dispose();
    }
}
