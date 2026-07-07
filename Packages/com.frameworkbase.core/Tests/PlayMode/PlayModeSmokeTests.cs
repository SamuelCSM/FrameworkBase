using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests.PlayMode
{
    /// <summary>
    /// PlayMode 冒烟测试：在真实玩家循环（Update/OnGUI/UniTask PlayerLoop 调度）下
    /// 验证框架基础设施可运转。EditMode 测的是逻辑正确性，这条跑道守的是
    /// "接到真实帧循环后没散架"——两者互补，都进 CI 门禁。
    /// 用例保持场景无关（不依赖 GameEntry 预制体/Addressables 配置），任何工程可跑。
    /// </summary>
    public class PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator UniTask_真实帧循环_Delay按时完成() => UniTask.ToCoroutine(async () =>
        {
            float start = Time.realtimeSinceStartup;

            await UniTask.Delay(200);
            await UniTask.Yield();

            Assert.GreaterOrEqual(Time.realtimeSinceStartup - start, 0.15f,
                "UniTask.Delay 应在真实玩家循环中按时长挂起");
        });

        [UnityTest]
        public IEnumerator TimerManager_真实帧驱动_定时器如期触发() => UniTask.ToCoroutine(async () =>
        {
            var timers = new TimerManager();
            timers.OnInit();
            var driver = new GameObject("TimerDriver").AddComponent<ComponentDriver>();
            driver.Target = timers;

            try
            {
                bool fired = false;
                int ticks = 0;
                timers.AddTimer(() => fired = true, 0.2f);
                timers.AddLoopTimer(() => ticks++, 0.1f, loopCount: 3);

                float deadline = Time.realtimeSinceStartup + 2f;
                while ((!fired || ticks < 3) && Time.realtimeSinceStartup < deadline)
                    await UniTask.Yield();

                Assert.IsTrue(fired, "单次定时器应在真实帧循环中触发");
                Assert.AreEqual(3, ticks, "循环定时器应触发满 3 次后停止");
            }
            finally
            {
                Object.Destroy(driver.gameObject);
                timers.OnShutdown();
            }
        });

        [UnityTest]
        public IEnumerator PerfHud_挂载采样渲染_不抛异常() => UniTask.ToCoroutine(async () =>
        {
            var go = new GameObject("PerfHudSmoke");
            try
            {
                go.AddComponent<PerfHud>();

                // 覆盖至少一个 0.5s 采样窗口 + 若干 OnGUI 帧；期间任何异常都会让用例失败
                float deadline = Time.realtimeSinceStartup + 0.8f;
                while (Time.realtimeSinceStartup < deadline)
                    await UniTask.Yield();

                Assert.IsNotNull(go.GetComponent<PerfHud>());
            }
            finally
            {
                Object.Destroy(go);
            }
        });

        /// <summary>把 FrameworkComponent 接上真实 Update 循环的驱动器（冒烟用最小 GameEntry 替身）。</summary>
        private sealed class ComponentDriver : MonoBehaviour
        {
            public Core.FrameworkComponent Target;

            private void Update()
            {
                Target?.OnUpdate(Time.deltaTime);
            }
        }
    }
}
