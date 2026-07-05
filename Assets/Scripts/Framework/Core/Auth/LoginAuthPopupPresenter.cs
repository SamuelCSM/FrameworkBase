using System;
using Cysharp.Threading.Tasks;
using Framework.UI;

namespace Framework.Core.Auth
{
    /// <summary>
    /// 将 AuthManager 的失败弹窗策略落到 LoginWindow 的 ErrorPanel。
    /// </summary>
    public sealed class LoginAuthPopupPresenter : IAuthPopupPresenter
    {
        private readonly LoginWindow _window;

        public LoginAuthPopupPresenter(LoginWindow window)
        {
            _window = window;
        }

        public UniTask PresentAsync(AuthPopupDecision decision, Func<UniTask> retryHandler, Action exitHandler)
        {
            if (_window == null)
                return UniTask.CompletedTask;

            // 取消类错误不弹阻塞面板，避免干扰用户。
            if (!decision.ShowRetry && !decision.ShowExit)
                return UniTask.CompletedTask;

            var tcs = new UniTaskCompletionSource();

            _window.ShowError(
                decision.Message,
                onRetry: decision.ShowRetry
                    ? () =>
                    {
                        retryHandler?.Invoke().Forget();
                        tcs.TrySetResult();
                    }
                    : null,
                onExit: decision.ShowExit
                    ? () =>
                    {
                        exitHandler?.Invoke();
                        tcs.TrySetResult();
                    }
                    : null);

            return tcs.Task;
        }
    }
}
