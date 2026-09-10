using System;
using System.Runtime.InteropServices;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Core.Tests.Support;

internal static class W3NativeHeif
{
    [DllImport("LivePhotoBox.Native", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_heif_enumerate_auxiliary_items", ExactSpelling = true)]
    private static extern unsafe NativeResult Enumerate(
        nint context,
        byte* input,
        nuint inputSize,
        NativeAuxiliaryItemFacts* outputItems,
        nuint outputCapacity,
        out nuint outputCount);

    internal static unsafe (NativeResult Result, string? Error, NativeAuxiliaryItemFacts[] Items) Enumerate(byte[] input)
    {
        using NativeContext context = NativeContext.Create();
        NativeAuxiliaryItemFacts[] items = new NativeAuxiliaryItemFacts[8];
        fixed (byte* pInput = input)
        fixed (NativeAuxiliaryItemFacts* pItems = items)
        {
            NativeResult result = Enumerate(
                context.Handle,
                pInput,
                (nuint)input.Length,
                pItems,
                (nuint)items.Length,
                out nuint count);
            int boundedCount = result == NativeResult.Ok
                ? checked((int)Math.Min(count, (nuint)items.Length))
                : 0;
            if (boundedCount != items.Length)
            {
                Array.Resize(ref items, boundedCount);
            }
            return (result, context.GetLastError(), items);
        }
    }
}
