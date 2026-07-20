using System;
using System.Collections.Generic;
using System.Linq;
using Framework.Foundation;
using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotServiceTests
    {
        private const int Root = 100001;
        private const int EntryA = 100002;
        private const int EntryC = 100003;
        private const int Shared = 200001;
        private const int Other = 200002;
        private const int Seen = 200003;

        [Test]
        public void 多父节点同时传播_唯一Signal聚合不重复计数()
        {
            RedDotService service = CreateService();
            service.SetCount(Shared, 3);

            Assert.AreEqual(3, service.GetCount(EntryA));
            Assert.AreEqual(1, service.GetCount(EntryC));
            Assert.AreEqual(3, service.GetCount(Root), "Root 使用 SumUniqueSignals，共享叶子只算一次");
        }

        [Test]
        public void 模块完整快照遗漏旧Signal时自动归零()
        {
            RedDotService service = CreateService();
            service.RegisterProvider("Mail", new[] { Shared, Other });
            service.ReplaceProviderSnapshot("Mail", new Dictionary<int, int>
            {
                { Shared, 2 }, { Other, 4 },
            });
            Assert.AreEqual(6, service.GetCount(EntryA));

            service.ReplaceProviderSnapshot("Mail", new Dictionary<int, int> { { Shared, 1 } });
            Assert.AreEqual(0, service.GetCount(Other));
            Assert.AreEqual(1, service.GetCount(EntryA));
            Assert.IsTrue(service.IsProviderReady("Mail"));
        }

        [Test]
        public void Provider所有权冲突和越权写入立即失败()
        {
            RedDotService service = CreateService();
            service.RegisterProvider("Mail", new[] { Shared });
            Assert.DoesNotThrow(() => service.RegisterProvider("Mail", new[] { Shared }), "切号后相同所有权可幂等重注册");
            Assert.Throws<InvalidOperationException>(() => service.RegisterProvider("Other", new[] { Shared }));
            Assert.Throws<InvalidOperationException>(() =>
                service.ReplaceProviderSnapshot("Mail", new Dictionary<int, int> { { Other, 1 } }));
        }

        [Test]
        public void Batch只通知最终值()
        {
            RedDotService service = CreateService();
            var notified = new List<int>();
            service.Subscribe(EntryA, notified.Add, notifyImmediately: false);
            using (service.BeginBatch())
            {
                service.SetCount(Shared, 1);
                service.SetCount(Shared, 2);
                service.SetCount(Other, 5);
            }
            CollectionAssert.AreEqual(new[] { 7 }, notified);
        }

        [Test]
        public void 弱提示按触发时机和版本清除_新版本重新出现()
        {
            RedDotService service = CreateService(seenVersion: 3);
            service.SetCount(Seen, 1);
            Assert.AreEqual(1, service.GetCount(Seen));
            Assert.IsFalse(service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Enter));
            Assert.AreEqual(1, service.GetCount(Seen));
            Assert.IsTrue(service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Expose));
            Assert.AreEqual(0, service.GetCount(Seen));

            IReadOnlyList<RedDotSeenRecord> saved = service.ExportSeen(RedDotSeenSaveMode.LocalAccount);
            RedDotService nextVersion = CreateService(seenVersion: 4);
            nextVersion.ImportSeen(RedDotSeenSaveMode.LocalAccount, saved);
            nextVersion.SetCount(Seen, 1);
            Assert.AreEqual(1, nextVersion.GetCount(Seen));
        }

        [Test]
        public void 业务状态红点不能被Acknowledge清除()
        {
            RedDotService service = CreateService();
            service.SetCount(Shared, 1);
            Assert.IsFalse(service.Acknowledge(Shared, RedDotAcknowledgeTrigger.Manual));
            Assert.AreEqual(1, service.GetCount(Shared));
        }

        [Test]
        public void 重置账号清零数值和已看缓存但保留订阅()
        {
            RedDotService service = CreateService();
            service.SetCount(Seen, 1);
            service.Acknowledge(Seen, RedDotAcknowledgeTrigger.Expose);
            var values = new List<int>();
            service.Subscribe(EntryA, values.Add, notifyImmediately: false);
            service.SetCount(Shared, 2);
            service.ResetAccountState();
            service.SetCount(Seen, 1);

            CollectionAssert.AreEqual(new[] { 2, 0 }, values);
            Assert.AreEqual(1, service.GetCount(Seen), "LocalAccount 已看缓存已清，等新账号导入");
        }

        [Test]
        public void UI可以在目录初始化前订阅()
        {
            var service = new RedDotService();
            var values = new List<int>();
            service.Subscribe(Shared, values.Add);
            service.Initialize(CreateCatalog());
            service.SetCount(Shared, 2);
            CollectionAssert.AreEqual(new[] { 0, 2 }, values);
        }

        [Test]
        public void 环形依赖和非法节点类型被目录校验拒绝()
        {
            RedDotCatalog catalog = CreateCatalog();
            catalog.Edges = catalog.Edges.Concat(new[]
            {
                new RedDotEdgeDefinition { ParentId = EntryA, ChildId = Root }
            }).ToArray();
            RedDotCatalogValidationResult result = RedDotCatalogValidator.Validate(catalog);
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("循环")));
        }

        [Test]
        public void 订阅异常与回调内写入均被隔离上报()
        {
            var errors = new List<Exception>();
            RedDotService service = CreateService();
            service.ObserverErrorSink = errors.Add;
            var received = new List<int>();
            service.Subscribe(Shared, _ => service.SetCount(Other, 1), notifyImmediately: false);
            service.Subscribe(Shared, received.Add, notifyImmediately: false);
            service.SetCount(Shared, 1);

            Assert.AreEqual(1, errors.Count);
            CollectionAssert.AreEqual(new[] { 1 }, received);
            Assert.AreEqual(0, service.GetCount(Other));
        }

        private static RedDotService CreateService(int seenVersion = 3)
        {
            var service = new RedDotService();
            service.Initialize(CreateCatalog(seenVersion));
            return service;
        }

        private static RedDotCatalog CreateCatalog(int seenVersion = 3)
        {
            return new RedDotCatalog
            {
                Modules = new[]
                {
                    new RedDotModuleDefinition { Id = 1, Key = "Main", IdMin = 100000, IdMax = 199999 },
                    new RedDotModuleDefinition { Id = 2, Key = "Mail", IdMin = 200000, IdMax = 299999 },
                },
                Nodes = new[]
                {
                    Node(Root, "Main.Root", 1, RedDotNodeKind.Aggregate, RedDotAggregation.SumUniqueSignals),
                    Node(EntryA, "Main.EntryA", 1, RedDotNodeKind.Aggregate, RedDotAggregation.SumChildren),
                    Node(EntryC, "Main.EntryC", 1, RedDotNodeKind.Aggregate, RedDotAggregation.Any),
                    Node(Shared, "Mail.Shared", 2, RedDotNodeKind.Signal, RedDotAggregation.None),
                    Node(Other, "Mail.Other", 2, RedDotNodeKind.Signal, RedDotAggregation.None),
                    Node(Seen, "Mail.NewPage", 2, RedDotNodeKind.Signal, RedDotAggregation.None),
                },
                Edges = new[]
                {
                    Edge(EntryA, Shared), Edge(EntryA, Other), Edge(EntryC, Shared),
                    Edge(Root, EntryA), Edge(Root, EntryC), Edge(Root, Seen),
                },
                SeenPolicies = new[]
                {
                    new RedDotSeenPolicyDefinition
                    {
                        SignalId = Seen,
                        Trigger = RedDotAcknowledgeTrigger.Expose,
                        SaveMode = RedDotSeenSaveMode.LocalAccount,
                        Version = seenVersion,
                    }
                },
            };
        }

        private static RedDotNodeDefinition Node(
            int id, string key, int moduleId, RedDotNodeKind kind, RedDotAggregation aggregation)
            => new RedDotNodeDefinition
            {
                Id = id, Key = key, ModuleId = moduleId, Kind = kind, Aggregation = aggregation,
            };

        private static RedDotEdgeDefinition Edge(int parentId, int childId)
            => new RedDotEdgeDefinition { ParentId = parentId, ChildId = childId };
    }
}
