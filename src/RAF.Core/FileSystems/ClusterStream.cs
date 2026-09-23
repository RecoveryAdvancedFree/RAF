using RAF.Core.Model;

using RAF.Core.FileSystems.Ntfs;

namespace RAF.Core.FileSystems;

/// <summary>
/// תוצאת חילוץ זרם. מבדילה בין בתים שנקראו בפועל לבין בתים שהושלמו באפסים
/// בגלל קריאה שנכשלה — הבחנה קריטית, אחרת קובץ ריק נראה כשחזור מוצלח.
/// </summary>
internal readonly record struct CopyOutcome(long BytesWritten, long UnreadableBytes, bool SawContent);

/// <summary>
/// חילוץ תוכן של זרם נתונים המתואר כרצף אשכולות.
///
/// המנגנון משותף לכל מערכות הקבצים: מקבל רשימת מקטעים וגודל אמיתי,
/// ומרכיב מהם את הקובץ. הדחיסה היא ייחודית ל-NTFS, ומופעלת רק כאשר
/// גודל יחידת הדחיסה שונה מאפס.
/// </summary>
internal sealed class ClusterStream
{
    private readonly IClusterVolume _volume;
    private readonly IReadOnlyList<DataExtent> _extents;
    private readonly int _compressionUnitClusters;
    private readonly long _realSize;

    /// <summary>מיפוי מספר אשכול לוגי בקובץ למיקומו הפיזי על המחיצה.</summary>
    private readonly List<(long StartVcn, DataExtent Extent)> _map = new();
    private readonly long _totalVcn;

    /// <summary>בנייה מתכונת NTFS, עם פרטי הדחיסה שלה.</summary>
    internal ClusterStream(IClusterVolume volume, NtfsAttribute attribute)
        : this(volume, attribute.Extents, attribute.RealSize,
               attribute.IsCompressed ? attribute.CompressionUnitClusters : 0)
    {
    }

    internal ClusterStream(
        IClusterVolume volume, IReadOnlyList<DataExtent> extents, long realSize, int compressionUnitClusters)
    {
        _volume = volume;
        _extents = extents;
        _realSize = realSize;
        _compressionUnitClusters = compressionUnitClusters;

        long vcn = 0;
        foreach (var extent in _extents)
        {
            _map.Add((vcn, extent));
            vcn += extent.ClusterCount;
        }
        _totalVcn = vcn;
    }

    /// <summary>מציאת האשכול הפיזי המתאים לאשכול לוגי. null פירושו מקטע דליל.</summary>
    private long? PhysicalCluster(long vcn)
    {
        if (vcn < 0 || vcn >= _totalVcn) return null;

        foreach (var (startVcn, extent) in _map)
        {
            if (vcn < startVcn || vcn >= startVcn + extent.ClusterCount) continue;
            return extent.IsSparse ? null : extent.StartCluster + (vcn - startVcn);
        }

        return null;
    }

    // ------------------------------------------------------------ אימות תוכן

    /// <summary>
    /// דגימת התוכן בפועל מהדיסק, לבדיקה האם הנתונים עדיין קיימים.
    ///
    /// זה ההבדל בין מטא-דאטה לבין נתונים: בכונן SSD עם TRIM רשומת ה-MFT
    /// שורדת אחרי מחיקה, אך הבקר מוחק פיזית את הבלוקים והקריאה מהם
    /// מחזירה אפסים. ללא דגימה בפועל, קובץ כזה נראה שלם ואינו כזה.
    /// </summary>
    internal ContentCheck SampleContent(int samples = 3, int sampleBytes = 512)
    {
        if (_realSize <= 0) return ContentCheck.NotChecked;

        // נדגמים רק אשכולות אמיתיים; מקטע דליל הוא אפסים לגיטימיים.
        var realVcns = new List<long>();
        foreach (var (startVcn, extent) in _map)
        {
            if (extent.IsSparse) continue;
            realVcns.Add(startVcn);
            if (extent.ClusterCount > 1) realVcns.Add(startVcn + extent.ClusterCount - 1);
        }

        if (realVcns.Count == 0) return ContentCheck.NotChecked;

        // פריסת הדגימות על פני הקובץ: התחלה, אמצע וסוף.
        var picks = new List<long>();
        for (int i = 0; i < samples; i++)
        {
            int index = realVcns.Count == 1 ? 0 : i * (realVcns.Count - 1) / Math.Max(1, samples - 1);
            long vcn = realVcns[Math.Min(index, realVcns.Count - 1)];
            if (!picks.Contains(vcn)) picks.Add(vcn);
        }

        bool anyRead = false;
        Span<byte> buffer = stackalloc byte[sampleBytes];

        foreach (long vcn in picks)
        {
            long? lcn = PhysicalCluster(vcn);
            if (lcn is null) continue;

            buffer.Clear();
            int read = _volume.ReadRaw(_volume.ClusterToOffset(lcn.Value), buffer);
            if (read <= 0) continue;

            anyRead = true;
            foreach (byte b in buffer[..read])
                if (b != 0) return ContentCheck.HasData;
        }

        return anyRead ? ContentCheck.Empty : ContentCheck.Unreadable;
    }

    // ------------------------------------------------------------ חילוץ

    /// <summary>כתיבת תוכן הזרם ליעד, עם דיווח מלא על מה שנקרא ומה שלא.</summary>
    internal CopyOutcome CopyTo(Stream output, CancellationToken token)
        => _compressionUnitClusters > 0
            ? CopyCompressed(output, token)
            : CopyPlain(output, token);

