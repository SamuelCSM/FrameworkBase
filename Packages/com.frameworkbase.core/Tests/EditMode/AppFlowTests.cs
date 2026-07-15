using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Core.Auth;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// AppFlow 纯逻辑单测：登录 ⇄ 业务 ⇄ 登出全链路脱离 Play 模式验证。
    /// 登录/业务入口活动用 TCS 闸门步进控制；登出拆卸经一次 Yield 解耦，断言前统一用 WaitUntil 泵帧。
    /// </summary>
    public class AppFlowTests
    {
        /// <summary>测试夹具：记录钩子调用序列，登录与业务入口可由测试闸门放行。</summary>
        private sealed class Harness : IDisposable
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public readonly Queue<UniTaskCompletionSource<LoginResult>> LoginGates =
                new Queue<UniTaskCompletionSource<LoginResult>>();
            public UniTaskCompletionSource EnterGate;
            public bool ThrowOnEnter;
            public bool ThrowOnExit;

            public AppFlow Flow;
            public CancellationTokenSource Cts = new CancellationTokenSource();
            public UniTask RunTask;

            public Harness()
            {
                Flow = new AppFlow(new AppFlowHooks
                {
                    RunLoginAsync = token =>
                    {
                        var gate = new UniTaskCompletionSource<LoginResult>();
                        LoginGates.Enqueue(gate);
                        Calls.Add("login-start");
                        return gate.Task.AttachExternalCancellation(token);
                    },
                    BindIdentity = result => Calls.Add($"bind:{result.UserId}"),
                    EnterBusinessAsync = async (result, _) =>
                    {
                        Calls.Add($"enter:{result.UserId}");
                        if (ThrowOnEnter) throw new InvalidOperationException("enter failed");
                        if (EnterGate != null) await EnterGate.Task;
                    },
                    ExitBusiness = reason =>
                    {
                        Calls.Add($"exit:{reason}");
                        if (ThrowOnExit) throw new InvalidOperationException("exit failed");
                    },
                    LogoutAuth = reason => Calls.Add($"auth-logout:{reason}"),
                    ClearIdentity = () => Calls.Add("clear-identity"),
                    Error = (message, ex) => Errors.Add(message),
                });
            }

            public void Start() => RunTask = Flow.RunAsync(Cts.Token);

            public void CompleteLogin(string userId)
            {
                Assert.IsTrue(LoginGates.Count > 0, "没有待放行的登录活动");
                LoginGates.Dequeue().TrySetResult(LoginResult.Ok(userId));
            }

            public async UniTask ShutdownAsync()
            {
                Cts.Cancel();
                await RunTask;
            }

            public void Dispose()
            {
                Cts.Dispose();
                Flow.Dispose();
            }
        }

        private static async UniTask WaitUntilAsync(Func<bool> condition, string what, int maxFrames = 600)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (condition()) return;
                await UniTask.Yield();
            }
            Assert.Fail($"等待超时: {what}");
        }

        [UnityTest]
        public System.Collections.IEnumerator 登录成功_按序贯通身份进业务并提交InGame() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.Start();

            Assert.AreEqual(AppFlowState.Login, h.Flow.CurrentState);
            h.CompleteLogin("u1");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "进入 InGame");

            CollectionAssert.AreEqual(new[] { "login-start", "bind:u1", "enter:u1" }, h.Calls);
            await h.ShutdownAsync();
        });

        [UnityTest]
        public System.Collections.IEnumerator 登出请求_拆卸按序回登录页并支持换号重登() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.Start();
            h.CompleteLogin("u1");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "u1 进入 InGame");

            Assert.IsTrue(h.Flow.RequestLogout("player_logout"));
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.Login, "拆卸后回 Login");

            // 拆卸顺序：业务退出 → 鉴权登出 → 清身份；随后自动拉起下一轮登录活动。
            CollectionAssert.AreEqual(
                new[]
                {
                    "login-start", "bind:u1", "enter:u1",
                    "exit:player_logout", "auth-logout:player_logout", "clear-identity",
                    "login-start",
                },
                h.Calls);

            // A→B 身份切换：第二个账号完整走贯通与业务入口。
            h.CompleteLogin("u2");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "u2 进入 InGame");
            CollectionAssert.AreEqual(new[] { "bind:u2", "enter:u2" }, h.Calls.GetRange(7, 2));
            await h.ShutdownAsync();
        });

        [UnityTest]
        public System.Collections.IEnumerator 登录态收到登出_NoOp不产生任何拆卸() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.Start();

            Assert.IsFalse(h.Flow.RequestLogout("early"), "登录态（未武装会话）收到登出应为 no-op");

            h.CompleteLogin("u1");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "进入 InGame");
            CollectionAssert.DoesNotContain(h.Calls, "exit:early");
            await h.ShutdownAsync();
        });

        [UnityTest]
        public System.Collections.IEnumerator 业务入口await期间登出_后置合并且入口完成后立即拆卸() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.EnterGate = new UniTaskCompletionSource();
            h.Start();
            h.CompleteLogin("u1");
            await WaitUntilAsync(() => h.Calls.Contains("enter:u1"), "业务入口开始");

            // 入口尚未完成：登出被记住（latch），且同会话第二个信号合并、首个原因生效。
            Assert.IsTrue(h.Flow.RequestLogout("server_force_logout:401"));
            Assert.IsTrue(h.Flow.RequestLogout("sdk_session_invalidated:expired"));
            Assert.AreEqual(AppFlowState.Login, h.Flow.CurrentState, "入口未完成前 InGame 不得提交");
            CollectionAssert.DoesNotContain(h.Calls, "exit:server_force_logout:401");

            h.EnterGate.TrySetResult();
            await WaitUntilAsync(
                () => h.Calls.Contains("clear-identity"), "入口完成后立即执行后置拆卸");
            Assert.IsTrue(h.Calls.Contains("exit:server_force_logout:401"), "首个登出原因生效");
            CollectionAssert.DoesNotContain(h.Calls, "exit:sdk_session_invalidated:expired");
            int exitCount = h.Calls.FindAll(c => c.StartsWith("exit:")).Count;
            Assert.AreEqual(1, exitCount, "多次登出信号必须合并为一次拆卸");
            await h.ShutdownAsync();
        });

        [UnityTest]
        public System.Collections.IEnumerator 钩子异常_全部隔离不中断循环不Faulted() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.ThrowOnEnter = true;
            h.ThrowOnExit = true;
            h.Start();
            h.CompleteLogin("u1");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "入口异常隔离后仍提交 InGame");

            Assert.IsTrue(h.Flow.RequestLogout("player_logout"));
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.Login, "退出异常隔离后仍完成拆卸");

            // 业务钩子异常被隔离上报，框架清理链（鉴权登出 + 清身份）必须走完。
            Assert.IsTrue(h.Calls.Contains("auth-logout:player_logout"));
            Assert.IsTrue(h.Calls.Contains("clear-identity"));
            Assert.AreEqual(2, h.Errors.Count, "入口与退出异常各上报一次");

            // 循环存活：还能再登录。
            h.CompleteLogin("u2");
            await WaitUntilAsync(() => h.Flow.CurrentState == AppFlowState.InGame, "异常后循环仍可再登录");
            await h.ShutdownAsync();
        });

        [UnityTest]
        public System.Collections.IEnumerator 应用退出取消_循环收束不再拉起登录() => UniTask.ToCoroutine(async () =>
        {
            using var h = new Harness();
            h.Start();
            Assert.AreEqual(1, h.LoginGates.Count);

            h.Cts.Cancel();
            await h.RunTask; // 静默收束，不抛异常。

            Assert.AreEqual(1, h.LoginGates.Count, "取消后不得再拉起新一轮登录活动");
        });

        [Test]
        public void RunAsync只允许驱动一次()
        {
            using var h = new Harness();
            h.Start();
            Assert.Throws<InvalidOperationException>(() => h.Flow.RunAsync(h.Cts.Token).GetAwaiter().GetResult());
            h.Cts.Cancel();
        }
    }
}
