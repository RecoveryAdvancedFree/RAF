using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using RAF.Core.Signatures;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// מחיצת NTFS ששני מגזרי האתחול שלה אבדו, כפי שחושבה מחדש מתוך רשומות הקבצים שלה.
/// BootSector — מגזר אתחול שנבנה מהנתונים שחושבו, לקריאה בזיכרון בלבד.
/// </summary>
public sealed record RebuiltNtfs(
    long Offset, long Size, int ClusterSize, long MftCluster, int RecordSize,
    int Records, int Confirmed, byte[] BootSector);

/// <summary>
/// בנייה מחדש של מחיצת NTFS בלי מגזר אתחול — כשגם העותק שבסוף המחיצה אבד.
///
/// רשומות הקבצים (FILE) שורדות גם אז, ובכל אחת כתוב איפה הקובץ: "אשכול 51,200".
/// המספר יחסי לתחילת המחיצה ובאשכולות שגודלם לא ידוע. הסריקה אוספת את הרשומות,
/// ובמקביל את המקומות שבהם מתחילים קבצים לפי החתימה שלהם (JPEG, PDF…). השערה
/// "המחיצה מתחילה ב-P והאשכול בגודל C" נכונה אם קבצי ה-JPEG שברשומות אכן מתחילים
/// ב-P + אשכול × C — ומספיק שעשרות רשומות מסכימות כדי שזה לא יהיה צירוף מקרים.
/// אישור נוסף בלי תלות בקבצים: רשומה 0 ($MFT) ורשומה 1 ($MFTMirr) מצביעות על
/// מקומות שבהם חייבות לשבת רשומות, ואת מקומן בפועל הסריקה כבר ראתה.
///
/// מהנתונים שחושבו נבנה מגזר אתחול, והמחיצה נקראת דרכו כרגיל — עם שמות ותיקיות.
/// על הכונן לא נכתב דבר.
/// </summary>
internal sealed class NtfsRebuild
{
    /// <summary>רשומת FILE שנמצאה בסריקה — רק מה שנחוץ לחישוב.</summary>
    private readonly record struct Hit(long Position, long Number, int RecordSize, long DataCluster, string? Extension, string? SystemName, long DataSize, long FirstRun);

    private const int MaxHits = 4_000_000;
    private const int MaxSignatures = 4_000_000;

    private readonly int _sectorSize;
    private readonly List<Hit> _hits = new();
    private readonly Dictionary<string, HashSet<long>> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private int _signatureCount;

    internal NtfsRebuild(int sectorSize) => _sectorSize = sectorSize;

    internal int RecordsSeen => _hits.Count;

    /// <summary>בדיקת קטע רציף מהכונן, שמתחיל בהיסט at. נקרא מהמעבר של חיפוש המחיצות.</summary>
    internal void Collect(ReadOnlySpan<byte> data, long at)
    {
        for (int s = 0; s + _sectorSize <= data.Length; s += _sectorSize)
        {
            var span = data[s..];
            if (span[0] == (byte)'F' && span[1] == (byte)'I' && span[2] == (byte)'L' && span[3] == (byte)'E')
            {
                CollectRecord(span, at + s);
                continue;
            }

            if (_signatureCount >= MaxSignatures) continue;
            var signature = FileSignatures.Identify(span[..Math.Min(64, span.Length)]);
            if (signature is null) continue;
            foreach (string ext in signature.Extensions)
            {
                if (!_signatures.TryGetValue(ext, out var set)) _signatures[ext] = set = new HashSet<long>();
                set.Add(at + s);
            }
            _signatureCount++;
        }
    }

