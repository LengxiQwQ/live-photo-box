using LivePhotoBox.Controls;
using LivePhotoBox.Helpers;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScrollPresentationTests
{
    [Fact]
    public void ScanFollow_NoTargetsDuringScanOrCompletion()
    {
        var follow = new ScanFollowPolicy();
        Assert.False(follow.TryGetTarget(500, false, out _));
        follow.Begin(0);
        Assert.False(follow.TryGetTarget(0, false, out _));
        Assert.False(follow.TryGetTarget(128, false, out int first));
        Assert.Equal(-1, first);
        Assert.False(follow.TryGetTarget(256, false, out int next));
        Assert.Equal(-1, next);
        follow.End();
        Assert.False(follow.TryGetTarget(5000, false, out _));
    }

    [Fact]
    public void ScanFollow_RespectsManualPauseAndAppendPosition()
    {
        var follow = new ScanFollowPolicy();
        follow.Begin(0);
        Assert.False(follow.TryGetTarget(128, true, out _));
        Assert.False(follow.TryGetTarget(128, false, out _));
        follow.Begin(128);
        Assert.False(follow.TryGetTarget(256, false, out _));
        follow.Begin(0);
        Assert.False(follow.TryGetTarget(64, false, out _));
    }

    [Fact]
    public void FileNameFit_ReusesOnlyIdenticalTextAndRenderingInputs()
    {
        var cache = new FileNameFitCache(32);
        var key = Key("IMG_123456789.JPG");
        cache.Store(key, "IMG_…6789.JPG");
        Assert.True(cache.TryGet(key, out string text));
        Assert.Equal("IMG_…6789.JPG", text);
        foreach (var changed in new[]
        {
            key with { Text = "other.JPG" }, key with { Width = 220 },
            key with { FontFamily = "other" }, key with { FontSize = 12 },
            key with { Weight = 700 }, key with { Style = 1 }, key with { Stretch = 1 },
            key with { Spacing = 10 }, key with { Language = "zh-CN" },
            key with { FlowDirection = 1 }, key with { ScaleEnabled = false },
            key with { TextScale = 1.5 }, key with { RasterizationScale = 2 }
        }) Assert.False(cache.TryGet(changed, out _));
    }

    [Fact]
    public void FileNameFitCache_IsBoundedAndEvictsLeastRecentlyUsedText()
    {
        var cache = new FileNameFitCache(2);
        cache.Store(Key("A"), "A"); cache.Store(Key("B"), "B");
        Assert.True(cache.TryGet(Key("A"), out _));
        cache.Store(Key("C"), "C");
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(Key("A"), out _));
        Assert.False(cache.TryGet(Key("B"), out _));
        cache.Store(Key("A"), "new-A");
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(Key("A"), out string updated));
        Assert.Equal("new-A", updated);
    }

    [Fact]
    public void FileNameFitCache_ZeroCapacityDoesNotRetainEntries()
    {
        var cache = new FileNameFitCache(0);
        cache.Store(Key("A"), "A");
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Key("A"), out _));
    }

    private static FileNameFitKey Key(string text) => new(text, 180, "Segoe UI", 11, 400, 0, 0, 0, "en-US", 0, true, 1, 1);
}
