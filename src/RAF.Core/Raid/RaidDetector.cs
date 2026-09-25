using System.Buffers.Binary;
using RAF.Core.FileSystems.Btrfs;

namespace RAF.Core.Raid;

/// <summary>
/// זיהוי מבנה של מערך בלי כותרת — כמו של כרטיס RAID במחשב, שאינו כותב על הכוננים דבר
/// שהתוכנה יכולה לקרוא. מקבלים את הכוננים, ומנחשים: סוג, גודל רצועה, סידור וסדר הכוננים.
///
/// 1. מראה: כל הכוננים זהים. 2. זוגיות: XOR של כל הכוננים באותו מקום נותן אפסים.
/// 3. לכל ניחוש — מרכיבים את המערך "על הנייר" ובודקים את מערכת הקבצים שבתוכו מול סימנים
///    שמקומם ידוע מראש: ב-NTFS רשומות הקבצים ממוספרות ברצף, ועותק של מגזר האתחול יושב
///    בסוף המחיצה; ב-ext4 לכל רשומת קובץ יש טביעת אצבע, ועותקי הכותרת נושאים את מספרם.
///    רק הניחוש הנכון מיישר את כולם. הנתונים מתחילים בתחילת כל כונן (הכרטיס שומר את
///    ההגדרות שלו בסוף הכונן).
/// </summary>
internal static class RaidDetector
{
    /// <summary>ניחוש: סוג, סידור, רצועה, וסדר הכוננים (לכל מקום במערך — מספר הכונן ברשימה).</summary>
    internal sealed record Guess(int Level, int Layout, long Chunk, int[] Order, int Matches, int Mismatches, string FileSystem);

    internal static readonly long[] Chunks = { 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152 };

    /// <summary>
    /// הניחושים שעברו את הבדיקה, מהטוב לפחות טוב. ריק — אף ניחוש לא התיישב (אולי חסר כונן,
    /// או שמערכת הקבצים אינה NTFS או ext4). read — קריאה מכונן לפי מספרו ברשימה.
    /// </summary>
    internal static List<Guess> Detect(Func<int, long, int, byte[]?> read, long[] sizes, CancellationToken token = default)
    {
        int n = sizes.Length;
        var cache = new Dictionary<(int, long), byte[]>();
        byte[] Block(int disk, long block)
        {
            lock (cache)
            {
                if (cache.TryGetValue((disk, block), out var b)) return b;
                b = read(disk, block * 65536, 65536) ?? Array.Empty<byte>();
                if (cache.Count > 8192) cache.Clear();
                return cache[(disk, block)] = b;
            }
        }
        int ReadDisk(int disk, long offset, Span<byte> buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                long at = offset + done;
                var b = Block(disk, at / 65536);
                int within = (int)(at % 65536), take = Math.Min(buffer.Length - done, b.Length - within);
                if (take <= 0) break;
                b.AsSpan(within, take).CopyTo(buffer[done..]);
                done += take;
            }
            return done;
        }

        var results = new List<Guess>();
        long min = sizes.Min();
        if (Mirror(ReadDisk, n, min))
        {
            if (Evaluate(1, 0, 0, Enumerable.Range(0, n).ToArray(), sizes, ReadDisk) is { } g) results.Add(g);
            return results;
        }

        bool parity = n >= 3 && Parity(ReadDisk, n, min);
        var layouts = parity ? new[] { (5, 2), (5, 0), (5, 3), (5, 1), (4, 5), (5, 4) } : new[] { (0, 0) };
        int? head = HeadDisk(ReadDisk, n);

