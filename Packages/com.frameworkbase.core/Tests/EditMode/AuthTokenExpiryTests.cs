using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core.Auth;
using Framework.Http;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 会话令牌过期时刻（expiresAt）契约测试：响应解析、缺省兼容、持久化透传与内存态生命周期。
    /// 过期后的跳过行为依赖 AuthManager 组件生命周期，由登录切片验收器在 Editor 联调覆盖。
    /// </summary>
    public class AuthTokenExpiryTests
    {
        [Test]
        public void HttpAuthBackend_ParsesExpiresAt()
        {
            var client = new CannedHttpClient(
                "{\"success\":true,\"userId\":\"u1\",\"sessionToken\":\"tok_1\",\"expiresAt\":1234567890123}");
            var backend = new HttpAuthBackend("http://127.0.0.1:9/login", client);

            LoginResult result = backend.LoginAsync(
                new LoginRequestContext { Mode = LoginMode.Guest, TimeoutMs = 1000 },
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.Success);
            Assert.AreEqual("tok_1", result.SessionToken);
            Assert.AreEqual(1234567890123L, result.SessionTokenExpiresAtMs);
        }

        [Test]
        public void HttpAuthBackend_MissingExpiresAt_DefaultsToZero()
        {
            // 老服务端兼容：响应无 expiresAt 字段 → 0 = 未提供，客户端不做过期预判。
            var client = new CannedHttpClient(
                "{\"success\":true,\"userId\":\"u1\",\"sessionToken\":\"tok_1\"}");
            var backend = new HttpAuthBackend("http://127.0.0.1:9/login", client);

            LoginResult result = backend.LoginAsync(
                new LoginRequestContext { Mode = LoginMode.Guest, TimeoutMs = 1000 },
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0L, result.SessionTokenExpiresAtMs);
        }

        [Test]
        public void LoginResult_Ok_NegativeExpiryNormalizedToZero()
        {
            Assert.AreEqual(0L, LoginResult.Ok("u1", "tok", -5).SessionTokenExpiresAtMs);
        }

        [Test]
        public void AuthSessionStore_BuildRecord_CopiesTokenExpiry()
        {
            AuthSessionStore.AuthSessionRecord record = AuthSessionStore.BuildRecord(
                LoginMode.Guest, LoginResult.Ok("u1", "tok_1", 42L), string.Empty);

            Assert.AreEqual("tok_1", record.SessionToken);
            Assert.AreEqual(42L, record.ExpiresAtMs);
        }

        [Test]
        public void AuthSession_ApplyAndClear_TracksExpiry()
        {
            try
            {
                AuthSession.Apply(LoginResult.Ok("u1", "tok_1", 42L));
                Assert.AreEqual(42L, AuthSession.SessionTokenExpiresAtMs);

                // 失败结果不得残留上一次会话的过期时刻。
                AuthSession.Apply(LoginResult.Fail("code", "msg"));
                Assert.AreEqual(0L, AuthSession.SessionTokenExpiresAtMs);
            }
            finally
            {
                AuthSession.Clear();
            }
        }

        private sealed class CannedHttpClient : IHttpClient
        {
            private readonly string _responseJson;

            public CannedHttpClient(string responseJson) => _responseJson = responseJson;

            public UniTask<HttpResponse> SendAsync(HttpRequest request)
            {
                return UniTask.FromResult(
                    new HttpResponse(200, null, Encoding.UTF8.GetBytes(_responseJson)));
            }
        }
    }
}
