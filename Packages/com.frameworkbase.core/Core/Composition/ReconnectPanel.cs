using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Framework.Core;

namespace Framework.UI
{
    /// <summary>
    /// 断线重连 UI 覆盖层。
    ///
    /// 使用方式：
    ///   1. 将 ReconnectPanel Prefab 绑定到 GameEntry._reconnectPanelPrefab
    ///   2. GameEntry 启动时实例化到 Canvas_System 层
    ///   3. 本组件自动订阅 NetworkManager 重连事件驱动显隐
    ///
    /// 状态机：
    ///   Hidden → Reconnecting（断线时自动显示）
    ///   Reconnecting → Hidden（重连成功自动隐藏）
    ///   Reconnecting → Failed（耗尽次数）
    ///   Failed → Reconnecting（玩家点击手动重连按钮）
    /// </summary>
    public class ReconnectPanel : MonoBehaviour
    {
        // ── UI 引用 ───────────────────────────────────────────────────────────
        [Header("遮罩")]
        [SerializeField] private CanvasGroup _canvasGroup;

        [Header("重连中")]
        [SerializeField] private GameObject  _reconnectingGroup;
        [SerializeField] private TextMeshProUGUI _statusText;     // "正在重连... (2/5)"
        [SerializeField] private TextMeshProUGUI _countdownText;  // "3 秒后重试"

        [Header("重连失败")]
        [SerializeField] private GameObject  _failedGroup;
        [SerializeField] private TextMeshProUGUI _failedText;     // "网络连接失败"
        [SerializeField] private Button      _retryButton;        // 手动重连
        [SerializeField] private Button      _exitButton;         // 返回登录/退出

        // ── 内部状态 ─────────────────────────────────────────────────────────
        private CancellationTokenSource _countdownCts;

        // ── 生命周期 ─────────────────────────────────────────────────────────

        private void Start()
        {
            EnsureRuntimeView();

            // 隐藏 Panel（初始状态）
            SetVisible(false);

            // 绑定按钮
            if (_retryButton != null)
                _retryButton.onClick.AddListener(OnRetryClicked);
            if (_exitButton != null)
                _exitButton.onClick.AddListener(OnExitClicked);

            // 订阅 NetworkManager 事件
            var nm = GameEntry.Network;
            if (nm == null)
            {
                GameLog.Warning("[ReconnectPanel] GameEntry.Network 为空，无法订阅重连事件");
                return;
            }

            nm.OnDisconnected       += HandleDisconnected;
            nm.OnReconnecting       += HandleReconnecting;
            nm.OnReconnectSucceeded += HandleReconnectSucceeded;
            nm.OnReconnectFailed    += HandleReconnectFailed;
        }

        private void OnDestroy()
        {
            StopCountdown();

            var nm = GameEntry.Network;
            if (nm == null) return;

            nm.OnDisconnected       -= HandleDisconnected;
            nm.OnReconnecting       -= HandleReconnecting;
            nm.OnReconnectSucceeded -= HandleReconnectSucceeded;
            nm.OnReconnectFailed    -= HandleReconnectFailed;
        }

        // ── 事件处理 ─────────────────────────────────────────────────────────
        // NetworkManager 的连接/重连事件统一经其 OnUpdate 的 DrainConnectionEvents 在主线程排空后触发，
        // 重连退避循环 TryReconnectAsync 也运行在主线程（UniTask），因此这里回调必定在主线程，可直接操作 UI。

        private void HandleDisconnected()
        {
            // 断线时立即显示重连中状态（等待第一个 OnReconnecting 事件更新详细信息）
            SetVisible(true);
            ShowReconnectingGroup("正在重连...", "");
        }

        private void HandleReconnecting(int attempt, int maxAttempts, float waitSeconds)
        {
            SetVisible(true);
            ShowReconnectingGroup(
                $"正在重连... ({attempt}/{maxAttempts})",
                countdownSeconds: (int)waitSeconds
            );
        }

        private void HandleReconnectSucceeded()
        {
            StopCountdown();
            SetVisible(false);
            GameLog.Log("[ReconnectPanel] 重连成功，隐藏面板");
        }

        private void HandleReconnectFailed()
        {
            StopCountdown();
            ShowFailedGroup("网络连接失败\n请检查网络后手动重试");
        }

        // ── 状态切换 ─────────────────────────────────────────────────────────

        private void ShowReconnectingGroup(string status, string countdown = "")
        {
            if (_reconnectingGroup != null) _reconnectingGroup.SetActive(true);
            if (_failedGroup       != null) _failedGroup.SetActive(false);

            if (_statusText   != null) _statusText.text   = status;
            if (_countdownText != null) _countdownText.text = countdown;
        }

