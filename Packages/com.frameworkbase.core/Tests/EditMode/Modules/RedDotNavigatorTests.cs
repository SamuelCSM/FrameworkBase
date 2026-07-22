using System;
using System.Collections.Generic;
using Framework.Foundation;
using Framework.RedDot;
using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotNavigatorTests
    {
        private const int Root = 100001;
        private const int EntryA = 100002;
        private const int Shared = 200001;
        private const int Other = 200002;

        [Test]
        public void 沿亮起路径从入口到来源依次触发处理器()
        {
            RedDotService service = CreateService();
            service.SetCount(Shared, 3);
            service.SetCount(Other, 5); // EntryA=8 最大；Other=5 > Shared=3，路径 Root→EntryA→Other

            var order = new List<int>();
            var navigator = new RedDotNavigator(service);
            navigator.Register(EntryA, () => order.Add(EntryA));
            navigator.Register(Other, () => order.Add(Other));
            navigator.Register(Shared, () => order.Add(Shared)); // 不在路径上，不应触发

            int invoked = navigator.Navigate(Root);

            Assert.AreEqual(2, invoked);
            CollectionAssert.AreEqual(new[] { EntryA, Other }, order, "按入口→来源顺序逐级跳转");
        }

        [Test]
        public void 入口未点亮时不跳转()
        {
            RedDotService service = CreateService();
            var navigator = new RedDotNavigator(service);
            navigator.Register(EntryA, () => Assert.Fail("未点亮不应跳转"));

            Assert.AreEqual(0, navigator.Navigate(Root));
            CollectionAssert.IsEmpty(navigator.GetPath(Root));
        }

        [Test]
        public void 单个处理器异常被隔离不阻断其余跳转()
        {
            RedDotService service = CreateService();
            service.SetCount(Other, 5);

            var errors = new List<Exception>();
            var order = new List<int>();
            var navigator = new RedDotNavigator(service);
            navigator.Register(EntryA, () => throw new InvalidOperationException("boom"));
            navigator.Register(Other, () => order.Add(Other));

            int invoked = navigator.Navigate(Root, errors.Add);

            Assert.AreEqual(1, invoked, "抛异常的处理器不计入成功跳转");
            Assert.AreEqual(1, errors.Count);
            CollectionAssert.AreEqual(new[] { Other }, order, "前一个处理器失败不阻断后续跳转");
        }

        [Test]
        public void GetPath返回入口到来源的稳定ID路径()
        {
            RedDotService service = CreateService();
            service.SetCount(Other, 5);
            CollectionAssert.AreEqual(new[] { Root, EntryA, Other }, navigatorPath(service, Root));
        }

        [Test]
        public void 注册与注销处理器()
        {
            RedDotService service = CreateService();
            var navigator = new RedDotNavigator(service);
            navigator.Register(EntryA, () => { });
            Assert.IsTrue(navigator.HasHandler(EntryA));
            Assert.IsTrue(navigator.Unregister(EntryA));
            Assert.IsFalse(navigator.HasHandler(EntryA));
            Assert.IsFalse(navigator.Unregister(EntryA));
            Assert.Throws<ArgumentOutOfRangeException>(() => navigator.Register(0, () => { }));
        }

        private static IReadOnlyList<int> navigatorPath(RedDotService service, int entryId)
            => new RedDotNavigator(service).GetPath(entryId);

        private static RedDotService CreateService()
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
                        Kind = RedDotNodeKind.Aggregate, Aggregation = RedDotAggregation.SumUniqueSignals,
                    },
                    new RedDotNodeDefinition
                    {
                        Id = EntryA, Key = "Main.EntryA", ModuleId = 1,
                        Kind = RedDotNodeKind.Aggregate, Aggregation = RedDotAggregation.SumChildren,
                    },
                    new RedDotNodeDefinition
                    {
                        Id = Shared, Key = "Mail.Shared", ModuleId = 2,
                        Kind = RedDotNodeKind.Signal, Aggregation = RedDotAggregation.None,
                    },
                    new RedDotNodeDefinition
                    {
                        Id = Other, Key = "Mail.Other", ModuleId = 2,
                        Kind = RedDotNodeKind.Signal, Aggregation = RedDotAggregation.None,
                    },
                },
                Edges = new[]
                {
                    new RedDotEdgeDefinition { ParentId = EntryA, ChildId = Shared },
                    new RedDotEdgeDefinition { ParentId = EntryA, ChildId = Other },
                    new RedDotEdgeDefinition { ParentId = Root, ChildId = EntryA },
                },
            });
            return service;
        }
    }
}
