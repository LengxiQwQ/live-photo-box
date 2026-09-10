using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Core.Tests.Support;

internal static class W2EvidenceWriter
{
    private sealed record OutputEvidence(string RelativePath, string Sha256, bool IsDeclaredArtifact);

    internal static async Task WriteAsync(
        W2EvidenceWorkspace workspace,
        W2SampleSnapshot sample,
        SourceMediaFacts facts,
        ExtractedMediaBundle bundle,
        string sourceBeforeSha256,
        string sourceAfterSha256,
        object independentEvidence,
        CancellationToken cancellationToken = default)
    {
        string originalAfterSha256 = await W2SampleEvidence
            .ComputeSha256Async(sample.OriginalPath, cancellationToken)
            .ConfigureAwait(false);
        var document = new
        {
            schema = "p2-w2-evidence-v1",
            sample = sample.FileName,
            originalPath = sample.OriginalPath,
            cachePath = sample.CachePath,
            originalBeforeSha256 = sample.OriginalSha256,
            originalAfterSha256,
            originalUnchanged = string.Equals(sample.OriginalSha256, originalAfterSha256, StringComparison.OrdinalIgnoreCase),
            cacheBeforeSha256 = sourceBeforeSha256,
            cacheAfterSha256 = sourceAfterSha256,
            cacheUnchanged = string.Equals(sourceBeforeSha256, sourceAfterSha256, StringComparison.OrdinalIgnoreCase),
            inspectorPrimarySha256 = facts.PrimarySha256,
            inspectorSecondarySha256 = facts.SecondarySha256,
            inspectorAuxiliary = facts.AuxiliaryItems.Select((item, index) => new
            {
                index,
                item.IsPresent,
                item.StableIdentity,
                item.Semantic,
                item.OwnerIdentity,
                item.Relationship,
                item.SourceIndex,
                item.ByteOffset,
                item.ByteLength,
                item.Sha256,
                item.Container,
                item.Codec,
                item.Representation,
                item.Ownership
            }),
            inspectorCarriers = facts.PreservationCarriers.Select(carrier => new
            {
                carrier.StableIdentity,
                carrier.Semantic,
                carrier.OwnerIdentity,
                carrier.Relationship,
                carrier.SourceIndex,
                carrier.SourceOffset,
                carrier.SourceLength,
                carrier.SourceSha256,
                carrier.Kind,
                carrier.ArtifactRole
            }),
            outputs = DescribeOutputs(workspace.RootDirectory, bundle),
            descriptors = bundle.AuxiliaryMedia.Select(descriptor => new
            {
                descriptor.ArtifactRole,
                descriptor.StableIdentity,
                descriptor.Semantic,
                descriptor.OwnerIdentity,
                descriptor.Relationship,
                descriptor.SourceIndex,
                descriptor.SourceOffset,
                descriptor.SourceLength,
                descriptor.SourceSha256,
                descriptor.ImageContainer,
                descriptor.Codec,
                descriptor.Representation,
                descriptor.Ownership,
                materializedPath = descriptor.MaterializedArtifact?.Path,
                materializedSha256 = descriptor.MaterializedArtifact?.Sha256
            }),
            carriers = bundle.PreservationCarriers.Select(carrier => new
            {
                carrier.StableIdentity,
                carrier.Semantic,
                carrier.OwnerIdentity,
                carrier.Relationship,
                carrier.SourceIndex,
                carrier.SourceOffset,
                carrier.SourceLength,
                carrier.SourceSha256,
                carrier.Kind
            }),
            independentEvidence
        };

        string path = Path.Combine(workspace.RootDirectory, "evidence.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<OutputEvidence> DescribeOutputs(string root, ExtractedMediaBundle bundle)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(bundle.PrimaryImage.Path);
        Add(bundle.MotionVideo?.Path);
        Add(bundle.GainMap?.Path);
        foreach (AuxiliaryMediaDescriptor descriptor in bundle.AuxiliaryMedia)
        {
            Add(descriptor.MaterializedArtifact?.Path);
        }

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }

        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new OutputEvidence(
                    Path.GetRelativePath(root, path),
                    W2SampleEvidence.ComputeSha256Async(path).GetAwaiter().GetResult(),
                    paths.Contains(path)))
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<OutputEvidence>();
    }
}
