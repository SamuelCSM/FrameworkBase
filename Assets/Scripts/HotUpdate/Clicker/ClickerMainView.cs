using System;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.UI;
using HotUpdate.RedDot.Generated;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 主界面（代码构建，挂在 UILayer.Normal）。订阅 <see cref="ClickerEvents.StateChanged"/>
    /// 刷新显示；按钮驱动 <see cref="ClickerModel"/>。金币不足/满级时禁用升级按钮（不弹失败提示）。
    /// </summary>
    public class ClickerMainView : MonoBehaviour
    {
        private ClickerModel _model;
        private TextMeshProUGUI _coinLabel;
        private TextMeshProUGUI _levelLabel;
        private TextMeshProUGUI _upgradeLabel;
        private IDisposable _stateSub;

        public Button ClickButton { get; private set; }
        public Button UpgradeButton { get; private set; }
        public Button ShopButton { get; private set; }

        public static ClickerMainView Create()
        {
            Transform parent = GameEntry.UI.GetLayerRoot(UILayer.Normal);
            var go = new GameObject("ClickerMainView", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<ClickerMainView>();
            view.Build();
            return view;
        }

        private void Build()
        {
            _model = ClickerGameDataManager.Model;
            ClickerUiKit.Stretch(gameObject);
            ClickerUiKit.Image(transform, "BG", new Color(0.10f, 0.11f, 0.15f, 1f), stretch: true);

            // 玩家 ID 常驻显示：验证组合根身份贯通（存档按 uid 隔离）的可视锚点，切号后应随之变化。
            ClickerUiKit.Text(transform, "UserLabel", $"UID {ClickerGameDataManager.UserId}", 24,
                TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(1200, 40));

            _coinLabel = ClickerUiKit.Text(transform, "CoinLabel", "", 52,
                TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0, -120), new Vector2(1200, 80));
            _levelLabel = ClickerUiKit.Text(transform, "LevelLabel", "", 30,
                TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0, -210), new Vector2(1400, 50));

            ClickButton = ClickerUiKit.Button(transform, "ClickButton", "点击赚金币", 44,
                new Vector2(0.5f, 0.5f), new Vector2(0, 40), new Vector2(460, 180));
            ClickButton.onClick.AddListener(OnClick);

            UpgradeButton = ClickerUiKit.Button(transform, "UpgradeButton", "", 30,
                new Vector2(0.5f, 0.5f), new Vector2(0, -170), new Vector2(560, 100));
            _upgradeLabel = UpgradeButton.GetComponentInChildren<TextMeshProUGUI>();
            UpgradeButton.onClick.AddListener(OnUpgrade);
            AttachRedDot(UpgradeButton, RedDotIds.Clicker.UpgradeAvailable);

            ShopButton = ClickerUiKit.Button(transform, "ShopButton", "商店", 28,
                new Vector2(0.5f, 0f), new Vector2(0, 90), new Vector2(300, 90));
            ShopButton.onClick.AddListener(OnShop);
            AttachRedDot(ShopButton, RedDotIds.Clicker.NewShop);

            _stateSub = GameEntry.Event.Subscribe(ClickerEvents.StateChanged, Refresh);
            Refresh();
        }

        private void OnClick() => _model.Click();

        private void OnUpgrade() => _model.TryUpgrade();

        private void OnShop() => OpenShopAsync().Forget();

        private async UniTask OpenShopAsync()
        {
            ClickerShopWindow window = await GameEntry.UI.OpenUIAsync<ClickerShopWindow>();
            if (window != null && GameEntry.RedDots != null && GameEntry.RedDots.IsInitialized)
            {
                GameEntry.RedDots.Acknowledge(
                    RedDotIds.Clicker.NewShop,
                    Framework.Foundation.RedDotAcknowledgeTrigger.Expose);
            }
        }

        private static void AttachRedDot(Button button, int redDotId)
        {
            var root = new GameObject("BadgeRoot", typeof(RectTransform), typeof(Image));
            root.layer = LayerMask.NameToLayer("UI");
            root.transform.SetParent(button.transform, false);
            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.one;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(-4f, -4f);
            rect.sizeDelta = new Vector2(26f, 26f);
            Image image = root.GetComponent<Image>();
            image.color = new Color(0.94f, 0.18f, 0.22f, 1f);
            image.raycastTarget = false;
            root.SetActive(false);

            bool active = button.gameObject.activeSelf;
            button.gameObject.SetActive(false); // 避免 AddComponent 后在字段尚未配置时先跑 OnEnable。
            RedDotBadge badge = button.gameObject.AddComponent<RedDotBadge>();
            badge.Configure(redDotId, root, displayMode: RedDotBadge.DisplayMode.DotOnly);
            button.gameObject.SetActive(active);
        }

        private void Refresh()
        {
            _coinLabel.text = $"金币 {_model.Coins}";
            _levelLabel.text = $"Lv.{_model.Level} {_model.LevelName}   点击+{_model.ClickGain}   挂机+{_model.IdleGainPerSec}/s"
                               + (_model.DoubleGain ? "   [双倍]" : string.Empty);
            _upgradeLabel.text = _model.IsMaxLevel ? "已满级" : $"升级  花费 {_model.UpgradeCost}";
            UpgradeButton.interactable = !_model.IsMaxLevel && _model.Coins >= _model.UpgradeCost;
        }

        private void OnDestroy()
        {
            _stateSub?.Dispose();
            _stateSub = null;
        }
    }
}
