using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NativeMediaServiceIoContractTests
{
    [Fact]
    public async Task Inspect_MissingPrimaryPath_MapsToTypedIoFailure()
    {
        using var workspace = new MediaWorkspace();
        string missingPath = workspace.AllocateFilePath("missing-primary", ".jpg");

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => NativeMediaService.InspectMediaAsync(missingPath));

        AssertIoFailure(error, "Primary");
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Fact]
    public async Task Inspect_MissingCompanionPath_MapsToTypedIoFailure()
    {
        using var workspace = new MediaWorkspace();
        string primaryPath = workspace.AllocateFilePath("existing-primary", ".jpg");
        string missingCompanionPath = workspace.AllocateFilePath("missing-companion", ".mov");
        File.WriteAllBytes(primaryPath, [0xFF, 0xD8, 0xFF, 0xD9]);

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => NativeMediaService.InspectMediaAsync(primaryPath, missingCompanionPath));

        AssertIoFailure(error, "Secondary");
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Fact]
    public async Task Inspect_DirectoryPath_MapsToTypedIoFailure()
    {
        using var workspace = new MediaWorkspace();

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => NativeMediaService.InspectMediaAsync(workspace.RootDirectory));

        AssertIoFailure(error, "Primary");
        Assert.NotNull(error.InnerException);
        Assert.True(
            error.InnerException is UnauthorizedAccessException or IOException,
            $"Unexpected preflight exception: {error.InnerException.GetType().FullName}");
    }

    [Fact]
    public async Task Inspect_InvalidPath_MapsToTypedIoFailure()
    {
        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => NativeMediaService.InspectMediaAsync("invalid\0path.jpg"));

        AssertIoFailure(error, "Primary");
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Fact]
    public async Task Inspect_NullPrimary_RemainsProgrammerError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => NativeMediaService.InspectMediaAsync(null!));
    }

    [Fact]
    public async Task Inspect_Cancellation_RemainsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NativeMediaService.InspectMediaAsync("missing.jpg", cancellationToken: cancellation.Token));
    }

    private static void AssertIoFailure(SourceInspectionException error, string role)
    {
        Assert.Equal(SourceInspectionFailureCategory.Io, error.Category);
        Assert.Equal(SourceInspectionStage.Read, error.Stage);
        Assert.Equal(NativeRuntime.FoundationCapability, error.Capability);
        Assert.Equal(error.Message, error.Reason);
        Assert.Contains(role, error.Reason, StringComparison.Ordinal);
    }
}
