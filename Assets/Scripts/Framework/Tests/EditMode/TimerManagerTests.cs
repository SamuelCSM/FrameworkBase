using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// TimerManager 单 Update 驱动 tick 模型单元测试。
    /// 直接以确定性的缩放时间步长驱动 OnUpdate(dt)，覆盖一次性/循环、暂停/恢复、
    /// 取消、单帧补偿触发、回调内新增、剩余时间/进度查询等行为。
    /// 注：真实时间（useRealTime）分支依赖 Time.unscaledDeltaTime，由引擎驱动，
    /// 其逻辑与缩放时间共用同一推进路径，此处以缩放时间路径做确定性回归。
    /// </summary>
    public class TimerManagerTests
    {
        private TimerManager _timer;

        [SetUp]
        public void SetUp()
        {
            _timer = new TimerManager();
            _timer.OnInit();
        }

        /// <summary>一次性定时器到点触发一次，随后被移除。</summary>
        [Test]
        public void OneShot_FiresOnce_ThenRemoved()
        {
            int fired = 0;
            int id = _timer.AddTimer(() => fired++, 1.0f);

            _timer.OnUpdate(0.5f);
            Assert.AreEqual(0, fired);
            Assert.IsTrue(_timer.HasTimer(id));

            _timer.OnUpdate(0.6f);
            Assert.AreEqual(1, fired);
            Assert.IsFalse(_timer.HasTimer(id));
            Assert.AreEqual(0, _timer.GetTimerCount());
        }

        /// <summary>有限循环触发恰好 N 次后结束。</summary>
        [Test]
        public void FiniteLoop_FiresExactlyNTimes()
        {
            int fired = 0;
            _timer.AddLoopTimer(() => fired++, 1.0f, loopCount: 3);

            for (int i = 0; i < 5; i++)
            {
                _timer.OnUpdate(1.0f);
            }

            Assert.AreEqual(3, fired);
            Assert.AreEqual(0, _timer.GetTimerCount());
        }

        /// <summary>无限循环持续触发。</summary>
        [Test]
        public void InfiniteLoop_KeepsFiring()
        {
            int fired = 0;
            _timer.AddLoopTimer(() => fired++, 1.0f, loopCount: -1);

            for (int i = 0; i < 4; i++)
            {
                _timer.OnUpdate(1.0f);
            }

            Assert.AreEqual(4, fired);
        }

        /// <summary>暂停期间不推进，恢复后从剩余时间继续。</summary>
        [Test]
        public void Pause_Freezes_ResumeContinuesFromRemaining()
        {
            int fired = 0;
            int id = _timer.AddTimer(() => fired++, 1.0f);

            _timer.OnUpdate(0.5f);
            _timer.PauseTimer(id);
            for (int i = 0; i < 5; i++)
            {
                _timer.OnUpdate(1.0f); // 暂停期大量步进不应推进
            }
            Assert.AreEqual(0, fired);
            Assert.IsTrue(_timer.IsTimerPaused(id));

            _timer.ResumeTimer(id);
            _timer.OnUpdate(0.4f);
            Assert.AreEqual(0, fired, "剩余 0.5，推进 0.4 还不应触发");
            _timer.OnUpdate(0.2f);
            Assert.AreEqual(1, fired);
        }

        /// <summary>取消后不再触发。</summary>
        [Test]
        public void Cancel_StopsTimer()
        {
            int fired = 0;
            int id = _timer.AddTimer(() => fired++, 1.0f);

            _timer.CancelTimer(id);
            _timer.OnUpdate(2.0f);

            Assert.AreEqual(0, fired);
            Assert.IsFalse(_timer.HasTimer(id));
        }

        /// <summary>单帧跨多个周期时循环定时器应补偿触发多次。</summary>
        [Test]
        public void CatchUp_FiresMultipleTimesInOneFrame()
        {
            int fired = 0;
            _timer.AddLoopTimer(() => fired++, 1.0f, loopCount: -1);

            _timer.OnUpdate(3.5f);

            Assert.AreEqual(3, fired);
        }

        /// <summary>回调内新增定时器应推迟到帧末并入，本帧不触发。</summary>
        [Test]
        public void CallbackAddingTimer_DeferredToNextFrame()
        {
            int inner = 0;
            _timer.AddTimer(() => _timer.AddTimer(() => inner++, 1.0f), 1.0f);

            _timer.OnUpdate(1.0f);
            Assert.AreEqual(0, inner);
            Assert.AreEqual(1, _timer.GetTimerCount());

            _timer.OnUpdate(1.0f);
            Assert.AreEqual(1, inner);
        }

        /// <summary>循环定时器在回调内取消自身后不再触发。</summary>
        [Test]
        public void CallbackCancellingSelf_StopsAfterFirstFire()
        {
            int fired = 0;
            int id = 0;
            id = _timer.AddLoopTimer(() => { fired++; _timer.CancelTimer(id); }, 1.0f, loopCount: -1);

            for (int i = 0; i < 5; i++)
            {
                _timer.OnUpdate(1.0f);
            }

            Assert.AreEqual(1, fired);
            Assert.AreEqual(0, _timer.GetTimerCount());
        }

        /// <summary>剩余时间与进度查询正确。</summary>
        [Test]
        public void Query_RemainingTimeAndProgress()
        {
            int id = _timer.AddTimer(() => { }, 2.0f);

            _timer.OnUpdate(0.5f);

            Assert.AreEqual(1.5f, _timer.GetRemainingTime(id), 1e-4f);
            Assert.AreEqual(0.25f, _timer.GetProgress(id), 1e-4f);
        }

        /// <summary>CancelAllTimers 清空全部，且之后仍可正常新增。</summary>
        [Test]
        public void CancelAll_ClearsAndStillUsable()
        {
            _timer.AddTimer(() => { }, 1.0f);
            _timer.AddLoopTimer(() => { }, 1.0f, -1);

            _timer.CancelAllTimers();
            Assert.AreEqual(0, _timer.GetTimerCount());

            int fired = 0;
            _timer.AddTimer(() => fired++, 1.0f);
            _timer.OnUpdate(1.0f);
            Assert.AreEqual(1, fired);
        }
    }
}
