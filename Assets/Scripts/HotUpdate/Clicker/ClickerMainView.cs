using System;
using Framework;
using Framework.Core;
using Framework.UI;
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

        public static ClickerMainView Create(ClickerModel model, string userId)
        {
            Transform parent = GameEntry.UI.GetLayerRoot(UILayer.Normal);
            var go = new GameObject("ClickerMainView", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<ClickerMainView>();
            view.Build(model, userId);
            return view;
        }

        private void Build(ClickerModel model, string userId)
        {
            _model = model;
            ClickerUiKit.Stretch(gameObject);
            ClickerUiKit.Image(transform, "BG", new Color(0.10f, 0.11f, 0.15f, 1f), stretch: true);

            // 玩家 ID 常驻显示：验证组合根身份贯通（存档按 uid 隔离）的可视锚点，切号后应随之变化。
            ClickerUiKit.Text(transform, "UserLabel", $"UID {userId}", 24,
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

            ShopButton = ClickerUiKit.Button(transform, "ShopButton", "商店", 28,
                new Vector2(0.5f, 0f), new Vector2(0, 90), new Vector2(300, 90));
            ShopButton.onClick.AddListener(OnShop);

            _stateSub = GameEntry.Event.Subscribe(ClickerEvents.StateChanged, Refresh);
            Refresh();
        }

        private void OnClick() => _model.Click();

        private void OnUpgrade() => _model.TryUpgrade();

        private void OnShop() => ClickerShopView.Open(_model);

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
