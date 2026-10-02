using System;
using System.Linq;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// Project-owned semantic eligibility rules for image container conversion.
/// Codec support alone is not authority to discard a semantic auxiliary image.
/// </summary>
public static class ImageConversionEligibility
{
    public const string SemanticAuxiliaryBlockedMessage =
        "HDR/GainMap or auxiliary image inputs require the explicit P5 semantic conversion path and are forbidden on ordinary R2 image routes.";

    /// <summary>
    /// Rejects all ordinary R2 routes when inspection establishes GainMap or
    /// auxiliary semantics. Same-container copies are also outside the
    /// ordinary-image R2 contract for these inputs.
    /// </summary>
    public static bool IsAllowed(
        SourceMediaFacts sourceFacts,
        ImageContainer sourceContainer,
        ImageContainer targetContainer,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(sourceFacts);

        if (sourceFacts.GainMap is { IsPresent: true } ||
            sourceFacts.AuxiliaryItems.Any(item => item.IsPresent))
        {
            errorMessage = SemanticAuxiliaryBlockedMessage;
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static bool IsAllowed(
        ImageConversionSourceFacts sourceFacts,
        ImageContainer targetContainer,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(sourceFacts);
        if (sourceFacts.HasGainMap || sourceFacts.HasAuxiliaryMedia)
        {
            errorMessage = SemanticAuxiliaryBlockedMessage;
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static ImageConversionSourceFacts ToConversionFacts(SourceMediaFacts facts) => new()
    {
        Container = facts.PrimaryImage.Container,
        Codec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
        HasGainMap = facts.GainMap is { IsPresent: true },
        HasAuxiliaryMedia = facts.GainMap is { IsPresent: true } || facts.AuxiliaryItems.Any(item => item.IsPresent)
    };
}
