using System;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>
    /// 命令处理会写日志，测试前初始化 LogService（幂等）。
    /// 用 Collection 同时串行化测试，避免重定向 Console 互相干扰。
    /// </summary>
    public sealed class CliLogFixture : IDisposable
    {
        public CliLogFixture()
        {
            LogService.Initialize(subDirectory: "CLI-Tests", logFilePrefix: "cli-test");
        }

        public void Dispose() { }
    }

    [CollectionDefinition("cli-log")]
    public sealed class CliLogCollection : ICollectionFixture<CliLogFixture>
    {
    }
}
