using System.Collections.Generic;
using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// EventManager 事件系统单元测试。
    /// 覆盖订阅/发布、参数化回调、取消订阅、优先级顺序、签名不匹配跳过、
    /// 递归触发防护与清理。
    /// </summary>
    public class EventManagerTests
    {
        private enum BusinessMessage
        {
            BagChanged = 20000,
        }

        private EventManager _event;

        [SetUp]
        public void SetUp()
        {
            _event = new EventManager();
            _event.OnInit();
        }

        [TearDown]
        public void TearDown()
        {
            _event.Clear();
        }

        /// <summary>无参订阅应在发布时被调用。</summary>
        [Test]
        public void Subscribe_NoArg_Invoked()
        {
            int calls = 0;
            _event.Subscribe(GameMessage.PlayerLogout, () => calls++);

            _event.Publish(GameMessage.PlayerLogout);

            Assert.AreEqual(1, calls);
        }

        /// <summary>带参订阅应收到正确的参数值。</summary>
        [Test]
        public void Subscribe_OneArg_ReceivesArgument()
        {
            string received = null;
            _event.Subscribe<string>(GameMessage.LanguageChanged, lang => received = lang);

            _event.Publish(GameMessage.LanguageChanged, "zh-CN");

            Assert.AreEqual("zh-CN", received);
        }

        /// <summary>业务热更消息可使用自建枚举，通过 int ID 订阅和发布。</summary>
        [Test]
        public void Subscribe_BusinessIntMessage_Invoked()
        {
            int received = 0;
            int messageId = (int)BusinessMessage.BagChanged;
            _event.Subscribe<int>(messageId, value => received = value);

            _event.Publish(messageId, 7);

            Assert.AreEqual(7, received);
        }

        /// <summary>取消订阅后再发布不应再触发回调。</summary>
        [Test]
        public void Unsubscribe_StopsFurtherCallbacks()
        {
            int calls = 0;
            var sub = _event.Subscribe(GameMessage.PlayerLogout, () => calls++);

            _event.Publish(GameMessage.PlayerLogout);
            sub.Dispose();
            _event.Publish(GameMessage.PlayerLogout);

            Assert.AreEqual(1, calls, "取消订阅后不应再次触发");
        }

        /// <summary>高优先级回调应先于低优先级触发。</summary>
        [Test]
        public void Publish_RespectsPriorityOrder()
        {
            var order = new List<string>();
            _event.Subscribe(GameMessage.PlayerLoginSuccess, () => order.Add("low"), priority: 0);
            _event.Subscribe(GameMessage.PlayerLoginSuccess, () => order.Add("high"), priority: 100);

            _event.Publish(GameMessage.PlayerLoginSuccess);

            Assert.AreEqual(new[] { "high", "low" }, order.ToArray());
        }

        /// <summary>参数签名不匹配的回调应被跳过，不抛异常。</summary>
        [Test]
        public void Publish_SignatureMismatch_SkipsCallback()
        {
            bool invoked = false;
            // 订阅 Action<int>，却以无参方式发布，签名不符应被跳过
            _event.Subscribe<int>(GameMessage.PlayerLogout, _ => invoked = true);

            _event.Publish(GameMessage.PlayerLogout);

            Assert.IsFalse(invoked, "签名不匹配的回调不应被调用");
        }

        /// <summary>回调内重复发布同一消息应被递归防护拦截，不造成栈溢出且只触发一次。</summary>
        [Test]
        public void Publish_RecursiveSameMessage_IsGuarded()
        {
            int calls = 0;
            _event.Subscribe(GameMessage.PlayerLogout, () =>
            {
                calls++;
                _event.Publish(GameMessage.PlayerLogout); // 递归发布应被拦截
            });

            _event.Publish(GameMessage.PlayerLogout);

            Assert.AreEqual(1, calls, "递归触发应被防护，仅执行一次");
        }

        /// <summary>一个回调抛异常不应影响其它回调的执行。</summary>
        [Test]
        public void Publish_OneThrowingCallback_DoesNotBlockOthers()
        {
            int second = 0;
            _event.Subscribe(GameMessage.PlayerLogout, () => throw new System.InvalidOperationException("boom"));
            _event.Subscribe(GameMessage.PlayerLogout, () => second++);

            // 异常被 EventManager 内部捕获并记录（Warning/Error 不影响后续回调）
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            _event.Publish(GameMessage.PlayerLogout);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(1, second, "前一个回调异常不应阻断后续回调");
        }

        /// <summary>Clear 后所有订阅失效。</summary>
        [Test]
        public void Clear_RemovesAllSubscriptions()
        {
            int calls = 0;
            _event.Subscribe(GameMessage.PlayerLogout, () => calls++);

            _event.Clear();
            _event.Publish(GameMessage.PlayerLogout);

            Assert.AreEqual(0, calls);
        }
    }
}
