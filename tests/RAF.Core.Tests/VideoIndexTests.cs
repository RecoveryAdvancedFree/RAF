using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

public class VideoIndexTests
{
    private static string Media(string name) => Path.Combine(AppContext.BaseDirectory, "Media", name);

    [Theory]
    [InlineData("h264_aac.mp4")]
    [InlineData("hevc_aac_mono.mp4")]
    public void Every_aac_frame_is_measured_exactly_as_the_index_says(string file)
    {
        var tracks = Mp4Index.ReadTracks(Media(file))!;
        var audio = tracks.Single(t => t.IsAac);
        byte[] data = File.ReadAllBytes(Media(file));

        Assert.True(audio.Sizes.Count > 100);
        for (int i = 0; i < audio.Sizes.Count; i++)
        {
            // המסגרת ואחריה הנתונים שבקובץ — המדידה לא יודעת איפה היא נגמרת.
            var span = data.AsSpan((int)audio.Offsets[i]);
            Assert.True(audio.Sizes[i] == AacFrame.Length(span, audio.AacSamplingIndex), $"מסגרת {i}");
        }
    }

    [Theory]
    [InlineData("h264_aac.mp4")]
    [InlineData("hevc_aac_mono.mp4")]
    [InlineData("h264_pcm.mov")]
    public void Every_video_frame_is_measured_exactly_and_in_display_order(string file)
    {
        var video = Mp4Index.ReadTracks(Media(file))!.Single(t => t.IsVideo);
        byte[] data = File.ReadAllBytes(Media(file));
        var reader = new VideoFrames(video.IsHevc, video.NalLengthSize, video.ParameterSets, 4 << 20);

        var frames = new List<VideoFrame>();
        for (int i = 0; i < video.Sizes.Count; i++)
        {
            var frame = reader.Read(data.AsSpan((int)video.Offsets[i]));
            Assert.True(frame is not null, $"תמונה {i}");
            Assert.True(video.Sizes[i] == frame!.Value.Length, $"תמונה {i}: {video.Sizes[i]} מול {frame.Value.Length}");
            if (video.SyncSamples is { } sync) Assert.Equal(sync.Contains(i), frame.Value.Key);
            frames.Add(frame.Value);
        }

        // סדר התצוגה המקורי: זמן הפענוח ועוד סטיית התצוגה, מדורג.
        var original = Enumerable.Range(0, frames.Count)
            .OrderBy(i => (long)i * 1000 + (video.CompositionOffsets.Count > 0 ? video.CompositionOffsets[i] * 1000L / video.TypicalDuration() : 0))
            .ToList();
        var ours = reader.DisplayOrder(frames);
        for (int rank = 0; rank < original.Count; rank++)
            Assert.Equal(rank, ours[original[rank]]);
    }

    /// <summary>הסרטון בלי האינדקס שלו — כמו הקלטה שנקטעה לפני שנכתב.</summary>
    private static string WithoutIndex(string file, string folder, Func<byte[], byte[]>? damage = null)
    {
        byte[] data = File.ReadAllBytes(Media(file));
        using var s = new MemoryStream(data);
        var moov = Mp4Index.TopLevel(s).Single(b => b.Type == "moov");
        byte[] broken = data.Take((int)moov.Start).Concat(data.Skip((int)moov.End)).ToArray();
        string path = Path.Combine(folder, "broken_" + file);
        File.WriteAllBytes(path, damage?.Invoke(broken) ?? broken);
        return path;
    }

    private static long MdatBody(string path)
    {
        using var s = File.OpenRead(path);
        return Mp4Index.TopLevel(s).First(b => b.Type == "mdat").Body;
    }

    private static int[] DisplayRanks(Mp4Track t)
    {
        long Pts(int i) => t.Durations.Take(i).Sum(d => (long)d) + (t.CompositionOffsets.Count > 0 ? t.CompositionOffsets[i] : 0);
        var order = Enumerable.Range(0, t.Sizes.Count).OrderBy(Pts).ToList();
        var ranks = new int[order.Count];
        for (int r = 0; r < order.Count; r++) ranks[order[r]] = r;
        return ranks;
    }

