using Framework.Core;
using Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Framework.Network
{
    /// <summary>
    /// 网络请求等待转圈 UI。
    /// 自行订阅 NetworkManager.OnWaitingStart / OnWaitingEnd 事件。
    /// NetworkManager 不知道本组件的存在，依赖方向：UI → Network。
    ///
    /// Prefab 结构：
    ///   NetworkWaiting (挂载本脚本，RectTransform 全屏拉伸)
    ///   ├── Content        — 可见内容根节点（默认隐藏）
    ///   │   ├── Blocker    — 半透明遮罩（防止连点）
    ///   │   └── Spinner    — 旋转图标
    /// </summary>
    public class NetworkWaitingUI : MonoBehaviour
    {
        [Header("可见内容根节点（显隐控制）")]
        [SerializeField] private GameObject content;

        [Header("转圈图标")]
        [SerializeField] private RectTransform spinnerIcon;

        [Header("旋转速度（度/秒）")]
        [SerializeField] private float rotateSpeed = 360f;

        /// <summary>是否正在显示。</summary>
        private bool isShowing;

        private void Start()
        {
            EnsureRuntimeView();

            // 初始隐藏
            if (content != null)
            {
                content.SetActive(false);
            }

            // 订阅网络等待事件
            if (GameEntry.Network != null)
            {
                GameEntry.Network.OnWaitingStart += Show;
                GameEntry.Network.OnWaitingEnd += Hide;
            }
        }

        private void OnDestroy()
        {
            if (GameEntry.Network != null)
            {
                GameEntry.Network.OnWaitingStart -= Show;
                GameEntry.Network.OnWaitingEnd -= Hide;
            }
        }

        private void Update()
        {
            if (isShowing && spinnerIcon != null)
            {
                spinnerIcon.Rotate(0f, 0f, -rotateSpeed * Time.deltaTime);
            }
        }

        /// <summary>
        /// 显示等待转圈。
        /// </summary>
        private void Show()
        {
            if (isShowing)
            {
                return;
            }

            isShowing = true;
            if (content != null)
            {
                content.SetActive(true);
            }
        }

        /// <summary>
        /// 隐藏等待转圈。
        /// </summary>
        private void Hide()
        {
            if (!isShowing)
            {
                return;
            }

            isShowing = false;
            if (content != null)
            {
                content.SetActive(false);
            }
        }

        /// <summary>
        /// 确保网络等待遮罩拥有可显示的默认 UI 结构。
        /// Prefab 引用完整时直接复用；字段丢失时运行时补齐，避免请求等待状态没有可见反馈。
        /// </summary>
        private void EnsureRuntimeView()
        {
            if (content != null && spinnerIcon != null)
            {
                return;
            }

            RectTransform rootRect = GetOrAdd<RectTransform>(gameObject);
            SetFullStretch(rootRect);

            content = CreateGroup(transform, "Content");
            SetFullStretch(content.GetComponent<RectTransform>());

            Image blocker = CreateImage(content.transform, "Blocker", new Color(0.02f, 0.04f, 0.08f, 0.38f));
            SetFullStretch(blocker.rectTransform);

            spinnerIcon = CreateGroup(content.transform, "Spinner").GetComponent<RectTransform>();
            SetCenterRect(spinnerIcon, 88f, 88f);

            for (int i = 0; i < 8; i++)
            {
                RectTransform dot = CreateImage(
                    spinnerIcon,
                    "Dot" + i,
                    new Color(0.55f, 0.78f, 1f, 0.35f + i * 0.08f)).rectTransform;
                SetCenterRect(dot, 12f, 12f);
                float angle = i * 45f * Mathf.Deg2Rad;
                dot.anchoredPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 34f;
            }

            TextMeshProEx waitingText = CreateText(content.transform, "WaitingText", "网络请求中...", 24f);
            RectTransform waitingRect = waitingText.GetComponent<RectTransform>();
            SetCenterRect(waitingRect, 240f, 44f);
            waitingRect.anchoredPosition = new Vector2(0f, -78f);
            waitingText.color = new Color(0.92f, 0.96f, 1f, 1f);
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
        /// 创建等待提示文本。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">节点名。</param>
        /// <param name="content">原始显示文本。</param>
        /// <param name="fontSize">字号。</param>
        /// <returns>文本组件。</returns>
        private static TextMeshProEx CreateText(Transform parent, string name, string content, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProEx));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);

            TextMeshProEx text = go.GetComponent<TextMeshProEx>();
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            text.SetRawText(content);
            return text;
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
