using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    // exiftool 常驻进程包装器：使用 -stay_open 模式，启动一次，通过 stdin/stdout 持续派发任务，
    // 避免每次调用都重新加载 Perl 运行时（节省 ~200-400ms/次）。
    // 线程安全：内部用 SemaphoreSlim 序列化 stdin/stdout 的读写。
    // 崩溃自动恢复：当 exiftool 进程意外退出时（如遇损坏文件触发 Win32 异常），
    // 自动重启进程并重试当前命令一次。若二次崩溃则放弃本条命令、抛出异常。
    public sealed class PersistentExifTool : IDisposable
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);
        private Process _process;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly StringBuilder _stderrCollector = new();
        private Task? _stderrTask;
        private CancellationTokenSource _shutdownCts = new();
        private readonly string _exifToolPath;
        private readonly string _toolDir;
        private readonly string _tempDir;
        private bool _disposed;

        // 第几次崩溃后重启（0 = 从未崩溃）。
        public int RestartCount { get; private set; }

        // 当 exiftool 进程意外退出并完成自动重启时触发。
        // 参数为可显示给用户的消息文本。
        public event Action<string>? OnRestarted;

        public PersistentExifTool(string exifToolPath)
        {
            _exifToolPath = exifToolPath;
            _toolDir = Path.GetDirectoryName(exifToolPath) ?? AppContext.BaseDirectory;
            _tempDir = Path.GetTempPath();

            _process = LaunchProcess();
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_shutdownCts.Token));
        }

        // 创建一个新的 exiftool -stay_open 进程。与构造函数共享的初始化逻辑。
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

        // 最近一条命令的参数，崩溃时用于诊断日志。
        private string[]? _lastCommandArgs;

        // 重启已崩溃的 exiftool 进程。调用方必须持有 _ioLock。
        // context: 崩溃时正在执行的命令描述（如文件路径）
        private void RestartProcess(string? context = null)
        {
            // 抢在进程被 Kill 之前收集 stderr（崩溃原因可能在里面）
            string stderr = FlushStderr();

            // 清理旧进程
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { try { if (!_process.HasExited) _process.Kill(); } catch { } }
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

        // 发送一条命令并等待 JSON 响应。线程安全。
        // 如 exiftool 卡住或在命令执行期间崩溃，精准终止并自动重启；总执行次数最多两次。
        public async Task<string> SendCommandAsync(CancellationToken token, params string[] args)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PersistentExifTool));

            await _ioLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string? context = args.Length > 0 ? args[^1] : null;
                Exception? lastException = null;

                for (int attempt = 1; attempt <= ExternalToolProcessGuard.MaxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (_process.HasExited)
                        RestartProcess(context);

                    using var timeoutCts = new CancellationTokenSource(CommandTimeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                    try
                    {
                        return await SendCommandInternalAsync(linkedCts.Token, args).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // 命令已写入 stdin 后被取消时，必须重启以丢弃残留 stdout。
                        try { RestartProcess("cancelled command"); } catch { }
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        lastException = ex;
                        LogService.Warn(
                            $"exiftool command timed out (attempt {attempt}/{ExternalToolProcessGuard.MaxAttempts}): {context}",
                            source: Models.LogSource.System);
                        try { RestartProcess($"timeout: {context}"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        LogService.Warn(
                            $"exiftool command failed (attempt {attempt}/{ExternalToolProcessGuard.MaxAttempts}): {context}: {ex.Message}",
                            ex.ToString(),
                            Models.LogSource.System);
                        try { RestartProcess(context); } catch { }
                    }

                    if (attempt == ExternalToolProcessGuard.MaxAttempts)
                        throw new TimeoutException(
                            $"exiftool failed after {ExternalToolProcessGuard.MaxAttempts} attempts: {context}",
                            lastException);
                }

                throw new InvalidOperationException("Unreachable exiftool retry state.");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        // 实际执行一次命令；重启和有限重试由 SendCommandAsync 统一管理。
        private async Task<string> SendCommandInternalAsync(
            CancellationToken token, string[] args)
        {
            _lastCommandArgs = args;

            // 从参数中提取文件路径作为崩溃诊断上下文
            // args 格式如: -j -Rotation -Orientation -ThumbnailImage -ContentIdentifier <filePath>
            string? context = args.Length > 0 ? args[^1] : null;

            if (_process.HasExited)
                throw new InvalidOperationException($"Persistent exiftool exited before command: {context}");

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
                    line = await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    line = null;
                }

                if (line == null)
                    throw new IOException($"Persistent exiftool stdout closed unexpectedly: {context}");

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

        // 获取并清空 stderr 缓冲区（用于日志记录）。
        // 线程安全，加锁后返回当前累积的 stderr 内容并清空。
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
                        _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }

            // 3. 等待 stderr 循环完全退出
            try { _stderrTask?.Wait(2000); } catch { }

            _process.Dispose();
            _ioLock.Dispose();
            _shutdownCts.Dispose();
        }
    }
}
