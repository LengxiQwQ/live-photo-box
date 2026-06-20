using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// exiftool 常驻进程包装器：使用 -stay_open 模式，启动一次，通过 stdin/stdout 持续派发任务，
    /// 避免每次调用都重新加载 Perl 运行时（节省 ~200-400ms/次）。
    /// 线程安全：内部用 SemaphoreSlim 序列化 stdin/stdout 的读写。
    /// </summary>
    public sealed class PersistentExifTool : IDisposable
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly StringBuilder _stderrCollector = new();
        private readonly Task? _stderrTask;
        private readonly CancellationTokenSource _shutdownCts = new();
        private bool _disposed;

        public PersistentExifTool(string exifToolPath)
        {
            string toolDir = Path.GetDirectoryName(exifToolPath) ?? AppContext.BaseDirectory;
            string tempDir = Path.GetTempPath();

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                WorkingDirectory = toolDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;
            psi.Environment["PAR_GLOBAL_TMPDIR"] = tempDir;

            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            psi.ArgumentList.Add("-stay_open");
            psi.ArgumentList.Add("True");
            psi.ArgumentList.Add("-@");
            psi.ArgumentList.Add("-");

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start persistent exiftool process.");

            // 后台消费 stderr，避免缓冲区满阻塞（必须在消费初始 {ready} 之前启动，防止死锁）。
            // 存下 Task 引用，Dispose 时等它退出，防止残留线程在新实例启动后还访问已释放资源。
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_shutdownCts.Token));
        }

        /// <summary>
        /// 发送一条命令并等待 JSON 响应。线程安全。
        /// </summary>
        public async Task<string> SendCommandAsync(CancellationToken token, params string[] args)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PersistentExifTool));

            await _ioLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_process.HasExited)
                    throw new InvalidOperationException("Persistent exiftool process has exited unexpectedly.");

                foreach (var arg in args)
                    await _process.StandardInput.WriteLineAsync(arg).ConfigureAwait(false);
                await _process.StandardInput.WriteLineAsync("-execute").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);

                // 读取直到 {ready} 标记
                var sb = new StringBuilder();
                while (true)
                {
                    string? line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        throw new InvalidOperationException("Persistent exiftool stdout closed unexpectedly.");
                    if (line.TrimEnd() == "{ready}")
                        break;
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(line);
                }

                return sb.ToString();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task ReadStderrLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    lock (_stderrCollector)
                    {
                        _stderrCollector.AppendLine(line);
                    }
                }
            }
            catch
            {
                // 进程退出时读取 stderr 可能抛异常，忽略
            }
        }

        /// <summary>
        /// 获取并清空 stderr 缓冲区（用于日志记录）
        /// </summary>
        public string FlushStderr()
        {
            lock (_stderrCollector)
            {
                string result = _stderrCollector.ToString();
                _stderrCollector.Clear();
                return result;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 1. 通知 stderr 循环退出
            _shutdownCts.Cancel();

            // 2. 优雅关闭 exiftool 进程
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine("-stay_open");
                    _process.StandardInput.WriteLine("False");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit(3000))
                        _process.Kill();
                }
            }
            catch
            {
                try { _process.Kill(); } catch { }
            }

            // 3. 等待 stderr 循环完全退出再释放资源，防止残留线程访问已释放的 Process
            try { _stderrTask?.Wait(2000); } catch { }

            _process.Dispose();
            _ioLock.Dispose();
            _shutdownCts.Dispose();
        }
    }
}
