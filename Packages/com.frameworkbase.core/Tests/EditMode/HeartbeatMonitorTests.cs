using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 心跳时序状态机单测：发送间隔、超时检测、收数据清零、前台探活冻结、开关、间隔联动超时、
    /// 超时短路优先于发送、序号自增、(重)连重置。纯逻辑手动 Advance 驱动——NetworkManager
    /// 心跳时序拆分后的回归基线。
    /// </summary>
    public class HeartbeatMonitorTests
    {
        [Test]
        public void 发送间隔到点返回Send并重置计时()
        {
            var hb = new HeartbeatMonitor();      // 默认间隔 30
            hb.SetTimeoutEnabled(false);          // 隔离发送逻辑
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(10f, false));
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(10f, false)); // 20
            Assert.AreEqual(HeartbeatAction.Send, hb.Advance(10f, false)); // 30 → Send
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(10f, false)); // 计时已重置，重新累积
        }

        [Test]
        public void 未收数据累积超阈值返回TimedOut()
        {
            var hb = new HeartbeatMonitor();      // 默认超时 75
            hb.SetSendEnabled(false);             // 隔离超时逻辑
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(70f, false));
            Assert.AreEqual(HeartbeatAction.TimedOut, hb.Advance(10f, false)); // 80 > 75
        }

        [Test]
        public void 收到数据清零超时累积防误判()
        {
            var hb = new HeartbeatMonitor();
            hb.SetSendEnabled(false);
            hb.Advance(70f, false);
            hb.OnDataReceived();                  // 清零
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(10f, false)); // 仅 10，不超时
        }

        [Test]
        public void 前台探活期间冻结超时检测()
        {
            var hb = new HeartbeatMonitor();
            hb.SetSendEnabled(false);
            // 探活中即使远超阈值也不判超时（探活自有超时兜底）
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(100f, foregroundProbePending: true));
        }

        [Test]
        public void 前台探活期间仍需发送保活()
        {
            var hb = new HeartbeatMonitor();
            hb.SetTimeoutEnabled(false);
            Assert.AreEqual(HeartbeatAction.Send, hb.Advance(30f, foregroundProbePending: true));
        }

        [Test]
        public void 超时短路优先于发送且不推进发送计时()
        {
            var hb = new HeartbeatMonitor();
            Assert.AreEqual(HeartbeatAction.TimedOut, hb.Advance(76f, false)); // 超时立即返回
            hb.SetTimeoutEnabled(false);          // 之后隔离发送，验证发送计时在超时那帧未被推进
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(29f, false)); // 0→29
            Assert.AreEqual(HeartbeatAction.Send, hb.Advance(1f, false));  // 30 → Send
        }

        [Test]
        public void 设置间隔联动超时阈值为二点五倍()
        {
            var hb = new HeartbeatMonitor();
            hb.SetInterval(10f);                  // 超时 = 25
            hb.SetSendEnabled(false);
            Assert.AreEqual(25f, hb.TimeoutSeconds, 0.001f);
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(25f, false));    // 25 不 > 25
            Assert.AreEqual(HeartbeatAction.TimedOut, hb.Advance(1f, false)); // 26 > 25
        }

        [Test]
        public void 设置间隔非正值被忽略()
        {
            var hb = new HeartbeatMonitor();
            hb.SetInterval(10f);
            hb.SetInterval(-5f);                  // 忽略
            hb.SetInterval(0f);                   // 忽略
            Assert.AreEqual(25f, hb.TimeoutSeconds, 0.001f);
        }

        [Test]
        public void 关闭发送与超时后任何推进都无动作()
        {
            var hb = new HeartbeatMonitor();
            hb.SetSendEnabled(false);
            hb.SetTimeoutEnabled(false);
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(1000f, false));
        }

        [Test]
        public void 序号自增()
        {
            var hb = new HeartbeatMonitor();
            Assert.AreEqual(1, hb.NextSequenceId());
            Assert.AreEqual(2, hb.NextSequenceId());
            Assert.AreEqual(3, hb.NextSequenceId());
        }

        [Test]
        public void 重连时OnConnected重置发送计时()
        {
            var hb = new HeartbeatMonitor();
            hb.SetTimeoutEnabled(false);
            hb.Advance(20f, false);               // 计时 20
            hb.OnConnected();                     // 重置
            Assert.AreEqual(HeartbeatAction.None, hb.Advance(29f, false)); // 0→29
            Assert.AreEqual(HeartbeatAction.Send, hb.Advance(1f, false));  // 30
        }
    }
}
