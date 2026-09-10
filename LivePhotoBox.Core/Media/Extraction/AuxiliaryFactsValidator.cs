using System;
using System.Collections.Generic;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Shared fail-closed validation for the auxiliary and preservation
/// relationships carried from Native inspection into the managed plan.
///
/// This is deliberately a structural validator.  It does not infer a
/// protocol relationship from bytes and it does not turn a descriptor into an
/// authority record; it only rejects an incomplete or internally inconsistent
/// snapshot before that snapshot reaches an extraction ABI.
/// </summary>
internal static class AuxiliaryFactsValidator
{
    private const int StableIdentityCapacity = 95;
    private const int OwnerIdentityCapacity = 95;
    private const int RelationshipCapacity = 63;
    private const int SemanticCapacity = 63;

    internal static void Validate(SourceMediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.AuxiliaryItems is null)
        {
            throw Invalid("Auxiliary item collection cannot be null.", MediaArtifactKind.AuxiliaryItem);
        }

        if (facts.PreservationCarriers is null)
        {
            throw Invalid("Preservation carrier collection cannot be null.", MediaArtifactKind.SourceContainer);
        }

        if (facts.AuxiliaryItems.Count > NativeRuntime.MaxAuxiliaryItems)
        {
            throw Invalid(
                $"Auxiliary item count exceeds the native ABI capacity of {NativeRuntime.MaxAuxiliaryItems}.",
                MediaArtifactKind.AuxiliaryItem);
        }

        var itemIds = new HashSet<uint>();
        var stableIdentities = new HashSet<string>(StringComparer.Ordinal);
        var sourceRangesAndSemantics = new HashSet<(int SourceIndex, long Offset, long Length, string Semantic)>();

