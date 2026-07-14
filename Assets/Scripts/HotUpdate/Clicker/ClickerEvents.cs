namespace HotUpdate.Clicker
{
    /// <summary>
    /// Clicker 业务事件 ID。走框架 EventManager 的 int 重载（Publish(int)/Subscribe(int)），
    /// 无需改动框架 GameMessage 枚举——业务事件段取 20000+，避开框架 EventDefine 的 10000 段。
    /// </summary>
    internal static class ClickerEvents
    {
        /// <summary>玩法状态（金币/等级/收益）变化，UI 订阅后刷新。</summary>
        internal const int StateChanged = 20001;
    }
}
