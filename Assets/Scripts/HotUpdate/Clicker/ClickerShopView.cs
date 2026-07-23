using Framework;
using HotUpdate.UI.Generated;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 商店的视图引用容器。窗口行为由 <see cref="ClickerShopWindow"/> 驱动，
    /// 创建与销毁由 UIManager 统一管理。
    /// </summary>
    public sealed class ClickerShopView : UIView
    {
        public Button BuyButton { get; internal set; }
        public Button CloseButton { get; internal set; }
    }

    /// <summary>纯代码 UI 工厂：只负责构建视图层级，不持有业务状态或窗口生命周期。</summary>
    internal static class ClickerShopViewFactory
    {
        internal static GameObject Create(Transform parent)
        {
            var root = new GameObject("ClickerShopView", typeof(RectTransform));
            root.layer = LayerMask.NameToLayer("UI");
            root.transform.SetParent(parent, false);
            ClickerUiKit.Stretch(root);

            var view = root.AddComponent<ClickerShopView>();
            Transform panel = ClickerUiKit.Panel(root.transform, "Panel", new Vector2(720, 460));
            ClickerUiKit.Text(panel, "Title", "商店", 40, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0, -50), new Vector2(600, 70),
                pivot: new Vector2(0.5f, 1f));

            view.BuyButton = ClickerUiKit.Button(panel, "BuyButton", "领 +100 金币", 30,
                new Vector2(0.5f, 0.5f), new Vector2(0, 20), new Vector2(440, 110));
            view.CloseButton = ClickerUiKit.Button(panel, "CloseButton", "关闭", 28,
                new Vector2(0.5f, 0f), new Vector2(0, 60), new Vector2(260, 90));
            view.BuyButton.gameObject.AddComponent<UITargetAnchor>().Configure(
                UITargetIds.Clicker.ShopBuyButton,
                view.BuyButton.transform as RectTransform,
                view.BuyButton);
            view.CloseButton.gameObject.AddComponent<UITargetAnchor>().Configure(
                UITargetIds.Clicker.ShopCloseButton,
                view.CloseButton.transform as RectTransform,
                view.CloseButton);
            return root;
        }
    }
}