    private void CollectRecord(ReadOnlySpan<byte> span, long position)
    {
        if (_hits.Count >= MaxHits || span.Length < 48) return;
        int allocated = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
        if (allocated is < 512 or > 65536 || !BitOperations.IsPow2(allocated) || allocated > span.Length) return;

        var record = MftRecord.Parse(span[..allocated].ToArray(), _sectorSize);
        if (record is null || record.BaseRecord != 0) return;

        var name = record.PreferredName();
        var data = record.PrimaryData();
        long cluster = -1, size = 0;
        if (data is { IsNonResident: true } && data.StartVcn == 0)
        {
            var first = data.Extents.FirstOrDefault(e => !e.IsSparse);
            if (first.ClusterCount > 0) cluster = first.StartCluster;
            size = data.RealSize;
        }

        string? ext = null;
        if (name is { } n)
        {
            int dot = n.Name.LastIndexOf('.');
            if (dot > 0 && dot < n.Name.Length - 1) ext = string.Intern(n.Name[(dot + 1)..].ToLowerInvariant());
        }

        // רשומות המערכת שהחישוב נשען עליהן: $MFT (0), $MFTMirr (1) ו-$Bitmap (6).
        string? system = record.RecordNumber is 0 or 1 or 6 && name?.Name is "$MFT" or "$MFTMirr" or "$Bitmap"
            ? name.Value.Name : null;
        long firstRun = system == "$MFT" && data is { IsNonResident: true } && data.Extents.Count > 0
            ? data.Extents[0].StartCluster : -1;

        _hits.Add(new Hit(position, record.RecordNumber, allocated, cluster, ext, system, size, firstRun));
    }

    /// <summary>
    /// החישוב עצמו, אחרי שכל הכונן נסרק. מחזיר את המחיצות שנמצאו, מהבטוחה ביותר.
    /// known — מחיצות שכבר ידועות (בטבלה, או לפי מגזר אתחול); הן אינן "אבודות".
    /// </summary>
    internal List<RebuiltNtfs> Infer(long diskLength, IReadOnlyCollection<long> known)
    {
        var result = new List<RebuiltNtfs>();
        if (_hits.Count < 8) return result;

        var recordAt = new Dictionary<long, Hit>(_hits.Count);
        foreach (var h in _hits) recordAt.TryAdd(h.Position, h);

        // המחיצות הידועות מתחרות גם הן: כך השערה שהיא רק הד של מחיצה קיימת נפסלת (ראו למטה).
        var candidates = Candidates();
        foreach (long k in known)
            foreach (int c in ClusterSizes)
                if (c >= _sectorSize) candidates.Add((k, c));

        var scored = new List<(long P, int C, int Score, int Matches, HashSet<int> Explained)>();
        foreach (var (p, c) in candidates)
        {
            var (score, matches, eligible, structure, explained) = Score(p, c, recordAt);
            // הקבצים מכריעים: לפחות שלוש רשומות, ולפחות 30% מאלו שאפשר לבדוק. מבנה הטבלה
            // לבדו ($MFT ו-$MFTMirr) מספיק רק כשאין במחיצה אף קובץ שסוגו מזוהה — כי עותק
            // הגיבוי של רשומה 0 יכול להתאים במקרה גם להשערה שגויה.
            bool byFiles = matches >= 3 && matches * 10 >= eligible * 3;
            bool byStructure = eligible == 0 && structure;
            if (byFiles || byStructure) scored.Add((p, c, score, matches, explained));
        }

        // השערה אחת לכל תחילת מחיצה — זו עם גודל האשכול שמסביר הכי הרבה.
        var best = scored.GroupBy(s => s.P).Select(g => g.MaxBy(s => s.Score)).OrderByDescending(s => s.Score).ToList();

        // הד: כשקבצים קטנים בגודל זהה שמורים ברצף, הזזה של כל המחיצה בכמה אשכולות מתאימה
        // כל רשומה לקובץ הבא בתור — ונראית כמעט טוב כמו האמת. השערה שרוב הרשומות שהיא
        // מסבירה כבר הוסברו על ידי השערה חזקה ממנה (או על ידי מחיצה ידועה) — נפסלת.
        var accepted = new List<(long P, int C, int Score, int Matches, HashSet<int> Explained)>();
        foreach (var s in best)
        {
            if (accepted.Any(a => Math.Abs(a.P - s.P) < (long)a.C * 64)) continue;
            if (accepted.Any(a => Echoes(s.Explained, a.Explained))) continue;
            accepted.Add(s);
        }
        accepted.RemoveAll(a => known.Contains(a.P));

        foreach (var s in accepted.OrderBy(a => a.P))
        {
            long end = accepted.Where(a => a.P > s.P).Select(a => a.P).DefaultIfEmpty(diskLength).Min();
            end = Math.Min(end, known.Where(k => k > s.P).DefaultIfEmpty(diskLength).Min());
            if (Build(s.P, s.C, end, s.Matches) is { } rebuilt) result.Add(rebuilt);
        }
        return result;
    }

