using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 协议日志正文脱敏单测：会话令牌、设备标识等机密不得进日志，普通业务字段照常展开。
    /// <para>
    /// 协议日志有两道约束——Release 构建整体剥离（编译期行为，EditMode 验不到）与开发包字段掩码。
    /// 本测试守的是后者：Development Build 与 Editor 日志同样会被回捞、转发，掩码是最后一道闸。
    /// </para>
    /// </summary>
    public class NetworkProtocolLoggerTests
    {
        /// <summary>模拟带凭证的登录协议，字段名覆盖默认标记表中的 token 与 deviceid。</summary>
        private sealed class LoginPayload
        {
            public string SessionToken;
            public string DeviceId;
            public string NickName;
            public int Level;
        }

        /// <summary>模拟把凭证藏在下一层的嵌套协议，验证脱敏随递归生效。</summary>
        private sealed class EnvelopePayload
        {
            public LoginPayload Inner;
            public string Region;
        }

        /// <summary>模拟业务专属敏感字段：默认标记表不认识，须由业务登记后才脱敏。</summary>
        private sealed class RealNamePayload
        {
            public string FbTestMarkerValue;
        }

        [Test]
        public void 凭证字段被掩码_普通字段原样输出()
        {
            string body = NetworkProtocolLogger.FormatBody(new LoginPayload
            {
                SessionToken = "eyJhbGciOiJIUzI1NiJ9.secret-token",
                DeviceId = "8f14e45fceea167a",
                NickName = "阿泰",
                Level = 42,
            });

            StringAssert.DoesNotContain("secret-token", body, "会话令牌不得出现在协议日志里");
            StringAssert.DoesNotContain("8f14e45fceea167a", body, "设备标识不得出现在协议日志里");
            StringAssert.Contains("SessionToken:***", body);
            StringAssert.Contains("DeviceId:***", body);
            StringAssert.Contains("NickName:阿泰", body, "普通业务字段仍需可读，否则日志失去排查价值");
            StringAssert.Contains("Level:42", body);
        }

        [Test]
        public void 敏感字段的未赋值与空值可区分()
        {
            string missing = NetworkProtocolLogger.FormatBody(new LoginPayload { SessionToken = null });
            string empty = NetworkProtocolLogger.FormatBody(new LoginPayload { SessionToken = string.Empty });

            // 三态可分是刻意保留的：排查"令牌根本没带上"不需要看见令牌本身。
            StringAssert.Contains("SessionToken:null", missing);
            StringAssert.Contains("SessionToken:(empty)", empty);
        }

        [Test]
        public void 嵌套对象里的凭证同样被掩码()
        {
            string body = NetworkProtocolLogger.FormatBody(new EnvelopePayload
            {
                Inner = new LoginPayload { SessionToken = "nested-token", NickName = "阿泰" },
                Region = "cn",
            });

            StringAssert.DoesNotContain("nested-token", body);
            StringAssert.Contains("SessionToken:***", body);
            StringAssert.Contains("Region:cn", body);
        }

        [Test]
        public void 业务登记标记后立即生效_不被成员缓存挡住()
        {
            var payload = new RealNamePayload { FbTestMarkerValue = "310101199001011234" };

            // 先格式化一次，让该类型以"无敏感成员"的判定进入缓存——这正是登记后必须清缓存的场景。
            string before = NetworkProtocolLogger.FormatBody(payload);
            StringAssert.Contains("310101199001011234", before);

            NetworkProtocolLogger.AddSensitiveMarker("FbTestMarker");

            string after = NetworkProtocolLogger.FormatBody(payload);
            StringAssert.DoesNotContain("310101199001011234", after);
            StringAssert.Contains("FbTestMarkerValue:***", after);
        }

        [Test]
        public void 空白标记被忽略_不会掩掉所有字段()
        {
            NetworkProtocolLogger.AddSensitiveMarker(null);
            NetworkProtocolLogger.AddSensitiveMarker("   ");

            string body = NetworkProtocolLogger.FormatBody(new LoginPayload { NickName = "阿泰", Level = 7 });

            // 空标记若被收下，子串匹配会命中任意成员名，日志将整体退化成掩码。
            StringAssert.Contains("NickName:阿泰", body);
            StringAssert.Contains("Level:7", body);
        }
    }
}
