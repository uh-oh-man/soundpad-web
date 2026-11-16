using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>2.45: guests have no Temp or Keep storage limits; the saved allowances are left as they are.</summary>
public sealed class NoLimitsTests
{
    [Fact]
    public void AGuestIsNeverPausedOrAskedToWaitWhateverTheSavedAllowances()
    {
        var budgets = new GuestBudgets();
        var tiny = new KeepSettings(TempMb: 1, KeptMb: 1);
        const long big = 500L * 1024 * 1024;
        Assert.Null(budgets.CheckCreate("g", "G", big, tiny, _ => true));
        Assert.Null(budgets.CheckKeep("g", "G", big, tiny));
        Assert.Null(budgets.ReserveKeep("g", "G", big, tiny));
        Assert.Null(budgets.ReserveKeep("g", "G", big, tiny));
        Assert.Empty(budgets.Requests());
        // The saved settings are not changed: an older companion reads the same allowances.
        Assert.Equal(1, tiny.TempMb);
        Assert.Equal(1, tiny.KeptMb);
    }
}
