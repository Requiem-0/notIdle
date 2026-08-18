using NotIdle.Core;
using Xunit;

namespace NotIdle.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void NormalizeClampsOutOfRangeValues()
    {
        var s = new Settings
        {
            IdleThresholdSeconds = 0,
            MinIntervalSeconds = 1,
            MaxIntervalSeconds = 999_999,
            AutoOffMinutes = -5,
        };

        s.Normalize();

        Assert.Equal(10, s.IdleThresholdSeconds);
        Assert.Equal(15, s.MinIntervalSeconds);
        Assert.Equal(3600, s.MaxIntervalSeconds);
        Assert.Equal(0, s.AutoOffMinutes);
    }

    [Fact]
    public void NormalizeKeepsMaxIntervalAtOrAboveMin()
    {
        var s = new Settings { MinIntervalSeconds = 300, MaxIntervalSeconds = 60 };
        s.Normalize();
        Assert.True(s.MaxIntervalSeconds >= s.MinIntervalSeconds);
    }

    [Fact]
    public void CloneCopiesEveryField()
    {
        var s = new Settings
        {
            Enabled = false,
            IdleThresholdSeconds = 42,
            MinIntervalSeconds = 20,
            MaxIntervalSeconds = 90,
            RestoreCursor = false,
            KeepDisplayOn = false,
            PauseWhenFullScreen = false,
            StartWithWindows = true,
            AutoOffMinutes = 120,
        };

        Settings copy = s.Clone();

        Assert.Equivalent(s, copy, strict: true);
        Assert.NotSame(s, copy);
    }

    [Theory]
    [InlineData(0, "never")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(90, "90 min")]
    public void DescribesAutoOffReadably(int minutes, string expected)
    {
        Assert.Equal(expected, new Settings { AutoOffMinutes = minutes }.DescribeAutoOff());
    }

    [Fact]
    public void SettingsManagerNormalizesAndPublishes()
    {
        var store = new InMemoryStore();
        var manager = new SettingsManager(store);

        Settings? published = null;
        manager.Changed += s => published = s;

        manager.Update(s => s.IdleThresholdSeconds = 1); // below the floor

        Assert.NotNull(published);
        Assert.Equal(10, published!.IdleThresholdSeconds);
        Assert.Equal(10, store.Saved!.IdleThresholdSeconds);
        Assert.Equal(10, manager.Current.IdleThresholdSeconds);
    }

    [Fact]
    public void SettingsManagerHandsOutDefensiveCopies()
    {
        var manager = new SettingsManager(new InMemoryStore());

        Settings first = manager.Current;
        first.IdleThresholdSeconds = 9999;

        Assert.NotEqual(9999, manager.Current.IdleThresholdSeconds);
    }

    [Fact]
    public void SettingsManagerSurvivesAnUnwritableStore()
    {
        var manager = new SettingsManager(new ThrowingStore());

        // A read-only %APPDATA% must degrade to session-only, not take down the app.
        manager.Update(s => s.IdleThresholdSeconds = 60);

        Assert.Equal(60, manager.Current.IdleThresholdSeconds);
    }

    private sealed class InMemoryStore : ISettingsStore
    {
        public Settings? Saved { get; private set; }
        public Settings Load() => new();
        public void Save(Settings settings) => Saved = settings.Clone();
    }

    private sealed class ThrowingStore : ISettingsStore
    {
        public Settings Load() => new();
        public void Save(Settings settings) => throw new UnauthorizedAccessException();
    }
}
