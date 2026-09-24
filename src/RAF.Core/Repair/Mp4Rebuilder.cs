using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace RAF.Core.Repair;

/// <summary>תוצאת בנייה מחדש של אינדקס לסרטון.</summary>
public sealed class VideoRebuildResult
{
    public bool Succeeded { get; init; }
    public string? OutputPath { get; init; }
    public int VideoFrames { get; init; }
    public int AudioFrames { get; init; }
    public double Seconds { get; init; }

    /// <summary>בתים שלא זוהו כתמונה או כקול — אזורים פגומים, או מסלולים שאינם נתמכים.</summary>
    public long UnrecognizedBytes { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// בניית האינדקס (moov) של סרטון MP4/MOV שלא נסגר — הקלטה שנקטעה בגלל סוללה, כרטיס
/// שנשלף או קריסה. מצלמות וטלפונים כותבים את האינדקס רק בסוף ההקלטה, ובלעדיו הנגן
/// אינו יודע היכן כל תמונה וכל קטע קול — אף שכולם נמצאים בקובץ.
///
/// מסרטון תקין מאותו מכשיר ובאותן הגדרות ("קובץ ייחוס") לוקחים את מה שאינו כתוב
/// בנתונים: סוג הקידוד והגדרותיו, קצב התמונות וקצב הקול. את הנתונים עצמם עוברים
/// מההתחלה עד הסוף ומזהים בהם תמונה אחר תמונה ומסגרת קול אחר מסגרת קול — כל אחת
/// נבדקת במבנה שלה (ראו VideoFrames ו-AacFrame) — ומהם נכתב אינדקס חדש.
/// הקובץ המקורי אינו משתנה.
/// </summary>
public static class Mp4Rebuilder
{
    /// <summary>מה חסר בסרטון: null — יש בו אינדקס, או שאינו MP4 בכלל.</summary>
    public sealed record MissingIndex(long DataStart, long DataEnd, bool Truncated);

    /// <summary>האם לסרטון חסר האינדקס, ואיפה הנתונים שלו.</summary>
    public static MissingIndex? Inspect(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var boxes = Mp4Index.TopLevel(s);
        if (boxes.Count == 0 || boxes[0].Type != "ftyp") return null;
        if (boxes.Any(b => b.Type == "moov")) return null;

        var mdat = boxes.Where(b => b.Type == "mdat").OrderByDescending(b => b.Size).FirstOrDefault();
        if (mdat.Type == "mdat")
        {
            // גודל שהוצהר ואינו קיים — ההקלטה נקטעה. גודל 0 — המכשיר לא הספיק לכתוב אותו.
            s.Position = mdat.Start;
            var header = new byte[8];
            s.ReadExactly(header);
            long declared = BinaryPrimitives.ReadUInt32BigEndian(header);
            bool truncated = declared == 0 || mdat.End >= s.Length;
            return mdat.BodySize > 1024 ? new MissingIndex(mdat.Body, mdat.End, truncated) : null;
        }

        // בלי תיבת נתונים מזוהה: הנתונים הם כל מה שאחרי התיבות שזוהו.
        long after = boxes[^1].End;
        return s.Length - after > 1024 ? new MissingIndex(after, s.Length, true) : null;
    }

    /// <summary>
    /// בדיקת סרטון ייחוס לפני הבנייה: null — מתאים; אחרת, מה חסר בו, במילים פשוטות.
    /// </summary>
    public static string? DescribeReference(string path)
    {
        var tracks = Mp4Index.ReadTracks(path);
        if (tracks is null)
            return "גם בסרטון הזה אין אינדקס, ולכן אי אפשר ללמוד ממנו. בחרו סרטון שנפתח ומתנגן כרגיל.";
        if (!tracks.Any(t => t.IsVideo))
            return "בסרטון הזה אין תמונה בקידוד H.264 או H.265 — הקידודים שטלפונים ומצלמות משתמשים בהם, " +
                   "ושאפשר לבנות להם אינדקס.";
        return null;
    }

    private enum Track : byte { Video, Audio }

    private readonly record struct Sample(Track Track, long Offset, int Size, int Count);

    public static VideoRebuildResult Rebuild(string brokenPath, string referencePath, string outputPath,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        var missing = Inspect(brokenPath)
            ?? throw new InvalidOperationException("לסרטון הזה לא חסר אינדקס — אין מה לבנות מחדש.");

        var tracks = Mp4Index.ReadTracks(referencePath)
            ?? throw new InvalidOperationException("בסרטון הייחוס לא נמצא אינדקס. בחרו סרטון שנפתח ומתנגן כרגיל.");
        var video = tracks.FirstOrDefault(t => t.IsVideo)
            ?? throw new InvalidOperationException(
                "בסרטון הייחוס אין תמונה בקידוד H.264 או H.265 — אלה הקידודים שאפשר לבנות להם אינדקס.");
        var audio = tracks.FirstOrDefault(t => t.IsAac || t.IsPcm);

        int maxFrame = (int)Math.Clamp((long)(video.Sizes.Count > 0 ? video.Sizes.Max() : 0) * 4, 1 << 20, 64 << 20);
        var reader = new VideoFrames(video.IsHevc, video.NalLengthSize, video.ParameterSets, maxFrame);

        var samples = new List<Sample>();
        var frames = new List<VideoFrame>();
        long unrecognized = 0;
        var clock = Stopwatch.StartNew();

        using (var source = new Window(brokenPath, maxFrame + (1 << 20)))
        {
            long p = missing.DataStart, end = missing.DataEnd;
            bool lost = true;                    // בתחילה, ואחרי אזור לא מזוהה — דורשים אישוש כפול
            var lastReport = TimeSpan.Zero;

            while (p < end)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = clock.Elapsed;
                    progress?.Report((p - missing.DataStart) * 100.0 / Math.Max(1, end - missing.DataStart));
                }

                var span = source.At(p, end);

                // 1. תמונה — נבדקת ראשונה: זיהוי שלה מחמיר יותר משל מסגרת קול.
                if (reader.Read(span) is { } frame && (!lost || Confirmed(span, frame.Length)))
                {
                    samples.Add(new Sample(Track.Video, p, frame.Length, 1));
                    frames.Add(frame);
                    p += frame.Length;
                    lost = false;
                    continue;
                }

                // 2. מסגרת קול AAC.
                if (audio is { IsAac: true } && AacFrame.Length(span[..Math.Min(span.Length, 8192)], audio.AacSamplingIndex) is > 0 and var length
                    && (!lost || Confirmed(span, length)))
                {
                    samples.Add(new Sample(Track.Audio, p, length, 1));
                    p += length;
                    lost = false;
                    continue;
                }

                // 3. קול לא דחוס: רצף באורך כפולה של גודל הדגימה, עד התמונה הבאה.
                if (audio is { IsPcm: true } && !lost && PcmRun(source, p, end, audio, reader) is > 0 and var run)
                {
                    samples.Add(new Sample(Track.Audio, p, audio.PcmFrameBytes, (int)(run / audio.PcmFrameBytes)));
                    p += run;
                    continue;
                }

                // 4. לא זוהה — מתקדמים בית אחד ומחפשים את ההמשך.
                unrecognized++;
                lost = true;
                p++;
            }

            // אישוש: אחרי מה שזוהה חייב לבוא עוד משהו מזוהה (או סוף הנתונים).
            bool Confirmed(ReadOnlySpan<byte> span, int length)
            {
                if (length >= span.Length) return true;
                var next = span[length..];
                if (reader.Read(next) is not null) return true;
                if (audio is { IsAac: true }) return AacFrame.Length(next[..Math.Min(next.Length, 8192)], audio.AacSamplingIndex) > 0;
                return audio is { IsPcm: true } && PcmRun(source, p + length, end, audio, reader) > 0;
            }
        }

        if (frames.Count == 0)
            return new VideoRebuildResult
            {
                Message = "לא נמצאו בסרטון תמונות שמתאימות להגדרות של סרטון הייחוס. " +
                          "ודאו שסרטון הייחוס צולם באותו מכשיר ובאותן הגדרות (רזולוציה, קצב תמונות).",
                UnrecognizedBytes = unrecognized,
            };

        progress?.Report(100);
        var written = Write(brokenPath, referencePath, outputPath, missing, video, audio, samples, frames, reader);

        int audioFrames = samples.Where(s => s.Track == Track.Audio).Sum(s => s.Count);
        double seconds = frames.Count * (double)video.TypicalDuration() / video.Timescale;
        return new VideoRebuildResult
        {
            Succeeded = true,
            OutputPath = written,
            VideoFrames = frames.Count,
            AudioFrames = audio is null ? 0 : audioFrames,
            Seconds = seconds,
            UnrecognizedBytes = unrecognized,
            Message = $"נבנה אינדקס חדש: {frames.Count:N0} תמונות" +
                      (audio is null ? "" : audioFrames > 0 ? " וקול" : " (הקול לא נמצא)") +
                      $" — {Duration(seconds)}." +
                      (unrecognized == 0 ? ""
                          : unrecognized < 4L << 20
                              ? $" חלק קטן ({Size(unrecognized)}) לא זוהה — בדרך כלל התמונה האחרונה, שנקטעה באמצע ההקלטה — והוא נשאר מחוץ לסרטון."
                              : $" {Size(unrecognized)} לא זוהו כתמונה או כקול — אזורים פגומים, או נתונים שהמכשיר שומר בנוסף — ונשארו מחוץ לסרטון."),
        };
    }

