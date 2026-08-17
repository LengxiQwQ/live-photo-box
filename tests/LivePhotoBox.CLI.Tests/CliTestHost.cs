using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>命令执行结果：退出码 + 捕获的 stdout/stderr。</summary>
    internal sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// CLI 命令测试宿主：在内存里构建 System.CommandLine 管道并执行，
    /// 重定向 Console.Out/Error 以便断言错误与 JSON 输出。
    /// </summary>
    internal static class CliTestHost
    {
        public static async Task<CliResult> RunAsync(Command command, params string[] args)
        {
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                int exitCode = await command.InvokeAsync(args);
                return new CliResult(exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }

        /// <summary>创建唯一临时目录（测试结束由调用方删除）。</summary>
        public static string CreateTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>创建占位文件（仅用于路径/参数校验类测试，不参与真实编解码）。</summary>
        public static string CreateDummyFile(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "dummy");
            return path;
        }
    }
}
