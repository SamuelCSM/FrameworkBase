using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// GameLog 文件日志单测：异步落盘、退出冲刷不丢、按体积轮转、按个数清理旧文件。
    /// 文件写入在后台线程，断言前先 Shutdown（内部 Join 写线程并冲刷队列）。
    /// </summary>
    public class GameLogTests
    {
        private string _dir;
        private long _origMaxBytes;
        private int _origMaxFiles;
        private LogLevel _origLevel;

        [SetUp]
        public void SetUp()
        {
            _origMaxBytes = GameLog.MaxLogFileBytes;
            _origMaxFiles = GameLog.MaxLogFiles;
            _origLevel = GameLog.CurrentLogLevel;

            _dir = Path.Combine(Application.temporaryCachePath, "GameLogTest_" + Path.GetRandomFileName());
        }

        [TearDown]
        public void TearDown()
        {
            GameLog.Shutdown(); // 确保写线程停掉、文件句柄释放
            GameLog.MaxLogFileBytes = _origMaxBytes;
            GameLog.MaxLogFiles = _origMaxFiles;
            GameLog.SetLogLevel(_origLevel);

            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, true);
            }
            catch { /* 清理失败无碍，临时目录 */ }
        }

        private string[] LogFiles() => Directory.GetFiles(_dir, "Log_*.txt");

        private int CountMarkerLines(string marker)
        {
            int count = 0;
            foreach (string file in LogFiles())
                foreach (string line in File.ReadAllLines(file))
                    if (line.Contains(marker))
                        count++;
            return count;
        }

        [Test]
        public void 异步落盘_退出冲刷后可读到全部行()
        {
            GameLog.SetLogLevel(LogLevel.Log);
            GameLog.EnableFileLog(true, _dir);

            const int total = 200;
            for (int i = 0; i < total; i++)
                GameLog.Log($"MARKER_A line {i}");

            GameLog.EnableFileLog(false); // 冲刷队列 + 停写线程

            Assert.AreEqual(total, CountMarkerLines("MARKER_A"),
                "异步写入的所有行在冲刷后都应落盘，退出时不丢日志");
        }

        [Test]
        public void 超过体积上限_自动轮转多文件()
        {
            GameLog.MaxLogFileBytes = 1024; // 极小阈值逼出轮转
            GameLog.MaxLogFiles = 100;      // 本用例不清理，只看轮转
            GameLog.SetLogLevel(LogLevel.Log);
            GameLog.EnableFileLog(true, _dir);

            string payload = new string('x', 200); // 每行约 200+ 字节
            for (int i = 0; i < 100; i++)
                GameLog.Log($"MARKER_B {i} {payload}");

            GameLog.EnableFileLog(false);

            Assert.Greater(LogFiles().Length, 1, "超过单文件体积上限应切分为多个日志文件");
            Assert.AreEqual(100, CountMarkerLines("MARKER_B"), "轮转过程中不得丢行");
        }

        [Test]
        public void 文件个数超上限_清理最旧()
        {
            GameLog.MaxLogFileBytes = 512; // 小阈值频繁轮转
            GameLog.MaxLogFiles = 3;       // 只保留最新 3 个
            GameLog.SetLogLevel(LogLevel.Log);
            GameLog.EnableFileLog(true, _dir);

            string payload = new string('y', 200);
            for (int i = 0; i < 200; i++)
                GameLog.Log($"MARKER_C {i} {payload}");

            GameLog.EnableFileLog(false);

            Assert.LessOrEqual(LogFiles().Length, GameLog.MaxLogFiles,
                "日志文件个数不得超过保留上限（旧文件应被清理）");
            Assert.Greater(LogFiles().Length, 0, "清理后仍应保留最新日志文件");
        }

        [Test]
        public void 关闭文件日志后_不再写入()
        {
            GameLog.SetLogLevel(LogLevel.Log);
            GameLog.EnableFileLog(true, _dir);
            GameLog.Log("MARKER_D before");
            GameLog.EnableFileLog(false);

            int before = CountMarkerLines("MARKER_D");
            GameLog.Log("MARKER_D after"); // 文件日志已关，仅走 Unity 控制台
            System.Threading.Thread.Sleep(50);

            Assert.AreEqual(before, CountMarkerLines("MARKER_D"),
                "关闭文件日志后不应再有新行落盘");
        }
    }
}
