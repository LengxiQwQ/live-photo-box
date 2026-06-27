/*
 * WindowsAppSdkBootstrap.cs
 *
 * 在程序入口 Main() 之前引导 Windows App SDK 运行时。
 *
 * 背景：WinUI 3 自动生成的 Program.Main() 在非打包模式下缺少
 * Bootstrap 调用，导致 WinRT 类型激活失败 → 0xc000027b。
 * [ModuleInitializer] 在 .NET 运行时加载此程序集后、调用任何
 * 入口方法之前执行，确保 WinRT 类型在 WinRT.ComWrappersSupport
 * .InitializeComWrappers() 之前已注册。
 *
 * 打包模式（MSIX）：Package.Current 已存在，Bootstrap 自动跳过。
 * 非打包模式：动态注册 WinAppSDK 框架包。
 *
 * 要求：.NET 5+ / C# 9+（本项目 .NET 9 / C# 13）
 */

using System.Runtime.CompilerServices;

namespace LivePhotoBox.Services
{
    internal static class WindowsAppSdkBootstrap
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            try
            {
                // 0x00010000 = 1.0.0.0，使用已安装的 WinAppSDK 版本
                // 打包模式下自动检测 package identity 并跳过
                Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(
                    0x00010000,
                    out _);
            }
            catch
            {
                // 静默 — 若打包模式已就绪或运行时缺失，
                // 后续 WinRT 初始化会给出更明确的错误
            }
        }
    }
}
