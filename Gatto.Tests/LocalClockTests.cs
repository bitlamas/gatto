using System.Runtime.Versioning;
using Gatto.Core;
using Microsoft.Win32;

namespace Gatto.Tests;

public sealed class LocalClockTests
{
    private static readonly DateTime Early = new(2026, 9, 14, 1, 32, 0);
    private static readonly DateTime Late = new(2026, 9, 14, 14, 21, 0);

    //the registry is a second reader of the same setting, so it can't hide a wrong locale argument by sharing the call
    [SupportedOSPlatform("windows")]
    private static string? RegistryShortTime() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International", "sShortTime", null) as string;

    [Fact]
    public void Windows_formats_a_twelve_hour_and_a_twenty_four_hour_pattern()
    {
        Assert.Equal("1:32 AM", LocalClock.Format(Early, "h:mm tt", "en-US"));
        Assert.Equal("2:21 PM", LocalClock.Format(Late, "h:mm tt", "en-US"));
        Assert.Equal("01:32", LocalClock.Format(Early, "HH:mm", "en-US"));
        Assert.Equal("14:21", LocalClock.Format(Late, "HH:mm", "en-US"));
    }

    [Fact]
    public void A_control_character_in_the_pattern_gives_no_time()
    {
        Assert.Equal("14:21", LocalClock.Format(Late, "HH:mm", "en-US"));
        Assert.Null(LocalClock.Format(Late, "HH:mm''", "en-US"));
        Assert.Null(LocalClock.Format(Late, null, "en-US"));
    }

    [Fact]
    public void The_user_default_pattern_is_the_short_time_format_in_the_regional_settings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = RegistryShortTime();
        Assert.False(string.IsNullOrEmpty(registry));
        Assert.Equal(registry, LocalClock.ShortTimePattern(null));
    }

    [Fact]
    public void Now_formats_the_current_time_with_the_users_own_pattern()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = RegistryShortTime();
        Assert.False(string.IsNullOrEmpty(registry));
        for (var attempt = 0; ; attempt++)
        {
            var before = DateTime.Now;
            var now = LocalClock.Now();
            var after = DateTime.Now;
            if (before.Ticks / TimeSpan.TicksPerSecond != after.Ticks / TimeSpan.TicksPerSecond && attempt < 3) continue;   //a second turned over during the read, so read again
            Assert.Equal(LocalClock.Format(before, registry, null), now);
            return;
        }
    }
}
