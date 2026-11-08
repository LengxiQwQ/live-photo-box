using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

// Windows 10+ shell operation, explicitly recycle-only. No permanent-delete
// fallback. GUI and CLI share this boundary and its final identity check.
internal static class SourceRecycleBinService
{
    internal static void Recycle(ProductOperationOutcome outcome, string path)
    {
        if (TryRecycle(outcome, path) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static Exception? TryRecycle(ProductOperationOutcome outcome, string path)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return TryRecycleCore(outcome, path);
        Exception? failure = null;
        var thread = new Thread(() => { try { failure = TryRecycleCore(outcome, path); } catch (Exception ex) { failure = ex; } })
            { IsBackground = true, Name = "LivePhotoBox source recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return failure;
    }

    private static Exception? TryRecycleCore(ProductOperationOutcome outcome, string path)
    {
        var guard = outcome.SourceActionGuard;
        if (guard is null) return new IOException("Missing source identity receipt; original retained.");
        string fullPath = Path.GetFullPath(path);
        if (guard.SourceValidationFailure(outcome, fullPath) is { } sourceFailure) return sourceFailure;
        if (Failure(CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */)) is { } initializationFailure) return initializationFailure;
        IntPtr operation = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            Guid clsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");
            Guid iid = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
            if (Failure(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out operation)) is { } creationFailure) return creationFailure;
            // FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE | FOF_ALLOWUNDO |
            // FOF_NOERRORUI | FOF_NOCONFIRMATION | FOF_SILENT.
            const uint flags = 0x00080000 | 0x00100000 | 0x0040 | 0x0400 | 0x0010 | 0x0004;
            if (Failure(Method<SetFlags>(operation, 5)(operation, flags)) is { } flagsFailure) return flagsFailure;
            Guid shellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            if (Failure(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref shellItem, out item)) is { } itemFailure) return itemFailure;
            if (Failure(Method<DeleteItem>(operation, 18)(operation, item, IntPtr.Zero)) is { } queueFailure) return queueFailure;
            if (guard.SourceValidationFailure(outcome, fullPath) is { } changedFailure) return changedFailure;
            if (Failure(Method<Perform>(operation, 21)(operation)) is { } operationFailure) return operationFailure;
            if (Failure(Method<Aborted>(operation, 22)(operation, out int aborted)) is { } statusFailure) return statusFailure;
            if (aborted != 0 || File.Exists(fullPath)) return new IOException("Recycle operation did not complete; no permanent-delete fallback was used.");
            return null;
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            CoUninitialize();
        }
    }

    private static Exception? Failure(int result) => result < 0 ? Marshal.GetExceptionForHR(result) : null;

    // Slots/signatures follow IFileOperationVtbl in the Windows SDK's
    // ShObjIdl_core.h; unused shell operations are deliberately not exposed.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetFlags(IntPtr self, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DeleteItem(IntPtr self, IntPtr item, IntPtr sink);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Perform(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Aborted(IntPtr self, out int aborted);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);
}
