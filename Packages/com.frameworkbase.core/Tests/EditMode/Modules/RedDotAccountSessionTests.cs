using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using Framework.RedDot;
using Framework.Save;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    public class RedDotAccountSessionTests
    {
        private sealed class FixedKeyProvider : ISaveKeyProvider
        {
            public string GetMasterSecret() => "red-dot-account-session-test-secret";
        }

        private string _user;

        [SetUp]
        public void SetUp()
        {
            SaveManager.Instance.SetSaveKeyProvider(new FixedKeyProvider());
            _user = "reddot_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            SaveManager.Instance.SetCurrentUser(_user);
        }

        [TearDown]
        public void TearDown()
        {
            try { SaveManager.Instance.DeleteCurrentUserSaves(); } catch { }
            SaveManager.Instance.ClearCurrentUser();
            SaveManager.Instance.SetSaveKeyProvider(new DeviceSaveKeyProvider());
        }

        [UnityTest]
        public IEnumerator LocalAccount已看版本随账号存档往返_不持久化计数() => UniTask.ToCoroutine(async () =>
        {
            RedDotService first = CreateService();
            await RedDotAccountSession.BeginAsync(first);
            first.SetCount(110001, 7);
            Assert.IsTrue(first.Acknowledge(110001, RedDotAcknowledgeTrigger.Expose));
            RedDotAccountSession.End(first);
            Assert.AreEqual(0, first.GetCount(110001), "退出会话立即清运行态计数");

            // LoadAsync 与 End 内 SaveAsync 使用同档案锁，天然等待异步保存完成。
            RedDotSeenSave saved = await SaveManager.Instance.LoadAsync<RedDotSeenSave>();
            Assert.AreEqual(1, saved.records.Count);
            Assert.AreEqual(110001, saved.records[0].signalId);
            Assert.AreEqual(3, saved.records[0].lastSeenVersion);

            RedDotService second = CreateService();
            await RedDotAccountSession.BeginAsync(second);
            Assert.AreEqual(0, second.GetCount(110001), "红点计数不落盘");
            second.SetCount(110001, 7);
            Assert.AreEqual(0, second.GetCount(110001), "同账号同版本已看后隐藏，计数本身未从存档恢复");
            RedDotAccountSession.End(second);
        });

        private static RedDotService CreateService()
        {
            var service = new RedDotService();
            service.Initialize(new RedDotCatalog
            {
                Modules = new[]
                {
                    new RedDotModuleDefinition { Id = 1, Key = "Test", IdMin = 110000, IdMax = 119999 },
                },
                Nodes = new[]
                {
                    new RedDotNodeDefinition
                    {
                        Id = 110001,
                        Key = "Test.NewPage",
                        ModuleId = 1,
                        Kind = RedDotNodeKind.Signal,
                        Aggregation = RedDotAggregation.None,
                    },
                },
                SeenPolicies = new[]
                {
                    new RedDotSeenPolicyDefinition
                    {
                        SignalId = 110001,
                        Trigger = RedDotAcknowledgeTrigger.Expose,
                        SaveMode = RedDotSeenSaveMode.LocalAccount,
                        Version = 3,
                    },
                },
            });
            return service;
        }
    }
}