    [Theory]
    [InlineData("h264_aac.mp4")]
    [InlineData("hevc_aac_mono.mp4")]
    [InlineData("h264_pcm.mov")]
    public void A_video_without_its_index_gets_the_same_index_back(string file)
    {
        string folder = Directory.CreateTempSubdirectory("raf-video-").FullName;
        try
        {
            string broken = WithoutIndex(file, folder);
            Assert.NotNull(Mp4Rebuilder.Inspect(broken));

            string output = Path.Combine(folder, "rebuilt_" + file);
            var result = Mp4Rebuilder.Rebuild(broken, Media(file), output);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(0, result.UnrecognizedBytes);

            var before = Mp4Index.ReadTracks(Media(file))!;
            var after = Mp4Index.ReadTracks(output)!;
            long shiftBefore = MdatBody(Media(file)), shiftAfter = MdatBody(output);

            var v0 = before.Single(t => t.IsVideo);
            var v1 = after.Single(t => t.IsVideo);
            Assert.Equal(v0.Sizes, v1.Sizes);
            Assert.Equal(v0.Offsets.Select(o => o - shiftBefore), v1.Offsets.Select(o => o - shiftAfter));
            Assert.Equal(v0.SyncSamples?.OrderBy(i => i), v1.SyncSamples?.OrderBy(i => i));
            Assert.Equal(DisplayRanks(v0), DisplayRanks(v1));

            var a0 = before.Single(t => t.Handler == "soun");
            var a1 = after.Single(t => t.Handler == "soun");
            Assert.Equal(a0.Sizes, a1.Sizes);
            Assert.Equal(a0.Offsets.Select(o => o - shiftBefore), a1.Offsets.Select(o => o - shiftAfter));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_area_is_skipped_and_the_rest_of_the_video_is_found()
    {
        string folder = Directory.CreateTempSubdirectory("raf-video-").FullName;
        try
        {
            // 20,000 בתים אקראיים באמצע הנתונים — כמו אזור שנדרס.
            var tracks = Mp4Index.ReadTracks(Media("h264_aac.mp4"))!;
            var video = tracks.Single(t => t.IsVideo);
            int from = (int)video.Offsets[60];
            string broken = WithoutIndex("h264_aac.mp4", folder, data =>
            {
                new Random(7).NextBytes(data.AsSpan(from, 20_000));
                return data;
            });

            string output = Path.Combine(folder, "rebuilt.mp4");
            var result = Mp4Rebuilder.Rebuild(broken, Media("h264_aac.mp4"), output);

            Assert.True(result.Succeeded, result.Message);
            var lostFrames = video.Offsets.Count(o => o >= from && o < from + 20_000) + 1;
            // כל התמונות שמחוץ לאזור הפגום נמצאו; לכל היותר אחת נוספת (שנחתכה בגבולו) אבדה.
            Assert.InRange(result.VideoFrames, video.Sizes.Count - lostFrames - 1, video.Sizes.Count - lostFrames + 1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_doctor_explains_the_missing_index_and_rebuilds_it_with_a_reference()
    {
        string folder = Directory.CreateTempSubdirectory("raf-video-").FullName;
        try
        {
            string broken = WithoutIndex("h264_aac.mp4", folder);

            var diagnosis = FileDoctor.Diagnose(broken);
            Assert.True(diagnosis.NeedsReferenceVideo);
            Assert.Equal(FileIssueKind.VideoIndexMissing, Assert.Single(diagnosis.Issues).Kind);

            var result = FileDoctor.RepairVideo(broken, Media("h264_aac.mp4"), Path.Combine(folder, "out"));
            Assert.True(result.Succeeded, result.Message);
            Assert.True(result.After!.IsHealthy);
            Assert.EndsWith("(תוקן).mp4", result.OutputPath);

            // סרטון תקין — אין בו מה לתקן.
            Assert.True(FileDoctor.Diagnose(Media("h264_aac.mp4")).IsHealthy);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_reference_index_is_read_with_its_codecs()
    {
        var h264 = Mp4Index.ReadTracks(Media("h264_aac.mp4"))!;
        var v = h264.Single(t => t.IsVideo);
        Assert.Equal("avc1", v.Codec);
        Assert.Equal(4, v.NalLengthSize);
        Assert.Equal(120, v.Sizes.Count);
        Assert.NotEmpty(v.CompositionOffsets);                    // x264 משתמש בתמונות B

        var hevc = Mp4Index.ReadTracks(Media("hevc_aac_mono.mp4"))!;
        Assert.True(hevc.Single(t => t.IsVideo).IsHevc);

        var pcm = Mp4Index.ReadTracks(Media("h264_pcm.mov"))!;
        Assert.Equal(2, pcm.Single(t => t.IsPcm).PcmFrameBytes);
    }
}
