using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 相机调度访问点契约单测：验证"默认空兜底、可注入、null 回退兜底、可复位"，
    /// 保证业务经 Cameras.Director 调用永不 NullReference。
    /// </summary>
    public class CameraDirectorTests
    {
        /// <summary>最小桩：记录最后一次指令，供断言注入是否真的生效。</summary>
        private sealed class StubDirector : ICameraDirector
        {
            public string ActiveCameraId { get; set; }
            public bool IsRegistered(string cameraId) => cameraId == "main";
            public void Activate(string cameraId) => ActiveCameraId = cameraId;
            public void Shake(float amplitude, float duration) { }
        }

        [TearDown]
        public void TearDown() => Cameras.Reset(); // 静态状态跨用例隔离

        [Test]
        public void 默认为空兜底_不NullReference()
        {
            Cameras.Reset();
            Assert.IsInstanceOf<NullCameraDirector>(Cameras.Director);
            Assert.IsNull(Cameras.Director.ActiveCameraId);
            Assert.IsFalse(Cameras.Director.IsRegistered("main"));
            Assert.DoesNotThrow(() => Cameras.Director.Activate("main"));
            Assert.DoesNotThrow(() => Cameras.Director.Shake(1f, 0.2f));
        }

        [Test]
        public void 注入实现_生效()
        {
            var stub = new StubDirector();
            Cameras.Register(stub);
            Assert.AreSame(stub, Cameras.Director);

            Cameras.Director.Activate("hero");
            Assert.AreEqual("hero", stub.ActiveCameraId, "指令应真正落到注入的实现");
        }

        [Test]
        public void 注入null_回退空兜底()
        {
            Cameras.Register(new StubDirector());
            Cameras.Register(null);
            Assert.IsInstanceOf<NullCameraDirector>(Cameras.Director, "传 null 视为撤销，回到空兜底而非把 Director 置空");
        }

        [Test]
        public void 复位_回到空兜底()
        {
            Cameras.Register(new StubDirector());
            Cameras.Reset();
            Assert.IsInstanceOf<NullCameraDirector>(Cameras.Director);
        }
    }
}
