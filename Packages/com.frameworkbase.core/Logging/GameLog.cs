using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Log = 1,
        Warning = 2,
        Error = 3,
        None = 4  // 关闭所有日志
    }

    /// <summary>
    /// 日志系统
    /// 提供统一的日志输出接口，支持多种日志级别、文件输出和格式化。
    ///
    /// 文件日志为异步写入：调用线程只做格式化 + 入队（微秒级），
    /// 磁盘 I/O 由后台线程批量完成，主线程零阻塞（帧循环里可放心打日志）。
    /// 代价是进程被强杀时可能丢最后一批未落盘的行——崩溃取证走 CrashReporter，
    /// 文件日志定位是运行流水，丢尾部可接受。
    ///
    /// 轮转：单文件超过 <see cref="MaxLogFileBytes"/> 即切新文件；
    /// 目录内按 <see cref="MaxLogFiles"/> 保留最新若干个，旧文件自动删除，
    /// 长期运营的真机设备不会被日志撑爆存储。
    /// </summary>
    public static class GameLog
    {
        private static LogLevel _logLevel = LogLevel.Debug;
        private static bool _enableFileLog = false;
        private static string _logFilePath = string.Empty;
        private static string _logDirectory = string.Empty;
        private static readonly object _fileLock = new object();
        private static StreamWriter _logWriter = null;
        private static long _currentFileBytes;

        // ── 异步写入 ─────────────────────────────────────────────────────────
        private static readonly ConcurrentQueue<string> _pendingLines = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private static Thread _writerThread;
        private static volatile bool _writerRunning;
        private static int _pendingCount;
        private static int _droppedLines;

        /// <summary>内存队列上限：磁盘卡死时封顶内存占用，超限丢行并计数补记。</summary>
        private const int MaxPendingLines = 8192;

        /// <summary>后台线程无信号时的兜底醒来间隔（毫秒）。</summary>
        private const int WriterIdleWaitMs = 500;

        /// <summary>单个日志文件体积上限（超过即轮转），可按项目调整。</summary>
        public static long MaxLogFileBytes = 4 * 1024 * 1024;

        /// <summary>日志目录保留的文件个数上限（含当前文件），可按项目调整。</summary>
        public static int MaxLogFiles = 5;

        /// <summary>
        /// 当前日志级别
        /// </summary>
        public static LogLevel CurrentLogLevel
        {
            get { return _logLevel; }
        }

        /// <summary>
        /// 是否启用文件日志
        /// </summary>
        public static bool IsFileLogEnabled
        {
            get { return _enableFileLog; }
        }

        /// <summary>
        /// 日志文件路径
        /// </summary>
        public static string LogFilePath
        {
            get { return _logFilePath; }
        }

        /// <summary>日志目录（未启用文件日志时为空串）。日志回捞打包的输入。</summary>
        public static string LogDirectory
        {
            get { return _logDirectory; }
        }

        /// <summary>
        /// 冲刷未落盘队列（日志回捞打包前调用，保证 zip 里是最新内容）。
        /// 唤醒写线程并有界等待队列排空，随后再做一次文件 Flush 兜底。
        /// 只保证调用时刻已入队的行；等待期间新入队的行不承诺包含。
        /// </summary>
        /// <param name="timeoutMs">等待队列排空的上限毫秒数。</param>
        /// <returns>超时前队列已排空返回 true；false 表示打包内容可能缺尾部若干行。</returns>
        public static bool FlushToDisk(int timeoutMs = 1000)
        {
            if (!_enableFileLog)
                return true;

            int deadline = Environment.TickCount + timeoutMs;
            while (Volatile.Read(ref _pendingCount) > 0 && Environment.TickCount - deadline < 0)
            {
                _writeSignal.Set();
                Thread.Sleep(10);
            }

            lock (_fileLock)
            {
                try { _logWriter?.Flush(); }
                catch { /* 与写线程同款：磁盘异常时丢日志可接受 */ }
            }
            return Volatile.Read(ref _pendingCount) == 0;
        }

        /// <summary>
        /// 查询指定级别当前是否会输出，供热路径在构造昂贵日志字符串前显式短路：
        /// <c>if (GameLog.IsEnabled(LogLevel.Log)) GameLog.Log(BuildExpensiveString());</c>
        /// </summary>
        /// <param name="level">待检查的日志级别。</param>
        /// <returns>该级别会输出时返回 true。</returns>
        public static bool IsEnabled(LogLevel level)
        {
            return level >= _logLevel;
        }

        /// <summary>
        /// Debug 级别是否会输出。注意 Debug 仅在 Editor / Development Build 编译保留，
        /// Release 构建中 <see cref="Debug"/> 调用连同其字符串参数会被编译器整体剥离。
        /// </summary>
        public static bool IsDebugEnabled => LogLevel.Debug >= _logLevel;

        /// <summary>
        /// 设置日志级别
        /// </summary>
        /// <param name="level">日志级别</param>
        public static void SetLogLevel(LogLevel level)
        {
            _logLevel = level;
            UnityEngine.Debug.Log($"[GameLog] 日志级别设置为: {level}");
        }

        /// <summary>
        /// 启用或禁用文件日志。启用时打开新文件、清理超出保留数的旧文件并启动后台写线程；
        /// 禁用时冲刷队列、停线程并关闭文件。
        /// </summary>
        /// <param name="enable">是否启用</param>
        /// <param name="logDirectory">日志目录（可选，默认为 PersistentDataPath/Logs）</param>
        public static void EnableFileLog(bool enable, string logDirectory = null)
        {
            if (enable == _enableFileLog)
                return;

            if (enable)
            {
                // 确定日志目录
                if (string.IsNullOrEmpty(logDirectory))
                {
                    logDirectory = Path.Combine(Application.persistentDataPath, "Logs");
                }

                // 创建日志目录
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                _logDirectory = logDirectory;

                try
                {
                    lock (_fileLock)
                    {
                        OpenNewLogFile();
                    }
                    CleanupOldFiles();
                    StartWriterThread();
                    _enableFileLog = true;

                    UnityEngine.Debug.Log($"[GameLog] 文件日志已启用（异步写入），路径: {_logFilePath}");
                    WriteToFile($"========== 日志开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GameLog] 启用文件日志失败: {ex.Message}");
                    _enableFileLog = false;
                }
            }
            else
            {
                // 先摘牌再收尾：停止入队后冲刷存量，保证已入队的行都落盘
                _enableFileLog = false;
                CloseFileLog();
                UnityEngine.Debug.Log("[GameLog] 文件日志已禁用");
            }
        }

        // ── 后台写线程 ───────────────────────────────────────────────────────

        private static void StartWriterThread()
        {
            if (_writerThread != null && _writerThread.IsAlive)
                return;

            _writerRunning = true;
            _writerThread = new Thread(WriterLoop)
            {
                Name = "GameLogWriter",
                IsBackground = true // 不阻止进程退出；正常退出走 Shutdown 冲刷
            };
            _writerThread.Start();
        }

        private static void WriterLoop()
        {
            while (_writerRunning)
            {
                _writeSignal.WaitOne(WriterIdleWaitMs);
                DrainQueue();
            }
            DrainQueue(); // 停机前收尾一遍
        }

        /// <summary>把队列里的行批量写入并做一次 Flush（批量落盘，不逐行刷）。</summary>
        private static void DrainQueue()
        {
            bool wroteAny = false;

            while (_pendingLines.TryDequeue(out string line))
            {
                Interlocked.Decrement(ref _pendingCount);
                WriteLineWithRotation(line);
                wroteAny = true;
            }

            int dropped = Interlocked.Exchange(ref _droppedLines, 0);
            if (dropped > 0)
            {
                WriteLineWithRotation($"[GameLog] 写入积压超过 {MaxPendingLines} 行，丢弃 {dropped} 行");
                wroteAny = true;
            }

            if (!wroteAny)
                return;

            lock (_fileLock)
            {
                try { _logWriter?.Flush(); }
                catch { /* 磁盘异常时丢日志可接受，不能让写线程崩掉 */ }
            }
        }

        /// <summary>写一行并在超过体积上限时轮转到新文件（仅写线程调用）。</summary>
        private static void WriteLineWithRotation(string line)
        {
            lock (_fileLock)
            {
                if (_logWriter == null)
                    return;

                try
                {
                    _logWriter.WriteLine(line);
                    // 估算字节数即可（UTF-8 中文按 1 字符≈1~3 字节），轮转阈值不需要精确
                    _currentFileBytes += line.Length + 2;

                    if (_currentFileBytes >= MaxLogFileBytes)
                    {
                        _logWriter.Flush();
                        _logWriter.Close();
                        _logWriter.Dispose();
                        OpenNewLogFile();
                        CleanupOldFiles();
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GameLog] 写入日志文件失败: {ex.Message}");
                }
            }
        }

        /// <summary>打开新日志文件（调用方持有 _fileLock）。文件名含毫秒避免快速轮转时撞名。</summary>
        private static void OpenNewLogFile()
        {
            string fileName = $"Log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt";
            _logFilePath = Path.Combine(_logDirectory, fileName);
            _logWriter = new StreamWriter(_logFilePath, true, Encoding.UTF8) { AutoFlush = false };
            _currentFileBytes = 0;
        }

        /// <summary>删除超出保留个数的旧日志（时间戳文件名按字典序即时间序）。</summary>
        private static void CleanupOldFiles()
        {
            try
            {
                string[] files = Directory.GetFiles(_logDirectory, "Log_*.txt");
                if (files.Length <= MaxLogFiles)
                    return;

                Array.Sort(files, StringComparer.Ordinal); // 旧在前
                int deleteCount = files.Length - MaxLogFiles;
                for (int i = 0; i < deleteCount; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { /* 单个文件删除失败不影响其余 */ }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[GameLog] 清理旧日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭文件日志：停写线程（等待其冲刷存量队列）后关文件。
        /// </summary>
        private static void CloseFileLog()
        {
            // 停线程：WriterLoop 退出前会把队列收尾写完
            if (_writerThread != null && _writerThread.IsAlive)
            {
                _writerRunning = false;
                _writeSignal.Set();
                if (!_writerThread.Join(2000))
                    UnityEngine.Debug.LogWarning("[GameLog] 写线程未在 2s 内退出，尾部日志可能丢失");
            }
            _writerThread = null;
            _writerRunning = false;

            lock (_fileLock)
            {
                if (_logWriter == null)
                    return;

                try
                {
                    _logWriter.WriteLine($"========== 日志结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
                    _logWriter.Flush();
                    _logWriter.Close();
                    _logWriter.Dispose();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GameLog] 关闭文件日志失败: {ex.Message}");
                }
                finally
                {
                    _logWriter = null;
                }
            }
        }

        /// <summary>
        /// 写入文件：只入队 + 唤醒写线程，调用线程不做磁盘 I/O。
        /// 队列超限时丢行计数，由写线程补记一条丢弃统计。
        /// </summary>
        /// <param name="message">消息</param>
        private static void WriteToFile(string message)
        {
            if (!_enableFileLog)
                return;

            if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _droppedLines);
                return;
            }

            _pendingLines.Enqueue(message);
            _writeSignal.Set();
        }

        /// <summary>
        /// 格式化日志消息
        /// </summary>
        /// <param name="level">日志级别</param>
        /// <param name="message">消息</param>
        /// <returns>格式化后的消息</returns>
        private static string FormatMessage(LogLevel level, string message)
        {
            // 格式：[时间戳] [级别] 消息
            return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        }

        /// <summary>
        /// 格式化日志消息（带堆栈信息）
        /// </summary>
        /// <param name="level">日志级别</param>
        /// <param name="message">消息</param>
        /// <param name="stackTrace">堆栈信息</param>
        /// <returns>格式化后的消息</returns>
        private static string FormatMessageWithStack(LogLevel level, string message, string stackTrace)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(FormatMessage(level, message));
            if (!string.IsNullOrEmpty(stackTrace))
            {
                sb.AppendLine("StackTrace:");
                sb.AppendLine(stackTrace);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 输出 Debug 级别日志。
        /// 通过 <see cref="System.Diagnostics.ConditionalAttribute"/> 仅在 Editor / Development Build 中保留；
        /// Release 构建里该方法的所有调用（含 $"..." 字符串插值实参）会被编译器整体移除，做到零 GC 零开销，
        /// 因此高频路径可放心使用 GameLog.Debug 而无需手动短路。
        /// </summary>
        /// <param name="message">消息</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Debug(string message)
        {
            if (_logLevel > LogLevel.Debug)
                return;

            string formattedMessage = FormatMessage(LogLevel.Debug, message);
            UnityEngine.Debug.Log(formattedMessage);

            if (_enableFileLog)
            {
                WriteToFile(formattedMessage);
            }
        }

        /// <summary>
        /// 输出 Log 级别日志
        /// </summary>
        /// <param name="message">消息</param>
        public static void Log(string message)
        {
            if (_logLevel > LogLevel.Log)
                return;

            string formattedMessage = FormatMessage(LogLevel.Log, message);
            UnityEngine.Debug.Log(formattedMessage);

            if (_enableFileLog)
            {
                WriteToFile(formattedMessage);
            }
        }

        /// <summary>
        /// 输出 Warning 级别日志
        /// </summary>
        /// <param name="message">消息</param>
        public static void Warning(string message)
        {
            if (_logLevel > LogLevel.Warning)
                return;

            string formattedMessage = FormatMessage(LogLevel.Warning, message);
            UnityEngine.Debug.LogWarning(formattedMessage);

            if (_enableFileLog)
            {
                WriteToFile(formattedMessage);
            }
        }

        /// <summary>
        /// 输出 Error 级别日志
        /// </summary>
        /// <param name="message">消息</param>
        public static void Error(string message)
        {
            if (_logLevel > LogLevel.Error)
                return;

            string formattedMessage = FormatMessageWithStack(LogLevel.Error, message, Environment.StackTrace);
            UnityEngine.Debug.LogError(formattedMessage);

            if (_enableFileLog)
            {
                WriteToFile(formattedMessage);
            }
        }

        /// <summary>
        /// 输出异常日志
        /// </summary>
        /// <param name="exception">异常对象</param>
        public static void Exception(Exception exception)
        {
            if (_logLevel > LogLevel.Error)
                return;

            string message = $"Exception: {exception.GetType().Name} - {exception.Message}";
            string formattedMessage = FormatMessageWithStack(LogLevel.Error, message, exception.StackTrace);
            UnityEngine.Debug.LogError(formattedMessage);

            if (_enableFileLog)
            {
                WriteToFile(formattedMessage);
            }
        }

        /// <summary>
        /// 清理日志系统（应用退出时调用）：冲刷未落盘队列并关闭文件。
        /// </summary>
        public static void Shutdown()
        {
            if (!_enableFileLog)
                return;
            _enableFileLog = false;
            CloseFileLog();
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器模式下，播放模式结束时自动关闭文件日志
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            UnityEditor.EditorApplication.playModeStateChanged += (state) =>
            {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                {
                    Shutdown();
                }
            };
        }
#endif
    }
}
