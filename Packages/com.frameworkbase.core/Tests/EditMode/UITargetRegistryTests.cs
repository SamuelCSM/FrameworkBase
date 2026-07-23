using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Framework.Tests
{
    public class UITargetRegistryTests
    {
        private sealed class TestView : UIView { }
        private sealed class TestWindow : UIBase<TestView> { }

        [Test]
        public void UIWindowId注册与旧Type查询共享同一份元数据且不实例化Prefab()
        {
            var ui = new UIManager();
            ui.OnInit();
            int factoryCalls = 0;

            ui.RegisterCodeUI<TestWindow>(
                900001,
                _ =>
                {
                    factoryCalls++;
                    return null;
                },
                UILayer.Popup);

            Assert.IsTrue(ui.IsUIRegistered(900001));
            Assert.IsTrue(ui.IsUIRegistered<TestWindow>());
            Assert.IsFalse(ui.IsUIOpened(900001));
            Assert.AreEqual(0, ui.GetUICount(900001));
            Assert.AreEqual(0, factoryCalls, "目录注册不得预加载/实例化 Prefab 或代码 View");
            ui.OnShutdown();
        }

        [Test]
        public void 同一TargetId支持多窗口Scope且无Scope解析时拒绝歧义()
        {
            var registry = new UITargetRegistry();
            var scopeA = new object();
            var scopeB = new object();
            GameObject first = CreateButton("first", out RectTransform firstRect, out Button firstButton);
            GameObject second = CreateButton("second", out RectTransform secondRect, out Button secondButton);
            try
            {
                using (registry.Register(1001, firstRect, firstButton, scopeA))
                using (registry.Register(1001, secondRect, secondButton, scopeB))
                {
                    Assert.IsFalse(registry.TryResolve(1001, out _));
                    Assert.AreSame(firstRect, registry.Resolve(1001, scopeA).RectTransform);
                    Assert.AreSame(secondRect, registry.Resolve(1001, scopeB).RectTransform);
                    Assert.Throws<InvalidOperationException>(() => registry.Resolve(1001));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void Button点击按Scope分发且注册释放后UnityEvent也解绑()
        {
            var registry = new UITargetRegistry();
            var scope = new object();
            GameObject go = CreateButton("target", out RectTransform rect, out Button button);
            try
            {
                int count = 0;
                IDisposable registration = registry.Register(2001, rect, button, scope);
                using (registry.SubscribeClick(2001, scope, _ => count++))
                {
                    button.onClick.Invoke();
                    registration.Dispose();
                    button.onClick.Invoke();
                }

                Assert.AreEqual(1, count);
                Assert.IsFalse(registry.TryResolve(2001, scope, out _));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void 通知期间注销其它Target的订阅_最外层结束即压实不滞留()
        {
            var registry = new UITargetRegistry();
            GameObject a = CreateButton("a", out RectTransform aRect, out Button aButton);
            GameObject b = CreateButton("b", out RectTransform bRect, out Button bButton);
            try
            {
                using (registry.Register(1001, aRect, aButton))
                using (registry.Register(2001, bRect, bButton))
                {
                    // B 的订阅在 A 的点击处理中被注销——通知进行中，注销须延后压实。
                    IDisposable subB = registry.SubscribeClick(2001, _ => { });
                    using (registry.SubscribeClick(1001, _ => subB.Dispose()))
                    {
                        aButton.onClick.Invoke();
                    }

                    // 修复前：B 的已释放订阅滞留到 2001 下次被点击才清；修复后：最外层通知结束即压实。
                    Assert.AreEqual(0, ActiveSubscriptionCount(registry, 2001),
                        "跨 Target 注销应在最外层通知结束时压实，不得滞留");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        /// <summary>反射读私有订阅表，验证 leak 修复（该状态无对外可观测的分发差异，只能查内部）。</summary>
        private static int ActiveSubscriptionCount(UITargetRegistry registry, int targetId)
        {
            var field = typeof(UITargetRegistry).GetField(
                "_clickSubscriptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var map = (System.Collections.IDictionary)field.GetValue(registry);
            if (!map.Contains(targetId)) return 0;
            return ((System.Collections.ICollection)map[targetId]).Count;
        }

        private static GameObject CreateButton(string name, out RectTransform rect, out Button button)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            rect = go.GetComponent<RectTransform>();
            button = go.GetComponent<Button>();
            return go;
        }
    }
}
