using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

// Real OS failures, not exception injection. Evidence is captured before disposal.
public sealed class P7R2FilesystemEvidenceTests
{
    [Fact]
    public void Runtime_ActualLoadedModulesAreBound()
    {
        var info = NativeRuntime.Probe(); Assert.True(info.IsAvailable, info.Diagnostic);
        var modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Where(m => m.ModuleName.StartsWith("LivePhotoBox.", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { path = m.FileName, bytes = new FileInfo(m.FileName).Length, sha256 = Sha(m.FileName) }).ToArray();
        Assert.Contains(modules, m => Path.GetFileName(m.path) == "LivePhotoBox.Native.dll");
        Save("runtime-loaded", new { info, assembly = typeof(P7R2FilesystemEvidenceTests).Assembly.Location, modules,
            elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) });
    }
    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    static object[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(p => (object)new { path = Path.GetRelativePath(root, p), bytes = new FileInfo(p).Length, sha256 = Sha(p) }).ToArray();
    static void Save(string name, object value)
    {
        string root = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE") ?? throw new InvalidOperationException("LPB_R2_EVIDENCE required");
        Directory.CreateDirectory(root);
        using var stream = new FileStream(Path.Combine(root, name + ".json"), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }
    sealed class FixedWorkspace(string root) : IMediaWorkspace
    {
        int index;
        public string RootDirectory => root;
        public string AllocateFilePath(string prefix, string extension) => Path.Combine(root, prefix + "-" + ++index + extension);
        public async Task<string> ComputeFileSha256Async(string p, CancellationToken ct = default)
        { using var f = File.OpenRead(p); return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)); }
        public async Task AssertSourceUnmodifiedAsync(string p, string expected, CancellationToken ct = default)
        { Assert.Equal(expected, await ComputeFileSha256Async(p, ct)); }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    static DirectorySecurity DenyCreate(string path)
    {
        var dir = new DirectoryInfo(path); var current = dir.GetAccessControl(AccessControlSections.Access);
        var old = new DirectorySecurity(); old.SetSecurityDescriptorBinaryForm(current.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        var changed = dir.GetAccessControl(AccessControlSections.Access);
        changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        dir.SetAccessControl(changed); return old;
    }
    [Fact, Trait("Category", "RealSamples")]
    public async Task Extract_RealAclDenyCreate_FailsWithoutOutputAndRestoresAcl()
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg"), before = Sha(source);
        using var temp = new MediaWorkspace(); using var inspected = await new SourceInspector().InspectWithPlanAsync(source);
        var old = DenyCreate(temp.RootDirectory); Exception? failure = null; int probeCode = 0;
        try
        {
            try { File.WriteAllBytes(Path.Combine(temp.RootDirectory, "must-not-create"), [1]); }
            catch (UnauthorizedAccessException ex) { probeCode = ex.HResult; }
            Assert.NotEqual(0, probeCode);
            failure = await Record.ExceptionAsync(() => new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, source, null, temp));
            var extractionError = Assert.IsType<ExtractionException>(failure);
            // Exclusive temp creation belongs to the extractor's existing publication
            // failure contract; no output stream was opened for a write failure.
            Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, extractionError.Category);
            Assert.Contains("Failed to create exclusive temporary file", extractionError.Message);
            Assert.Contains("Win32 error: 5", extractionError.Message);
            Assert.Empty(Files(temp.RootDirectory)); Assert.Equal(before, Sha(source));
        }
        finally
        {
            new DirectoryInfo(temp.RootDirectory).SetAccessControl(old);
            Save("extract-acl", new { kind = "RealWindowsAcl", probeCode, source, before, after = Sha(source), failure = failure?.ToString(), files = Files(temp.RootDirectory), aclRestored = new DirectoryInfo(temp.RootDirectory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == old.GetSecurityDescriptorSddlForm(AccessControlSections.Access) });
        }
    }
    [Fact, Trait("Category", "RealSamples")]
    public async Task Clean_RealStagingAclDenyCreate_RollsBackAndRestoresAcl()
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg"), before = Sha(source);
        using var temp = new MediaWorkspace(); using var inspected = await new SourceInspector().InspectWithPlanAsync(source);
        var bundle = await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, source, null, temp);
        string[] baseline = Directory.GetFiles(temp.RootDirectory).Order().ToArray();
        var cleaner = new SourceProtocolCleaner(); string? staging = null; DirectorySecurity? old = null;
        ProtocolCleanResult? result = null;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "BeforeNative")
            { staging = Assert.Single(Directory.GetDirectories(temp.RootDirectory, "staging_*")); old = DenyCreate(staging); }
            return Task.CompletedTask;
        };
        try
        {
            result = await cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = bundle }, temp);
            Assert.NotNull(staging); Assert.False(result.Success);
            Assert.Equal(CleanerFailureStage.Staging, result.FailureStage);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Empty(Directory.GetDirectories(temp.RootDirectory, "staging_*"));
            Assert.Equal(baseline, Directory.GetFiles(temp.RootDirectory).Order()); Assert.Equal(before, Sha(source));
        }
        finally
        {
            if (staging != null && Directory.Exists(staging) && old != null) new DirectoryInfo(staging).SetAccessControl(old);
            Save("clean-staging-acl", new { kind = "RealWindowsAclAtNativeCreate", source, before, after = Sha(source), result, staging, aclRestoredOrOwnedDirectoryDeleted = staging != null && !Directory.Exists(staging), files = Files(temp.RootDirectory) });
        }
    }
    [Theory, Trait("Category", "RealSamples")]
    [InlineData("Extract")]
    [InlineData("Clean")]
    [InlineData("ImageCopy")]
    [InlineData("Split")]
    public async Task Transaction_RealDiskFull_RollsBackAndPreservesSource(string operation)
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg"), before = Sha(source);
        using var inspected = await new SourceInspector().InspectWithPlanAsync(source);
        using var preparation = new MediaWorkspace();
        ExtractedMediaBundle? bundle = operation == "Extract" ? null : await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, source, null, preparation);
        string mount = Environment.GetEnvironmentVariable("LPB_R2_DISK_ROOT") ?? throw new InvalidOperationException("Real dedicated disk-full volume required; not a skip");
        Assert.EndsWith(Path.Combine("disk-full", "mount"), mount.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True((File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0, "Dedicated volume must actually be mounted");
        string root = Path.Combine(mount, "r2-" + operation); Assert.False(Directory.Exists(root)); Directory.CreateDirectory(root);
        string fill = Path.Combine(root, "fill.bin"); Exception? failure = null; int realDiskFullCode = 0; long fillBytes = 0;
        try
        {
            using (var stream = new FileStream(fill, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] block = new byte[1024 * 1024];
                try { for (int i = 0; i < 256; ++i) stream.Write(block); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 112 or 39) { realDiskFullCode = ex.HResult; }
                Assert.NotEqual(0, realDiskFullCode);
                fillBytes = stream.Length;
                // Leave enough space for metadata and a partial write, less than the real primary image.
                stream.SetLength(Math.Max(0, fillBytes - 256 * 1024)); stream.Flush(true);
            }
            var workspace = new FixedWorkspace(root);
            if (operation == "Extract")
            {
                failure = await Record.ExceptionAsync(() => new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, source, null, workspace));
                var extractionError = Assert.IsType<ExtractionException>(failure); Assert.Equal(ExtractionFailureCategory.DiskFull, extractionError.Category);
            }
            else if (operation == "Clean")
            {
                var result = await new SourceProtocolCleaner().CleanAsync(new ProtocolCleanRequest { ExtractedBundle = bundle! }, workspace);
                Save("clean-disk-full-result", result);
                Assert.False(result.Success); Assert.Equal(CleanerFailureStage.Staging, result.FailureStage);
                Assert.Equal(CleanerFailureCategory.DiskFull, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            }
            else if (operation == "ImageCopy")
            {
                failure = await Record.ExceptionAsync(() => NativeMediaService.ConvertImageAsync(bundle!.PrimaryImage.Path, Path.Combine(root, "image.jpg"), ImageContainer.Jpeg, 90));
                var copyFailure = Assert.IsType<InvalidOperationException>(failure);
                Assert.Contains("owned output", copyFailure.Message, StringComparison.OrdinalIgnoreCase);
            }
            else if (operation == "Split")
            {
                failure = await Record.ExceptionAsync(() => WindowsSplitOutputPublisher.PublishAsync(bundle!.PrimaryImage.Path, bundle.MotionVideo!.Path, Path.Combine(root, "image.jpg"), Path.Combine(root, "video.mp4"), false, null, CancellationToken.None));
                Assert.IsAssignableFrom<IOException>(failure);
            }
            else throw new InvalidOperationException(operation);
            Assert.Equal(before, Sha(source)); Assert.Equal([fill], Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            var residue = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => p != fill).ToArray();
            if (File.Exists(fill)) File.Delete(fill);
            Save(operation + "-disk-full", new { kind = "RealWindowsDiskFull", operation, realDiskFullCode, fillBytes, source, before, after = Sha(source), failure = failure?.ToString(), residue, fillRemoved = !File.Exists(fill) });
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }
}
