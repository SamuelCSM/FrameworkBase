using System;
using Cysharp.Threading.Tasks;
using Framework.Core.Auth;
using Framework.Input;
using Framework.UI;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 登录流程编排：Loading 销毁后实例化随包 LoginView，驱动 AuthManager。
    /// </summary>
    public static class LoginFlow
    {
        /// <summary>登录鉴权期间的输入屏蔽句柄。</summary>
        private static InputBlockHandle authInputBlock;

        /// <summary>
        /// 显示登录界面并等待一次成功登录。
        /// 若 AppConfig.AutoGuestLogin 为 true，跳过 UI 直接以访客身份自动登录。
        /// </summary>
        public static async UniTask<LoginResult> RunAsync(LoginView prefab, Transform parent)
        {
            if (prefab == null)
                throw new ArgumentNullException(nameof(prefab));
            if (parent == null)
                throw new ArgumentNullException(nameof(parent));

            // 自动访客登录：内网测试包跳过登录界面，资源加载完直接进游戏
            if (AppConfig.Load().AutoGuestLogin)
            {
                using (InputBlockScope.Begin("AutoGuestLogin"))
                {
                    return await GameEntry.Auth.LoginGuestAsync();
                }
            }

            var go = UnityEngine.Object.Instantiate(prefab.gameObject, parent);
            var view = go.GetComponent<LoginView>();
            var window = new LoginWindow(view);
            window.SetVersionFromLocal();

            var presenter = new LoginAuthPopupPresenter(window);
            GameEntry.Auth.SetPopupPresenter(presenter);

            var loginTcs = new UniTaskCompletionSource<LoginResult>();
            bool loginCompleted = false;
            IDisposable stateSubscription = SubscribeAuthState(window);

            window.BindLoginActions(
                onGuestLogin: () => RunLoginAsync(
                    LoginMode.Guest, string.Empty, string.Empty, window, loginTcs, () => loginCompleted, v => loginCompleted = v).Forget(),
                onAccountLogin: () => RunLoginAsync(
                    LoginMode.Account,
                    window.GetAccount(),
                    window.GetPassword(),
                    window,
                    loginTcs,
                    () => loginCompleted,
                    v => loginCompleted = v).Forget());

            try
            {
                LoginResult result = await loginTcs.Task;
                await window.HideAsync();
                return result;
            }
            finally
            {
                ReleaseAuthInputBlock();
                stateSubscription?.Dispose();
                GameEntry.Auth.SetPopupPresenter(new LogOnlyAuthPopupPresenter());
            }
        }

        private static IDisposable SubscribeAuthState(LoginWindow window)
        {
            void Handler(LoginStateSnapshot snapshot)
            {
                switch (snapshot.State)
                {
                    case LoginFlowState.Preparing:
                    case LoginFlowState.Connecting:
                    case LoginFlowState.Authenticating:
                        EnsureAuthInputBlock();
                        window.SetStatus("正在登录...");
                        window.SetInteractable(false);
                        break;
                    case LoginFlowState.Failed:
                    case LoginFlowState.Cancelled:
                        ReleaseAuthInputBlock();
                        window.SetInteractable(true);
                        window.SetStatus("登录失败，请重试");
                        break;
                    case LoginFlowState.Success:
                        ReleaseAuthInputBlock();
                        window.SetStatus("登录成功");
                        break;
                    default:
                        ReleaseAuthInputBlock();
                        window.SetInteractable(true);
                        break;
                }
            }

            GameEntry.Auth.OnStateChanged += Handler;
            return new AuthStateSubscription(() => GameEntry.Auth.OnStateChanged -= Handler);
        }

        private static async UniTaskVoid RunLoginAsync(
            LoginMode mode,
            string account,
            string password,
            LoginWindow window,
            UniTaskCompletionSource<LoginResult> loginTcs,
            Func<bool> isCompleted,
            Action<bool> setCompleted)
        {
            if (isCompleted())
                return;

            window.HideError();
            window.SetInteractable(false);

            LoginResult result;
            using (InputBlockScope.Begin("LoginRequest"))
            {
                result = mode == LoginMode.Guest
                    ? await GameEntry.Auth.LoginGuestAsync()
                    : await GameEntry.Auth.LoginAccountAsync(account, password);
            }

            window.SetInteractable(true);

            if (result.Success && !isCompleted())
            {
                setCompleted(true);
                loginTcs.TrySetResult(result);
            }
        }

        private sealed class AuthStateSubscription : IDisposable
        {
            private readonly Action _dispose;
            public AuthStateSubscription(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose?.Invoke();
        }

        /// <summary>
        /// 确保登录鉴权期间已压入输入屏蔽层。
        /// </summary>
        private static void EnsureAuthInputBlock()
        {
            if (authInputBlock != null && authInputBlock.IsActive)
            {
                return;
            }

            authInputBlock = GameEntry.Input?.Blocks.Push("LoginAuthenticating");
        }

        /// <summary>
        /// 解除登录鉴权期间的输入屏蔽层。
        /// </summary>
        private static void ReleaseAuthInputBlock()
        {
            authInputBlock?.Dispose();
            authInputBlock = null;
        }
    }
}
