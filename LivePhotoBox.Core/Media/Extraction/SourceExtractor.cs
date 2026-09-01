using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Extracts primary image, motion video, and auxiliary artifacts directly using pre-inspected SourceMediaFacts.
/// Strictly enforces source file immutability by asserting source hashes before and after extraction.
/// </summary>
public sealed class SourceExtractor : ISourceExtractor
{
    private const int BufferSize = 81920;

    public async Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        IMediaWorkspace workspace,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(workspace);

        ct.ThrowIfCancellationRequested();

        // 1. Validate initial source integrity if source hash is recorded
        if (!string.IsNullOrWhiteSpace(facts.SourceSha256))
        {
            await workspace.AssertSourceUnmodifiedAsync(facts.SourceFilePath, facts.SourceSha256, ct).ConfigureAwait(false);
        }

        MediaArtifact? primaryArtifact = null;
        MediaArtifact? videoArtifact = null;
        MediaArtifact? gainMapArtifact = null;
        var auxArtifacts = new List<MediaArtifact>();

        // 2. Extract Primary Image
        if (facts.PrimaryImage != null)
        {
            string ext = facts.PrimaryImage.Container == ImageContainer.Heic ? "heic" : "jpg";
            string destPath = workspace.AllocateTempFilePath("primary_image", ext);

            await ExtractRangeToFileAsync(
                facts.PrimaryImage.FilePath,
                facts.PrimaryImage.ByteOffset,
                facts.PrimaryImage.ByteLength,
                destPath,
                ct).ConfigureAwait(false);

            string sha256 = await workspace.ComputeFileSha256Async(destPath, ct).ConfigureAwait(false);
            long len = new FileInfo(destPath).Length;

            primaryArtifact = new MediaArtifact
            {
                Path = destPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ImageCodec = facts.PrimaryImage.Codec,
                ByteLength = len,
                SourceRange = (facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength),
                Sha256 = sha256
            };
        }

        // 3. Extract Motion Video
        if (facts.MotionVideo != null)
        {
            string ext = facts.MotionVideo.Container == VideoContainer.Mov ? "mov" : "mp4";
            string destPath = workspace.AllocateTempFilePath("motion_video", ext);

            await ExtractRangeToFileAsync(
                facts.MotionVideo.FilePath,
                facts.MotionVideo.ByteOffset,
                facts.MotionVideo.ByteLength,
                destPath,
                ct).ConfigureAwait(false);

            string sha256 = await workspace.ComputeFileSha256Async(destPath, ct).ConfigureAwait(false);
            long len = new FileInfo(destPath).Length;

            videoArtifact = new MediaArtifact
            {
                Path = destPath,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = facts.MotionVideo.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = facts.MotionVideo.Container,
                VideoCodec = facts.MotionVideo.Codec,
                ByteLength = len,
                SourceRange = (facts.MotionVideo.ByteOffset, facts.MotionVideo.ByteLength),
                Sha256 = sha256
            };
        }

        // 4. Extract GainMap if present
        if (facts.GainMap is { IsPresent: true, ByteLength: > 0 })
        {
            string destPath = workspace.AllocateTempFilePath("gain_map", "jpg");

            await ExtractRangeToFileAsync(
                facts.SourceFilePath,
                facts.GainMap.ByteOffset,
                facts.GainMap.ByteLength,
                destPath,
                ct).ConfigureAwait(false);

            string sha256 = await workspace.ComputeFileSha256Async(destPath, ct).ConfigureAwait(false);
            long len = new FileInfo(destPath).Length;

            gainMapArtifact = new MediaArtifact
            {
                Path = destPath,
                Kind = MediaArtifactKind.GainMap,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = len,
                SourceRange = (facts.GainMap.ByteOffset, facts.GainMap.ByteLength),
                Sha256 = sha256
            };
        }

        // 5. Extract Auxiliary Items
        foreach (var aux in facts.AuxiliaryItems)
        {
            if (aux.ByteLength <= 0) continue;

            string destPath = workspace.AllocateTempFilePath($"aux_{aux.Name}", "bin");
            await ExtractRangeToFileAsync(facts.SourceFilePath, aux.ByteOffset, aux.ByteLength, destPath, ct).ConfigureAwait(false);

            string sha256 = await workspace.ComputeFileSha256Async(destPath, ct).ConfigureAwait(false);
            long len = new FileInfo(destPath).Length;

            auxArtifacts.Add(new MediaArtifact
            {
                Path = destPath,
                Kind = MediaArtifactKind.AuxiliaryImage,
                MimeType = aux.MimeType,
                ByteLength = len,
                SourceRange = (aux.ByteOffset, aux.ByteLength),
                Sha256 = sha256
            });
        }

        // 6. Validate source immutability after extraction
        if (!string.IsNullOrWhiteSpace(facts.SourceSha256))
        {
            await workspace.AssertSourceUnmodifiedAsync(facts.SourceFilePath, facts.SourceSha256, ct).ConfigureAwait(false);
        }

        if (primaryArtifact == null)
        {
            throw new InvalidOperationException($"No primary image could be extracted for source '{Path.GetFileName(facts.SourceFilePath)}'.");
        }

        return new ExtractedMediaBundle
        {
            PrimaryImage = primaryArtifact,
            MotionVideo = videoArtifact,
            GainMap = gainMapArtifact,
            AuxiliaryArtifacts = auxArtifacts,
            Facts = facts
        };
    }

    private static async Task ExtractRangeToFileAsync(
        string sourcePath,
        long offset,
        long length,
        string destinationPath,
        CancellationToken ct)
    {
        if (length <= 0)
        {
            // Empty file
            await File.WriteAllBytesAsync(destinationPath, [], ct).ConfigureAwait(false);
            return;
        }

        using var srcStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
        srcStream.Seek(offset, SeekOrigin.Begin);

        using var dstStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        byte[] buffer = new byte[BufferSize];
        long remaining = length;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await srcStream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;

            await dstStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }
}
