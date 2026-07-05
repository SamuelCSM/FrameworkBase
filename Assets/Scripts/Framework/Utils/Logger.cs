using System;
using System.IO;
using System.Text;
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
    /// 提供统一的日志输出接口，支持多种日志级别、文件输出和格式化
    /// </summary>
    public static class Logger
    {
        private static LogLevel _logLevel = LogLevel.Debug;
        private static bool _enableFileLog = false;
        private static string _logFilePath = string.Empty;
        private static readonly object _fileLock = new object();
        private static StreamWriter _logWriter = null;

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

        /// <summary>
        /// 查询指定级别当前是否会输出，供热路径在构造昂贵日志字符串前显式短路：
        /// <c>if (Logger.IsEnabled(LogLevel.Log)) Logger.Log(BuildExpensiveString());</c>
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
            UnityEngine.Debug.Log($"[Logger] 日志级别设置为: {level}");
        }

        /// <summary>
        /// 启用或禁用文件日志
        /// </summary>
        /// <param name="enable">是否启用</param>
        /// <param name="logDirectory">日志目录（可选，默认为 PersistentDataPath/Logs）</param>
        public static void EnableFileLog(bool enable, string logDirectory = null)
        {
            if (enable == _enableFileLog)
                return;

            _enableFileLog = enable;

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

                // 生成日志文件名（按日期分割）
                string fileName = $"Log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                _logFilePath = Path.Combine(logDirectory, fileName);

                try
                {
                    // 创建文件写入器
                    _logWriter = new StreamWriter(_logFilePath, true, Encoding.UTF8);
                    _logWriter.AutoFlush = true;

                    UnityEngine.Debug.Log($"[Logger] 文件日志已启用，路径: {_logFilePath}");
                    WriteToFile($"========== 日志开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[Logger] 启用文件日志失败: {ex.Message}");
                    _enableFileLog = false;
                }
            }
            else
            {
                // 关闭文件日志
                CloseFileLog();
                UnityEngine.Debug.Log("[Logger] 文件日志已禁用");
            }
        }

        /// <summary>
        /// 关闭文件日志
        /// </summary>
        private static void CloseFileLog()
        {
            if (_logWriter != null)
            {
                lock (_fileLock)
                {
                    try
                    {
                        WriteToFile($"========== 日志结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
                        _logWriter.Close();
                        _logWriter.Dispose();
                        _logWriter = null;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[Logger] 关闭文件日志失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 写入文件
        /// </summary>
        /// <param name="message">消息</param>
        private static void WriteToFile(string message)
        {
            if (!_enableFileLog || _logWriter == null)
                return;

            lock (_fileLock)
            {
                try
                {
                    _logWriter.WriteLine(message);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[Logger] 写入日志文件失败: {ex.Message}");
                }
            }
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
        /// 因此高频路径可放心使用 Logger.Debug 而无需手动短路。
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
        /// 清理日志系统（应用退出时调用）
        /// </summary>
        public static void Shutdown()
        {
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