        private void ShowReconnectingGroup(string status, int countdownSeconds)
        {
            ShowReconnectingGroup(status, "");
            if (countdownSeconds > 0)
            {
                StopCountdown();
                _countdownCts = new CancellationTokenSource();
                CountdownAsync(countdownSeconds, _countdownCts.Token).Forget();
            }
        }

        private void ShowFailedGroup(string message)
        {
            if (_reconnectingGroup != null) _reconnectingGroup.SetActive(false);
            if (_failedGroup       != null) _failedGroup.SetActive(true);
            if (_failedText        != null) _failedText.text = message;
        }

        private void SetVisible(bool visible)
        {
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha          = visible ? 1f : 0f;
                _canvasGroup.blocksRaycasts = visible;
                _canvasGroup.interactable   = visible;
            }
            else
            {
                gameObject.SetActive(visible);
            }
        }

        private void StopCountdown()
        {
            _countdownCts?.Cancel();
            _countdownCts?.Dispose();
            _countdownCts = null;
        }

        // ── 倒计时（UniTask）────────────────────────────────────────────────

        private async UniTaskVoid CountdownAsync(int seconds, CancellationToken ct)
        {
            try
            {
                while (seconds > 0)
                {
                    if (_countdownText != null)
                        _countdownText.text = $"{seconds} 秒后重试";

                    // ignoreTimeScale：即使游戏暂停（Time.timeScale=0）倒计时也继续
                    await UniTask.Delay(1000, ignoreTimeScale: true, cancellationToken: ct);
                    seconds--;
                }

                if (_countdownText != null)
                    _countdownText.text = "正在连接...";
            }
            catch (OperationCanceledException)
            {
                // 被 StopCountdown 取消，正常退出，不需要处理
            }
        }

        // ── 按钮回调 ─────────────────────────────────────────────────────────

        private void OnRetryClicked()
        {
            ShowReconnectingGroup("正在重连...", "");
            var nm = GameEntry.Network;
            if (nm != null) nm.ReconnectAsync().Forget();
        }

        private void OnExitClicked()
        {
            GameLog.Log("[ReconnectPanel] 玩家选择返回登录");
            SetVisible(false);
            Framework.Core.Auth.AuthSession.Clear();
            GameEntry.Auth?.RetryLastLoginAsync().Forget();
        }

        /// <summary>
        /// 确保运行时可用的默认 UI 结构存在。
        /// Prefab 正常绑定时沿用 Inspector 引用；资源字段丢失时自动补齐，避免断线重连时静默无界面。
        /// </summary>
        private void EnsureRuntimeView()
        {
            if (_canvasGroup == null)
            {
                _canvasGroup = GetOrAdd<CanvasGroup>(gameObject);
                _canvasGroup.alpha = 0f;
                _canvasGroup.interactable = false;
                _canvasGroup.blocksRaycasts = false;
            }

            bool missingRequiredReference =
                _reconnectingGroup == null ||
                _statusText == null ||
                _countdownText == null ||
                _failedGroup == null ||
                _failedText == null ||
                _retryButton == null ||
                _exitButton == null;

            if (!missingRequiredReference)
            {
                return;
            }

            CreateDefaultView();
        }

        /// <summary>
        /// 创建默认断线重连界面。
        /// </summary>
        private void CreateDefaultView()
        {
            RectTransform rootRect = GetOrAdd<RectTransform>(gameObject);
            SetFullStretch(rootRect);

            Image blocker = CreateImage(transform, "Blocker", new Color(0.02f, 0.04f, 0.08f, 0.72f));
            SetFullStretch(blocker.rectTransform);

            RectTransform card = CreateImage(transform, "Card", new Color(0.08f, 0.11f, 0.16f, 0.96f)).rectTransform;
            SetCenterRect(card, 520f, 300f);
            ConfigureVerticalLayout(card.gameObject, 28, 32, 18f);

            TextMeshProEx titleText = CreateText(card, "TitleText", "网络连接异常", 34f, TextAlignmentOptions.Center);
            titleText.color = new Color(0.95f, 0.98f, 1f, 1f);

            _reconnectingGroup = CreateGroup(card, "ReconnectingGroup");
            ConfigureVerticalLayout(_reconnectingGroup, 0, 0, 10f);
            _statusText = CreateText(_reconnectingGroup.transform, "StatusText", "正在重连...", 28f, TextAlignmentOptions.Center);
            _countdownText = CreateText(_reconnectingGroup.transform, "CountdownText", string.Empty, 24f, TextAlignmentOptions.Center);
            _countdownText.color = new Color(0.66f, 0.82f, 1f, 1f);

            _failedGroup = CreateGroup(card, "FailedGroup");
            ConfigureVerticalLayout(_failedGroup, 0, 0, 14f);
            _failedText = CreateText(_failedGroup.transform, "FailedText", "网络连接失败\n请检查网络后手动重试", 25f, TextAlignmentOptions.Center);
            _failedText.enableWordWrapping = true;

            RectTransform buttonRow = CreateGroup(_failedGroup.transform, "ButtonRow").GetComponent<RectTransform>();
            buttonRow.sizeDelta = new Vector2(420f, 58f);
            HorizontalLayoutGroup buttonLayout = buttonRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttonLayout.spacing = 16f;
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;
            buttonLayout.childControlWidth = false;
            buttonLayout.childControlHeight = false;
            buttonLayout.childForceExpandWidth = false;
            buttonLayout.childForceExpandHeight = false;

            _retryButton = CreateButton(buttonRow, "RetryButton", "重试", new Color(0.18f, 0.54f, 0.92f, 1f));
            _exitButton = CreateButton(buttonRow, "ExitButton", "返回登录", new Color(0.26f, 0.30f, 0.36f, 1f));
            _failedGroup.SetActive(false);
        }

