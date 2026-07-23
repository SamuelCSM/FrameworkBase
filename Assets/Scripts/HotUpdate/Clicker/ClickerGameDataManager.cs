using System;
using Cysharp.Threading.Tasks;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 会话级游戏数据入口。统一拥有 Model 与玩家身份的初始化、访问和释放，
    /// UI、红点和业务入口不再各自保存或传递不同的数据实例。
    /// </summary>
    public static class ClickerGameDataManager
    {
        private static ClickerModel _model;

        public static bool IsInitialized => _model != null;
        public static string UserId { get; private set; } = string.Empty;

        public static ClickerModel Model => _model ?? throw new InvalidOperationException(
            "ClickerGameDataManager 尚未初始化，必须在登录后的业务入口完成 InitializeAsync。");

        public static async UniTask InitializeAsync(string userId)
        {
            Shutdown();

            var model = new ClickerModel();
            await model.InitAsync();
            UserId = userId ?? string.Empty;
            _model = model;
        }

        public static bool TryGetModel(out ClickerModel model)
        {
            model = _model;
            return model != null;
        }

        /// <summary>保存并释放当前账号的数据会话；重复调用安全。</summary>
        public static void Shutdown()
        {
            ClickerModel model = _model;
            _model = null;
            UserId = string.Empty;
            model?.Dispose();
        }
    }
}
