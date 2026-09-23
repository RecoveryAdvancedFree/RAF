using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות נתיב הקריאה הגולמית מול קובץ אמיתי.
///
/// RawDevice פותח התקנים עם FILE_FLAG_NO_BUFFERING, שמטיל דרישות יישור
/// על ההיסט, על האורך ועל כתובת המאגר בזיכרון. כשל שקט כאן מחזיר אפס בתים,
/// והשכבות שמעל ממלאות אפסים — כלומר קובץ משוחזר שנראה תקין בגודלו
/// אך תוכנו ריק לחלוטין. לכן הנתיב הזה נבדק ישירות.
/// </summary>
public class RawDeviceTests : IDisposable
{
    private readonly string _path;
    private readonly byte[] _content;

    public RawDeviceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"raf-rawdevice-{Guid.NewGuid():N}.bin");

        // תוכן מזוהה, גדול מספיק כדי לכסות ריבוי סקטורים.
        _content = new byte[1024 * 1024];
        for (int i = 0; i < _content.Length; i++)
            _content[i] = (byte)(i * 31 + 7);

        File.WriteAllBytes(_path, _content);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    private RawDevice Open()
        => RawDevice.TryOpen(_path, sectorSize: 512)
           ?? throw new IOException($"לא ניתן לפתוח את קובץ הבדיקה. Win32: {RawDevice.LastError}");

    [Theory]
    [InlineData(0, 4096)]              // מיושר לחלוטין
    [InlineData(0, 512 * 1024)]        // קריאה גדולה, כמו בחילוץ קובץ
    [InlineData(512, 4096)]            // היסט מיושר לסקטור
    [InlineData(100, 1000)]            // היסט ואורך שאינם מיושרים
    [InlineData(700 * 1024, 64 * 1024)] // היסט גדול בתוך הקובץ
    [InlineData(1023 * 1024, 1024)]    // הסקטורים האחרונים
    public void Reads_return_the_real_bytes(long offset, int length)
    {
        using var device = Open();

        byte[] actual = device.ReadBlock(offset, length);

        Assert.Equal(length, actual.Length);
        Assert.Equal(_content.AsSpan((int)offset, length).ToArray(), actual);
    }

    [Fact]
    public void Read_does_not_silently_return_zeros()
    {
        using var device = Open();

        byte[] actual = device.ReadBlock(0, 64 * 1024);

        // זהו הכשל שיצר קבצים משוחזרים ריקים: קריאה שנכשלת ומוחזרת כאפסים.
        Assert.Contains(actual, b => b != 0);
    }

    [Fact]
    public void Reading_past_the_end_returns_what_exists_rather_than_padding()
    {
        using var device = Open();

        byte[] actual = device.ReadBlock(_content.Length - 512, 4096);

        // אסור שהקריאה תמציא בתים שאינם קיימים בקובץ.
        Assert.True(actual.Length <= 4096);
        Assert.Equal(_content.AsSpan(_content.Length - 512, actual.Length).ToArray(), actual);
    }
}
