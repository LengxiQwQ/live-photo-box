using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed unsafe class P5TransformSourceLeaseTests
{
    private sealed class Mutation(string path)
    {
        public string Path = path;
        public int Calls;
        public int SuccessfulWrites;
    }

    // A valid fixed 8x8 grayscale JPEG. Transpose is a perfect DCT transform
    // even for a partial MCU. The byte mutation is a real shared-file write,
    // performed from the existing cancellation callback after observation read.
    private const string Jpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/wAALCAAIAAgBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AOf/AOSX/wDFpvhN/wAjr/x6694ms/8AmCdntbZx/wAvfUPIP+PflV/f5Nv/AP/Z";

    [Fact]
    public void Transform_PinsSourceAcrossObservationAndRead()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = System.IO.Path.Combine(workspace.RootDirectory, "source.jpg");
        string output = System.IO.Path.Combine(workspace.RootDirectory, "transpose.jpg");
        byte[] original = Convert.FromBase64String(Jpeg);
        File.WriteAllBytes(source, original);
        var state = new Mutation(source);
        GCHandle stateHandle = GCHandle.Alloc(state);
        nint context = 0;
        try
        {
            var options = new NativeContextOptions { StructSize = (uint)sizeof(NativeContextOptions),
                AbiVersion = NativeRuntime.SupportedAbiVersion,
                CancelCallback = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&AttemptMutation,
                UserData = GCHandle.ToIntPtr(stateHandle) };
            Assert.Equal(NativeResult.Ok, NativeMethods.CreateContext((nint)(&options), out context));
            Assert.Equal(NativeResult.Ok, NativeMethods.TransformJpegLosslessly(context, source, output, 5));
            Assert.True(state.Calls >= 2, "The real Native observation/read boundary was not reached.");
            Assert.Equal(0, state.SuccessfulWrites);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally
        {
            if (context != 0) NativeMethods.DestroyContext(context);
            stateHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AttemptMutation(nint userData)
    {
        try
        {
            var state = (Mutation)GCHandle.FromIntPtr(userData).Target!;
            if (++state.Calls < 2) return 0;
            using var writer = new FileStream(state.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            // Rewrite the same byte: the assertion is exclusive source ownership,
            // independent of whether a raced replacement happens to look valid.
            writer.WriteByte(0xff);
            state.SuccessfulWrites++;
        }
        catch { /* A read lease must reject this real write attempt. */ }
        return 0;
    }
}
