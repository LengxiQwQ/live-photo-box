using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class WorkspaceCleanupEnumerationTests
{
    [Fact]
    public void AccessRevokedAfterLease_EnumerationFailureIsDataWithoutFirstChanceException()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        var directory = new DirectoryInfo(root);
        string originalAcl = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var previous = MediaWorkspace.BeforeCleanupEnumerationForTests.Value;
        bool reached = false;
        MediaWorkspace.BeforeCleanupEnumerationForTests.Value = _ =>
        {
            reached = true;
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            directory.SetAccessControl(acl);
        };
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        MediaWorkspace.WorkspaceCleanupAttempt attempt;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try { attempt = workspace.TryCleanup(); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
            Assert.True(reached);
            Assert.Empty(events);
            Assert.False(attempt.Succeeded);
            Assert.Contains("枚举", attempt.LimitationMessage!);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            MediaWorkspace.BeforeCleanupEnumerationForTests.Value = previous;
            var restored = new DirectorySecurity(); restored.SetSecurityDescriptorSddlForm(originalAcl, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void RestrictedRoot_EnumerationAndCleanupReturnFailuresWithoutFirstChanceException()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        var directory = new DirectoryInfo(root);
        string originalAcl = directory.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            events.Enqueue(e.Exception);
        };

        try
        {
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny));
            directory.SetAccessControl(acl);

            AppDomain.CurrentDomain.FirstChanceException += observer;
            try
            {
                var enumeration = ScanFileSystem.Enumerate(root, CancellationToken.None);
                Assert.Null(enumeration.Entries);
                Assert.NotNull(enumeration.Failure);
                var cleanup = workspace.TryCleanup();
                Assert.False(cleanup.Succeeded);
                Assert.NotNull(cleanup.Failure);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= observer;
            }
            Assert.Empty(events);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(originalAcl, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
