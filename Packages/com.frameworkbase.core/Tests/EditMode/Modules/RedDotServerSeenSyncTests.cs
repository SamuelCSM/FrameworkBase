using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using Framework.RedDot;
using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotServerSeenSyncTests
    {
        private const int Root = 100001;
        private const int Seen = 200003;

        private static void Wait(UniTask task) => task.GetAwaiter().GetResult();

        [Test]
        public void 登录拉取按max合并_服务端不低于本地时不回推()
        {
            RedDotService service = CreateService();
            var backend = new FakeBackend
            {
                PullResult = { new RedDotSeenRecord(Seen, 2) },
            };
            var sync = new RedDotServerSeenSync(service, backend, debounceMilliseconds: 0);

            Wait(sync.BeginAsync());

            Assert.AreEqual(1, backend.PullCount);
            Assert.AreEqual(0, backend.PushCount, "服务端版本不低于本地，无需回推");
            IReadOnlyList<RedDotSeenRecord> merged = service.ExportSeen(RedDotSeenSaveMode.ServerAccount);
            Assert.AreEqual(1, merged.Count);
            Assert.AreEqual(2, merged[0].LastSeenVersion, "拉取结果按 max 合并进本地");
            sync.Dispose();
        }

        [Test]
        public void 本地领先服务端时回推合并结果()
        {
            RedDotService service = CreateService();
            // 模拟离线/历史迁移得到更高本地进度。
            service.MergeSeen(RedDotSeenSaveMode.ServerAccount, new[] { new RedDotSeenRecord(Seen, 5) });

            var backend = new FakeBackend
            {
                PullResult = { new RedDotSeenRecord(Seen, 2) },
            };
            var sync = new RedDotServerSeenSync(service, backend, debounceMilliseconds: 0);

            Wait(sync.BeginAsync());

            Assert.AreEqual(1, backend.PushCount, "本地领先应回推");
            Assert.AreEqual(1, backend.LastPushed.Count);
            Assert.AreEqual(5, backend.LastPushed[0].LastSeenVersion, "取 max 后回推 5");
            sync.Dispose();
        }

        [Test]
        public void 会话内Acknowledge标记待推_Flush后回推并清除()
        {
            RedDotService service = CreateService();
            var backend = new FakeBackend();
            var sync = new RedDotServerSeenSync(service, backend, debounceMilliseconds: 0);
            Wait(sync.BeginAsync());
            Assert.IsFalse(sync.HasPendingPush);

            Assert.IsTrue(service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Expose));
            Assert.IsTrue(sync.HasPendingPush, "ServerAccount 确认应标记待推");

            Wait(sync.FlushAsync());
            Assert.AreEqual(1, backend.PushCount);
            Assert.AreEqual(3, backend.LastPushed[0].LastSeenVersion);
            Assert.IsFalse(sync.HasPendingPush);

            Wait(sync.FlushAsync());
            Assert.AreEqual(1, backend.PushCount, "无待推时 Flush 不重复上报");
            sync.Dispose();
        }

        [Test]
        public void 回推失败保持待推并上报异常()
        {
            RedDotService service = CreateService();
            var errors = new List<Exception>();
            var backend = new FakeBackend { FailPush = true };
            var sync = new RedDotServerSeenSync(service, backend, debounceMilliseconds: 0, errorSink: errors.Add);
            Wait(sync.BeginAsync());

            Assert.IsTrue(service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Expose));
            Wait(sync.FlushAsync());

            Assert.AreEqual(1, backend.PushCount);
            Assert.IsTrue(sync.HasPendingPush, "上报失败应保持待推，等待下次机会");
            Assert.AreEqual(1, errors.Count);
            sync.Dispose();
        }

        [Test]
        public void 释放后解绑_确认不再标记待推()
        {
            RedDotService service = CreateService();
            var backend = new FakeBackend();
            var sync = new RedDotServerSeenSync(service, backend, debounceMilliseconds: 0);
            Wait(sync.BeginAsync());
            sync.Dispose();

            service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Expose);
            Assert.IsFalse(sync.HasPendingPush, "释放后不应再响应 ServerSeenChanged");
        }

        private static RedDotService CreateService(int seenVersion = 3)
        {
            var service = new RedDotService();
            service.Initialize(new RedDotCatalog
            {
                Modules = new[]
                {
                    new RedDotModuleDefinition { Id = 1, Key = "Main", IdMin = 100000, IdMax = 199999 },
                    new RedDotModuleDefinition { Id = 2, Key = "Mail", IdMin = 200000, IdMax = 299999 },
                },
                Nodes = new[]
                {
                    new RedDotNodeDefinition
                    {
                        Id = Root, Key = "Main.Root", ModuleId = 1,
                        Kind = RedDotNodeKind.Aggregate, Aggregation = RedDotAggregation.Any,
                    },
                    new RedDotNodeDefinition
                    {
                        Id = Seen, Key = "Mail.NewPage", ModuleId = 2,
                        Kind = RedDotNodeKind.Signal, Aggregation = RedDotAggregation.None,
                    },
                },
                Edges = new[]
                {
                    new RedDotEdgeDefinition { ParentId = Root, ChildId = Seen },
                },
                SeenPolicies = new[]
                {
                    new RedDotSeenPolicyDefinition
                    {
                        SignalId = Seen,
                        Trigger = RedDotAcknowledgeTrigger.Expose,
                        SaveMode = RedDotSeenSaveMode.ServerAccount,
                        Version = seenVersion,
                    },
                },
            });
            return service;
        }

        private sealed class FakeBackend : IRedDotSeenSyncBackend
        {
            public readonly List<RedDotSeenRecord> PullResult = new List<RedDotSeenRecord>();
            public int PullCount;
            public int PushCount;
            public List<RedDotSeenRecord> LastPushed;
            public bool FailPush;

            public UniTask<IReadOnlyList<RedDotSeenRecord>> PullAsync(CancellationToken cancellationToken)
            {
                PullCount++;
                return UniTask.FromResult<IReadOnlyList<RedDotSeenRecord>>(PullResult);
            }

            public UniTask PushAsync(IReadOnlyList<RedDotSeenRecord> records, CancellationToken cancellationToken)
            {
                PushCount++;
                LastPushed = new List<RedDotSeenRecord>(records);
                if (FailPush) throw new InvalidOperationException("模拟上报失败");
                return UniTask.CompletedTask;
            }
        }
    }
}
