using Framework.RemoteConfig;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 远程配置客户端属性的下发方式单测。
    /// <para>
    /// 查询参数会随整条 URL 落进 CDN、反向代理、WAF 与服务端访问日志，保留期不由客户端控制；
    /// 设备与用户标识出现在那里属 CWE-598。POST body 模式让这些标识不进 URL。
    /// </para>
    /// </summary>
    public class RemoteConfigIdentityTransportTests
    {
        private static RemoteConfigRequest SampleRequest() => new RemoteConfigRequest
        {
            DeviceId = "dev-123",
            UserId = "u-456",
            AppVersion = "1.2.3",
            Channel = "taptap",
            Env = "prod",
        };

        [Test]
        public void 查询参数模式_标识出现在URL中()
        {
            var backend = new HttpRemoteConfigBackend(
                "https://config.game.test/v1",
                identityTransport: RemoteConfigIdentityTransport.QueryString);

            string url = backend.BuildUrl(SampleRequest());

            // 默认保持这一行为：静态 CDN 端点与只读 query 的服务端都依赖它。
            StringAssert.Contains("device_id=dev-123", url);
            StringAssert.Contains("user_id=u-456", url);
        }

        [Test]
        public void POST模式_标识只在body中不进URL()
        {
            string body = HttpRemoteConfigBackend.BuildBody(SampleRequest());

            StringAssert.Contains("\"device_id\":\"dev-123\"", body);
            StringAssert.Contains("\"user_id\":\"u-456\"", body);
            StringAssert.Contains("\"channel\":\"taptap\"", body);
        }

        [Test]
        public void 两种模式_空属性都省略()
        {
            var backend = new HttpRemoteConfigBackend(
                "https://config.game.test/v1",
                identityTransport: RemoteConfigIdentityTransport.QueryString);
            var request = new RemoteConfigRequest { AppVersion = "1.0.0" };

            string url = backend.BuildUrl(request);
            string body = HttpRemoteConfigBackend.BuildBody(request);

            StringAssert.DoesNotContain("device_id", url);
            StringAssert.DoesNotContain("user_id", url);
            StringAssert.DoesNotContain("device_id", body);
            StringAssert.Contains("app_version", body);
        }
    }
}
