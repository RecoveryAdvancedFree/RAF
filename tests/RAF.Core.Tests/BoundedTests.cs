using System.Diagnostics;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// כונן גוסס עלול להיתקע בתוך מנהל ההתקן לדקות. הבדיקות מדמות פעולה
/// תקועה ומוודאות שהתוכנה ממשיכה בזמן, ושתשובה מאוחרת עדיין מגיעה.
/// </summary>
public class BoundedTests
{
    [Fact]
    public void A_stuck_device_does_not_hold_the_caller()
    {
        using var release = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();

        bool inTime = Bounded.TryRun(() => { release.Wait(); return 42; }, TimeSpan.FromMilliseconds(200), out int value);

        Assert.False(inTime);
        Assert.Equal(0, value);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"waited {clock.ElapsedMilliseconds} ms");
        release.Set();
    }

    [Fact]
    public void A_late_answer_is_still_delivered()
    {
        using var release = new ManualResetEventSlim(false);
        using var delivered = new ManualResetEventSlim(false);
        int lateValue = 0;

        Bounded.TryRun(() => { release.Wait(); return 7; }, TimeSpan.FromMilliseconds(100), out _,
            late: v => { lateValue = v; delivered.Set(); });

        release.Set();
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, lateValue);
    }

    [Fact]
    public void A_quick_answer_is_returned_and_not_delivered_twice()
    {
        int lateCalls = 0;
        bool inTime = Bounded.TryRun(() => "ok", TimeSpan.FromSeconds(5), out var value, late: _ => lateCalls++);

        Assert.True(inTime);
        Assert.Equal("ok", value);
        Thread.Sleep(100);
        Assert.Equal(0, lateCalls);
    }

    [Fact]
    public void A_device_that_throws_counts_as_no_information()
    {
        bool inTime = Bounded.TryRun<string>(() => throw new IOException("סקטור פגום"), TimeSpan.FromSeconds(5), out var value);

        Assert.True(inTime);
        Assert.Null(value);
    }
}
