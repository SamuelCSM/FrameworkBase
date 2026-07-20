using System.Collections.Generic;
using Framework.Foundation;
using HotUpdate.RedDot.Generated;

namespace HotUpdate.Clicker
{
    /// <summary>Clicker 模块唯一红点事实来源；只读取已初始化的 Model，不主动发网络请求。</summary>
    public sealed class ClickerRedDotProvider : IRedDotProvider
    {
        private static readonly int[] Signals =
        {
            RedDotIds.Clicker.UpgradeAvailable,
            RedDotIds.Clicker.NewShop,
        };

        private readonly ClickerModel _model;

        public ClickerRedDotProvider(ClickerModel model)
        {
            _model = model;
        }

        public string Owner => "Clicker";
        public IReadOnlyCollection<int> OwnedSignalIds => Signals;
        public bool IsReady => _model != null;

        public void Collect(RedDotUpdateBuffer buffer)
        {
            buffer.SetBool(
                RedDotIds.Clicker.UpgradeAvailable,
                !_model.IsMaxLevel && _model.Coins >= _model.UpgradeCost);

            // 新商店内容当前版本存在；是否已曝光由 SeenPolicy.Version + LocalAccount 记录门控。
            buffer.SetBool(RedDotIds.Clicker.NewShop, true);
        }
    }
}
