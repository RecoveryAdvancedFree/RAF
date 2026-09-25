using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RAF.Core.Native;

/// <summary>
/// תמונת דיסק בפורמט E01 — הפורמט של כלי החקירה (EnCase, FTK Imager, ewfacquire).
///
/// הכונן נשמר בחלקים ("chunks") של 32KB בדרך כלל, כל חלק דחוס או לא, וטבלאות
/// מפרטות היכן כל חלק נמצא. תמונה גדולה מתחלקת לכמה קבצים — E01, E02… E99, EAA…
/// והחלקים ממוספרים ברצף על פני כולם. כאן מתרגמים היסט בכונן לחלק, קוראים ופורסים
/// אותו; חלקים שנקראו לאחרונה נשמרים, כי הסריקה קוראת ברצף.
///
/// הכלי שיצר את התמונה שומר בה גם טביעת אצבע (MD5) של הכונן כולו — Md5 — וכך
/// אפשר לבדוק שכל בית נקרא נכון.
/// </summary>
internal sealed class EwfImage
{
    private static readonly byte[] Signature = [(byte)'E', (byte)'V', (byte)'F', 0x09, 0x0D, 0x0A, 0xFF, 0x00];
    private static readonly byte[] Version2 = [(byte)'E', (byte)'V', (byte)'F', (byte)'2', 0x0D, 0x0A, 0x81, 0x00];

    private const int CacheChunks = 64;

    private readonly List<FileStream> _segments;
    private readonly List<(int Segment, long Offset, long End, bool Compressed)> _chunks;
    private readonly int _chunkSize;
    private readonly Dictionary<long, byte[]> _cache = new();
    private readonly LinkedList<long> _order = new();
    private readonly object _gate = new();

    public long Size { get; }
    public int SectorSize { get; }

    /// <summary>טביעת האצבע של הכונן המקורי, כפי שהכלי שיצר את התמונה חישב. null — לא נשמרה.</summary>
    public byte[]? Md5 { get; }

    private EwfImage(List<FileStream> segments, List<(int, long, long, bool)> chunks, int chunkSize,
        long size, int sectorSize, byte[]? md5)
    {
        _segments = segments;
        _chunks = chunks;
        _chunkSize = chunkSize;
        Size = size;
        SectorSize = sectorSize;
        Md5 = md5;
    }

    internal static bool IsEwf(ReadOnlySpan<byte> start) => start.StartsWith(Signature);
    internal static bool IsEwf2(ReadOnlySpan<byte> start) => start.StartsWith(Version2);

    /// <summary>שם הקובץ של חלק מספר n (1 = הקובץ הראשון): E01…E99, ואז EAA…EZZ, FAA…</summary>
    internal static string SegmentPath(string first, int n)
    {
        string ext = Path.GetExtension(first);
        bool lower = ext.Length > 1 && char.IsLower(ext[1]);
        string suffix;
        if (n <= 99) suffix = $"{(char)ext[1]}{n:D2}";
        else
        {
            int i = n - 100;
            suffix = $"{(char)(char.ToUpperInvariant(ext[1]) + i / 676)}{(char)('A' + i / 26 % 26)}{(char)('A' + i % 26)}";
            if (lower) suffix = suffix.ToLowerInvariant();
        }
        return Path.ChangeExtension(first, suffix);
    }

