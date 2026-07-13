using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core.Auth;
using Framework.Security;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 鉴权异步竞态回归测试：模拟底层传输忽略 CancellationToken、在登出之后仍返回成功，
    /// 验证 AuthManager 的操作世代能够阻止旧响应复活内存会话、持久化令牌或覆盖新登录结果。
    /// </summary>
    public sealed class AuthManagerConcurrencyTests
    {
        private InMemorySecureStorage _storage;
        private AuthManager _manager;
        private DeferredAuthBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _storage = new InMemorySecureStorage();
            SecureStorage.SetBackend(_storage);
            AuthSession.Clear();

            _manager = new AuthManager();
            _manager.OnInit();
            _backend = new DeferredAuthBackend();
            _manager.SetBackend(_backend);
        }

        [TearDown]
        public void TearDown()
        {
            _manager?.OnShutdown();
            AuthSession.Clear();
            SecureStorage.SetBackend(new InMemorySecureStorage());
        }

        [UnityTest]
        public IEnumerator 登出后迟到成功响应_不得复活会话或重新持久化令牌() => UniTask.ToCoroutine(async () =>
        {
            UniTask<LoginResult> login = _manager.LoginGuestAsync(5000);
            await UniTask.WaitUntil(() => _backend.CallCount >= 1);

            _manager.Logout("test_logout");
            _backend.Complete(0, LoginResult.Ok("old-user", "old-token"));
            LoginResult staleResult = await login;

            Assert.IsFalse(staleResult.Success, "已过期登录响应必须折算为取消/失败");
            Assert.AreEqual(LoginFlowState.Idle, _manager.State, "旧操作不得覆盖登出后的 Idle 状态");
            Assert.IsFalse(AuthSession.IsLoggedIn, "登出后迟到响应不得恢复内存会话");
            Assert.IsFalse(_storage.TryGet("framework.auth.session", out _), "登出后迟到响应不得重新写入持久化令牌");
        });

        [UnityTest]
        public IEnumerator 旧登录退出时新登录已开始_旧操作不得释放或覆盖新操作() => UniTask.ToCoroutine(async () =>
        {
            UniTask<LoginResult> oldLogin = _manager.LoginGuestAsync(5000);
            await UniTask.WaitUntil(() => _backend.CallCount >= 1);

            _manager.Logout("switch_account");
            UniTask<LoginResult> newLogin = _manager.LoginAccountAsync("new-account", "new-password", 5000);
            await UniTask.WaitUntil(() => _backend.CallCount >= 2);

            // 先让旧请求返回；其 finally 只能释放自己的 CTS，不能取消或清空新请求。
            _backend.Complete(0, LoginResult.Ok("old-user", "old-token"));
            LoginResult oldResult = await oldLogin;
            Assert.IsFalse(oldResult.Success);
            Assert.IsFalse(AuthSession.IsLoggedIn);

            _backend.Complete(1, LoginResult.Ok("new-user", "new-token"));
            LoginResult newResult = await newLogin;

            Assert.IsTrue(newResult.Success);
            Assert.AreEqual("new-user", AuthSession.UserId);
            Assert.AreEqual("new-token", AuthSession.SessionToken);
            Assert.AreEqual(LoginFlowState.Success, _manager.State);
        });

        /// <summary>
        /// 可控鉴权后端：刻意忽略 CancellationToken，复现真实网络库不能及时取消、旧响应迟到的最坏情况。
        /// </summary>
        private sealed class DeferredAuthBackend : IAuthBackend
        {
            private readonly List<UniTaskCompletionSource<LoginResult>> _calls =
                new List<UniTaskCompletionSource<LoginResult>>();

            public int CallCount => _calls.Count;

            public UniTask<LoginResult> LoginAsync(LoginRequestContext context, CancellationToken cancellationToken)
            {
                var completion = new UniTaskCompletionSource<LoginResult>();
                _calls.Add(completion);
                return completion.Task;
            }

            public void Complete(int index, LoginResult result) => _calls[index].TrySetResult(result);
        }
    }
}