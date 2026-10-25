using System;
using System.Collections.Generic;
using System.Linq;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

internal enum ProductCapabilityAvailability { Unknown, Available, Unavailable }

internal sealed record ProductCapabilityComponent(
    NativeRuntimeOperationClass Operation,
    ProductCapabilityAvailability Availability,
    NativeRuntimeCapabilityIdentity? RuntimeIdentity);

/// <summary>
/// Observed preparation backends, not a promise that a source profile, preservation
/// request or target writer is supported. Native validates each actual request.
/// A missing coarse capability need not prevent a request that uses passthrough
/// or a supported subset of its source routes.
/// </summary>
internal sealed record ProductCapabilityAssessment(
    OutputProfile? OutputProfile,
    MediaFormatRequirement MediaRequirement,
    ProductCapabilityAvailability Availability,
    IReadOnlyList<ProductCapabilityComponent> Components,
    string Diagnostic)
{
    public bool RequiresRequestValidation => true;
}

internal static class ProductCapabilityQuery
{
    public static ProductCapabilityAssessment Query(OutputProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Assess(profile.MediaRequirement, NativeRuntime.Probe(), profile);
    }

    public static ProductCapabilityAssessment Query(SplitMediaFormat format) =>
        Assess(SplitMediaRequirements.Get(format), NativeRuntime.Probe());

    internal static ProductCapabilityAssessment Assess(MediaFormatRequirement requirement,
        NativeRuntimeInfo runtime, OutputProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(runtime);
        var operations = new List<NativeRuntimeOperationClass>();
        switch (requirement.ImageContainer)
        {
            case ImageContainer.Jpeg: operations.Add(NativeRuntimeOperationClass.JpegCodec); break;
            case ImageContainer.Heic: operations.Add(NativeRuntimeOperationClass.HeicCodec); break;
            case ImageContainer.Unknown: break;
            default: throw new ArgumentOutOfRangeException(nameof(requirement));
        }
        operations.Add(requirement.VideoCodec switch
        {
            VideoCodec.Copy => NativeRuntimeOperationClass.VideoRemux,
            VideoCodec.H264 => NativeRuntimeOperationClass.VideoTranscodeSdrH264,
            VideoCodec.Hevc => NativeRuntimeOperationClass.VideoTranscodeSdrHevc,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement))
        });
        var components = operations.Select(operation =>
        {
            var identity = runtime.CapabilityIdentities.SingleOrDefault(item => item.Operation == operation);
            var available = !runtime.IsAvailable ? ProductCapabilityAvailability.Unavailable
                : identity is null ? ProductCapabilityAvailability.Unknown
                : identity.IsAvailable ? ProductCapabilityAvailability.Available
                // H264 is a coarse all-source-routes capability. Its false value
                // cannot reject a feasible ordinary AVC/MF request, for example.
                : operation == NativeRuntimeOperationClass.VideoTranscodeSdrH264
                    ? ProductCapabilityAvailability.Unknown : ProductCapabilityAvailability.Unavailable;
            return new ProductCapabilityComponent(operation, available, identity);
        }).ToArray();
        var availability = !runtime.IsAvailable ? ProductCapabilityAvailability.Unavailable
            : components.All(item => item.Availability == ProductCapabilityAvailability.Available)
                ? ProductCapabilityAvailability.Available : ProductCapabilityAvailability.Unknown;
        string diagnostic = !runtime.IsAvailable ? runtime.Diagnostic ?? "Native runtime is unavailable."
            : availability == ProductCapabilityAvailability.Available
                ? "Preparation backends available; source profile, preservation and writer validation still required."
                : "Preparation capability is partial or unknown; validate the actual request. Passthrough may not require an encoder.";
        return new(profile, requirement, availability, components, diagnostic);
    }
}
