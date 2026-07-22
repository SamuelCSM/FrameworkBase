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

        private static GameObject CreateButton(string name, out RectTransform rect, out Button button)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            rect = go.GetComponent<RectTransform>();
            button = go.GetComponent<Button>();
            return go;
        }
    }
}
