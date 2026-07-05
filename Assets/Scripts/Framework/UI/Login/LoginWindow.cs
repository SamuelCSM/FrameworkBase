using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework.UI
{
    /// <summary>
    /// 登录界面控制层（纯 C#）。
    /// 不依赖 UIManager / Addressables，与 LoadingWindow 同级随包策略。
    /// </summary>
    public class LoginWindow
    {
        private readonly LoginView _view;
        private const float FadeDuration = 0.35f;

        public LoginView View => _view;

        public LoginWindow(LoginView view)
        {
            _view = view;
            if (_view == null)
            {
                GameLog.Error("[LoginWindow] LoginView 为 null");
                return;
            }

            _view.gameObject.SetVisible(true);
            SetStatus("请选择登录方式");
        }

        /// <summary>设置左下角版本号（与 Loading 同格式）。</summary>
        public void SetVersion(string appVersion, int resourceVersion, int codeVersion)
        {
            _view?.versionText.SetText(
                $"v{appVersion}  |  Res.{resourceVersion}  |  Code.{codeVersion}");
        }

        /// <summary>使用 VersionManager 本地版本刷新显示。</summary>
        public void SetVersionFromLocal()
        {
            _view?.versionText.SetText(VersionDisplayHelper.FormatLocal());
        }

        public void SetStatus(string message)
            => _view?.statusText.SetText(message);

        /// <summary>登录进行中禁用按钮，避免重复提交。</summary>
        public void SetInteractable(bool interactable)
        {
            if (_view == null) return;
            _view.guestLoginButton.SetInteractable(interactable);
            _view.accountLoginButton.SetInteractable(interactable);
            if (_view.accountInput != null) _view.accountInput.interactable = interactable;
            if (_view.passwordInput != null) _view.passwordInput.interactable = interactable;
        }

        public string GetAccount()
            => _view?.accountInput != null ? _view.accountInput.text : string.Empty;

        public string GetPassword()
            => _view?.passwordInput != null ? _view.passwordInput.text : string.Empty;

        /// <summary>绑定游客/账号登录按钮。</summary>
        public void BindLoginActions(Action onGuestLogin, Action onAccountLogin)
        {
            if (_view == null) return;
            _view.guestLoginButton.AddClick(() => onGuestLogin?.Invoke());
            _view.accountLoginButton.AddClick(() => onAccountLogin?.Invoke());
        }

        /// <summary>
        /// 显示登录错误面板。
        /// onRetry / onExit 为 null 时隐藏对应按钮。
        /// </summary>
        public void ShowError(string message, Action onRetry = null, Action onExit = null)
        {
            if (_view?.errorPanel == null) return;

            _view.errorMessageText.SetText(message);

            bool hasRetry = onRetry != null;
            _view.retryButton.SetVisible(hasRetry);
            if (hasRetry)
                _view.retryButton.AddClick(() => { HideError(); onRetry(); });

            bool hasExit = onExit != null;
            _view.exitButton.SetVisible(hasExit);
            if (hasExit)
                _view.exitButton.AddClick(() => { HideError(); onExit(); });

            if (!hasRetry && !hasExit)
            {
                _view.retryButton.SetVisible(false);
                _view.exitButton.SetVisible(false);
            }

            _view.errorPanel.SetVisible(true);
        }

        public void HideError()
            => _view?.errorPanel.SetVisible(false);

        /// <summary>登录成功后淡出并销毁。</summary>
        public async UniTask HideAsync(CancellationToken ct = default)
        {
            if (_view?.canvasGroup != null)
            {
                float elapsed = 0f;
                while (elapsed < FadeDuration)
                {
                    if (ct.IsCancellationRequested) break;
                    elapsed += Time.deltaTime;
                    _view.canvasGroup.SetAlpha(1f - Mathf.Clamp01(elapsed / FadeDuration));
                    await UniTask.Yield(ct);
                }
            }

            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
        }
    }
}