    /// <summary>חילוץ זרם רגיל: העתקת האשכולות ברצף עד לגודל האמיתי.</summary>
    private CopyOutcome CopyPlain(Stream output, CancellationToken token)
    {
        int clusterSize = _volume.BytesPerCluster;
        long remaining = _realSize;
        long written = 0, unreadable = 0;
        bool sawContent = false;

        // קריאה בבלוקים במקום אשכול-אשכול, כדי לצמצם פניות לדיסק.
        const int targetBlock = 1 * 1024 * 1024;
        int clustersPerBlock = Math.Max(1, targetBlock / clusterSize);

        foreach (var extent in _extents)
        {
            if (token.IsCancellationRequested || remaining <= 0) break;

            for (long done = 0; done < extent.ClusterCount && remaining > 0; done += clustersPerBlock)
            {
                if (token.IsCancellationRequested) break;

                int take = (int)Math.Min(clustersPerBlock, extent.ClusterCount - done);
                int bytes = (int)Math.Min((long)take * clusterSize, remaining);

                if (extent.IsSparse)
                {
                    // מקטע דליל מייצג אפסים שמעולם לא נכתבו לדיסק — זה תקין.
                    WriteZeros(output, bytes);
                }
                else
                {
                    long offset = _volume.ClusterToOffset(extent.StartCluster + done);
                    byte[] buffer = new byte[bytes];
                    int read = _volume.ReadRaw(offset, buffer);

                    if (read < bytes)
                    {
                        // סקטור פגום או אזור בלתי קריא: משלימים באפסים כדי לשמור
                        // על יישור שאר הקובץ, אך סופרים זאת כנתונים חסרים.
                        int missing = bytes - Math.Max(0, read);
                        Array.Clear(buffer, Math.Max(0, read), missing);
                        unreadable += missing;
                    }

                    if (!sawContent)
                        for (int i = 0; i < bytes; i++)
                            if (buffer[i] != 0) { sawContent = true; break; }

                    output.Write(buffer, 0, bytes);
                }

                written += bytes;
                remaining -= bytes;
            }
        }

        return new CopyOutcome(written, unreadable, sawContent);
    }

    /// <summary>
    /// חילוץ זרם דחוס. NTFS דוחס ביחידות קבועות, ויחידה שנדחסה בהצלחה
    /// תופסת פחות אשכולות מהמלאי — ההפרש מיוצג כמקטע דליל.
    /// </summary>
    private CopyOutcome CopyCompressed(Stream output, CancellationToken token)
    {
        int clusterSize = _volume.BytesPerCluster;
        int unitClusters = _compressionUnitClusters;
        int unitBytes = unitClusters * clusterSize;

        long remaining = _realSize;
        long written = 0, unreadable = 0;
        bool sawContent = false;

        for (long vcn = 0; vcn < _totalVcn && remaining > 0; vcn += unitClusters)
        {
            if (token.IsCancellationRequested) break;

            int allocated = 0;
            for (int i = 0; i < unitClusters; i++)
                if (PhysicalCluster(vcn + i) is not null) allocated++;

            int emit = (int)Math.Min(unitBytes, remaining);

            if (allocated == 0)
            {
                // יחידה דלילה לחלוטין — אזור של אפסים בקובץ.
                WriteZeros(output, emit);
            }
            else
            {
                byte[] plain;

                if (allocated == unitClusters)
                {
                    // יחידה מלאה אינה דחוסה: NTFS שומר אותה כמות שהיא.
                    var raw = ReadUnit(vcn, unitClusters, unitBytes);
                    unreadable += raw.Missing;
                    plain = raw.Data;
                }
                else
                {
                    // יחידה דחוסה: האשכולות שהוקצו מכילים את הנתונים הדחוסים.
                    var raw = ReadUnit(vcn, allocated, allocated * clusterSize);
                    unreadable += raw.Missing;
                    plain = Lznt1.Decompress(raw.Data, unitBytes);
                }

                int copy = Math.Min(emit, plain.Length);

                if (!sawContent)
                    for (int i = 0; i < copy; i++)
                        if (plain[i] != 0) { sawContent = true; break; }

                output.Write(plain, 0, copy);
                if (copy < emit) WriteZeros(output, emit - copy);
            }

            written += emit;
            remaining -= emit;
        }

        return new CopyOutcome(written, unreadable, sawContent);
    }

    /// <summary>קריאת אשכולות רצופים לוגית, גם כשהם מפוזרים פיזית.</summary>
    private (byte[] Data, long Missing) ReadUnit(long startVcn, int clusterCount, int byteCount)
    {
        int clusterSize = _volume.BytesPerCluster;
        byte[] buffer = new byte[byteCount];
        int written = 0;
        long missing = 0;

        for (int i = 0; i < clusterCount && written < byteCount; i++)
        {
            long? lcn = PhysicalCluster(startVcn + i);
            int take = Math.Min(clusterSize, byteCount - written);

            if (lcn is null)
            {
                // אשכול דליל בתוך יחידה נחשב לאפסים; המאגר כבר מאותחל.
                written += take;
                continue;
            }

            int read = _volume.ReadRaw(_volume.ClusterToOffset(lcn.Value), buffer.AsSpan(written, take));
            if (read < take) missing += take - Math.Max(0, read);

            written += take;
        }

        return (buffer, missing);
    }

    private static void WriteZeros(Stream output, int count)
    {
        byte[] zeros = new byte[Math.Min(Math.Max(count, 1), 64 * 1024)];
        int remaining = count;

        while (remaining > 0)
        {
            int take = Math.Min(remaining, zeros.Length);
            output.Write(zeros, 0, take);
            remaining -= take;
        }
    }
}
