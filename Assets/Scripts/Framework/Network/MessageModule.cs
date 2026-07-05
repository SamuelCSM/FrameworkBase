namespace Framework.Network
{
    /// <summary>
    /// 协议主 ID 常量。
    /// </summary>
    public static class MessageModule
    {
        /// <summary>系统协议，当前用于心跳：009_001 请求 / 009_001 响应。</summary>
        public const byte System = 9;

        /// <summary>登录/注册协议：001_xxx。</summary>
        public const byte Login = 1;

        /// <summary>玩家档案协议：002_xxx。</summary>
        public const byte Player = 2;

        /// <summary>匹配/房间协议：004_xxx。</summary>
        public const byte MatchRoom = 4;

        /// <summary>对局协议：005_xxx。</summary>
        public const byte Battle = 5;

        /// <summary>社交/好友协议：006_xxx。</summary>
        public const byte Social = 6;

        /// <summary>聊天协议：007_xxx。</summary>
        public const byte Chat = 7;

        /// <summary>观战/回放协议：008_xxx。</summary>
        public const byte SpectateReplay = 8;
    }
}
