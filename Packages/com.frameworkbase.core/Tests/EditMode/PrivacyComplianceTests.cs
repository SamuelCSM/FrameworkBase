using System.Collections.Generic;
using System.IO;
using Framework.Analytics;
using Framework.Core.Privacy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 隐私合规单测：同意版本管理往返、协议改版旧同意失效、埋点采集闸门（Track 不产生数据、
    /// Flush 不出网）、RTBF 抹除编排（文件被删、逐项报告、无 GameEntry 环境不抛异常）。
    /// </summary>
    public class PrivacyComplianceTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            PlayerPrefs.DeleteKey("Privacy.AcceptedPolicyVersion");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey("Privacy.AcceptedPolicyVersion");
            LogAssert.ignoreFailingMessages = false;
        }

        // ── 同意管理 ─────────────────────────────────────────────────────────

        [Test]
        public void 同意_版本化往返()
        {
            Assert.IsFalse(PrivacyConsent.IsAccepted(1), "从未同意");
            Assert.AreEqual(0, PrivacyConsent.AcceptedPolicyVersion);

            PrivacyConsent.Accept(2);
            Assert.IsTrue(PrivacyConsent.IsAccepted(2));
            Assert.IsTrue(PrivacyConsent.IsAccepted(1), "同意了 v2 覆盖 v1");
            Assert.IsFalse(PrivacyConsent.IsAccepted(3), "协议改版到 v3 后旧同意失效，须重新征得");
        }

        [Test]
        public void 撤回_回到未同意态()
        {
            PrivacyConsent.Accept(2);
            PrivacyConsent.Revoke();

            Assert.IsFalse(PrivacyConsent.IsAccepted(2));
            Assert.AreEqual(0, PrivacyConsent.AcceptedPolicyVersion);
        }

        [Test]
        public void 非法版本号_拒绝记录()
        {
            PrivacyConsent.Accept(0);
            PrivacyConsent.Accept(-1);
            Assert.AreEqual(0, PrivacyConsent.AcceptedPolicyVersion);
            Assert.IsFalse(PrivacyConsent.IsAccepted(0), "版本 0 永远不算已同意");
        }

        // ── 采集闸门 ─────────────────────────────────────────────────────────

        [Test]
        public void 采集闸门关闭_Track不产生数据_Flush不出网()
        {
            var analytics = new AnalyticsManager();
            analytics.OnInit();
            analytics.ClearQueue();
            var backend = new CountingBackend();
            analytics.SetBackend(backend);

            analytics.CollectionEnabled = false;
            for (int i = 0; i < 10; i++)
                analytics.Track("e");
            Assert.AreEqual(0, analytics.QueuedCount, "闸门关闭时数据根本不产生（不进队列）");

            Assert.IsTrue(analytics.FlushAsync().GetAwaiter().GetResult());
            Assert.AreEqual(0, backend.SendCount, "闸门关闭时不出网");

            analytics.CollectionEnabled = true;
            analytics.Track("e_after_consent");
            Assert.AreEqual(1, analytics.QueuedCount, "同意后恢复采集");

            analytics.ClearQueue();
            analytics.OnShutdown();
        }

        // ── RTBF 抹除 ────────────────────────────────────────────────────────

        [Test]
        public void 抹除_删除遥测残留文件且逐项报告()
        {
            // 造崩溃记录与启动指标假文件
            string crash = Path.Combine(Application.persistentDataPath, "crash_reports.jsonl");
            string metrics = Path.Combine(Application.persistentDataPath, "launch_metrics_last.json");
            File.WriteAllText(crash, "{}");
            File.WriteAllText(metrics, "{}");

            List<PrivacyCompliance.EraseEntry> report = PrivacyCompliance.EraseAllLocalUserData();

            Assert.IsFalse(File.Exists(crash), "崩溃记录应被删除");
            Assert.IsFalse(File.Exists(metrics), "启动指标快照应被删除");
            Assert.Greater(report.Count, 0, "应产出逐项报告");
            foreach (PrivacyCompliance.EraseEntry entry in report)
                Assert.IsTrue(entry.Success, $"纯单测环境所有项都应成功: {entry}");
        }

        [Test]
        public void 抹除_同意状态一并清空()
        {
            PrivacyConsent.Accept(5);
            PrivacyCompliance.EraseAllLocalUserData(); // DeleteAllPrefs 连同意记录一起清

            Assert.IsFalse(PrivacyConsent.IsAccepted(5), "抹除后按未同意处理（语义正确）");
        }

        private sealed class CountingBackend : IAnalyticsBackend
        {
            public int SendCount;
            public string Name => "counting";

            public Cysharp.Threading.Tasks.UniTask<bool> SendAsync(IReadOnlyList<string> batch)
            {
                SendCount++;
                return Cysharp.Threading.Tasks.UniTask.FromResult(true);
            }
        }
    }
}