        foreach (var order in Permutations(n))
            foreach (var (level, layout) in layouts)
            {
                if (token.IsCancellationRequested) return results;
                // תחילת המערך (טבלת מחיצות או מגזר אתחול) — בכונן שהרצועה הראשונה שלו אצלו.
                if (head is { } h && order[FirstDataRole(level, layout, n)] != h) continue;
                foreach (long chunk in Chunks)
                    if (Evaluate(level, layout, chunk, order, sizes, ReadDisk) is { } g) results.Add(g);
            }
        return results.OrderByDescending(g => g.Matches - 10 * g.Mismatches).ThenBy(g => Math.Abs(Math.Log2(g.Chunk / 65536.0))).ToList();
    }

    private delegate int DiskRead(int disk, long offset, Span<byte> buffer);

    private static int FirstDataRole(int level, int layout, int n)
        => level == 0 ? 0 : new RaidArray(level, layout, 65536, n, 0, new long[n], new long[n], new bool[n]).Locate(0, 0).Data;

    /// <summary>הכונן שמתחיל בטבלת מחיצות או במגזר אתחול — שם יושבת תחילת המערך. null — לא חד משמעי.</summary>
    private static int? HeadDisk(DiskRead read, int n)
    {
        var heads = new List<int>();
        var buffer = new byte[2048];
        for (int d = 0; d < n; d++)
        {
            if (read(d, 0, buffer) < 2048) continue;
            bool boot = buffer[510] == 0x55 && buffer[511] == 0xAA;
            bool ext = buffer[1080] == 0x53 && buffer[1081] == 0xEF;
            if (boot || ext || buffer.AsSpan(0, 4).SequenceEqual("XFSB"u8)) heads.Add(d);
        }
        return heads.Count == 1 ? heads[0] : null;
    }

    /// <summary>דגימות לאורך הכוננים: כולם זהים (במקומות שיש בהם תוכן).</summary>
    private static bool Mirror(DiskRead read, int n, long size)
    {
        int same = 0, content = 0;
        var a = new byte[4096];
        var b = new byte[4096];
        for (int i = 0; i < 64; i++)
        {
            long at = size / 64 * i / 4096 * 4096;
            if (read(0, at, a) < 4096 || a.All(x => x == 0)) continue;
            content++;
            bool all = true;
            for (int d = 1; d < n && all; d++) all = read(d, at, b) == 4096 && a.AsSpan().SequenceEqual(b);
            if (all) same++;
        }
        return content >= 4 && same >= content * 0.9;
    }

    /// <summary>זוגיות: XOR של כל הכוננים באותו מקום הוא אפסים (במקומות שיש בהם תוכן).</summary>
    private static bool Parity(DiskRead read, int n, long size)
    {
        int zero = 0, content = 0;
        var x = new byte[4096];
        var b = new byte[4096];
        for (int i = 0; i < 64; i++)
        {
            long at = size / 64 * i / 4096 * 4096;
            Array.Clear(x);
            bool any = false, ok = true;
            for (int d = 0; d < n && ok; d++)
            {
                ok = read(d, at, b) == 4096;
                any |= b.Any(v => v != 0);
                for (int k = 0; k < 4096; k++) x[k] ^= b[k];
            }
            if (!ok || !any) continue;
            content++;
            if (x.All(v => v == 0)) zero++;
        }
        return content >= 4 && zero >= content * 0.9;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        return Permute(a, 0);

        static IEnumerable<int[]> Permute(int[] a, int k)
        {
            if (k == a.Length) { yield return (int[])a.Clone(); yield break; }
            for (int i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Permute(a, k + 1)) yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
    }

    /// <summary>הרכבה "על הנייר" ובדיקת מערכת הקבצים. null — לא התיישב.</summary>
    private static Guess? Evaluate(int level, int layout, long chunk, int[] order, long[] sizes, DiskRead read)
    {
        int n = order.Length;
        var array = new RaidArray(level, layout, chunk, n, 0, new long[n], order.Select(d => sizes[d]).ToArray(), Enumerable.Repeat(true, n).ToArray());
        byte[] Read(long offset, int length)
        {
            var buffer = new byte[length];
            int got = array.Read(offset, buffer, (role, at, b) => read(order[role], at, b));
            return got == length ? buffer : buffer[..Math.Max(0, got)];
        }

        long start = PartitionStart(Read);
        var boot = Read(start, 4096);
        if (boot.Length < 4096) return null;
        (int Match, int Miss, string Fs) score =
            boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8) ? Ntfs(Read, start, boot, array.Size)
            : boot[1080] == 0x53 && boot[1081] == 0xEF ? Ext(Read, start, boot)
            : (0, 0, "");
        // מספיק סימנים, וכמעט בלי סתירות.
        if (score.Match < 8 || score.Miss > score.Match / 20) return null;
        return new Guess(level, layout, chunk, order, score.Match, score.Miss, score.Fs);
    }

    /// <summary>תחילת המחיצה הראשונה: טבלת GPT או MBR, או 0 — מערכת קבצים ישירות על המערך.</summary>
    private static long PartitionStart(Func<long, int, byte[]> read)
    {
        var s = read(0, 1024);
        if (s.Length < 1024 || s[510] != 0x55 || s[511] != 0xAA) return 0;
        if (s.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) return 0;
        if (s.AsSpan(512, 8).SequenceEqual("EFI PART"u8))
        {
            long entries = (long)BinaryPrimitives.ReadUInt64LittleEndian(s.AsSpan(512 + 72)) * 512;
            var e = read(entries, 128);
            return e.Length == 128 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(32)) * 512 : 0;
        }
        for (int i = 0; i < 4; i++)
        {
            int at = 446 + i * 16;
            uint lba = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(at + 8));
            if (s[at + 4] != 0 && lba > 0) return lba * 512L;
        }
        return 0;
    }

    /// <summary>NTFS: רשומות הקבצים ממוספרות ברצף, ומגזר האתחול משוכפל בסוף המחיצה.</summary>
    private static (int, int, string) Ntfs(Func<long, int, byte[]> read, long start, byte[] boot, long arraySize)
    {
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
        int spc = boot[13] > 0x80 ? 1 << (256 - boot[13]) : boot[13];
        long cluster = (long)bps * spc;
        long mft = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x30)) * cluster;
        long sectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x28));
        sbyte raw = (sbyte)boot[0x40];
        int record = raw > 0 ? (int)(raw * cluster) : 1 << -raw;
        if (bps is < 512 or > 4096 || cluster == 0 || record is < 512 or > 65536) return (0, 0, "");

        sbyte rawIndex = (sbyte)boot[0x44];
        int indexBlock = rawIndex > 0 ? (int)(rawIndex * cluster) : 1 << -rawIndex;

        int match = 0, miss = 0, indexProbes = 0;
        var region = read(start + mft, Math.Min(4096 * record, 4 * 1024 * 1024));
        for (int i = 0; (i + 1) * record <= region.Length; i++)
        {
            var r = region.AsSpan(i * record, record).ToArray();
            if (!r.AsSpan(0, 4).SequenceEqual("FILE"u8)) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x2C)) != (uint)i) { miss++; continue; }
            match++;
            // בלוקי אינדקס של תיקייה (INDX) — פזורים בכונן, וכל אחד רושם את מספרו הסידורי בתיקייה.
            if (indexProbes < 256 && indexBlock is >= 512 and <= 65536 && Fixup(r, bps))
                foreach (var (vcn, lcn) in IndexBlocks(r, cluster, indexBlock))
                {
                    if (++indexProbes > 256) break;
                    var block = read(start + lcn * cluster, 64);
                    if (block.Length < 64 || !block.AsSpan(0, 4).SequenceEqual("INDX"u8)) { miss++; continue; }
                    if ((long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x10)) == vcn) match += 2; else miss++;
                }
        }
        // עותק מגזר האתחול בסקטור האחרון של המחיצה — רחוק מההתחלה, ולכן מבדיל היטב בין ניחושים.
        long backup = start + sectors * bps;
        if (backup + 512 <= arraySize)
        {
            var copy = read(backup, 512);
            if (copy.Length == 512 && copy.AsSpan(0, 512).SequenceEqual(boot.AsSpan(0, 512))) match += 4; else miss += 4;
        }
        return (match, miss, "NTFS");
    }

    /// <summary>תיקון הסקטורים של רשומה (שני הבתים האחרונים בכל סקטור הוחלפו בסימן ביקורת).</summary>
    private static bool Fixup(byte[] r, int sector)
    {
        int usa = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(4)), count = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(6));
        if (count < 2 || usa + count * 2 > r.Length || (count - 1) * sector > r.Length) return false;
        for (int i = 1; i < count; i++)
        {
            int at = i * sector - 2;
            r[at] = r[usa + i * 2];
            r[at + 1] = r[usa + i * 2 + 1];
        }
        return true;
    }

    /// <summary>
    /// מתכונת ההקצאה של אינדקס תיקייה (0xA0) ברשומה: לכל בלוק — המספר הסידורי שלו (VCN, כפי
    /// שנרשם בכותרת הבלוק) והאשכול שלו בכונן.
    /// </summary>
    private static IEnumerable<(long Vcn, long Lcn)> IndexBlocks(byte[] r, long cluster, int indexBlock)
    {
        int at = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x14));
        long perBlock = Math.Max(1, indexBlock / cluster);
        long vcnStep = indexBlock >= cluster ? perBlock : 1;
        while (at + 16 <= r.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at + 4));
            if (type == 0xFFFFFFFF || length < 16 || at + length > r.Length) yield break;
            if (type == 0xA0 && r[at + 8] == 1)
            {
                long vcn = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(at + 0x10));
                int run = at + BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(at + 0x20));
                long lcn = 0;
                while (run < at + length && r[run] != 0)
                {
                    int lenBytes = r[run] & 0xF, offBytes = r[run] >> 4;
                    if (lenBytes == 0 || run + 1 + lenBytes + offBytes > at + length) yield break;
                    long count = 0, delta = 0;
                    for (int i = 0; i < lenBytes; i++) count |= (long)r[run + 1 + i] << (8 * i);
                    for (int i = 0; i < offBytes; i++) delta |= (long)r[run + 1 + lenBytes + i] << (8 * i);
                    if (offBytes > 0 && (r[run + lenBytes + offBytes] & 0x80) != 0) delta -= 1L << (8 * offBytes);
                    run += 1 + lenBytes + offBytes;
                    if (offBytes == 0) { vcn += count; continue; }   // קטע דליל
                    lcn += delta;
                    for (long c = 0; c + perBlock <= count; c += perBlock)
                        yield return (vcn + c / perBlock * vcnStep, lcn + c);
                    vcn += count;
                }
            }
            at += length;
        }
    }

    /// <summary>ext4: טביעות האצבע של רשומות הקבצים בטבלה של הקבוצה הראשונה, ועותקי הכותרת.</summary>
    private static (int, int, string) Ext(Func<long, int, byte[]> read, long start, byte[] head)
    {
        var sb = head.AsSpan(1024, 1024);
        int blockSize = 1024 << (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
        long firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..]);
        long perGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[32..]);
        long inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]);
        int inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb[0x58..]);
        uint incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x60..]);
        uint roCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x64..]);
        long groups = (BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]) - firstData + perGroup - 1) / Math.Max(1, perGroup);
        if (blockSize > 65536 || perGroup == 0 || inodeSize < 128) return (0, 0, "");

        int match = 0, miss = 0;
        // עותקי הכותרת בקבוצות 1, 3, 5, 7, 9, 25, 27, 49...: חתימה, ומספר הקבוצה בתוכם.
        // (בתכונה "עותקים בשתי קבוצות בלבד" — sparse_super2 — הם במקום אחר, ומדלגים.)
        bool twoBackups = (BinaryPrimitives.ReadUInt32LittleEndian(sb[0x5C..]) & 0x200) != 0;
        foreach (long g in new long[] { 1, 3, 5, 7, 9, 25, 27, 49, 81, 125, 243 }.Where(g => g < groups && !twoBackups))
        {
            var copy = read(start + (g * perGroup + firstData) * blockSize, 1024);
            bool ok = copy.Length == 1024 && copy[56] == 0x53 && copy[57] == 0xEF && BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(0x5A)) == g;
            if (ok) match += 2; else miss += 2;
        }

        // טביעות אצבע של רשומות קבצים (metadata_csum).
        if ((roCompat & 0x400) != 0)
        {
            var desc = read(start + (firstData + 1) * blockSize, 64);
            if (desc.Length < 64) return (match, miss, "ext4");
            long table = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(8));
            int descSize = (incompat & 0x80) != 0 ? Math.Max(32, (int)BinaryPrimitives.ReadUInt16LittleEndian(sb[0xFE..])) : 32;
            if (descSize >= 64) table |= (long)BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(0x28)) << 32;
            uint seed = (incompat & 0x2000) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(sb[0x270..]) : Crc32C.Update(0xFFFFFFFF, sb.Slice(0x68, 16));

            int count = (int)Math.Min(inodesPerGroup, 2048);
            var inodes = read(start + table * blockSize, count * inodeSize);
            for (int i = 0; (i + 1) * inodeSize <= inodes.Length; i++)
            {
                var raw = inodes.AsSpan(i * inodeSize, inodeSize).ToArray();
                if (raw.All(b => b == 0)) continue;
                if (InodeChecksumOk(raw, (uint)(i + 1), seed)) match++; else miss++;
            }
        }
        return (match, miss, "ext4");
    }

    private static bool InodeChecksumOk(byte[] raw, uint number, uint seed)
    {
        ushort lo = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x7C));
        bool hasHi = raw.Length > 128 && 128 + BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x80)) >= 0x84;
        ushort hi = hasHi ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x82)) : (ushort)0;
        raw[0x7C] = raw[0x7D] = 0;
        if (hasHi) raw[0x82] = raw[0x83] = 0;
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, number);
        uint crc = Crc32C.Update(seed, le);
        crc = Crc32C.Update(crc, raw.AsSpan(0x64, 4));   // מספר הדור של הרשומה
        crc = Crc32C.Update(crc, raw);
        return (crc & 0xFFFF) == lo && (!hasHi || crc >> 16 == hi);
    }
}
