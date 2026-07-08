using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 资源作用域记账单测：借出登记、Dispose 全额归还、幂等、提前归还销账、
    /// Dispose 后拒借、外部销毁实例跳过、异步打断自动归还。
    /// 用假宿主（IResourceScopeHost）离线验证，不碰 Addressables。
    /// </summary>
    public class ResourceScopeTests
    {
        private FakeHost _host;

        [SetUp]
        public void SetUp()
        {
            _host = new FakeHost();
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        [Test]
        public void 借出登记_计数与实例被跟踪()
        {
            var scope = new ResourceScope(_host, "test");

            Wait(scope.LoadAssetAsync<ScriptableObject>("a"));
            Wait(scope.LoadAssetAsync<ScriptableObject>("a")); // 同地址二次借出
            Wait(scope.LoadAssetAsync<ScriptableObject>("b"));
            GameObject go = Wait(scope.InstantiateAsync("inst"));

            Assert.AreEqual(3, scope.AssetRefCount, "同地址多次借出按次数计");
            Assert.AreEqual(1, scope.InstanceCount);
            Assert.IsNotNull(go);
            scope.Dispose();
        }

        [Test]
        public void Dispose_全额归还并幂等()
        {
            var scope = new ResourceScope(_host, "test");
            Wait(scope.LoadAssetAsync<ScriptableObject>("a"));
            Wait(scope.LoadAssetAsync<ScriptableObject>("a"));
            Wait(scope.InstantiateAsync("inst"));

            scope.Dispose();

            Assert.AreEqual(2, _host.ReleasedAssets.Count(x => x == "a"), "两次借出归还两次");
            Assert.AreEqual(1, _host.ReleasedInstances.Count);
            Assert.IsTrue(scope.IsDisposed);
            Assert.AreEqual(0, scope.AssetRefCount);

            scope.Dispose(); // 幂等：不得重复归还
            Assert.AreEqual(2, _host.ReleasedAssets.Count(x => x == "a"));
            Assert.AreEqual(1, _host.ReleasedInstances.Count);
        }

        [Test]
        public void 提前归还_销账后Dispose不重复归还()
        {
            var scope = new ResourceScope(_host, "test");
            Wait(scope.LoadAssetAsync<ScriptableObject>("a"));
            GameObject go = Wait(scope.InstantiateAsync("inst"));

            scope.ReleaseAsset("a");
            scope.ReleaseInstance(go);
            Assert.AreEqual(0, scope.AssetRefCount);
            Assert.AreEqual(0, scope.InstanceCount);

            scope.Dispose();

            Assert.AreEqual(1, _host.ReleasedAssets.Count(x => x == "a"), "提前还过的不再重复归还");
            Assert.AreEqual(1, _host.ReleasedInstances.Count);
        }

        [Test]
        public void 未持有的归还_忽略且不透传宿主()
        {
            var scope = new ResourceScope(_host, "test");

            scope.ReleaseAsset("never_loaded");
            scope.ReleaseInstance(new GameObject("outsider"));

            Assert.AreEqual(0, _host.ReleasedAssets.Count, "未经作用域借出的归还必须被忽略");
            Assert.AreEqual(0, _host.ReleasedInstances.Count);
            scope.Dispose();
        }

        [Test]
        public void Dispose后再借_拒绝并返回null()
        {
            var scope = new ResourceScope(_host, "test");
            scope.Dispose();

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[ResourceScope\] ""test"" 已 Dispose，拒绝加载 a"));
            Assert.IsNull(Wait(scope.LoadAssetAsync<ScriptableObject>("a")));
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[ResourceScope\] ""test"" 已 Dispose，拒绝实例化 inst"));
            Assert.IsNull(Wait(scope.InstantiateAsync("inst")));
            Assert.AreEqual(0, _host.LoadCount, "Dispose 后不应再穿透到宿主");
        }

        [Test]
        public void 加载失败_不记账()
        {
            _host.FailLoads = true;
            var scope = new ResourceScope(_host, "test");

            Assert.IsNull(Wait(scope.LoadAssetAsync<ScriptableObject>("a")));
            Assert.AreEqual(0, scope.AssetRefCount, "失败的加载宿主已回滚，作用域不得记账");

            scope.Dispose();
            Assert.AreEqual(0, _host.ReleasedAssets.Count);
        }

        [Test]
        public void 外部销毁的实例_Dispose跳过()
        {
            var scope = new ResourceScope(_host, "test");
            GameObject go = Wait(scope.InstantiateAsync("inst"));

            Object.DestroyImmediate(go); // 业务绕过作用域直接销毁

            scope.Dispose();
            Assert.AreEqual(0, _host.ReleasedInstances.Count, "已销毁实例的句柄随对象失效，跳过归还");
        }

        [Test]
        public void 异步打断_加载完成前Dispose_自动归还该笔()
        {
            _host.HoldLoads = true; // 挂起加载，模拟 await 中途
            var scope = new ResourceScope(_host, "test");

            UniTask<ScriptableObject> pending = scope.LoadAssetAsync<ScriptableObject>("a");
            scope.Dispose();          // 阶段被打断
            _host.ReleasePending();   // 加载此刻才完成

            Assert.IsNull(Wait(pending), "作用域已关，调用方拿到 null");
            Assert.AreEqual(1, _host.ReleasedAssets.Count(x => x == "a"), "迟到的这笔引用必须被自动归还");
        }

        // ── 假宿主 ───────────────────────────────────────────────────────────

        private sealed class FakeHost : IResourceScopeHost
        {
            public readonly List<string> ReleasedAssets = new List<string>();
            public readonly List<GameObject> ReleasedInstances = new List<GameObject>();
            public bool FailLoads;
            public bool HoldLoads;
            public int LoadCount;

            private UniTaskCompletionSource _gate;

            public async UniTask<T> LoadAssetAsync<T>(string address) where T : Object
            {
                LoadCount++;
                if (HoldLoads)
                {
                    _gate = new UniTaskCompletionSource();
                    await _gate.Task;
                }
                if (FailLoads)
                    return null;
                return ScriptableObject.CreateInstance<TestAsset>() as T;
            }

            public UniTask<GameObject> InstantiateAsync(string address, Transform parent = null)
            {
                return UniTask.FromResult(new GameObject(address));
            }

            public void ReleaseAsset(string address) => ReleasedAssets.Add(address);

            public void ReleaseInstance(GameObject instance) => ReleasedInstances.Add(instance);

            public void ReleasePending() => _gate?.TrySetResult();
        }

        private sealed class TestAsset : ScriptableObject { }
    }
}