        for (int index = 0; index < facts.AuxiliaryItems.Count; index++)
        {
            AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[index];
            if (!auxiliary.IsPresent)
            {
                throw Invalid(
                    $"Auxiliary item {index} is not marked present.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            ValidateAuxiliary(index, auxiliary);

            if (!itemIds.Add(auxiliary.ItemId))
            {
                throw Invalid(
                    $"Auxiliary item {index} reuses item identity {auxiliary.ItemId}.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            if (!stableIdentities.Add(auxiliary.StableIdentity))
            {
                throw Invalid(
                    $"Auxiliary item {index} reuses stable identity '{auxiliary.StableIdentity}'.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            if (!sourceRangesAndSemantics.Add((
                    auxiliary.SourceIndex,
                    auxiliary.ByteOffset,
                    auxiliary.ByteLength,
                    auxiliary.Semantic)))
            {
                throw Invalid(
                    $"Auxiliary item {index} duplicates a source range and semantic relationship.",
                    MediaArtifactKind.AuxiliaryItem);
            }
        }

        ValidateGainMap(facts.GainMap, facts.AuxiliaryItems);
        ValidatePreservationCarriers(facts.PreservationCarriers);
    }

    private static void ValidateAuxiliary(int index, AuxiliaryMediaFacts auxiliary)
    {
        if (auxiliary.Container is not (ImageContainer.Jpeg or ImageContainer.Heic))
        {
            throw Invalid(
                $"Auxiliary item {index} has an unknown or unsupported image container.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.Representation is < AuxiliaryRepresentation.Embedded or > AuxiliaryRepresentation.Materialized)
        {
            throw Invalid(
                $"Auxiliary item {index} has an unknown representation.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.Ownership is < AuxiliaryOwnership.Primary or > AuxiliaryOwnership.Auxiliary)
        {
            throw Invalid(
                $"Auxiliary item {index} has an unknown ownership value.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.Codec is < AuxiliaryCodec.Jpeg or > AuxiliaryCodec.Copy)
        {
            throw Invalid(
                $"Auxiliary item {index} has no concrete codec identity.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.ItemId == 0 || auxiliary.ByteOffset < 0 || auxiliary.ByteLength <= 0)
        {
            throw Invalid(
                $"Auxiliary item {index} has an invalid item identity or source range.",
                MediaArtifactKind.AuxiliaryItem,
                auxiliary.ByteOffset,
                auxiliary.ByteLength);
        }

        if (auxiliary.SourceIndex is < 0 or > 1)
        {
            throw Invalid(
                $"Auxiliary item {index} has invalid SourceIndex {auxiliary.SourceIndex}.",
                MediaArtifactKind.AuxiliaryItem);
        }

        RequireBoundedText(auxiliary.StableIdentity, "stable identity", StableIdentityCapacity, index);
        RequireBoundedText(auxiliary.Semantic, "semantic", SemanticCapacity, index);
        RequireBoundedText(auxiliary.OwnerIdentity, "owner identity", OwnerIdentityCapacity, index);
        RequireBoundedText(auxiliary.Relationship, "relationship", RelationshipCapacity, index);
        ValidateSha256(auxiliary.Sha256, $"Auxiliary item {index}");

        if (auxiliary.Ownership == AuxiliaryOwnership.Primary &&
            !string.Equals(auxiliary.OwnerIdentity, "primary:0", StringComparison.Ordinal))
        {
            throw Invalid(
                $"Primary-owned auxiliary item {index} must be owned by 'primary:0'.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.Ownership == AuxiliaryOwnership.Auxiliary &&
            string.Equals(auxiliary.OwnerIdentity, "primary:0", StringComparison.Ordinal))
        {
            throw Invalid(
                $"Auxiliary-owned item {index} cannot claim primary owner identity 'primary:0'.",
                MediaArtifactKind.AuxiliaryItem);
        }

        if (auxiliary.Container == ImageContainer.Heic &&
            (string.Equals(auxiliary.ItemType, "grid", StringComparison.Ordinal) ||
             string.Equals(auxiliary.ItemType, "tmap", StringComparison.Ordinal) ||
             auxiliary.Dependencies.Count > 0))
        {
            if (!auxiliary.GraphComplete)
            {
                throw Invalid(
                    $"HEIF auxiliary item {index} has an incomplete derived-item graph.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            var dependencyIds = new HashSet<uint>();
            foreach (HeifDependencyFacts dependency in auxiliary.Dependencies)
            {
                if (dependency.ItemId == 0 || !dependencyIds.Add(dependency.ItemId) ||
                    dependency.ByteOffset < 0 || dependency.ByteLength <= 0 ||
                    string.IsNullOrWhiteSpace(dependency.ItemType) ||
                    Encoding.UTF8.GetByteCount(dependency.ItemType) > 7)
                {
                    throw Invalid(
                        $"HEIF auxiliary item {index} has a duplicate or invalid dependency graph entry.",
                        MediaArtifactKind.AuxiliaryItem);
                }
            }
        }
    }

    private static void ValidateGainMap(
        GainMapFacts? gainMap,
        IReadOnlyList<AuxiliaryMediaFacts> auxiliaryItems)
    {
        if (gainMap is null || !gainMap.IsPresent)
        {
            return;
        }

        if (gainMap.Container is not (ImageContainer.Jpeg or ImageContainer.Heic) ||
            gainMap.Representation is < AuxiliaryRepresentation.Embedded or > AuxiliaryRepresentation.Materialized ||
            gainMap.Ownership is < AuxiliaryOwnership.Primary or > AuxiliaryOwnership.Auxiliary ||
            gainMap.OwnerArtifactRole != (gainMap.Ownership == AuxiliaryOwnership.Primary
                ? MediaArtifactKind.PrimaryImage
                : MediaArtifactKind.AuxiliaryItem) ||
            gainMap.ItemId == 0 || gainMap.ByteOffset < 0 || gainMap.ByteLength <= 0 ||
            string.IsNullOrWhiteSpace(gainMap.Relationship) ||
            ContainsNul(gainMap.Relationship) ||
            Encoding.UTF8.GetByteCount(gainMap.Relationship) > RelationshipCapacity)
        {
            throw Invalid("GainMap facts are incomplete or have inconsistent ownership.", MediaArtifactKind.GainMap);
        }

        if (gainMap.AuxiliaryIndex >= (uint)auxiliaryItems.Count)
        {
            throw Invalid("GainMap facts must reference one unique auxiliary item.", MediaArtifactKind.GainMap);
        }

        AuxiliaryMediaFacts auxiliary = auxiliaryItems[(int)gainMap.AuxiliaryIndex];
        if (!auxiliary.IsPresent ||
            !string.Equals(auxiliary.Semantic, "GainMap", StringComparison.Ordinal) ||
            auxiliary.Container != gainMap.Container ||
            auxiliary.Representation != gainMap.Representation ||
            auxiliary.Ownership != gainMap.Ownership ||
            auxiliary.ItemId != gainMap.ItemId ||
            auxiliary.ByteOffset != gainMap.ByteOffset ||
            auxiliary.ByteLength != gainMap.ByteLength ||
            !string.Equals(auxiliary.Relationship, gainMap.Relationship, StringComparison.Ordinal))
        {
            throw Invalid(
                "GainMap facts are not bound to the referenced auxiliary relationship.",
                MediaArtifactKind.GainMap);
        }
    }

    private static void ValidatePreservationCarriers(IReadOnlyList<PreservationCarrier> carriers)
    {
        if (carriers.Count > NativeRuntime.MaxAuxiliaryItems)
        {
            throw Invalid(
                $"Preservation carrier count exceeds the native ABI capacity of {NativeRuntime.MaxAuxiliaryItems}.",
                MediaArtifactKind.SourceContainer);
        }

        var stableIdentities = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < carriers.Count; index++)
        {
            PreservationCarrier carrier = carriers[index];
            if (carrier.Kind is <= PreservationCarrierKind.Unknown or > PreservationCarrierKind.OpaqueFragment ||
                carrier.ArtifactRole is < MediaArtifactKind.PrimaryImage or > MediaArtifactKind.SourceContainer ||
                carrier.SourceIndex is < 0 or > 1 ||
                carrier.SourceOffset < 0 || carrier.SourceLength <= 0)
            {
                throw Invalid(
                    $"Preservation carrier {index} is incomplete or has an invalid range.",
                    MediaArtifactKind.SourceContainer);
            }

            RequireBoundedText(carrier.StableIdentity, "preservation carrier stable identity", 95, index);
            RequireBoundedText(carrier.Semantic, "preservation carrier semantic", 95, index);
            RequireBoundedText(carrier.OwnerIdentity, "preservation carrier owner identity", 95, index);
            RequireBoundedText(carrier.Relationship, "preservation carrier relationship", 95, index);
            ValidateSha256(carrier.SourceSha256, $"Preservation carrier {index}");

            if (!stableIdentities.Add(carrier.StableIdentity))
            {
                throw Invalid(
                    $"Preservation carrier {index} reuses stable identity '{carrier.StableIdentity}'.",
                    MediaArtifactKind.SourceContainer);
            }
        }
    }

    private static void RequireBoundedText(string? value, string field, int maxUtf8Bytes, int index)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsNul(value) || Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw Invalid(
                $"Auxiliary relationship {index} has an invalid {field}.",
                MediaArtifactKind.AuxiliaryItem);
        }
    }

    private static void ValidateSha256(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{field} SHA-256 is required.", MediaArtifactKind.AuxiliaryItem);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value.Trim());
        }
        catch (FormatException ex)
        {
            throw Invalid($"{field} SHA-256 is malformed.", MediaArtifactKind.AuxiliaryItem, innerException: ex);
        }

        if (bytes.Length != 32 || IsAllZero(bytes))
        {
            throw Invalid($"{field} SHA-256 must be a nonzero 32-byte digest.", MediaArtifactKind.AuxiliaryItem);
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0) return false;
        }
        return true;
    }

    private static bool ContainsNul(string value) => value.IndexOf('\0') >= 0;

    private static ExtractionException Invalid(
        string message,
        MediaArtifactKind artifactKind,
        long? offset = null,
        long? length = null,
        Exception? innerException = null) =>
        new(
            ExtractionFailureCategory.InvalidFacts,
            message,
            artifactKind,
            offset: offset,
            length: length,
            innerException: innerException);
}
