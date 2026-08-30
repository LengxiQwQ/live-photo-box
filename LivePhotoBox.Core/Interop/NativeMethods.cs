namespace LivePhotoBox.Interop
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    internal enum NativeResult
    {
        Ok = 0,
        InvalidArgument = 1,
        AbiMismatch = 2,
        Cancelled = 3,
        BufferTooSmall = 4,
        InternalError = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRuntimeInfoData
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong Capabilities;
    }

    internal static partial class NativeMethods
    {
        internal const string LibraryName = "LivePhotoBox.Native";

        [LibraryImport(LibraryName, EntryPoint = "lpb_get_abi_version")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "lpb_get_version")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nint GetVersion();

        [LibraryImport(LibraryName, EntryPoint = "lpb_create_context")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial NativeResult CreateContext(nint options, out nint context);

        [LibraryImport(LibraryName, EntryPoint = "lpb_destroy_context")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void DestroyContext(nint context);

        [LibraryImport(LibraryName, EntryPoint = "lpb_get_runtime_info")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial NativeResult GetRuntimeInfo(
            nint context,
            ref NativeRuntimeInfoData info);

        [LibraryImport(LibraryName, EntryPoint = "lpb_get_last_error")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial NativeResult GetLastError(
            nint context,
            nint utf8Buffer,
            nuint bufferSize,
            out nuint requiredSize);

        [LibraryImport(LibraryName, EntryPoint = "lpb_vivo_rewrite_image_metadata")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial NativeResult RewriteVivoImageMetadata(
            nint context,
            byte* input,
            nuint inputSize,
            byte* vivoJson,
            nuint vivoJsonSize,
            int replaceExisting,
            byte* output,
            nuint outputSize,
            out nuint requiredSize);

        [LibraryImport(LibraryName, EntryPoint = "lpb_vivo_rewrite_video_metadata")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial NativeResult RewriteVivoVideoMetadata(
            nint context,
            byte* input,
            nuint inputSize,
            byte* vivoJson,
            nuint vivoJsonSize,
            byte* output,
            nuint outputSize,
            out nuint requiredSize);

        [LibraryImport(LibraryName, EntryPoint = "lpb_huawei_build_tail", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial NativeResult HuaweiBuildTail(
            nint context,
            int coverFrame,
            int totalFrames,
            ulong mp4Size,
            int originalCoverMs,
            int originalDurationMs,
            string prefix,
            byte* output,
            nuint outputSize,
            out nuint requiredSize);

        [LibraryImport(LibraryName, EntryPoint = "lpb_huawei_patch_heic_ftyp")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial NativeResult HuaweiPatchHeicFtyp(
            nint context,
            byte* data,
            nuint dataSize);

        [LibraryImport(LibraryName, EntryPoint = "lpb_huawei_patch_mp4")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial NativeResult HuaweiPatchMp4(
            nint context,
            byte* data,
            nuint dataSize);
    }
}
