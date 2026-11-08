using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class CliBatchExpectedResultTests
{
    [Fact]
    public async Task MergePrecancelled_Returns130WithoutFirstChanceException()
    {
        string root = CliTestHost.CreateTempDir("lpb-merge-precancel-result-");
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var method = typeof(MergeCommand).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] arguments = [null, null, new DirectoryInfo(root), "motionphoto", new DirectoryInfo(Path.Combine(root, "output")), null,
                "keep", false, 1, true, true, false, false, false, false, false,
                "name", "none", false, null, new StructuredMergeOptions(), false, cancellation.Token];
            var exceptions = new ConcurrentQueue<Exception>();
            EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) => exceptions.Enqueue(e.Exception);
            using var stdout = new StringWriter(); TextWriter originalOut = Console.Out;
            int code;
            try
            {
                Console.SetOut(stdout);
                AppDomain.CurrentDomain.FirstChanceException += handler;
                try { code = await (Task<int>)method.Invoke(null, arguments)!; }
                finally { AppDomain.CurrentDomain.FirstChanceException -= handler; }
            }
            finally { Console.SetOut(originalOut); }
            Assert.Equal(130, code); Assert.Empty(exceptions);
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
            Assert.False(Directory.Exists(Path.Combine(root, "output")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RepairRecursive_ReportsDeniedDirectoryAndProcessesIndependentFile()
    {
        string root = CliTestHost.CreateTempDir("lpb-repair-acl-");
        string deniedPath = Path.Combine(root, "denied");
        Directory.CreateDirectory(deniedPath);
        File.WriteAllBytes(Path.Combine(deniedPath, "hidden.jpg"), [1, 2, 3]);
        string independent = Path.Combine(root, "independent.jpg");
        File.WriteAllBytes(independent, [1, 2, 3, 4, 5]);
        var directory = new DirectoryInfo(deniedPath);
        string original = directory.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            var restricted = directory.GetAccessControl(AccessControlSections.Access);
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(restricted);
            Assert.NotNull(ScanFileSystem.Enumerate(deniedPath, CancellationToken.None).Failure);

            var result = await CliTestHost.RunAsync(RepairCommand.Create(), "--dir", root, "--recursive", "--dry-run", "--json");
            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Contains(json.RootElement.GetProperty("scanErrors").EnumerateArray(),
                error => string.Equals(error.GetProperty("path").GetString(), deniedPath, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
            Assert.Contains(json.RootElement.GetProperty("results").EnumerateArray(),
                item => string.Equals(item.GetProperty("file").GetString(), independent, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(independent));
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            Assert.Equal(original, directory.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepairPrecancelled_Returns130AndSummaryWithoutFirstChanceException()
    {
        string root = CliTestHost.CreateTempDir("lpb-repair-precancel-");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var method = typeof(RepairCommand).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] arguments = [null, new DirectoryInfo(root), null,
                false, false, false, false, false, false, false,
                1, true, true, false, false, false, false, false, cancellation.Token];
            using var stdout = new StringWriter();
            TextWriter originalOut = Console.Out;
            var exceptions = new ConcurrentQueue<Exception>();
            EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) => exceptions.Enqueue(e.Exception);
            int code;
            try
            {
                Console.SetOut(stdout);
                AppDomain.CurrentDomain.FirstChanceException += handler;
                try { code = await (Task<int>)method.Invoke(null, arguments)!; }
                finally { AppDomain.CurrentDomain.FirstChanceException -= handler; }
            }
            finally { Console.SetOut(originalOut); }

            Assert.Equal(130, code);
            Assert.Empty(exceptions);
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.True(json.RootElement.GetProperty("cancelled").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("results").EnumerateArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}


