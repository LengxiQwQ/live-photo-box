using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class WorkspaceCreationResultTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("parent-file")]
    [InlineData("denied")]
    [InlineData("cancelled")]
    public void CreationReturnsExternalFailureAndKeepsExistingObjects(string condition)
    {
        using var fixture = new TestFixtureWorkspace();
        string parent = Path.Combine(fixture.RootDirectory, "private-parent");
        string? originalAcl = null;
        if (condition == "parent-file") File.WriteAllText(parent, "foreign bytes");
        else Directory.CreateDirectory(parent);
        if (condition == "denied")
        {
            var directory = new DirectoryInfo(parent);
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            originalAcl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateDirectories, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(acl);
        }
        using var cancellation = new CancellationTokenSource(); if (condition == "cancelled") cancellation.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        MediaWorkspace.CreationAttempt attempt;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try { attempt = MediaWorkspace.TryCreate(cancellation.Token, parent); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
            Assert.Empty(events);
            if (condition == "valid")
            {
                Assert.Null(attempt.Failure);
                string path = attempt.Workspace!.RootDirectory;
                Assert.True(Directory.Exists(path));
                attempt.Workspace.Dispose();
                Assert.False(Directory.Exists(path));
            }
            else
            {
                Assert.NotNull(attempt.Failure); Assert.Null(attempt.Workspace);
                if (condition == "parent-file") Assert.Equal("foreign bytes", File.ReadAllText(parent));
                else Assert.Empty(Directory.GetFileSystemEntries(parent));
            }
        }
        finally
        {
            if (originalAcl is not null)
            {
                var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm(originalAcl, AccessControlSections.Access);
                new DirectoryInfo(parent).SetAccessControl(acl);
            }
        }
    }
}
