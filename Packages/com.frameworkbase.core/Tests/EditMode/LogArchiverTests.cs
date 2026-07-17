using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using NUnit.Framework;

namespace Framework.Tests
{
    public class LogArchiverTests
    {
        private string _root;
        private string _logDir;
        private string _outDir;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "LogArchiverTests_" + Guid.NewGuid().ToString("N"));
            _logDir = Path.Combine(_root, "Logs");
            _outDir = Path.Combine(_root, "Dumps");
            Directory.CreateDirectory(_logDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* 清理失败不影响断言结果 */ }
        }

        private void WriteLog(string fileName, string content)
            => File.WriteAllText(Path.Combine(_logDir, fileName), content);

        [Test]
        public void 打包_全部日志文件进zip且内容可读回()
        {
            WriteLog("Log_2026-07-16_10-00-00-000.txt", "first-line");
            WriteLog("Log_2026-07-16_11-00-00-000.txt", "second-line");
            WriteLog("not-a-log.dat", "excluded"); // 不匹配通配，不进包

            LogArchiveResult result = LogArchiver.CreateArchive(_logDir, _outDir);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(2, result.FileCount);
            Assert.Greater(result.ArchiveBytes, 0);
            Assert.IsTrue(File.Exists(result.ArchivePath));

            using (ZipArchive zip = ZipFile.OpenRead(result.ArchivePath))
            {
                Assert.AreEqual(2, zip.Entries.Count);
                using (var reader = new StreamReader(zip.GetEntry("Log_2026-07-16_10-00-00-000.txt").Open()))
                    Assert.AreEqual("first-line", reader.ReadToEnd());
            }
        }

        [Test]
        public void 打包_正被独占写句柄持有的日志也能共享读进包()
        {
            string held = Path.Combine(_logDir, "Log_2026-07-16_12-00-00-000.txt");
            // 模拟 GameLog 写线程持有的写句柄（StreamWriter 底层 FileShare.Read）
            using (var writer = new StreamWriter(new FileStream(
                       held, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
            {
                writer.Write("live-content");
                writer.Flush();

                LogArchiveResult result = LogArchiver.CreateArchive(_logDir, _outDir);
                Assert.IsTrue(result.Success, result.Error);
                Assert.AreEqual(1, result.FileCount);
            }
        }

        [Test]
        public void 保留上限_最旧产物先删且不误删目录内其它文件()
        {
            WriteLog("Log_2026-07-16_10-00-00-000.txt", "x");
            Directory.CreateDirectory(_outDir);
            string unrelated = Path.Combine(_outDir, "keep-me.zip");
            File.WriteAllText(unrelated, "unrelated");

            for (int i = 0; i < 4; i++)
            {
                LogArchiveResult r = LogArchiver.CreateArchive(_logDir, _outDir, maxArchives: 2);
                Assert.IsTrue(r.Success, r.Error);
                Thread.Sleep(5); // 产物文件名含毫秒时间戳，隔开避免 CreateNew 撞名
            }

            string[] archives = Directory.GetFiles(_outDir, LogArchiver.ArchivePrefix + "*.zip");
            Assert.AreEqual(2, archives.Length, "超出保留上限的旧产物应被清理");
            Assert.IsTrue(File.Exists(unrelated), "非本前缀的文件不得被保留清理误删");
        }

        [Test]
        public void 失败路径_目录不存在_目录为空_产物目录与日志目录相同()
        {
            Assert.IsFalse(LogArchiver.CreateArchive(Path.Combine(_root, "nope"), _outDir).Success);
            Assert.IsFalse(LogArchiver.CreateArchive(_logDir, _outDir).Success, "空日志目录应失败");
            WriteLog("Log_2026-07-16_10-00-00-000.txt", "x");
            Assert.IsFalse(LogArchiver.CreateArchive(_logDir, _logDir).Success, "产物目录与日志目录相同应拒绝");
        }

        [Test]
        public void GameLog冲刷_已入队的行在FlushToDisk后可从文件读到()
        {
            string gameLogDir = Path.Combine(_root, "GameLogFlush");
            GameLog.EnableFileLog(true, gameLogDir);
            try
            {
                GameLog.Log("flush-marker-line");
                Assert.IsTrue(GameLog.FlushToDisk(2000), "队列应在超时前排空");

                // 写线程仍持有文件句柄，共享读校验内容已落盘
                using (var stream = new FileStream(
                           GameLog.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    StringAssert.Contains("flush-marker-line", reader.ReadToEnd());
                }
            }
            finally
            {
                GameLog.EnableFileLog(false);
            }
        }
    }
}
