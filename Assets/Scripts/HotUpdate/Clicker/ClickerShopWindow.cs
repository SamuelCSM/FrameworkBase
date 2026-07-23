using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// 商店弹窗控制层。通过 UIManager 获得单实例、遮罩、动画与统一关闭语义，
    /// 业务数据统一从 <see cref="ClickerGameDataManager"/> 获取。
    /// </summary>
    public sealed class ClickerShopWindow : UIBase<ClickerShopView>
    {
        protected override UIAnimationConfig AnimConfig => UIAnimationConfig.ScalePop();

        protected override void OnOpen(object userData)
        {
            View.BuyButton.onClick.AddListener(OnBuy);
            View.CloseButton.onClick.AddListener(OnCloseClicked);
        }

        protected override void OnClose()
        {
            View.BuyButton.onClick.RemoveListener(OnBuy);
            View.CloseButton.onClick.RemoveListener(OnCloseClicked);
        }

        private static void OnBuy()
        {
            if (ClickerGameDataManager.TryGetModel(out ClickerModel model))
                model.GrantCoins(100);
        }

        private void OnCloseClicked()
        {
            GameEntry.UI.CloseUIAsync(this).Forget();
        }
    }
}
