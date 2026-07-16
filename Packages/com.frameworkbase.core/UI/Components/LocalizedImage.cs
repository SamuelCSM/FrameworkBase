using System;
using Framework.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 本地化图片组件：按 <see cref="LocalizedAssets"/> 候选链（当前语言 → 回退链 → 默认语言 →
    /// 原始地址）加载 Sprite 并赋给 Image；切语言自动重载，与 TextMeshProEx 的文案刷新同款体验。
    /// <para>
    /// 生命周期：OnEnable 加载 + 订阅 LanguageChanged，OnDisable 退订 + 归还引用计数；
    /// await 期间对象被禁用/销毁时，迟到的资源立即归还不悬挂。地址填不带语言后缀的原始地址，
    /// 本地化变体按 <c>地址@语言</c> 约定放 Addressables（如 <c>UI/banner@en_us</c>）。
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class LocalizedImage : MonoBehaviour
    {
        [Tooltip("不带语言后缀的原始地址，如 UI/banner；变体按 地址@语言 约定命名")]
        [SerializeField] private string _address;

        [Tooltip("换图后是否 SetNativeSize（各语言图尺寸不一致时开）")]
        [SerializeField] private bool _setNativeSize;

        private Image _image;
        private string _loadedAddress;
        private IDisposable _languageSubscription;
        private int _refreshSequence;

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_address))
            {
                Debug.LogError($"[LocalizedImage] {name} 未配置资源地址", this);
                return;
            }

            if (_image == null)
                _image = GetComponent<Image>();

            _languageSubscription = GameEntry.Event?.Subscribe<string>(
                GameMessage.LanguageChanged, _ => Refresh());
            Refresh();
        }

        private void OnDisable()
        {
            _languageSubscription?.Dispose();
            _languageSubscription = null;
            _refreshSequence++; // 使 await 中的刷新失效，迟到资源走立即归还路径
            ReleaseLoaded();
        }

        /// <summary>按当前语言重新解析并加载（幂等：解析结果与已加载一致时无动作）。</summary>
        public void Refresh()
        {
            RefreshAsync(++_refreshSequence).Forget();
        }

        private async Cysharp.Threading.Tasks.UniTaskVoid RefreshAsync(int sequence)
        {
            string resolved = await LocalizedAssets.ResolveAsync(_address);
            if (sequence != _refreshSequence || resolved == _loadedAddress)
                return;

            Sprite sprite = await GameEntry.Resource.LoadAssetAsync<Sprite>(resolved);
            if (sprite == null)
                return; // 加载失败已由 ResourceManager 记错误日志，保持现图

            // await 期间被禁用/销毁或有更新一轮刷新：本次资源立即归还，不覆盖不悬挂
            if (this == null || sequence != _refreshSequence)
            {
                GameEntry.Resource.ReleaseAsset(resolved);
                return;
            }

            ReleaseLoaded();
            _loadedAddress = resolved;
            _image.sprite = sprite;
            if (_setNativeSize)
                _image.SetNativeSize();
        }

        private void ReleaseLoaded()
        {
            if (string.IsNullOrEmpty(_loadedAddress))
                return;
            GameEntry.Resource?.ReleaseAsset(_loadedAddress);
            _loadedAddress = null;
        }
    }
}
