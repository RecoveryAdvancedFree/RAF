using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Repair;

/// <summary>תיבה בקובץ MP4: סוג, מיקום התוכן שלה ואורכו.</summary>
internal readonly record struct Mp4Box(string Type, long Start, long HeaderSize, long Size)
{
    public long Body => Start + HeaderSize;
    public long BodySize => Size - HeaderSize;
    public long End => Start + Size;
}

/// <summary>מסלול אחד בסרטון (תמונה או קול), כפי שהאינדקס (moov) מתאר אותו.</summary>
internal sealed class Mp4Track
{
    public string Handler { get; init; } = "";          // "vide" / "soun" / אחר
    public uint Timescale { get; init; }
    public string Codec { get; init; } = "";             // avc1 / hvc1 / mp4a / sowt ...

    /// <summary>התיבות שעוברות כמו שהן לאינדקס החדש.</summary>
    public byte[] Tkhd { get; init; } = [];
    public byte[] Mdhd { get; init; } = [];
    public byte[] Hdlr { get; init; } = [];
    public byte[] MediaHeader { get; init; } = [];       // vmhd / smhd
    public byte[] Dinf { get; init; } = [];
    public byte[] Stsd { get; init; } = [];

    /// <summary>H.264 / H.265: אורך שדה האורך שלפני כל יחידת NAL (בדרך כלל 4).</summary>
    public int NalLengthSize { get; init; }
    public List<byte[]> ParameterSets { get; init; } = new();

    /// <summary>AAC: אינדקס קצב הדגימה של הליבה, ומספר הדגימות במסגרת (1024).</summary>
    public int AacSamplingIndex { get; init; } = -1;

    /// <summary>PCM: בתים לכל דגימה (כל הערוצים יחד).</summary>
    public int PcmFrameBytes { get; init; }

    public List<long> Offsets { get; } = new();
    public List<int> Sizes { get; } = new();
    public List<uint> Durations { get; } = new();
    public List<int> CompositionOffsets { get; } = new();
    public HashSet<int>? SyncSamples { get; set; }

    /// <summary>כמה דגימות המכשיר כותב בכל מקטע, מהנפוץ לנדיר — לקול לא דחוס, שאין בו גבולות אחרים.</summary>
    public List<int> ChunkSampleCounts { get; } = new();

    public bool IsVideo => Handler == "vide" && Codec is "avc1" or "avc3" or "hvc1" or "hev1";
    public bool IsHevc => Codec is "hvc1" or "hev1";
    public bool IsAac => Handler == "soun" && Codec == "mp4a" && AacSamplingIndex >= 0;
    public bool IsPcm => Handler == "soun" && PcmFrameBytes > 0;

    /// <summary>משך דגימה טיפוסי — החציון, כדי שקצב משתנה לא יטה אותו.</summary>
    public uint TypicalDuration()
    {
        if (Durations.Count == 0) return 0;
        var sorted = Durations.OrderBy(d => d).ToList();
        return sorted[sorted.Count / 2];
    }
}

internal static class Mp4Index
{
    /// <summary>התיבות ברמה העליונה של הקובץ. תיבה שחורגת מסוף הקובץ נחתכת בו.</summary>
    public static List<Mp4Box> TopLevel(Stream s)
    {
        var boxes = new List<Mp4Box>();
        long at = 0, length = s.Length;
        var header = new byte[16];

        while (at + 8 <= length && boxes.Count < 10_000)
        {
            s.Position = at;
            int read = s.Read(header, 0, 16);
            if (read < 8) break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (!type.All(c => c is >= ' ' and <= '~')) break;

            long headerSize = 8;
            if (size == 1)
            {
                if (read < 16) break;
                size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0) size = length - at;                          // עד סוף הקובץ

            if (size < headerSize) break;
            if (at + size > length) size = length - at;                       // נקטעה
            boxes.Add(new Mp4Box(type, at, headerSize, size));
            at += size;
        }

        return boxes;
    }

