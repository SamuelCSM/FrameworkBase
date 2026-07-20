using System.Globalization;
using System.Text;
using Framework.Http;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 遥测上报签名器契约测试。golden 向量锁死跨端算法
    /// （服务端按同式重算，改算法 = 改契约 = 过冻结点评审）。
    /// </summary>
    public class TelemetryRequestSignerTests
    {
        [TearDown]
        public void TearDown()
        {
            TelemetryRequestSigner.SetCredentialsProvider(null);
        }

        [Test]
        public void ComputeSignature_MatchesGoldenVector()
        {
            // key=UTF8("tok_sign_me"), message=UTF8("1700000000000\n[{"event":"perf_window"}]")
            string signature = TelemetryRequestSigner.ComputeSignature(
                "tok_sign_me",
                1700000000000L,
                Encoding.UTF8.GetBytes("[{\"event\":\"perf_window\"}]"));

            Assert.AreEqual(
                "d24f30423e741805402019d17041c321c760bb3b4293f1237ecb4a709549ebfa",
                signature);
        }

        [Test]
        public void ComputeSignature_NullBody_EqualsEmptyBody()
        {
            Assert.AreEqual(
                TelemetryRequestSigner.ComputeSignature("s", 1L, null),
                TelemetryRequestSigner.ComputeSignature("s", 1L, new byte[0]));
        }

        [Test]
        public void TrySign_WithCredentials_SetsConsistentHeaders()
        {
            TelemetryRequestSigner.SetCredentialsProvider(() =>
                new TelemetrySigningCredentials("u1", "tok_sign_me"));

            byte[] body = Encoding.UTF8.GetBytes("[{\"event\":\"launch_run\"}]");
            HttpRequest request = HttpRequest.Post("http://127.0.0.1:9/events", body, "application/json");

            Assert.IsTrue(TelemetryRequestSigner.TrySign(request));
            Assert.AreEqual("u1", request.Headers[TelemetryRequestSigner.UserHeader]);

            long ts = long.Parse(
                request.Headers[TelemetryRequestSigner.TimestampHeader], CultureInfo.InvariantCulture);
            Assert.AreEqual(
                TelemetryRequestSigner.ComputeSignature("tok_sign_me", ts, body),
                request.Headers[TelemetryRequestSigner.SignatureHeader]);
        }

        [Test]
        public void TrySign_WithoutProvider_LeavesRequestUntouched()
        {
            HttpRequest request = HttpRequest.Post("http://127.0.0.1:9/events", new byte[] { 1 }, "application/json");

            Assert.IsFalse(TelemetryRequestSigner.TrySign(request));
            Assert.AreEqual(0, request.Headers.Count);
        }

        [Test]
        public void TrySign_EmptyCredentials_LeavesRequestUntouched()
        {
            // 未登录：UserId/Secret 为空 → 不签名照常发送（服务端未签名通道）。
            TelemetryRequestSigner.SetCredentialsProvider(() =>
                new TelemetrySigningCredentials(string.Empty, string.Empty));

            HttpRequest request = HttpRequest.Post("http://127.0.0.1:9/events", new byte[] { 1 }, "application/json");

            Assert.IsFalse(TelemetryRequestSigner.TrySign(request));
            Assert.AreEqual(0, request.Headers.Count);
        }
    }
}
