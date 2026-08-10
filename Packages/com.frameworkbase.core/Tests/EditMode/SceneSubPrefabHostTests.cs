using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 场景子预制宿主的异步生命周期测试：并发加载共享同一次请求、加载在途被释放时迟到实例不落到字段里。
    /// <para>
    /// 这两条都只在"取实例的 await 尚未完成"的窗口内成立，因此用一个手动放行的假 provider
    /// 精确控制完成时机，而不是靠时序碰运气。
    /// </para>
    /// </summary>
    public class SceneSubPrefabHostTests
    {
        /// <summary>测试用 View，只需存在于实例上供宿主 GetComponent。</summary>
        private sealed class FakeSubView : SceneSubView
        {
        }

        /// <summary>测试用子预制控制类，绑定 <see cref="FakeSubView"/>。</summary>
        private sealed class FakeSubPrefab : SceneSubPrefab<FakeSubView>
        {
        }

        /// <summary>
        /// 手动放行的实例提供者：GetAsync 挂起直到 <see cref="CompleteAll"/> 被调用，
        /// 从而把"加载在途"这个窗口变成可控状态而非时序赌博。
        /// </summary>
        private sealed class GateProvider : IGameObjectProvider
        {
            private readonly List<UniTaskCompletionSource<GameObject>> _pending =
                new List<UniTaskCompletionSource<GameObject>>();

            /// <summary>GetAsync 被调用的次数，用于断言并发是否共享了同一次请求。</summary>
            public int GetCalls { get; private set; }

            /// <summary>被归还的实例，用于断言迟到实例没有泄漏。</summary>
            public List<GameObject> Released { get; } = new List<GameObject>();

            public UniTask<GameObject> GetAsync(string key, Transform parent = null)
            {
                GetCalls++;
                var source = new UniTaskCompletionSource<GameObject>();
                _pending.Add(source);
                return source.Task;
            }

            public void Release(GameObject instance) => Released.Add(instance);

            /// <summary>放行全部在途请求，每个返回一个带 View 组件的新实例。</summary>
            public void CompleteAll()
            {
                foreach (UniTaskCompletionSource<GameObject> source in _pending)
                {
                    var go = new GameObject("fake-sub-prefab");
                    go.AddComponent<FakeSubView>();
                    source.TrySetResult(go);
                }
                _pending.Clear();
            }
        }

        private GameObject _parentObject;
        private GateProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _parentObject = new GameObject("sub-prefab-parent");
            _provider = new GateProvider();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject released in _provider.Released)
            {
                if (released != null) Object.DestroyImmediate(released);
            }
            if (_parentObject != null) Object.DestroyImmediate(_parentObject);
        }

        [UnityTest]
        public IEnumerator 并发加载_共享同一次实例请求() => UniTask.ToCoroutine(async () =>
        {
            var host = new SceneSubPrefabHost<FakeSubPrefab>("k", _parentObject.transform, _provider);

            UniTask<FakeSubPrefab> first = host.LoadAsync();
            UniTask<FakeSubPrefab> second = host.LoadAsync();

            // 各自往下走会各实例化一个对象，后者覆盖宿主字段，前者从此无人引用也无人释放。
            Assert.AreEqual(1, _provider.GetCalls, "并发加载必须共享同一次实例请求");

            _provider.CompleteAll();
            FakeSubPrefab a = await first;
            FakeSubPrefab b = await second;

            Assert.IsNotNull(a);
            Assert.AreSame(a, b, "两个调用方应拿到同一个子预制控制类");

            host.Dispose();
        });

        [UnityTest]
        public IEnumerator 加载在途被释放_迟到实例归还且不落入字段() => UniTask.ToCoroutine(async () =>
        {
            var host = new SceneSubPrefabHost<FakeSubPrefab>("k", _parentObject.transform, _provider);

            UniTask<FakeSubPrefab> loading = host.LoadAsync();
            host.Dispose(); // 加载还没回来，宿主已被释放

            _provider.CompleteAll();
            FakeSubPrefab result = await loading;

            Assert.IsNull(result, "宿主已释放，迟到的实例不应再被装配成子预制");
            Assert.IsFalse(host.IsLoaded);
            Assert.AreEqual(1, _provider.Released.Count, "迟到实例必须归还，否则永远无人释放");
        });
    }
}