    /// <summary>התיבות שבתוך תוכן של תיבה.</summary>
    public static List<Mp4Box> Children(ReadOnlySpan<byte> data, long start = 0)
    {
        var boxes = new List<Mp4Box>();
        long at = start;
        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[(int)at..]);
            string type = Encoding.ASCII.GetString(data.Slice((int)at + 4, 4));
            long headerSize = 8;
            if (size == 1)
            {
                if (at + 16 > data.Length) break;
                size = BinaryPrimitives.ReadInt64BigEndian(data[((int)at + 8)..]);
                headerSize = 16;
            }
            else if (size == 0) size = data.Length - at;
            if (size < headerSize || at + size > data.Length) break;
            boxes.Add(new Mp4Box(type, at, headerSize, size));
            at += size;
        }
        return boxes;
    }

    private static Mp4Box? Find(ReadOnlySpan<byte> data, Mp4Box parent, string type, int skip = 0)
    {
        foreach (var b in Children(data[..(int)parent.End], parent.Body + skip))
            if (b.Type == type) return b;
        return null;
    }

    private static byte[] Bytes(ReadOnlySpan<byte> data, Mp4Box? box)
        => box is { } b ? data.Slice((int)b.Start, (int)b.Size).ToArray() : [];

    /// <summary>קריאת האינדקס מקובץ שלם: המסלולים, והיכן כל דגימה שלהם יושבת.</summary>
    public static List<Mp4Track>? ReadTracks(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var moov = TopLevel(s).FirstOrDefault(b => b.Type == "moov");
        if (moov.Type != "moov" || moov.Size > 512L * 1024 * 1024) return null;

        var data = new byte[moov.Size];
        s.Position = moov.Start;
        s.ReadExactly(data);
        return ReadTracks(data);
    }

    /// <summary>קריאת המסלולים מתוך תיבת moov שלמה (כולל הכותרת שלה).</summary>
    public static List<Mp4Track> ReadTracks(byte[] moovBytes)
    {
        ReadOnlySpan<byte> data = moovBytes;
        var root = Children(data).First();
        var tracks = new List<Mp4Track>();

        foreach (var trak in Children(data[..(int)root.End], root.Body).Where(b => b.Type == "trak"))
        {
            var mdia = Find(data, trak, "mdia");
            if (mdia is null) continue;
            var mdhd = Find(data, mdia.Value, "mdhd");
            var hdlr = Find(data, mdia.Value, "hdlr");
            var minf = Find(data, mdia.Value, "minf");
            var stbl = minf is { } m ? Find(data, m, "stbl") : null;
            if (mdhd is null || hdlr is null || stbl is null) continue;

            string handler = Encoding.ASCII.GetString(data.Slice((int)hdlr.Value.Body + 8, 4));
            var mdhdBody = data[(int)mdhd.Value.Body..];
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhdBody[(mdhdBody[0] == 1 ? 20 : 12)..]);

            var stsd = Find(data, stbl.Value, "stsd");
            if (stsd is null) continue;
            var entry = Children(data[..(int)stsd.Value.End], stsd.Value.Body + 8).FirstOrDefault();
            string codec = entry.Type ?? "";

            int nalLength = 0, samplingIndex = -1, pcmBytes = 0;
            var parameterSets = new List<byte[]>();

            if (codec is "avc1" or "avc3" or "hvc1" or "hev1")
            {
                // תיאור החזותי: 78 בתים קבועים אחרי הכותרת, ואחריהם תיבות — avcC / hvcC.
                var config = Children(data[..(int)entry.End], entry.Body + 78)
                    .FirstOrDefault(b => b.Type is "avcC" or "hvcC");
                if (config.Type == "avcC") nalLength = ReadAvcC(data.Slice((int)config.Body, (int)config.BodySize), parameterSets);
                if (config.Type == "hvcC") nalLength = ReadHvcC(data.Slice((int)config.Body, (int)config.BodySize), parameterSets);
            }
            else if (codec == "mp4a")
            {
                samplingIndex = AacSamplingIndex(data.Slice((int)entry.Start, (int)entry.Size));
            }
            else if (codec is "sowt" or "twos" or "lpcm" or "ipcm" or "in24" or "in32" or "fl32")
            {
                // קול לא דחוס: גודל קבוע לכל דגימה — נלקח מטבלת הגדלים.
                var stszBox = Find(data, stbl.Value, "stsz");
                if (stszBox is { } z)
                    pcmBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(data[((int)z.Body + 4)..]);
            }

            var track = new Mp4Track
            {
                Handler = handler,
                Timescale = timescale,
                Codec = codec,
                Tkhd = Bytes(data, Find(data, trak, "tkhd")),
                Mdhd = Bytes(data, mdhd),
                Hdlr = Bytes(data, hdlr),
                MediaHeader = Bytes(data, Find(data, minf!.Value, "vmhd") ?? Find(data, minf.Value, "smhd")),
                Dinf = Bytes(data, Find(data, minf.Value, "dinf")),
                Stsd = Bytes(data, stsd),
                NalLengthSize = nalLength,
                ParameterSets = parameterSets,
                AacSamplingIndex = samplingIndex,
                PcmFrameBytes = pcmBytes,
            };
            ReadSampleTable(data, stbl.Value, track);
            tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>avcC: אורך שדה האורך של NAL, ו-SPS/PPS.</summary>
    private static int ReadAvcC(ReadOnlySpan<byte> c, List<byte[]> sets)
    {
        if (c.Length < 7) return 0;
        int lengthSize = (c[4] & 3) + 1;
        int at = 5;
        for (int pass = 0; pass < 2; pass++)
        {
            int count = pass == 0 ? c[at++] & 0x1F : c[at++];
            for (int i = 0; i < count && at + 2 <= c.Length; i++)
            {
                int n = BinaryPrimitives.ReadUInt16BigEndian(c[at..]);
                at += 2;
                if (at + n > c.Length) return lengthSize;
                sets.Add(c.Slice(at, n).ToArray());
                at += n;
            }
            if (at >= c.Length) break;
        }
        return lengthSize;
    }

    /// <summary>hvcC: אורך שדה האורך, ו-VPS/SPS/PPS.</summary>
    private static int ReadHvcC(ReadOnlySpan<byte> c, List<byte[]> sets)
    {
        if (c.Length < 23) return 0;
        int lengthSize = (c[21] & 3) + 1;
        int arrays = c[22], at = 23;
        for (int a = 0; a < arrays && at + 3 <= c.Length; a++)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(c[(at + 1)..]);
            at += 3;
            for (int i = 0; i < count && at + 2 <= c.Length; i++)
            {
                int n = BinaryPrimitives.ReadUInt16BigEndian(c[at..]);
                at += 2;
                if (at + n > c.Length) return lengthSize;
                sets.Add(c.Slice(at, n).ToArray());
                at += n;
            }
        }
        return lengthSize;
    }

    /// <summary>
    /// אינדקס קצב הדגימה של AAC מתוך esds (AudioSpecificConfig). ב-HE-AAC זה קצב
    /// הליבה — המסגרות עצמן בנויות לפיו. ‎-1 — לא AAC, או מבנה שאינו מוכר.
    /// </summary>
    private static int AacSamplingIndex(ReadOnlySpan<byte> entry)
    {
        // החיפוש אחר תג DecoderSpecificInfo (0x05) אחרי תג DecoderConfig (0x04) של אודיו (0x40).
        for (int i = 8; i + 2 < entry.Length; i++)
        {
            if (entry[i] != 0x04) continue;
            int at = i + 1, len = 0;
            for (int k = 0; k < 4 && at < entry.Length; k++)
            {
                len = len << 7 | entry[at] & 0x7F;
                if ((entry[at++] & 0x80) == 0) break;
            }
            if (at >= entry.Length || entry[at] != 0x40) continue;           // לא MPEG-4 Audio
            int dsi = at + 13;
            if (dsi >= entry.Length || entry[dsi] != 0x05) continue;
            at = dsi + 1;
            for (int k = 0; k < 4 && at < entry.Length; k++)
                if ((entry[at++] & 0x80) == 0) break;
            if (at + 2 > entry.Length) return -1;

            int objectType = entry[at] >> 3;
            int index = (entry[at] & 7) << 1 | entry[at + 1] >> 7;
            // Main / LC / SBR / PS — בכולם הליבה היא מסגרות AAC רגילות.
            return objectType is 1 or 2 or 5 or 29 && index <= 12 ? index : -1;
        }
        return -1;
    }

    /// <summary>פריסת טבלאות הדגימות: היסט, גודל, משך, סטיית תצוגה ודגימות מפתח.</summary>
    private static void ReadSampleTable(ReadOnlySpan<byte> data, Mp4Box stbl, Mp4Track track)
    {
        static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> data, Mp4Box? b)
            => b is { } x ? data.Slice((int)x.Body, (int)x.BodySize) : default;
        static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32BigEndian(s[at..]);

        var stsz = Body(data, Find(data, stbl, "stsz"));
        var stz2 = Body(data, Find(data, stbl, "stz2"));
        var stsc = Body(data, Find(data, stbl, "stsc"));
        var stco = Body(data, Find(data, stbl, "stco"));
        var co64 = Body(data, Find(data, stbl, "co64"));
        var stts = Body(data, Find(data, stbl, "stts"));
        var ctts = Body(data, Find(data, stbl, "ctts"));
        var stss = Body(data, Find(data, stbl, "stss"));

        if (stsz.Length >= 12)
        {
            uint constant = U32(stsz, 4), count = U32(stsz, 8);
            for (int i = 0; i < count; i++)
                track.Sizes.Add((int)(constant != 0 ? constant : U32(stsz, 12 + i * 4)));
        }
        else if (stz2.Length >= 12 && stz2[7] == 16)
        {
            uint count = U32(stz2, 8);
            for (int i = 0; i < count; i++)
                track.Sizes.Add(BinaryPrimitives.ReadUInt16BigEndian(stz2[(12 + i * 2)..]));
        }

        var chunks = new List<long>();
        if (stco.Length >= 8)
            for (int i = 0; i < U32(stco, 4); i++) chunks.Add(U32(stco, 8 + i * 4));
        else if (co64.Length >= 8)
            for (int i = 0; i < U32(co64, 4); i++) chunks.Add(BinaryPrimitives.ReadInt64BigEndian(co64[(8 + i * 8)..]));

        // stsc: רצפים של "מקטע ראשון, דגימות למקטע".
        int sample = 0;
        var perChunkCounts = new Dictionary<int, int>();
        uint entries = stsc.Length >= 8 ? U32(stsc, 4) : 0;
        for (int e = 0; e < entries && sample < track.Sizes.Count; e++)
        {
            int first = (int)U32(stsc, 8 + e * 12) - 1;
            int perChunk = (int)U32(stsc, 12 + e * 12);
            int last = e + 1 < entries ? (int)U32(stsc, 8 + (e + 1) * 12) - 1 : chunks.Count;
            for (int c = first; c < last && c < chunks.Count; c++)
            {
                perChunkCounts[perChunk] = perChunkCounts.GetValueOrDefault(perChunk) + 1;
                long at = chunks[c];
                for (int k = 0; k < perChunk && sample < track.Sizes.Count; k++)
                {
                    track.Offsets.Add(at);
                    at += track.Sizes[sample++];
                }
            }
        }

        track.ChunkSampleCounts.AddRange(perChunkCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));

        if (stts.Length >= 8)
            for (int e = 0; e < U32(stts, 4); e++)
            {
                uint count = U32(stts, 8 + e * 8), delta = U32(stts, 12 + e * 8);
                for (int k = 0; k < count && track.Durations.Count < track.Sizes.Count; k++) track.Durations.Add(delta);
            }

        if (ctts.Length >= 8)
            for (int e = 0; e < U32(ctts, 4); e++)
            {
                uint count = U32(ctts, 8 + e * 8);
                int offset = (int)U32(ctts, 12 + e * 8);
                for (int k = 0; k < count && track.CompositionOffsets.Count < track.Sizes.Count; k++)
                    track.CompositionOffsets.Add(offset);
            }

        if (stss.Length >= 8)
        {
            track.SyncSamples = new HashSet<int>();
            for (int i = 0; i < U32(stss, 4); i++) track.SyncSamples.Add((int)U32(stss, 8 + i * 4) - 1);
        }
    }
}
