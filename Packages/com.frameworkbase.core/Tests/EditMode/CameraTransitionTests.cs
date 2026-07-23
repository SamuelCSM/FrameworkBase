using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 相机过渡纯核单测：缓动曲线与归一化时钟无 Unity 依赖、dt 注入，
    /// 脱离 PlayerLoop 即可验证时序与插值边界。驱动的 MonoBehaviour 侧靠集成/真机验证。
    /// </summary>
    public class CameraTransitionTests
    {
        private const float Eps = 1e-4f;

        // ── 缓动曲线 ─────────────────────────────────────────────────────────

        [Test]
        public void 缓动_越界一律钳到端点()
        {
            Assert.AreEqual(0f, CameraEase.Evaluate(CameraEasing.SmoothStep, -1f), Eps, "t<0 钳到 0，不外推过冲");
            Assert.AreEqual(1f, CameraEase.Evaluate(CameraEasing.SmoothStep, 2f), Eps, "t>1 钳到 1");
            Assert.AreEqual(0f, CameraEase.Evaluate(CameraEasing.EaseIn, 0f), Eps);
            Assert.AreEqual(1f, CameraEase.Evaluate(CameraEasing.EaseOut, 1f), Eps);
        }

        [Test]
        public void 缓动_Linear为恒等()
        {
            Assert.AreEqual(0.25f, CameraEase.Evaluate(CameraEasing.Linear, 0.25f), Eps);
            Assert.AreEqual(0.5f, CameraEase.Evaluate(CameraEasing.Linear, 0.5f), Eps);
        }

        [Test]
        public void 缓动_SmoothStep中点对称两端平滑()
        {
            Assert.AreEqual(0.5f, CameraEase.Evaluate(CameraEasing.SmoothStep, 0.5f), Eps, "3t²−2t³ 在 0.5 处为 0.5");
            // 单调递增
            float a = CameraEase.Evaluate(CameraEasing.SmoothStep, 0.3f);
            float b = CameraEase.Evaluate(CameraEasing.SmoothStep, 0.6f);
            Assert.Less(a, b, "缓动进度必须随时间单调不减");
        }

        [Test]
        public void 缓动_EaseIn起步慢_EaseOut收尾慢()
        {
            Assert.AreEqual(0.25f, CameraEase.Evaluate(CameraEasing.EaseIn, 0.5f), Eps, "t² 在中点 0.25，起步慢");
            Assert.AreEqual(0.75f, CameraEase.Evaluate(CameraEasing.EaseOut, 0.5f), Eps, "t(2−t) 在中点 0.75，收尾慢");
        }

        // ── 归一化时钟 ───────────────────────────────────────────────────────

        [Test]
        public void 时钟_瞬时时长_构造即完成()
        {
            var zero = new CameraTransitionClock(0f, CameraEasing.SmoothStep);
            Assert.IsTrue(zero.IsComplete, "duration=0 应构造即完成");
            Assert.AreEqual(1f, zero.Progress, Eps, "瞬时时钟进度恒 1，供调用方直接落终点");

            var negative = new CameraTransitionClock(-5f, CameraEasing.Linear);
            Assert.IsTrue(negative.IsComplete, "负时长同样视为瞬时");
            Assert.AreEqual(1f, negative.Progress, Eps);
        }

        [Test]
        public void 时钟_推进累积到完成()
        {
            var clock = new CameraTransitionClock(1f, CameraEasing.Linear);

            Assert.IsFalse(clock.Advance(0.5f), "半程未完成");
            Assert.AreEqual(0.5f, clock.Progress, Eps);

            Assert.IsTrue(clock.Advance(0.5f), "累计到 1 秒完成");
            Assert.AreEqual(1f, clock.Progress, Eps);
        }

        [Test]
        public void 时钟_超推进钳制不越界()
        {
            var clock = new CameraTransitionClock(1f, CameraEasing.Linear);

            Assert.IsTrue(clock.Advance(3f), "一次推进超总时长即完成");
            Assert.AreEqual(1f, clock.Progress, Eps, "进度钳在 1，不因超推进而 >1（否则 LerpUnclamped 会过冲）");
        }

        [Test]
        public void 时钟_负dt被忽略()
        {
            var clock = new CameraTransitionClock(1f, CameraEasing.Linear);
            Assert.IsFalse(clock.Advance(-1f), "负 dt 不推进");
            Assert.AreEqual(0f, clock.Progress, Eps);
        }

        [Test]
        public void 时钟_进度经缓动重整()
        {
            var smooth = new CameraTransitionClock(1f, CameraEasing.SmoothStep);
            smooth.Advance(0.5f);
            Assert.AreEqual(0.5f, smooth.Progress, Eps, "SmoothStep 半程仍是 0.5");

            var easeIn = new CameraTransitionClock(1f, CameraEasing.EaseIn);
            easeIn.Advance(0.5f);
            Assert.AreEqual(0.25f, easeIn.Progress, Eps, "EaseIn 半程时间对应 0.25 进度（起步慢）");
        }
    }
}
