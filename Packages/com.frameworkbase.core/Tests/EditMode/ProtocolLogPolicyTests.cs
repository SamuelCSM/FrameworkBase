using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 协议日志准入策略单测：总开关、心跳独立开关、按协议号屏蔽表及三者的判定优先级。
    /// 纯逻辑，不碰真实网络——这是 NetworkManager 协议日志行为拆分后的回归基线。
    /// </summary>
    public class ProtocolLogPolicyTests
    {
        // 约定：把 (mainId=10, subId=1) 当作心跳协议，其余为普通协议。
        private const byte HbMain = 10;
        private const byte HbSub = 1;

        private static ProtocolLogPolicy NewPolicy()
            => new ProtocolLogPolicy((main, sub) => main == HbMain && sub == HbSub);

        [Test]
        public void 默认_普通协议打印_心跳不打印()
        {
            var policy = NewPolicy();
            Assert.IsTrue(policy.ShouldLog(2, 3), "默认总开关开启，普通协议应打印");
            Assert.IsFalse(policy.ShouldLog(HbMain, HbSub), "心跳日志默认关闭，不应打印");
        }

        [Test]
        public void 总开关关闭_任何协议都不打印()
        {
            var policy = NewPolicy();
            policy.SetHeartbeatEnabled(true); // 即便心跳开关打开
            policy.SetEnabled(false);         // 总开关关闭优先级最高
            Assert.IsFalse(policy.ShouldLog(2, 3));
            Assert.IsFalse(policy.ShouldLog(HbMain, HbSub));
        }

        [Test]
        public void 心跳开关打开_心跳可打印()
        {
            var policy = NewPolicy();
            policy.SetHeartbeatEnabled(true);
            Assert.IsTrue(policy.ShouldLog(HbMain, HbSub));
        }

        [Test]
        public void 屏蔽表_命中协议不打印_解除后恢复()
        {
            var policy = NewPolicy();
            policy.Ignore(2, 3);
            Assert.IsFalse(policy.ShouldLog(2, 3), "已屏蔽协议不应打印");
            Assert.IsTrue(policy.ShouldLog(2, 4), "未屏蔽的相邻协议不受影响");

            policy.Unignore(2, 3);
            Assert.IsTrue(policy.ShouldLog(2, 3), "解除屏蔽后应恢复打印");
        }

        [Test]
        public void 清空屏蔽表_全部恢复_不影响心跳开关()
        {
            var policy = NewPolicy();
            policy.Ignore(2, 3);
            policy.Ignore(4, 5);
            policy.ClearIgnored();
            Assert.IsTrue(policy.ShouldLog(2, 3));
            Assert.IsTrue(policy.ShouldLog(4, 5));
            // 清屏蔽表不应打开心跳日志
            Assert.IsFalse(policy.ShouldLog(HbMain, HbSub));
        }

        [Test]
        public void 空心跳谓词_视所有协议为非心跳()
        {
            var policy = new ProtocolLogPolicy(null);
            Assert.IsTrue(policy.ShouldLog(HbMain, HbSub), "谓词为空时不把任何协议判为心跳，按普通协议打印");
        }
    }
}
