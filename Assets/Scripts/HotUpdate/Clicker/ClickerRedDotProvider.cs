using System;
using System.Collections.Generic;
using Framework.Core;
using Framework.Foundation;
using HotUpdate.RedDot.Generated;

namespace HotUpdate.Clicker
{
    /// <summary>Clicker 模块唯一红点事实来源；只读取已初始化的 Model，不主动发网络请求。</summary>
    public sealed class ClickerRedDotProvider : IRedDotProvider, IReactiveRedDotProvider
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
                _model.CanUpgrade);

            // 新商店内容当前版本存在；是否已曝光由 SeenPolicy.Version + LocalAccount 记录门控。
            buffer.SetBool(RedDotIds.Clicker.NewShop, true);
        }

        /// <summary>
        /// 正常运行只监听会改变红点语义的领域事件，并精确更新对应 Signal；
        /// 不再因每次金币数值变化而重建 Clicker 的完整 Provider 快照。
        /// </summary>
        public IDisposable Bind(IRedDotWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (GameEntry.Event == null)
                throw new InvalidOperationException("EventManager 尚未初始化，无法绑定 Clicker 红点事件。");

            var bindings = new RedDotBindingGroup();
            bindings.Add(GameEntry.Event.Subscribe(
                ClickerEvents.UpgradeAvailabilityChanged,
                () => writer.SetBool(RedDotIds.Clicker.UpgradeAvailable, _model.CanUpgrade)));
            return bindings;
        }
    }
}