    public static EwfImage Open(string path)
    {
        var segments = new List<FileStream>();
        var chunks = new List<(int, long, long, bool)>();
        long sectors = 0;
        int sectorsPerChunk = 0, bytesPerSector = 0;
        long expectedChunks = 0;
        byte[]? md5 = null;

        try
        {
            for (int n = 1; ; n++)
            {
                string file = n == 1 ? path : SegmentPath(path, n);
                if (!File.Exists(file))
                    throw new InvalidOperationException(
                        L.T("חסר קובץ של התמונה: \"{0}\". כל קובצי התמונה (E01, E02 וכן הלאה) צריכים להיות באותה תיקייה.", Path.GetFileName(file)));

                var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
                segments.Add(stream);
                int segment = segments.Count - 1;

                byte[] head = ReadAt(stream, 0, 13);
                if (head.Length < 13 || !IsEwf(head))
                    throw new InvalidDataException(L.T("הקובץ \"{0}\" אינו חלק של תמונת E01, או שתחילתו פגומה.", Path.GetFileName(file)));

                bool last = false;
                long at = 13;
                for (int guard = 0; guard < 100_000; guard++)
                {
                    byte[] d = ReadAt(stream, at, 76);
                    if (d.Length < 76) { last = true; break; }

                    string type = Encoding.ASCII.GetString(d, 0, 16).TrimEnd('\0');
                    long next = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16));
                    long size = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(24));
                    long data = at + 76;

                    switch (type)
                    {
                        case "volume" or "disk" when sectorsPerChunk == 0:
                        {
                            byte[] v = ReadAt(stream, data, 24);
                            expectedChunks = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(4));
                            sectorsPerChunk = (int)BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(8));
                            bytesPerSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(12));
                            sectors = BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(16));
                            break;
                        }
                        case "table":
                            ReadTable(stream, segment, data, at + size, chunks);
                            break;
                        case "hash":
                            md5 = ReadAt(stream, data, 16);
                            break;
                        case "digest":
                            md5 ??= ReadAt(stream, data, 16);
                            break;
                        case "done":
                            last = true;
                            break;
                    }

                    if (last || type == "next" || next <= at) break;
                    at = next;
                }

                if (last) break;
            }

            int chunkSize = sectorsPerChunk * bytesPerSector;
            if (chunkSize is <= 0 or > 64 * 1024 * 1024 || bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectors <= 0)
                throw new InvalidDataException(L.T("המידע על הכונן שבתוך תמונת ה-E01 פגום."));

            long needed = (sectors * bytesPerSector + chunkSize - 1) / chunkSize;
            if (chunks.Count < needed)
                throw new InvalidDataException(
                    L.T("בתמונה חסרים חלקים: נמצאו {0} מתוך {1}. ייתכן שחסר אחד מקובצי התמונה, או שהתמונה לא הושלמה.", chunks.Count.ToString("N0"), needed.ToString("N0")));

            FixEnds(chunks, segments, chunkSize);
            return new EwfImage(segments, chunks, chunkSize, sectors * bytesPerSector, bytesPerSector,
                md5 is { Length: 16 } && md5.Any(b => b != 0) ? md5 : null);
        }
        catch
        {
            foreach (var s in segments) s.Dispose();
            throw;
        }
    }

    /// <summary>
    /// טבלת חלקים: מספר רשומות, "היסט בסיס" (בגרסאות החדשות; בישנות אפס), ולכל חלק —
    /// היסט של 31 ביט, והביט העליון מציין שהחלק דחוס.
    /// </summary>
    private static void ReadTable(FileStream stream, int segment, long data, long sectionEnd, List<(int, long, long, bool)> chunks)
    {
        byte[] header = ReadAt(stream, data, 24);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        long baseOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        if (count is <= 0 or > 1_000_000) return;

        byte[] entries = ReadAt(stream, data + 24, count * 4);
        for (int i = 0; i < count && i * 4 + 4 <= entries.Length; i++)
        {
            uint e = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 4));
            chunks.Add((segment, baseOffset + (e & 0x7FFFFFFF), -1, (e & 0x80000000) != 0));
        }
    }

    /// <summary>
    /// סוף כל חלק: תחילת החלק הבא באותו קובץ. לחלק האחרון בכל קובץ אין "הבא" — הוא
    /// מוגבל בגודל חלק פרוס ועוד תוספת קטנה (חלק דחוס לא יכול להיות גדול בהרבה).
    /// </summary>
    private static void FixEnds(List<(int Segment, long Offset, long End, bool Compressed)> chunks, List<FileStream> segments, int chunkSize)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            long end = i + 1 < chunks.Count && chunks[i + 1].Segment == c.Segment && chunks[i + 1].Offset > c.Offset
                ? chunks[i + 1].Offset
                : Math.Min(segments[c.Segment].Length, c.Offset + chunkSize + 1024);
            chunks[i] = (c.Segment, c.Offset, end, c.Compressed);
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset >= Size) return 0;
        int total = (int)Math.Min(destination.Length, Size - offset);

        for (int done = 0; done < total;)
        {
            long position = offset + done;
            long index = position / _chunkSize;
            int within = (int)(position % _chunkSize);
            int part = Math.Min(total - done, _chunkSize - within);

            byte[]? chunk = Chunk(index);
            if (chunk is null) return done;                                      // חלק שלא נקרא — כמו סקטור פגום
            chunk.AsSpan(within, Math.Min(part, Math.Max(0, chunk.Length - within))).CopyTo(destination.Slice(done));
            done += part;
        }
        return total;
    }

    private byte[]? Chunk(long index)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(index, out var cached)) return cached;
            if (index >= _chunks.Count) return null;

            var (segment, offset, end, compressed) = _chunks[(int)index];
            byte[] raw = ReadAt(_segments[segment], offset, (int)Math.Max(0, end - offset));

            byte[] chunk;
            try
            {
                if (compressed)
                {
                    using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                    chunk = new byte[_chunkSize];
                    int got = 0;
                    while (got < chunk.Length)
                    {
                        int n = z.Read(chunk, got, chunk.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }
                else
                {
                    // החלק האחרון קצר כשגודל הכונן לא מתחלק בגודל חלק. אחרי כל חלק — ארבעה בתי בדיקה.
                    int want = (int)Math.Min(_chunkSize, Size - index * _chunkSize);
                    if (raw.Length < want) return null;
                    chunk = raw.AsSpan(0, want).ToArray();
                }
            }
            catch (InvalidDataException)
            {
                return null;                                                     // חלק דחוס פגום
            }

            _cache[index] = chunk;
            _order.AddLast(index);
            if (_order.Count > CacheChunks)
            {
                _cache.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return chunk;
        }
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length || count <= 0) return [];
        byte[] buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.Position = offset;
        int got = 0;
        while (got < buffer.Length)
        {
            int n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0) break;
            got += n;
        }
        return got == buffer.Length ? buffer : buffer[..got];
    }

    public void Close()
    {
        foreach (var s in _segments) s.Dispose();
    }
}
