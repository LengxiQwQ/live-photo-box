using System;

namespace LivePhotoBox.Models
{
    // Represents a banner image preset for the home page.
    public class BannerPreset
    {
        // Display name shown below the FlipView.
        public string Name { get; init; } = string.Empty;

        // Unique key used for settings persistence.
        public string Key { get; init; } = string.Empty;

        // ms-appx:/// asset path to the banner image.
        public string AssetPath { get; init; } = string.Empty;

        // 返回预设的显示名称。
        public override string ToString() => Name;
    }
}