    private static string Duration(double seconds) => seconds < 60
        ? $"{Math.Round(seconds):0} שניות"
        : $"{TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}".TrimStart('0', ':') + " דקות";

    /// <summary>גודל מבודד משמאל לימין, כדי שלא יתהפך בתוך משפט בעברית.</summary>
    private static string Size(long bytes) => "⁦" + (bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    }) + "⁩";

    /// <summary>
    /// אורך רצף הקול הלא דחוס שמתחיל ב-p, או 0. לקול כזה אין מבנה שאפשר לבדוק, ושקט
    /// או ערכים קטנים עלולים להיראות כתחילת תמונה — לכן קודם נבדקים האורכים שהמכשיר
    /// כותב (מסרטון הייחוס), ורק אחר כך חיפוש צעד אחר צעד, עם אישוש כפול.
    /// </summary>
    private static long PcmRun(Window source, long p, long end, Mp4Track audio, VideoFrames reader)
    {
        const long MaxRun = 64L << 20;
        int frameBytes = audio.PcmFrameBytes;

        foreach (int count in audio.ChunkSampleCounts)
        {
            long q = p + (long)count * frameBytes;
            if (q == end) return q - p;
            if (q < end && reader.Read(source.At(q, end)) is not null) return q - p;
        }

        for (long q = p + frameBytes; q <= end && q - p <= MaxRun; q += frameBytes)
        {
            if (q == end) return q - p;
            var span = source.At(q, end);
            if (reader.Read(span) is not { } f) continue;
            if (f.Length >= span.Length) return q - p;
            long after = q + f.Length;
            if (reader.Read(source.At(after, end)) is not null) return q - p;
            if (audio.ChunkSampleCounts.Any(c => after + (long)c * frameBytes is var r && (r == end || r < end && reader.Read(source.At(r, end)) is not null)))
                return q - p;
        }
        return 0;
    }

    // ------------------------------------------------------------ כתיבה

    private static string Write(string brokenPath, string referencePath, string outputPath, MissingIndex missing,
        Mp4Track video, Mp4Track? audio, List<Sample> samples, List<VideoFrame> frames, VideoFrames reader)
    {
        byte[] ftyp = FirstBox(brokenPath, "ftyp") ?? FirstBox(referencePath, "ftyp") ?? Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\u0002\0isomiso2mp41"));
        long mdatBody = ftyp.Length + 16;
        long Shift(long offset) => mdatBody + (offset - missing.DataStart);

        // --- מסלול התמונה: משכים, סדר תצוגה, תמונות מפתח
        uint duration = Math.Max(1, video.TypicalDuration());
        int[] order = reader.DisplayOrder(frames);
        int delay = 0;
        for (int i = 0; i < order.Length; i++) delay = Math.Max(delay, i - order[i]);
        var composition = order.Select((o, i) => (o - i + delay) * (int)duration).ToList();
        bool needsComposition = composition.Any(c => c != 0);

        var videoSamples = samples.Where(s => s.Track == Track.Video).ToList();
        var videoTrak = TrackBox(video, 1, videoSamples.Select(s => (Shift(s.Offset), s.Size, 1)).ToList(),
            duration,
            composition: needsComposition ? composition : null,
            sync: frames.All(f => f.Key) ? null : frames.Select((f, i) => (f, i)).Where(x => x.f.Key).Select(x => x.i).ToList(),
            editDelay: delay * (long)duration,
            out long videoMediaDuration);

        var traks = new List<byte[]> { videoTrak };
        long movieDuration = videoMediaDuration * 1000 / video.Timescale;

        var audioSamples = samples.Where(s => s.Track == Track.Audio).ToList();
        if (audio is not null && audioSamples.Count > 0)
        {
            uint audioDuration = Math.Max(1, audio.TypicalDuration());
            var entries = audioSamples.Select(s => (Shift(s.Offset), s.Size, s.Count)).ToList();
            int count = audioSamples.Sum(s => s.Count);
            traks.Add(TrackBox(audio, 2, entries, audioDuration,
                composition: null, sync: null, editDelay: 0, out long audioMediaDuration));
            movieDuration = Math.Max(movieDuration, audioMediaDuration * 1000 / audio.Timescale);
        }

        byte[] moov = Box("moov", Concat(new[] { Mvhd(movieDuration, (uint)traks.Count + 1) }.Concat(traks)));

        string path = outputPath;
        using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        using (var source = new FileStream(brokenPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            target.Write(ftyp);
            long length = missing.DataEnd - missing.DataStart;
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            Encoding.ASCII.GetBytes("mdat").CopyTo(header, 4);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), length + 16);
            target.Write(header);

            source.Position = missing.DataStart;
            var buffer = new byte[1 << 20];
            for (long left = length; left > 0;)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) throw new EndOfStreamException("הסרטון המקורי התקצר בזמן הבנייה.");
                target.Write(buffer, 0, read);
                left -= read;
            }
            target.Write(moov);
        }
        return path;
    }

    /// <summary>
    /// תיבת trak למסלול, מתבנית המסלול של סרטון הייחוס: תיאור הקידוד עובר כמו שהוא,
    /// וטבלאות הדגימות נכתבות מחדש. דגימות צמודות של אותו מסלול — מקטע אחד.
    /// </summary>
    private static byte[] TrackBox(Mp4Track t, uint trackId, List<(long Offset, int Size, int Count)> entries,
        uint duration, List<int>? composition, List<int>? sync, long editDelay, out long mediaDuration)
    {
        // כל הדגימות באותו משך: קצב קבוע, כמו בסרטון הייחוס. דגימות של קול לא דחוס
        // יכולות להיות מאות מיליונים — לכן גם הגודל נכתב פעם אחת כשהוא קבוע.
        long sampleCount = entries.Sum(e => (long)e.Count);
        mediaDuration = sampleCount * duration;
        long movieDuration = mediaDuration * 1000 / Math.Max(1, t.Timescale);

        // --- מקטעים
        var chunkOffsets = new List<long>();
        var chunkCounts = new List<int>();
        long expected = -1;
        foreach (var (offset, size, count) in entries)
        {
            if (offset == expected && chunkCounts.Count > 0) chunkCounts[^1] += count;
            else
            {
                chunkOffsets.Add(offset);
                chunkCounts.Add(count);
            }
            expected = offset + (long)size * count;
        }

        bool constantSize = entries.All(e => e.Size == entries[0].Size);

        var stbl = new List<byte[]> { t.Stsd, Full("stts", 0, W32(1), W32((uint)sampleCount), W32(duration)) };
        if (composition is not null) stbl.Add(Ctts(composition));
        if (sync is not null) stbl.Add(Full("stss", 0, W32((uint)sync.Count), Concat(sync.Select(i => W32((uint)i + 1)))));
        stbl.Add(Stsc(chunkCounts));
        stbl.Add(constantSize
            ? Full("stsz", 0, W32((uint)entries[0].Size), W32((uint)sampleCount))
            : Full("stsz", 0, W32(0), W32((uint)sampleCount),
                Concat(entries.SelectMany(e => Enumerable.Repeat(W32((uint)e.Size), e.Count)))));
        stbl.Add(Full("co64", 0, W32((uint)chunkOffsets.Count), Concat(chunkOffsets.Select(W64))));

        byte[] dinf = t.Dinf.Length > 0 ? t.Dinf
            : Box("dinf", Full("dref", 0, W32(1), Full("url ", 1)));
        byte[] minf = Box("minf", Concat(new[] { t.MediaHeader, dinf, Box("stbl", Concat(stbl)) }));
        byte[] mdia = Box("mdia", Concat(new[] { WithDuration(t.Mdhd, mediaDuration, mdhd: true), t.Hdlr, minf }));

        var parts = new List<byte[]> { WithTrackId(WithDuration(t.Tkhd, movieDuration, mdhd: false), trackId) };
        if (editDelay > 0)
        {
            // רשימת עריכה: הסרטון מתחיל בתמונה שמוצגת ראשונה, ולא אחרי השהיית הסידור מחדש.
            parts.Add(Box("edts", Full("elst", 0, W32(1), W32((uint)movieDuration), W32((uint)editDelay), W32(0x00010000))));
        }
        parts.Add(mdia);
        return Box("trak", Concat(parts));
    }

    private static byte[] Ctts(List<int> offsets)
    {
        var runs = new List<(uint Count, int Offset)>();
        foreach (var o in offsets)
        {
            if (runs.Count > 0 && runs[^1].Offset == o) runs[^1] = (runs[^1].Count + 1, o);
            else runs.Add((1, o));
        }
        return Full("ctts", 0, W32((uint)runs.Count), Concat(runs.Select(r => Concat(new[] { W32(r.Count), W32((uint)r.Offset) }))));
    }

    private static byte[] Stsc(List<int> perChunk)
    {
        var entries = new List<byte[]>();
        int last = -1;
        for (int c = 0; c < perChunk.Count; c++)
        {
            if (perChunk[c] == last) continue;
            last = perChunk[c];
            entries.Add(Concat(new[] { W32((uint)c + 1), W32((uint)last), W32(1) }));
        }
        return Full("stsc", 0, W32((uint)entries.Count), Concat(entries));
    }

    private static byte[] Mvhd(long durationMs, uint nextTrack)
    {
        var body = new byte[96];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), 1000);                 // timescale
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), (uint)Math.Min(durationMs, uint.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), 0x00010000);          // rate 1.0
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(20), 0x0100);              // volume 1.0
        // מטריצת הזהות
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(32), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(48), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(64), 0x40000000);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(92), nextTrack);
        return Full("mvhd", 0, body);
    }

    /// <summary>עדכון שדה המשך ב-tkhd (ביחידות הסרטון) או ב-mdhd (ביחידות המסלול).</summary>
    private static byte[] WithDuration(byte[] box, long duration, bool mdhd)
    {
        var copy = (byte[])box.Clone();
        if (copy.Length < 12) return copy;
        bool v1 = copy[8] == 1;
        int at = mdhd ? (v1 ? 32 : 24) : (v1 ? 36 : 28);
        if (v1 && at + 8 <= copy.Length) BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(at), duration);
        else if (!v1 && at + 4 <= copy.Length) BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(at), (uint)Math.Min(duration, uint.MaxValue));
        return copy;
    }

    private static byte[] WithTrackId(byte[] tkhd, uint id)
    {
        if (tkhd.Length < 12) return tkhd;
        int at = tkhd[8] == 1 ? 28 : 20;
        if (at + 4 <= tkhd.Length) BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(at), id);
        return tkhd;
    }

    private static byte[]? FirstBox(string path, string type)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var boxes = Mp4Index.TopLevel(s);
        if (boxes.Count == 0 || boxes[0].Type != type || boxes[0].Size > 4096) return null;
        var data = new byte[boxes[0].Size];
        s.Position = 0;
        s.ReadExactly(data);
        return data;
    }

    // ------------------------------------------------------------ תיבות

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] body = Concat(parts);
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Full(string type, uint flags, params byte[][] parts)
        => Box(type, new[] { W32(flags) }.Concat(parts).ToArray());

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var list = parts as ICollection<byte[]> ?? parts.ToList();
        var result = new byte[list.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in list)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }
        return result;
    }

    private static byte[] W32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] W64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    /// <summary>
    /// חלון קריאה על קובץ גדול: הנתונים שמהמקום הנוכחי ואילך, לפחות באורך התמונה
    /// הגדולה ביותר האפשרית — בלי לטעון סרטון של כמה ג'יגה-בתים לזיכרון.
    /// </summary>
    private sealed class Window : IDisposable
    {
        private readonly FileStream _file;
        private readonly byte[] _buffer;
        private readonly int _lookahead;
        private long _start = -1;
        private int _length;

        public Window(string path, int lookahead)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _lookahead = lookahead;
            _buffer = new byte[Math.Max(lookahead * 2, 16 << 20)];
        }

        public ReadOnlySpan<byte> At(long position, long end)
        {
            long available = Math.Min(end, _file.Length) - position;
            int want = (int)Math.Min(available, _lookahead);
            if (_start < 0 || position < _start || position + want > _start + _length)
            {
                _start = position;
                _file.Position = position;
                int total = 0, n;
                int limit = (int)Math.Min(_buffer.Length, Math.Min(end, _file.Length) - position);
                while (total < limit && (n = _file.Read(_buffer, total, limit - total)) > 0) total += n;
                _length = total;
            }
            return _buffer.AsSpan((int)(position - _start), Math.Min(want, _length - (int)(position - _start)));
        }

        public void Dispose() => _file.Dispose();
    }
}
