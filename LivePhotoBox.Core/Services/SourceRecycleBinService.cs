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
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { RecycleCore(outcome, path); return; }
        Exception? failure = null;
        var thread = new Thread(() => { try { RecycleCore(outcome, path); } catch (Exception ex) { failure = ex; } })
            { IsBackground = true, Name = "LivePhotoBox source recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RecycleCore(ProductOperationOutcome outcome, string path)
    {
        var guard = outcome.SourceActionGuard ?? throw new IOException("Missing source identity receipt; original retained.");
        string fullPath = Path.GetFullPath(path);
        guard.ValidateSource(outcome, fullPath);
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */));
        IntPtr operation = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            Guid clsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");
            Guid iid = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out operation));
            // FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE | FOF_ALLOWUNDO |
            // FOF_NOERRORUI | FOF_NOCONFIRMATION | FOF_SILENT.
            const uint flags = 0x00080000 | 0x00100000 | 0x0040 | 0x0400 | 0x0010 | 0x0004;
            Marshal.ThrowExceptionForHR(Method<SetFlags>(operation, 5)(operation, flags));
            Guid shellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref shellItem, out item));
            Marshal.ThrowExceptionForHR(Method<DeleteItem>(operation, 18)(operation, item, IntPtr.Zero));
            guard.ValidateSource(outcome, fullPath);
            Marshal.ThrowExceptionForHR(Method<Perform>(operation, 21)(operation));
            Marshal.ThrowExceptionForHR(Method<Aborted>(operation, 22)(operation, out int aborted));
            if (aborted != 0 || File.Exists(fullPath)) throw new IOException("Recycle operation did not complete; no permanent-delete fallback was used.");
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            CoUninitialize();
        }
    }

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
