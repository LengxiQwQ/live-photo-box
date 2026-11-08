using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NativeFailureDiagnosticTests
{
    [Fact]
    public void AdditiveDiagnosticHasVersionedBoundedLayoutAndRejectsUnknownVersion()
    {
        Assert.Equal(24, Marshal.SizeOf<NativeFailureDiagnosticV1>());
        using var context = NativeContext.Create();
        var diagnostic = new NativeFailureDiagnosticV1 { StructSize = 24, Version = 1 };
        Assert.Equal(NativeResult.Ok, NativeMethods.GetFailureDiagnosticV1(context.Handle, ref diagnostic));
        Assert.Equal(24U, diagnostic.StructSize);
        Assert.Equal(1U, diagnostic.Version);
        Assert.Equal(0U, diagnostic.Reason);
        diagnostic.Version = 2;
        Assert.Equal(NativeResult.InvalidArgument, NativeMethods.GetFailureDiagnosticV1(context.Handle, ref diagnostic));
        diagnostic.Version = 1; diagnostic.StructSize = 20;
        Assert.Equal(NativeResult.InvalidArgument, NativeMethods.GetFailureDiagnosticV1(context.Handle, ref diagnostic));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(3)]
    [InlineData(8)]
    public void ProductFailureClassificationDoesNotNeedDiagnosticWords(int code)
    {
        var result = (NativeResult)code;
        using var context = NativeContext.Create();
        var events = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += observer;
        Exception failure;
        try { failure = context.CreateFailure(result); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.True(result switch {
            NativeResult.Unsupported => failure is NotSupportedException,
            NativeResult.Cancelled => failure is OperationCanceledException,
            _ => failure is LivePhotoBox.Media.Extraction.ExtractionException
                { Category: LivePhotoBox.Media.Extraction.ExtractionFailureCategory.SourceChanged }
        });
    }
}
