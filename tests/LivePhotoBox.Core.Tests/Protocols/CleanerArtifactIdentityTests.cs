using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class CleanerArtifactIdentityTests
{
    [Fact]
    public async Task CleanupSource_IdenticalByteReplacement_FailsBeforeDestructiveInvoker()
    {
        using var workspace = new MediaWorkspace();
        byte[] bytes = [0xFF, 0xD8, 0x10, 0x20, 0xFF, 0xD9];
        string primaryPath = workspace.AllocateFilePath("primary", ".jpg");
        string cleanupPath = workspace.AllocateFilePath("cleanup-source", ".jpg");
        await File.WriteAllBytesAsync(primaryPath, bytes);
        await File.WriteAllBytesAsync(cleanupPath, bytes);

        WindowsFileIdentity extractedIdentity = WindowsFileIdentity.Capture(cleanupPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));

        string replacementPath = workspace.AllocateFilePath("replacement", ".jpg");
        await File.WriteAllBytesAsync(replacementPath, bytes);
        File.Delete(cleanupPath);
        File.Move(replacementPath, cleanupPath);

        Assert.NotEqual(extractedIdentity, WindowsFileIdentity.Capture(cleanupPath));

        bool destructiveInvokerCalled = false;
        var cleaner = new SourceProtocolCleaner(
            cleanInvoker: (facts, actions, inputImage, inputVideo, outputImage, outputVideo, cancellationToken) =>
            {
                destructiveInvokerCalled = true;
                return Task.FromResult<IReadOnlyList<RemovedProtocolFact>>([]);
            });

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.SamsungMotionPhotoJpeg,
                PrimarySha256 = sha256,
                PrimaryImage = new ImageFacts
                {
                    IsPresent = true,
                    ByteOffset = 0,
                    ByteLength = bytes.Length,
                    Container = ImageContainer.Jpeg
                },
                PreservationCarriers =
                [
                    new PreservationCarrier
                    {
                        Kind = PreservationCarrierKind.SamsungSef,
                        SourceIndex = 0,
                        ArtifactRole = MediaArtifactKind.PrimaryImage,
                        StableIdentity = "samsung:sef:index",
                        Semantic = "SEF index",
                        OwnerIdentity = "primary:0",
                        Relationship = "SEFH/SEFT",
                        SourceOffset = 0,
                        SourceLength = bytes.Length,
                        SourceSha256 = sha256
                    }
                ]
            },
            PrimaryImage = new MediaArtifact
            {
                Path = primaryPath,
                Kind = MediaArtifactKind.PrimaryImage,
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = bytes.Length,
                Sha256 = sha256
            },
            CleanupSource = new MediaArtifact
            {
                Path = cleanupPath,
                Kind = MediaArtifactKind.SourceContainer,
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = bytes.Length,
                Sha256 = sha256,
                FileIdentity = extractedIdentity
            }
        };

        ProtocolCleanResult result = await cleaner.CleanAsync(
            new ProtocolCleanRequest { ExtractedBundle = bundle },
            workspace,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
        Assert.False(destructiveInvokerCalled);
    }
}
