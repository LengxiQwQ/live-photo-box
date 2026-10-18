using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2WorkspaceHandoffTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "FaultInjection")]
    public async Task ManagedHandoffPreparationFailure_RollsBackNativeObjectsAndRetiresExactWorkspaceReceipts()
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg");
        string sourceHash = Hash(source);
        WindowsFileIdentity sourceIdentity = WindowsFileIdentity.Capture(source);
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        using var inspected = await new SourceInspector().InspectWithPlanAsync(source);
        string[] producedPaths = [];
        WindowsFileIdentity[] producedIdentities = [];
        bool preparationReached = false;
        var injectedFailure = new OutOfMemoryException("R2 synthetic managed handoff preparation allocation failure.");
        Exception? observedFailure = null;
        Action? previousHook = SourceExtractor.BeforeCleanupPlanIssueTestHook.Value;
        try
        {
            SourceExtractor.BeforeCleanupPlanIssueTestHook.Value = () =>
            {
                preparationReached = true;
                producedPaths = Directory.GetFiles(root);
                Assert.NotEmpty(producedPaths);
                producedIdentities = producedPaths.Select(WindowsFileIdentity.Capture).ToArray();
                for (int i = 0; i < producedPaths.Length; i++)
                    Assert.True(workspace.HasRegisteredFile(producedPaths[i], producedIdentities[i]));
                throw injectedFailure;
            };

            observedFailure = await Record.ExceptionAsync(() => new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan, source, null, workspace));
            Assert.Same(injectedFailure, observedFailure);
            Assert.True(preparationReached);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            for (int i = 0; i < producedPaths.Length; i++)
                Assert.False(workspace.HasRegisteredFile(producedPaths[i], producedIdentities[i]));
            Assert.Equal(sourceHash, Hash(source));
            Assert.Equal(sourceIdentity, WindowsFileIdentity.Capture(source));

            // Keep the caller's inspected plan alive here: successful cleanup
            // must follow the rollback proof, not deferred context destruction.
            workspace.Dispose();
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SourceExtractor.BeforeCleanupPlanIssueTestHook.Value = previousHook;
            string evidenceRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE")
                ?? throw new InvalidOperationException("LPB_R2_EVIDENCE is required.");
            Directory.CreateDirectory(evidenceRoot);
            using (var output = new FileStream(Path.Combine(evidenceRoot, "workspace-managed-handoff-preparation-failure.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(output, new
                {
                    category = "FaultInjection / real Native extraction and filesystem rollback",
                    injectedFailure = injectedFailure.Message,
                    observedFailure = observedFailure?.GetType().FullName,
                    preparationReached, producedPaths, producedIdentities,
                    sourceBefore = new { sha256 = sourceHash, identity = sourceIdentity },
                    sourceAfter = new { sha256 = Hash(source), identity = WindowsFileIdentity.Capture(source) },
                    rootAbsentAfterExplicitDispose = !Directory.Exists(root)
                }, new JsonSerializerOptions { WriteIndented = true });
                output.Flush(flushToDisk: true);
            }
            if (Directory.Exists(root)) workspace.Dispose();
        }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