    private static readonly int[] ClusterSizes = { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152 };

    /// <summary>
    /// השערות מועמדות. שני מקורות: רשומה 0, שמיקומה ידוע ובה כתוב האשכול של עצמה;
    /// וזוגות של רשומה וקובץ מאותו סוג — מדגם, כי כל הזוגות היו מיליארדים.
    /// </summary>
    private HashSet<(long P, int C)> Candidates()
    {
        var set = new HashSet<(long, int)>();

        foreach (var h in _hits.Where(h => h.SystemName == "$MFT" && h.FirstRun > 0))
            foreach (int c in ClusterSizes)
                if (c >= _sectorSize) Add(h.Position - h.FirstRun * c, c);

        // מדגם: הרשומות מהסוגים הנדירים ביותר קודם — בהם זוג מקרי נדיר.
        var sample = _hits
            .Where(h => h.DataCluster > 0 && h.Extension is not null && _signatures.ContainsKey(h.Extension))
            .GroupBy(h => h.Extension!)
            .OrderBy(g => _signatures[g.Key].Count)
            .SelectMany(g => g.Take(20))
            .Take(120)
            .ToList();

        var votes = new Dictionary<(long, int), int>();
        foreach (var h in sample)
        {
            var hits = _signatures[h.Extension!];
            int taken = 0;
            foreach (long at in hits)
            {
                if (++taken > 4000) break;
                foreach (int c in ClusterSizes)
                {
                    if (c < _sectorSize) continue;
                    long p = at - h.DataCluster * c;
                    if (p < 0 || p % _sectorSize != 0) continue;
                    var key = (p, c);
                    votes[key] = votes.GetValueOrDefault(key) + 1;
                }
            }
        }
        foreach (var (key, count) in votes.OrderByDescending(v => v.Value).Take(40))
            if (count >= 2) set.Add(key);

        return set;

        void Add(long p, int c)
        {
            if (p >= 0 && p % _sectorSize == 0) set.Add((p, c));
        }
    }

    /// <summary>
    /// ציון להשערה: כמה רשומות קבצים מצביעות על מקום שבו באמת מתחיל קובץ מאותו סוג,
    /// ועוד אישור חזק כשרשומות $MFT ו-$MFTMirr מצביעות על רשומות שנמצאו במקומן.
    /// </summary>
    private (int Score, int Matches, int Eligible, bool Structure, HashSet<int> Explained) Score(
        long p, int c, Dictionary<long, Hit> recordAt)
    {
        int matches = 0, eligible = 0;
        var explained = new HashSet<int>();
        for (int i = 0; i < _hits.Count; i++)
        {
            var h = _hits[i];
            if (h.Position < p || h.DataCluster <= 0 || h.Extension is null) continue;
            if (!_signatures.TryGetValue(h.Extension, out var set)) continue;
            eligible++;
            if (set.Contains(p + h.DataCluster * c)) { matches++; explained.Add(i); }
        }

        int score = matches;
        long mft = -1, mirror = -1;
        foreach (var h in _hits)
        {
            if (h.Position < p) continue;
            if (h.SystemName == "$MFT" && h.FirstRun > 0 && p + h.FirstRun * c == h.Position) mft = h.Position;
            if (h.SystemName == "$MFTMirr" && h.DataCluster > 0 &&
                recordAt.TryGetValue(p + h.DataCluster * c, out var copy) && copy.Number == 0) mirror = copy.Position;
        }
        if (mft >= 0) score += 10;
        if (mirror >= 0) score += 10;
        return (score, matches, eligible, mft >= 0 && mirror >= 0 && mft != mirror, explained);
    }

