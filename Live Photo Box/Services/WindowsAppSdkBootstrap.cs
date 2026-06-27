/*
 * WindowsAppSdkBootstrap.cs
 *
 * 在程序入口 Main() 之前初始化 Windows App SDK 自包含运行时。
 * [ModuleInitializer] 在 .NET 加载此程序集后、调用任何入口方法之前执行。
 *
 * 核心原理：
 *   项目使用 WindowsAppSDKSelfContained=true（框架 DLL 在输出目录中）。
 *   自包含部署不应调用 TryInitialize（它查找系统安装的框架包，会与
 *   本地 DLL 冲突）。只需 UndockedRegFreeWinRT —— 设置基础目录 +
 *   强制加载 Microsoft.WindowsAppRuntime.dll，触发 SxS 重定向，
 *   使 ms-appx:/// URI 和 WinRT 类型可从本地 DLL 免注册激活。
 *
 * 打包模式（MSIX）：此初始化无害。环境变量和 DllImport 加载不影响
 *   MSIX 打包行为——包清单已声明框架依赖。
 *
 * 要求：.NET 5+ / C# 9+（本项目 .NET 9 / C# 13）
 */

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    internal static class WindowsAppSdkBootstrap
    {
        /// <summary>
        /// 加载 Microsoft.WindowsAppRuntime.dll。
        /// 函数本身只返回 S_OK——真正作用是通过 DllImport 强制加载 DLL，
        /// 触发其模块初始化器设置 SxS 重定向等基础设施。
        /// </summary>
        [DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsAppRuntime_EnsureIsLoaded();

        [ModuleInitializer]
        internal static void Initialize()
        {
            // 切勿在此方法中引用任何 WinRT 投影类型！
            // WinRT 尚未初始化，访问投影类型会导致其 DLL 的模块构造器
            // 因类型激活失败而损坏 WinRT 状态。

            try
            {
                // 设置基础目录环境变量。
                // WinAppSDK 内部用此变量定位自包含框架 DLL。
                Environment.SetEnvironmentVariable(
                    "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
                    AppContext.BaseDirectory);

                // 强制加载 Microsoft.WindowsAppRuntime.dll。
                // DllImport 触发的 DLL 加载会执行其模块初始化器，
                // 设置 SxS 重定向、MRT Core 等基础设施。
                WindowsAppRuntime_EnsureIsLoaded();

                Debug.WriteLine("[LivePhotoBox] UndockedRegFreeWinRT initialized — WinRT ready.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LivePhotoBox] ERROR: UndockedRegFreeWinRT failed: {ex.Message}");
            }
        }
    }
}
