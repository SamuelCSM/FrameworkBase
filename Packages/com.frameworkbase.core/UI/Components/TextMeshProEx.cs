using TMPro;
using UnityEngine;
using Framework;
using Framework.Core;

namespace Framework.UI
{
    /// <summary>
    /// 项目统一使用的 UI 文本组件。
    /// 继承 TextMeshProUGUI，保留 TMP 原生渲染能力，并扩展 #2 静态文本自动翻译与语言切换刷新。
    /// </summary>
    [AddComponentMenu("UI/TextMeshProEx")]
    public class TextMeshProEx : TextMeshProUGUI
    {
        /// <summary>绑定的多语言 key。为空时可从当前 text 字段读取 #2 key。</summary>
        [SerializeField] private string languageKey;

        /// <summary>是否允许把 TextMeshPro 文本框中填写的 #2 文本作为多语言 key。</summary>
        [SerializeField] private bool useTextAsKey = true;

        /// <summary>组件启用时是否自动刷新一次多语言文本。</summary>
        [SerializeField] private bool refreshOnEnable = true;

        /// <summary>是否监听 LanguageChanged 事件，并在切换语言时自动刷新。</summary>
        [SerializeField] private bool refreshWhenLanguageChanged = true;

        /// <summary>格式化参数缓存，用于 SetLang(key, args) 后语言切换时重新格式化。</summary>
        private object[] _formatArgs;

        /// <summary>是否已经注册语言变化事件，避免重复注册或重复注销。</summary>
        private bool _languageEventRegistered;

        /// <summary>语言变化事件订阅句柄。</summary>
        private EventSubscription _languageEventSubscription;

        /// <summary>当前绑定的多语言 key。</summary>
        public string LanguageKey => languageKey;

        /// <summary>
        /// 重写 TMP 文本属性。
        /// 当赋值为 #2 key 时自动绑定并翻译；普通文本则按原始文本显示。
        /// </summary>
        public override string text
        {
            get => base.text;
            set => SetTextValue(value);
        }

        /// <summary>
        /// Unity 生命周期：组件初始化。
        /// 先执行 TMP 原生初始化，再缓存 Prefab 上填写的 #2 key 并尝试翻译。
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            CacheTextKeyIfNeeded();
            RefreshLanguage();
        }

        /// <summary>
        /// Unity 生命周期：组件启用。
        /// 注册语言变化事件，并按配置刷新显示文本。
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            RegisterLanguageEvent();

            if (refreshOnEnable)
                RefreshLanguage();
        }

        /// <summary>
        /// Unity 生命周期：组件禁用。
        /// 注销语言变化事件，避免对象销毁或隐藏后继续收到刷新回调。
        /// </summary>
        protected override void OnDisable()
        {
            UnregisterLanguageEvent();
            base.OnDisable();
        }

        /// <summary>
        /// 设置多语言 key 并立即刷新文本。
        /// </summary>
        /// <param name="key">language 表中的 Key，通常为 #1 或 #2 开头。</param>
        public void SetLang(string key)
        {
            SetLang(key, null);
        }

        /// <summary>
        /// 设置带格式化参数的多语言 key 并立即刷新文本。
        /// </summary>
        /// <param name="key">language 表中的 Key，通常为 #1 或 #2 开头。</param>
        /// <param name="args">格式化参数，用于填充文案中的 {0}、{1}。</param>
        public void SetLang(string key, params object[] args)
        {
            languageKey = key;
            _formatArgs = args;
            RegisterLanguageEvent();
            RefreshLanguage();
        }

        /// <summary>
        /// 设置原始文本并清空多语言绑定。
        /// 用于玩家名、版本号、数字等不应该参与翻译的内容。
        /// </summary>
        /// <param name="value">原始显示文本。</param>
        public void SetRawText(string value)
        {
            languageKey = string.Empty;
            _formatArgs = null;
            UnregisterLanguageEvent();
            base.text = value ?? string.Empty;
        }

        /// <summary>
        /// 按当前语言刷新显示文本。
        /// 如果 RefData 尚未准备好，Language.Get 会返回原 key，后续 Language.Refresh 会再次刷新。
        /// </summary>
        public void RefreshLanguage()
        {
            CacheTextKeyIfNeeded();

            if (string.IsNullOrEmpty(languageKey))
                return;

            base.text = _formatArgs == null || _formatArgs.Length == 0
                ? Language.Get(languageKey)
                : Language.Get(languageKey, _formatArgs);
        }

        /// <summary>
        /// 处理外部对 text 属性的赋值。
        /// </summary>
        /// <param name="value">外部设置的文本或 #2 key。</param>
        private void SetTextValue(string value)
        {
            value ??= string.Empty;

            if (Language.IsAutoTextKey(value))
            {
                languageKey = value;
                _formatArgs = null;
                RegisterLanguageEvent();
                RefreshLanguage();
                return;
            }

            languageKey = string.Empty;
            _formatArgs = null;
            UnregisterLanguageEvent();
            base.text = value;
        }

        /// <summary>
        /// 在未手动指定 languageKey 时，从 TMP 当前文本中缓存 #2 key。
        /// </summary>
        private void CacheTextKeyIfNeeded()
        {
            if (!string.IsNullOrEmpty(languageKey) || !useTextAsKey)
                return;

            string rawText = base.text;
            if (Language.IsAutoTextKey(rawText))
                languageKey = rawText;
        }

        /// <summary>
        /// 注册语言变化事件。
        /// 只在运行时注册，避免编辑器非播放状态产生无意义监听。
        /// </summary>
        private void RegisterLanguageEvent()
        {
            if (!Application.isPlaying || !refreshWhenLanguageChanged || _languageEventRegistered)
                return;

            CacheTextKeyIfNeeded();
            if (string.IsNullOrEmpty(languageKey))
                return;

            if (GameEntry.Event == null)
                return;

            _languageEventSubscription = GameEntry.Event.Subscribe<string>(
                GameMessage.LanguageChanged,
                OnLanguageChanged);
            _languageEventRegistered = true;
        }

        /// <summary>
        /// 注销语言变化事件。
        /// </summary>
        private void UnregisterLanguageEvent()
        {
            if (!_languageEventRegistered)
                return;

            _languageEventSubscription?.Unsubscribe();
            _languageEventSubscription = null;
            _languageEventRegistered = false;
        }

        /// <summary>
        /// 语言切换事件回调。
        /// </summary>
        /// <param name="language">切换后的语言代码。</param>
        private void OnLanguageChanged(string language)
        {
            RefreshLanguage();
        }
    }
}
