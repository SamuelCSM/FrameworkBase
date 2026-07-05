using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.UI
{
    /// <summary>
    /// 加载界面控制层（纯 C#，不继承 MonoBehaviour）
    ///
    /// 职责：
    ///   驱动 LoadingView 展示启动流程的每个阶段状态，
    ///   包括进度更新、错误处理、强制更新引导。
    ///
    /// 设计原则：
    ///   • 不依赖 UIManager / Addressables（在 Step 2 之前就需要显示）
    ///   • 所有 UI 操作通过 UIExtensions 扩展方法调用，零 null 检查噪音
    ///   • 所有更新方法均为同步，HideAsync 是唯一异步方法
    ///
    /// 典型调用顺序（对应 GameEntry 9 步流程）：
    ///   var loading = new LoadingWindow(view);
    ///   loading.SetStatus("初始化...");
    ///   loading.ShowDownload(totalBytes);
    ///   loading.UpdateDownloadProgress(progress, downloaded, total);
    ///   loading.HideDownload();
    ///   await loading.HideAsync();
    /// </summary>
    public class LoadingWindow
    {
        private readonly LoadingView _view;

        private const float FadeDuration = 0.4f;

        public LoadingWindow(LoadingView view)
        {
            _view = view;
            if (_view == null)
            {
                GameLog.Error("[LoadingWindow] LoadingView 为 null，请在 GameEntry Inspector 中赋值");
                return;
            }

            _view.progressBar.SetProgress(0f);
            _view.statusText.SetText("正在启动...");
            _view.gameObject.SetVisible(true);
        }

        // ── 状态文字 ──────────────────────────────────────────────────────
        /// <summary>设置状态说明文字，例如"正在检查更新..."</summary>
        public void SetStatus(string message)
            => _view?.statusText.SetText(message);

        // ── 进度条 ────────────────────────────────────────────────────────
        /// <summary>设置进度条（0~1）并同步更新百分比文字</summary>
        public void SetProgress(float progress)
        {
            if (_view == null) return;
            _view.progressBar.SetProgress(progress);
            _view.progressText.SetText($"{Mathf.RoundToInt(Mathf.Clamp01(progress) * 100)}%");
        }

        // ── 下载进度 ──────────────────────────────────────────────────────
        /// <summary>显示下载信息区域并展示总大小</summary>
        public void ShowDownload(long totalBytes)
        {
            if (_view == null) return;
            _view.downloadText.SetVisible(true);
            _view.downloadText.SetText($"待下载：{FileUtils.FormatBytes(totalBytes)}");
        }

        /// <summary>更新下载进度（progress 0~1，同时显示字节数）</summary>
        public void UpdateDownloadProgress(float progress, long downloadedBytes, long totalBytes)
        {
            SetProgress(progress);
            _view?.downloadText.SetText(
                $"{FileUtils.FormatBytes(downloadedBytes)} / {FileUtils.FormatBytes(totalBytes)}");
        }

        /// <summary>隐藏下载信息区域（下载完成后调用）</summary>
        public void HideDownload()
            => _view?.downloadText.SetVisible(false);

        // ── 版本号 ────────────────────────────────────────────────────────
        /// <summary>设置左下角版本号（应用版本 / 资源版本 / 代码版本）</summary>
        public void SetVersion(string appVersion, int resourceVersion, int codeVersion)
            => _view?.versionText.SetText(
                $"v{appVersion}  |  Res.{resourceVersion}  |  Code.{codeVersion}");

        // ── 错误面板 ──────────────────────────────────────────────────────
        /// <summary>
        /// 显示错误面板。
        /// onRetry 不为 null 时显示重试按钮；为 null 时仅显示退出按钮。
        /// </summary>
        public void ShowError(string message, Action onRetry = null)
        {
            if (_view?.errorPanel == null) return;

            SetProgress(0f);
            _view.errorMessageText.SetText(message);

            bool hasRetry = onRetry != null;
            _view.retryButton.SetVisible(hasRetry);
            if (hasRetry)
                _view.retryButton.AddClick(() => { HideError(); onRetry?.Invoke(); });

            _view.exitButton.AddClick(() => Application.Quit());
            _view.errorPanel.SetVisible(true);
        }

        /// <summary>隐藏错误面板</summary>
        public void HideError()
            => _view?.errorPanel.SetVisible(false);

        // ── 强制更新面板 ──────────────────────────────────────────────────
        /// <summary>显示强制更新面板（整包更新时调用）</summary>
        public void ShowForceUpdate(string description, string updateUrl = null)
        {
            if (_view?.forceUpdatePanel == null) return;

            _view.updateDescText.SetText(description);

            bool hasUrl = !string.IsNullOrEmpty(updateUrl);
            _view.updateButton.SetVisible(hasUrl);
            if (hasUrl)
                _view.updateButton.AddClick(() => Application.OpenURL(updateUrl));

            _view.forceUpdatePanel.SetVisible(true);
        }

        // ── 淡出并销毁 ───────────────────────────────────────────────────────
        /// <summary>
        /// 流程完成后调用：进度推到 100% → 短暂停顿 → 淡出 → 销毁 GameObject。
        /// LoadingWindow 在整个生命周期内只会使用一次，淡出后不再需要，
        /// 销毁可释放背景图、Logo 等资产的内存引用。
        /// </summary>
        public async UniTask HideAsync(CancellationToken ct = default)
        {
            SetProgress(1f);
            SetStatus("启动完成");

            await UniTask.Delay(300, cancellationToken: ct);

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

            // 淡出完成后直接销毁，释放内存；启动流程只走一次，不需要复用
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
        }
    }
}
