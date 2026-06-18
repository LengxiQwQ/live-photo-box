namespace LivePhotoBox.Models;

/// <summary>
/// Represents a banner image preset for the home page.
/// </summary>
public class BannerPreset
{
    /// <summary>
    /// Display name shown below the FlipView.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Unique key used for settings persistence.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// ms-appx:/// asset path to the banner image.
    /// </summary>
    public string AssetPath { get; init; } = "";

    public override string ToString() => Name;
}
