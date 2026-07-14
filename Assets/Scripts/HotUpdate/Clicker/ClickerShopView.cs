using Framework;
using Framework.Core;
using Framework.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// 商店弹窗（代码构建，挂在 UILayer.Popup，渲染于主界面之上）。
    /// 存在意义：验证 UI 分层（Popup &gt; Normal）与二级窗口开/关/回主界面的栈行为。
    /// </summary>
    public class ClickerShopView : MonoBehaviour
    {
        private ClickerModel _model;

        public Button BuyButton { get; private set; }
        public Button CloseButton { get; private set; }

        public static ClickerShopView Open(ClickerModel model)
        {
            Transform parent = GameEntry.UI.GetLayerRoot(UILayer.Popup);
            var go = new GameObject("ClickerShopView", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<ClickerShopView>();
            view.Build(model);
            return view;
        }

        private void Build(ClickerModel model)
        {
            _model = model;
            ClickerUiKit.Stretch(gameObject);
            // 半透明遮罩（吃点击，避免穿透到主界面）
            var dim = ClickerUiKit.Image(transform, "Dim", new Color(0f, 0f, 0f, 0.6f), stretch: true);
            dim.raycastTarget = true;

            Transform panel = ClickerUiKit.Panel(transform, "Panel", new Vector2(720, 460));
            ClickerUiKit.Text(panel, "Title", "商店", 40, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0, -50), new Vector2(600, 70), pivot: new Vector2(0.5f, 1f));

            BuyButton = ClickerUiKit.Button(panel, "BuyButton", "领 +100 金币", 30,
                new Vector2(0.5f, 0.5f), new Vector2(0, 20), new Vector2(440, 110));
            BuyButton.onClick.AddListener(() => _model.GrantCoins(100));

            CloseButton = ClickerUiKit.Button(panel, "CloseButton", "关闭", 28,
                new Vector2(0.5f, 0f), new Vector2(0, 60), new Vector2(260, 90));
            CloseButton.onClick.AddListener(Close);
        }

        private void Close() => Destroy(gameObject);
    }
}
