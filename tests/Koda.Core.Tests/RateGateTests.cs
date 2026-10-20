using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>The Android YouTubeRateLimit contract: only 429 arms it, and it never parks a caller.</summary>
public class RateGateTests
{
    [Fact]
    public void FourTwentyNineArmsTheHoldAndOtherStatusesDoNot()
    {
        var gate = new RateGate();
        var holds = new List<TimeSpan>();
        gate.Throttled += hold => holds.Add(hold);

        Assert.False(gate.Hit(500));
        Assert.False(gate.Hit(403)); // a googlevideo 403 is a visitorData verdict, not a throttle
        Assert.False(gate.IsHeld);
        Assert.Empty(holds);

        Assert.True(gate.Hit(429));
        Assert.True(gate.IsHeld);
        Assert.Equal(RateGate.DefaultHold, Assert.Single(holds));
    }

    [Fact]
    public void RetryAfterIsClampedAndAHoldIsNeverShortened()
    {
        var gate = new RateGate();
        gate.Hit(429, TimeSpan.FromHours(5));
        Assert.True(gate.Remaining <= RateGate.MaxHold);
        Assert.True(gate.Remaining > RateGate.MaxHold - TimeSpan.FromMinutes(1));

        gate.Hit(429, TimeSpan.FromSeconds(1)); // shorter: must not undo the longer one
        Assert.True(gate.Remaining > RateGate.DefaultHold);

        gate.Clear();
        Assert.False(gate.IsHeld);
    }
}
