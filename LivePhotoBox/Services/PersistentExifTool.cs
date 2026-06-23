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
    ///
    /// 崩溃自动恢复：当 exiftool 进程意外退出时（如遇损坏文件触发 Win32 异常），
    /// 自动重启进程并重试当前命令一次。若二次崩溃则放弃本条命令、抛出异常。
    /// </summary>
    public sealed class PersistentExifTool : IDisposable
    {
        private Process _process;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly StringBuilder _stderrCollector = new();
        private Task? _stderrTask;
        private CancellationTokenSource _shutdownCts = new();
        private readonly string _exifToolPath;
        private readonly string _toolDir;
        private readonly string _tempDir;
        private bool _disposed;

        /// <summary>第几次崩溃后重启（0 = 从未崩溃）。</summary>
        public int RestartCount { get; private set; }

        /// <summary>
        /// 当 exiftool 进程意外退出并完成自动重启时触发。
        /// 参数为可显示给用户的消息文本。
        /// </summary>
        public event Action<string>? OnRestarted;

        public PersistentExifTool(string exifToolPath)
        {
            _exifToolPath = exifToolPath;
            _toolDir = Path.GetDirectoryName(exifToolPath) ?? AppContext.BaseDirectory;
            _tempDir = Path.GetTempPath();

            _process = LaunchProcess();
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_shutdownCts.Token));
        }

        /// <summary>
        /// 创建一个新的 exiftool -stay_open 进程。与构造函数共享的初始化逻辑。
        /// </summary>
        private Process LaunchProcess()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exifToolPath,
                WorkingDirectory = _toolDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            psi.Environment["TEMP"] = _tempDir;
            psi.Environment["TMP"] = _tempDir;
            psi.Environment["PAR_GLOBAL_TMPDIR"] = _tempDir;

            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            psi.ArgumentList.Add("-stay_open");
            psi.ArgumentList.Add("True");
            psi.ArgumentList.Add("-@");
            psi.ArgumentList.Add("-");

            return Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start persistent exiftool process.");
        }

        /// <summary>最近一条命令的参数，崩溃时用于诊断日志。</summary>
        private string[]? _lastCommandArgs;

        /// <summary>
        /// 重启已崩溃的 exiftool 进程。调用方必须持有 _ioLock。
        /// </summary>
        /// <param name="context">崩溃时正在执行的命令描述（如文件路径）</param>
        private void RestartProcess(string? context = null)
        {
            // 抢在进程被 Kill 之前收集 stderr（崩溃原因可能在里面）
            string stderr = FlushStderr();

            // 清理旧进程
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            _process.Dispose();

            // 取消旧 stderr 循环
            var oldCts = _shutdownCts;
            _shutdownCts = new CancellationTokenSource();
            try { oldCts.Cancel(); } catch { }
            oldCts.Dispose();

            // 创建新进程
            _process = LaunchProcess();

            // 启动新 stderr 循环
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_shutdownCts.Token));

            RestartCount++;

            // 组装详细日志
            var msg = $"exiftool 进程异常退出，已自动重启 (第 {RestartCount} 次)";
            if (!string.IsNullOrWhiteSpace(context))
                msg += $"\n触发文件/命令: {context}";
            if (!string.IsNullOrWhiteSpace(stderr))
                msg += $"\nexiftool stderr: {stderr.Trim()}";

            LogService.Repair(msg, Models.LogLevel.Warning);

            // 通知 UI 层（只用首行，避免状态栏太长）
            string uiMsg = $"⚠ exiftool 异常退出，已自动重启 (第 {RestartCount} 次)";
            OnRestarted?.Invoke(uiMsg);
        }

        /// <summary>
        /// 发送一条命令并等待 JSON 响应。线程安全。
        /// 如 exiftool 在命令执行期间崩溃，自动重启并重试一次。
        /// </summary>
        public async Task<string> SendCommandAsync(CancellationToken token, params string[] args)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PersistentExifTool));

            await _ioLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await SendCommandInternalAsync(token, args, isRetry: false)
                    .ConfigureAwait(false);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// 实际执行命令。isRetry=true 表示这是一次崩溃后的重试，
        /// 如再次崩溃则不再重试，直接抛出异常。
        /// </summary>
        private async Task<string> SendCommandInternalAsync(
            CancellationToken token, string[] args, bool isRetry)
        {
            _lastCommandArgs = args;

            // 从参数中提取文件路径作为崩溃诊断上下文
            // args 格式如: -j -Rotation -Orientation -ThumbnailImage -ContentIdentifier <filePath>
            string? context = args.Length > 0 ? args[^1] : null;

            // 如果进程在上一次命令中崩了，先重启
            if (_process.HasExited)
            {
                if (isRetry)
                {
                    throw new InvalidOperationException(
                        $"Persistent exiftool crashed again after restart on file '{context}'. " +
                        "The file likely contains malformed data that exiftool cannot parse.");
                }
                RestartProcess(context);
            }

            // 写入参数
            foreach (var arg in args)
                await _process.StandardInput.WriteLineAsync(arg).ConfigureAwait(false);
            await _process.StandardInput.WriteLineAsync("-execute").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);

            // 读取直到 {ready}
            var sb = new StringBuilder();
            while (true)
            {
                token.ThrowIfCancellationRequested();

                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    line = null;
                }

                if (line == null)
                {
                    if (!isRetry)
                    {
                        RestartProcess(context);
                        return await SendCommandInternalAsync(token, args, isRetry: true)
                            .ConfigureAwait(false);
                    }
                    throw new InvalidOperationException(
                        $"Persistent exiftool stdout closed unexpectedly on file '{context}' and restart also failed.");
                }

                if (line.TrimEnd() == "{ready}")
                    break;

                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line);
            }

            return sb.ToString();
        }

        private async Task ReadStderrLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }

                    if (line == null) break;

                    lock (_stderrCollector)
                    {
                        _stderrCollector.AppendLine(line);
                    }
                }
            }
            catch (OperationCanceledException) { }
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
            try { _shutdownCts.Cancel(); } catch { }

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

            // 3. 等待 stderr 循环完全退出
            try { _stderrTask?.Wait(2000); } catch { }

            _process.Dispose();
            _ioLock.Dispose();
            _shutdownCts.Dispose();
        }
    }
}
