using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// Project-owned semantic eligibility rules for image container conversion.
/// Codec support alone is not authority to discard a semantic auxiliary image.
/// </summary>
public static class ImageConversionEligibility
{
    public const string GainMapHeicToJpegBlockedMessage =
        "HDR/GainMap HEIC to JPEG requires the explicit P5 semantic conversion path. Plain SDR JPEG fallback is forbidden.";

    /// <summary>
    /// Rejects a lossy, primary-only HEIC-to-JPEG conversion when the project
    /// inspector has established that the source contains a GainMap semantic.
    /// P5 owns the semantic conversion; R5 must not manufacture a successful
    /// SDR-only result in its place.
    /// </summary>
    public static bool IsAllowed(
        SourceMediaFacts sourceFacts,
        ImageContainer sourceContainer,
        ImageContainer targetContainer,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(sourceFacts);

        if (sourceContainer == ImageContainer.Heic &&
            targetContainer == ImageContainer.Jpeg &&
            sourceFacts.GainMap is { IsPresent: true })
        {
            errorMessage = GainMapHeicToJpegBlockedMessage;
            return false;
        }

        errorMessage = null;
        return true;
    }
}
