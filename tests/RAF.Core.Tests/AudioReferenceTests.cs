using System.Buffers.Binary;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>בניית כותרת WAV שנדרסה בעזרת הקלטה תקינה מאותו מכשיר (WavTransplant).</summary>
public class AudioReferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _output;

    public AudioReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"raf-wav-{Guid.NewGuid():N}");
        _output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>הקלטה: שני ערוצים שונים (כדי שהחלפה ביניהם תתגלה), פרטי הקלטה לפני השמע ואחריו.</summary>
    private static (byte[] File, byte[] Audio) Wav(int seed, int seconds = 3, int bits = 16, int rate = 44100)
    {
        var random = new Random(seed);
        int frames = rate * seconds, bytes = bits / 8, align = bytes * 2;
        byte[] audio = new byte[frames * align];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < 2; c++)
            {
                double x = Math.Sin(i * (c == 0 ? 0.03 : 0.011) + seed) * (c == 0 ? 0.6 : 0.2) + (random.NextDouble() - 0.5) * 0.02;
                int v = (int)(x * ((1 << (bits - 1)) - 1));
                for (int k = 0; k < bytes; k++) audio[i * align + c * bytes + k] = (byte)(v >> (8 * k));
            }

        var s = new MemoryStream();
        void Chunk(string id, byte[] body)
        {
            s.Write(System.Text.Encoding.ASCII.GetBytes(id));
            s.Write(BitConverter.GetBytes(body.Length));
            s.Write(body);
            if (body.Length % 2 == 1) s.WriteByte(0);
        }
        s.Write("RIFF\0\0\0\0WAVE"u8);
        byte[] fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), rate * align);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        Chunk("fmt ", fmt);
        Chunk("LIST", "INFOISFT\u000e\0\0\0Recorder 1.0\0\0"u8.ToArray());
        Chunk("data", audio);
        Chunk("LIST", "INFOICMT\u0006\0\0\0note\0\0"u8.ToArray());
        byte[] file = s.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        return (file, audio);
    }

    private static byte[] Audio(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        int at = d.AsSpan().IndexOf("data"u8);
        int length = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
        return d.AsSpan(at + 8, length).ToArray();
    }

    private static byte[] Damage(byte[] data, int count)
    {
        byte[] damaged = (byte[])data.Clone();
        new Random(count).NextBytes(damaged.AsSpan(0, count));
        return damaged;
    }

    [Theory]
    [InlineData(40)]        // עד הסימן "data" — הוא שרד
    [InlineData(78)]        // כל הכותרת, כולל הסימן — עד השמע עצמו
    public void Header_lost_is_rebuilt_from_the_reference(int damage)
    {
        var (file, audio) = Wav(1);
        string broken = Write("broken.wav", Damage(file, damage));
        string donor = Write("donor.wav", Wav(2, seconds: 1).File);

        Assert.True(FileDoctor.Diagnose(broken).NeedsReferenceAudio);
        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(audio, Audio(r.OutputPath!));          // אותו שמע, בלי פרטי ההקלטה שבסוף, ובלי החלפת ערוצים
    }

    [Fact]
    public void Reference_with_other_bit_depth_is_rejected()
    {
        var (file, _) = Wav(3);
        string broken = Write("broken.wav", Damage(file, 78));
        var r = FileDoctor.RepairPhoto(broken, Write("donor.wav", Wav(4, seconds: 1, bits: 24).File), _output);
        Assert.Null(r.OutputPath);
        Assert.Contains("באותן הגדרות", r.Message);
    }

    [Fact]
    public void Healthy_and_signature_only_damage_do_not_ask_for_a_reference()
    {
        var (file, _) = Wav(5);
        Assert.False(FileDoctor.Diagnose(Write("ok.wav", file)).NeedsReferenceAudio);
        Assert.False(FileDoctor.Diagnose(Write("sig.wav", Damage(file, 4))).NeedsReferenceAudio);
        Assert.NotNull(WavTransplant.DescribeReference(Write("text.wav", new byte[5000])));
    }
}
