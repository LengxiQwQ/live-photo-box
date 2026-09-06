using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

public interface ITargetedPostCleanVerifier
{
    Task VerifyPostCleanAsync(
        SourceMediaFacts originalFacts,
        ProtocolCleanupPlan cleanupPlan,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken = default);
}

public sealed class TargetedPostCleanVerifier : ITargetedPostCleanVerifier
{
    private readonly ISourceInspector? _customInspector;

    public TargetedPostCleanVerifier(ISourceInspector? inspector = null)
    {
        if (inspector != null && inspector.GetType() != typeof(SourceInspector))
        {
            _customInspector = inspector;
        }
    }

    public async Task VerifyPostCleanAsync(
        SourceMediaFacts originalFacts,
        ProtocolCleanupPlan cleanupPlan,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalFacts);
        ArgumentNullException.ThrowIfNull(cleanupPlan);
        ArgumentNullException.ThrowIfNull(stagedImgPath);

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(stagedImgPath) || new FileInfo(stagedImgPath).Length == 0)
        {
            throw new CleanerException(
                CleanerFailureCategory.MediaInvalid,
                CleanerFailureStage.PostCleanInspection,
                originalFacts.Protocol,
                "Cleaned image artifact is missing or empty.",
                MediaArtifactKind.PrimaryImage);
        }

        if (stagedVidPath != null && (!File.Exists(stagedVidPath) || new FileInfo(stagedVidPath).Length == 0))
        {
            throw new CleanerException(
                CleanerFailureCategory.MediaInvalid,
                CleanerFailureStage.PostCleanInspection,
                originalFacts.Protocol,
                "Cleaned video artifact is missing or empty.",
                MediaArtifactKind.MotionVideo);
        }

        if (_customInspector != null)
        {
            await VerifyWithCustomInspectorAsync(originalFacts, stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] imgBytes = await File.ReadAllBytesAsync(stagedImgPath, cancellationToken).ConfigureAwait(false);

        byte[]? tiffBytes = MetadataPreservationVerifier.ExtractTiff(imgBytes, stagedImgPath);
        var tiff = tiffBytes != null ? MetadataPreservationVerifier.ParseTiff(tiffBytes) : null;

        bool hasMakerNoteAction = cleanupPlan.Actions.Any(a =>
            a.StructureKind == ResidueStructureKind.ExifMakerNoteTag ||
            a.ResidueId.Contains("makernote", StringComparison.OrdinalIgnoreCase) ||
            a.OwnerProtocol == SourceProtocol.AppleLivePhoto);

        if (hasMakerNoteAction && tiff?.MakerNote != null && tiff.MakerNote.Length > 0)
        {
            var appleEntries = MetadataPreservationVerifier.ParseAppleMakerNote(tiff.MakerNote);
            var liveTags = new ushort[] { 0x0011, 0x0017, 0x0025, 0x002b };
            foreach (var entry in appleEntries)
            {
                if (liveTags.Contains(entry.Tag))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        originalFacts.Protocol,
                        $"Post-clean targeted check failed: Apple MakerNote live tag 0x{entry.Tag:X4} is still present in cleaned image.",
                        MediaArtifactKind.PrimaryImage);
                }
            }
        }

        string xmp = MetadataPreservationVerifier.ExtractXmp(imgBytes, stagedImgPath);
        if (!string.IsNullOrEmpty(xmp))
        {
            XDocument? xmpDoc = null;
            try
            {
                xmpDoc = XDocument.Parse(xmp);
            }
            catch
            {
                // Fall back to text matching if XMP is malformed
            }

            bool hasGoogleAction = cleanupPlan.Actions.Any(a =>
                a.OwnerProtocol is SourceProtocol.GoogleMotionPhotoV2 or SourceProtocol.GoogleMicroVideoV1 ||
                a.ResidueId.Contains("gcamera", StringComparison.OrdinalIgnoreCase) ||
                a.ResidueId.Contains("microvideo", StringComparison.OrdinalIgnoreCase));

            if (hasGoogleAction || originalFacts.Protocol is SourceProtocol.GoogleMotionPhotoV2 or SourceProtocol.GoogleMicroVideoV1)
            {
                bool googleResidueFound = false;
                if (xmpDoc != null)
                {
                    XNamespace gcam = "http://ns.google.com/photos/1.0/camera/";
                    XNamespace gcont = "http://ns.google.com/photos/1.0/container/";
                    XNamespace gitem = "http://ns.google.com/photos/1.0/container/item/";
                    googleResidueFound = xmpDoc.Descendants().Any(e =>
                        (e.Name.Namespace == gcam && ((e.Name.LocalName == "MicroVideo" && e.Value.Trim() == "1") || (e.Name.LocalName == "MotionPhoto" && e.Value.Trim() == "1"))) ||
                        (e.Name.Namespace == gcont && e.Name.LocalName == "Item" && e.Attributes().Any(a => (a.Name.Namespace == gitem || a.Name.Namespace == gcont) && a.Name.LocalName == "Semantic" && a.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))) ||
                        e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace == gcam &&
                            ((a.Name.LocalName == "MicroVideo" && a.Value.Trim() == "1") ||
                             (a.Name.LocalName == "MotionPhoto" && a.Value.Trim() == "1") ||
                             a.Name.LocalName == "MicroVideoOffset")));
                }
                else
                {
                    googleResidueFound = xmp.Contains("MicroVideo=\"1\"", StringComparison.OrdinalIgnoreCase) ||
                        xmp.Contains("GCamera:MicroVideo", StringComparison.OrdinalIgnoreCase) ||
                        xmp.Contains("MicroVideoOffset", StringComparison.OrdinalIgnoreCase);
                }

                if (googleResidueFound)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        originalFacts.Protocol,
                        "Post-clean targeted check failed: Google MicroVideo / MotionPhoto XMP marker is still present in cleaned image.",
                        MediaArtifactKind.PrimaryImage);
                }
            }

            bool hasSamsungAction = cleanupPlan.Actions.Any(a =>
                a.OwnerProtocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic ||
                a.ResidueId.Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
                a.Selector.Contains("MotionPhoto", StringComparison.OrdinalIgnoreCase));

            if (hasSamsungAction || originalFacts.Protocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
            {
                if (xmp.Contains("MotionPhoto_Data", StringComparison.OrdinalIgnoreCase) ||
                    xmp.Contains("Embedded_Video_Data", StringComparison.OrdinalIgnoreCase) ||
                    xmp.Contains("<Camera:MotionPhoto>1</Camera:MotionPhoto>", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        originalFacts.Protocol,
                        "Post-clean targeted check failed: Samsung MotionPhoto XMP residue is still present in cleaned image.",
                        MediaArtifactKind.PrimaryImage);
                }
            }

            foreach (var action in cleanupPlan.Actions)
            {
                if (action.StructureKind == ResidueStructureKind.XmpProperty && !string.IsNullOrEmpty(action.Selector))
                {
                    bool propPresent = xmpDoc != null
                        ? CheckXmpPropertyPresentInDoc(xmpDoc, action)
                        : xmp.Contains(action.Selector, StringComparison.OrdinalIgnoreCase);

                    if (propPresent)
                    {
                        throw new CleanerException(
                            CleanerFailureCategory.ProtocolStillDetected,
                            CleanerFailureStage.PostCleanInspection,
                            originalFacts.Protocol,
                            $"Post-clean targeted check failed: targeted XMP selector '{action.Selector}' still present.",
                            action.ArtifactRole);
                    }
                }
            }
        }

        bool hasTailAction = cleanupPlan.Actions.Any(a =>
            a.StructureKind is ResidueStructureKind.ProtocolTailRange or ResidueStructureKind.SefEntry ||
            a.ResidueId.Contains("tail", StringComparison.OrdinalIgnoreCase) ||
            a.ResidueId.Contains("trailer", StringComparison.OrdinalIgnoreCase));

        if (hasTailAction || originalFacts.ProtocolTailLength > 0)
        {
            if (imgBytes.Length >= 32)
            {
                int tailCheckLen = Math.Min(imgBytes.Length, 256);
                var tailSpan = imgBytes.AsSpan(imgBytes.Length - tailCheckLen, tailCheckLen);
                if (tailSpan.IndexOf("MotionPhoto_Data"u8) >= 0)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        originalFacts.Protocol,
                        "Post-clean targeted check failed: MotionPhoto trailer signature still present at tail of cleaned image.",
                        MediaArtifactKind.PrimaryImage);
                }
            }
        }

        if (stagedVidPath != null)
        {
            bool hasAppleVidAction = cleanupPlan.Actions.Any(a =>
                a.ArtifactRole == MediaArtifactKind.MotionVideo &&
                (a.StructureKind == ResidueStructureKind.QuickTimeMdtaKey ||
                 a.Selector.Contains("content.identifier", StringComparison.OrdinalIgnoreCase)));

            if (hasAppleVidAction || originalFacts.Protocol == SourceProtocol.AppleLivePhoto)
            {
                bool hasCid = await CheckQuickTimeContentIdentifierAsync(stagedVidPath, cancellationToken).ConfigureAwait(false);
                if (hasCid)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        originalFacts.Protocol,
                        "Post-clean targeted check failed: Apple QuickTime Content Identifier still present in cleaned video.",
                        MediaArtifactKind.MotionVideo);
                }
            }
        }
    }

    private async Task VerifyWithCustomInspectorAsync(
        SourceMediaFacts facts,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken)
    {
        bool isDualSource = facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };

        var imgRecheck = await _customInspector!.InspectAsync(stagedImgPath, null, cancellationToken).ConfigureAwait(false);
        if (imgRecheck.Protocol != SourceProtocol.NonLive ||
            imgRecheck.MotionVideo != null ||
            imgRecheck.ProtocolTailLength != 0 ||
            imgRecheck.PairingIdentifier != null)
        {
            throw new CleanerException(
                CleanerFailureCategory.ProtocolStillDetected,
                CleanerFailureStage.PostCleanInspection,
                facts.Protocol,
                $"Post-clean inspection failed: image artifact still recognized as {imgRecheck.Protocol} (PairingId='{imgRecheck.PairingIdentifier}').",
                MediaArtifactKind.PrimaryImage);
        }

        if (stagedVidPath != null)
        {
            var vidRecheck = await _customInspector.InspectAsync(stagedVidPath, null, cancellationToken).ConfigureAwait(false);
            if (vidRecheck.Protocol != SourceProtocol.NonLive ||
                vidRecheck.ProtocolTailLength != 0 ||
                (isDualSource && vidRecheck.PairingIdentifier != null) ||
                (vidRecheck.PairingIdentifier != null && vidRecheck.PairingIdentifier == imgRecheck.PairingIdentifier))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean inspection failed: video artifact still recognized as {vidRecheck.Protocol} (PairingId='{vidRecheck.PairingIdentifier}').",
                    MediaArtifactKind.MotionVideo);
            }
        }

        if (isDualSource && stagedVidPath != null)
        {
            var pairRecheck = await _customInspector.InspectAsync(stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);
            if (pairRecheck.Protocol != SourceProtocol.NonLive ||
                pairRecheck.PairingIdentifier != null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean bundle inspection failed: pair still recognized as {pairRecheck.Protocol} (PairingId='{pairRecheck.PairingIdentifier}').");
            }
        }
    }

    private static async Task<bool> CheckQuickTimeContentIdentifierAsync(string videoPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(videoPath)) return false;

        using var fs = File.OpenRead(videoPath);
        byte[] header = new byte[8];

        while (fs.Position + 8 <= fs.Length)
        {
            long boxStart = fs.Position;
            int read = await fs.ReadAsync(header.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
            if (read < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);

            long boxSize = size32;
            if (size32 == 1)
            {
                byte[] extHeader = new byte[8];
                read = await fs.ReadAsync(extHeader.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
                if (read < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extHeader);
            }
            else if (size32 == 0)
            {
                boxSize = fs.Length - boxStart;
            }

            if (boxSize < 8 || boxStart + boxSize > fs.Length) break;

            if (type == "moov")
            {
                long moovBodySize = boxSize - (fs.Position - boxStart);
                if (moovBodySize > 0 && moovBodySize <= 10 * 1024 * 1024)
                {
                    byte[] moovBytes = new byte[moovBodySize];
                    int r = await fs.ReadAsync(moovBytes.AsMemory(0, (int)moovBodySize), cancellationToken).ConfigureAwait(false);
                    if (r > 0)
                    {
                        var span = moovBytes.AsSpan(0, r);
                        if (span.IndexOf("com.apple.quicktime.content.identifier"u8) >= 0)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            fs.Seek(boxStart + boxSize, SeekOrigin.Begin);
        }

        return false;
    }

    private static bool CheckXmpPropertyPresentInDoc(XDocument doc, PlannedCleanupAction action)
    {
        string selector = action.Selector ?? "";
        string prefix = "";
        string localName = selector;
        int colonIdx = selector.IndexOf(':');
        if (colonIdx >= 0)
        {
            prefix = selector.Substring(0, colonIdx);
            localName = selector.Substring(colonIdx + 1);
        }

        string? expectedNs = null;
        if (action.OwnerProtocol is SourceProtocol.GoogleMotionPhotoV2 or SourceProtocol.GoogleMicroVideoV1)
        {
            if (prefix.Equals("Container", StringComparison.OrdinalIgnoreCase))
                expectedNs = "http://ns.google.com/photos/1.0/container/";
            else if (prefix.Equals("Item", StringComparison.OrdinalIgnoreCase))
                expectedNs = "http://ns.google.com/photos/1.0/container/item/";
            else
                expectedNs = "http://ns.google.com/photos/1.0/camera/";
        }
        else if (action.OwnerProtocol is SourceProtocol.OppoLivePhoto)
        {
            expectedNs = "http://ns.oplus.com/photos/1.0/camera/";
        }
        else if (action.OwnerProtocol is SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile)
        {
            expectedNs = "http://ns.vivo.com/photos/1.0/camera/";
        }
        else if (action.OwnerProtocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
        {
            expectedNs = "http://ns.samsung.com/photos/1.0/camera/";
        }

        foreach (var elem in doc.Descendants())
        {
            if (elem.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                if (expectedNs == null || elem.Name.NamespaceName.Equals(expectedNs, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var attr in elem.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;
                if (attr.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                {
                    if (expectedNs == null || attr.Name.NamespaceName.Equals(expectedNs, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}