        /// <summary>
        /// 创建透明分组节点。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">节点名。</param>
        /// <returns>创建后的 GameObject。</returns>
        private static GameObject CreateGroup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>
        /// 创建纯色 Image 节点。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">节点名。</param>
        /// <param name="color">填充颜色。</param>
        /// <returns>Image 组件。</returns>
        private static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        /// <summary>
        /// 创建 TextMeshProEx 文本。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">节点名。</param>
        /// <param name="content">原始显示文本。</param>
        /// <param name="fontSize">字号。</param>
        /// <param name="alignment">对齐方式。</param>
        /// <returns>文本组件。</returns>
        private static TextMeshProEx CreateText(Transform parent, string name, string content, float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProEx), typeof(LayoutElement));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(440f, Mathf.Max(44f, fontSize + 18f));

            LayoutElement layout = go.GetComponent<LayoutElement>();
            layout.preferredWidth = rect.sizeDelta.x;
            layout.preferredHeight = rect.sizeDelta.y;

            TextMeshProEx text = go.GetComponent<TextMeshProEx>();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.SetRawText(content);
            return text;
        }

        /// <summary>
        /// 创建文本按钮。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">节点名。</param>
        /// <param name="label">按钮文字。</param>
        /// <param name="color">按钮底色。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateButton(Transform parent, string name, string label, Color color)
        {
            var go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button),
                typeof(LayoutElement));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(190f, 56f);

            LayoutElement layout = go.GetComponent<LayoutElement>();
            layout.preferredWidth = 190f;
            layout.preferredHeight = 56f;

            Image image = go.GetComponent<Image>();
            image.color = color;

            Button button = go.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = color * 1.15f;
            colors.pressedColor = color * 0.82f;
            colors.disabledColor = new Color(0.20f, 0.22f, 0.25f, 0.7f);
            button.colors = colors;

            TextMeshProEx buttonText = CreateText(go.transform, "Label", label, 24f, TextAlignmentOptions.Center);
            SetFullStretch(buttonText.GetComponent<RectTransform>());
            return button;
        }

        /// <summary>
        /// 配置垂直布局。
        /// </summary>
        /// <param name="go">目标节点。</param>
        /// <param name="horizontalPadding">左右内边距。</param>
        /// <param name="verticalPadding">上下内边距。</param>
        /// <param name="spacing">子节点间距。</param>
        private static void ConfigureVerticalLayout(GameObject go, int horizontalPadding, int verticalPadding, float spacing)
        {
            VerticalLayoutGroup layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(horizontalPadding, horizontalPadding, verticalPadding, verticalPadding);
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        /// <summary>
        /// 设置 RectTransform 为父节点全屏拉伸。
        /// </summary>
        /// <param name="rect">目标 RectTransform。</param>
        private static void SetFullStretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// 设置居中固定尺寸矩形。
        /// </summary>
        /// <param name="rect">目标 RectTransform。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        private static void SetCenterRect(RectTransform rect, float width, float height)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>
        /// 获取或添加指定组件。
        /// </summary>
        /// <typeparam name="T">组件类型。</typeparam>
        /// <param name="target">目标节点。</param>
        /// <returns>组件实例。</returns>
        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            T component = target.GetComponent<T>();
            return component != null ? component : target.AddComponent<T>();
        }
    }
}