    /// <summary>רוב הרשומות ש-candidate מסבירה כבר מוסברות ב-stronger.</summary>
    private static bool Echoes(HashSet<int> candidate, HashSet<int> stronger)
    {
        if (candidate.Count == 0 || stronger.Count == 0) return false;
        int shared = candidate.Count(stronger.Contains);
        return shared * 2 > candidate.Count;
    }

    private RebuiltNtfs? Build(long p, int c, long end, int matches)
    {
        var mine = _hits.Where(h => h.Position >= p && h.Position < end).ToList();
        if (mine.Count == 0) return null;
        int recordSize = mine.GroupBy(h => h.RecordSize).MaxBy(g => g.Count())!.Key;

        // תחילת ה-MFT: מרשומה 0 שיושבת במקום שעליו היא מצביעה; אחרת — מהרשומה בעלת
        // המספר הנמוך ביותר, בהנחה שתחילת הטבלה רציפה (כך היא נוצרת בפירמוט).
        long mft = mine.Where(h => h.SystemName == "$MFT" && h.FirstRun > 0 && p + h.FirstRun * c == h.Position)
                       .Select(h => h.FirstRun).FirstOrDefault(-1);
        if (mft < 0)
        {
            var lowest = mine.MinBy(h => h.Number);
            long offset = lowest.Position - lowest.Number * recordSize - p;
            mft = offset > 0 && offset % c == 0 ? offset / c : (lowest.Position - p) / c;
        }
        if (mft <= 0) mft = 1;

        long mirror = mine.Where(h => h.SystemName == "$MFTMirr" && h.DataCluster > 0).Select(h => h.DataCluster).FirstOrDefault(mft);

        // הגודל: מ-$Bitmap (סיבית לכל אשכול); אחרת — עד המחיצה הבאה או סוף הכונן.
        long size = end - p;
        var bitmap = mine.Where(h => h.SystemName == "$Bitmap" && h.DataSize > 0).Select(h => h.DataSize).FirstOrDefault();
        if (bitmap > 0) size = Math.Min(size, bitmap * 8 * c);
        size = size / _sectorSize * _sectorSize;
        if (size <= (long)c * 16) return null;

        return new RebuiltNtfs(p, size, c, mft, recordSize, mine.Count, matches, BootSector(size, c, mft, mirror, recordSize));
    }

    /// <summary>מגזר אתחול NTFS מהנתונים שחושבו — מה שנחוץ כדי לקרוא את המחיצה.</summary>
    private byte[] BootSector(long size, int clusterSize, long mft, long mirror, int recordSize)
    {
        byte[] s = new byte[_sectorSize];
        s[0] = 0xEB; s[1] = 0x52; s[2] = 0x90;
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(s, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(11), (ushort)_sectorSize);
        int perCluster = clusterSize / _sectorSize;
        s[13] = perCluster <= 128 ? (byte)perCluster : (byte)(256 - BitOperations.Log2((uint)perCluster));
        s[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(24), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(26), 255);
        // הסקטור האחרון שמור לעותק מגזר האתחול, ואינו נספר.
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(40), size / _sectorSize - 1);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(48), mft);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(56), mirror);
        s[64] = SizeField(recordSize, clusterSize);
        s[68] = SizeField(4096, clusterSize);
        s[510] = 0x55; s[511] = 0xAA;
        return s;

        // גודל שקטן מאשכול נכתב כחזקה שלילית של 2; אחרת — במספר אשכולות.
        static byte SizeField(int bytes, int cluster)
            => bytes >= cluster ? (byte)(bytes / cluster) : unchecked((byte)(sbyte)-BitOperations.Log2((uint)bytes));
    }
}
