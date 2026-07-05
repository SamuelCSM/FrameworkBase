namespace Framework
{
    /// <summary>
    /// 游戏内通用消息 ID 表。
    /// 所有业务广播消息必须在这里统一占号，避免不同模块私自定义重复 ID。
    /// </summary>
    public enum GameMessage
    {
        // ==================== Framework 系统消息：9000-9999 ====================

        /// <summary>语言切换，参数：string language。</summary>
        LanguageChanged = 9005,

        // ==================== 玩家与登录消息：10000-10999 ====================

        /// <summary>玩家登录成功。</summary>
        PlayerLoginSuccess = 10001,

        /// <summary>玩家登录失败，参数：string errorCode。</summary>
        PlayerLoginFailed = 10002,

        /// <summary>玩家登出。</summary>
        PlayerLogout = 10003,

        /// <summary>玩家档案刷新，参数：PlayerProfileData profile。</summary>
        PlayerProfileChanged = 10004,

        // ==================== Blokus 消息：20000-20999 ====================
        // 后续 Blokus 业务广播从这里开始追加，例如 RoomUpdated、MatchStateChanged。

        /// <summary>好友列表数据变化（全量刷新或在线态变更），无参数；订阅方从 BlokusRuntime.Social.Friends 读取最新缓存。</summary>
        FriendListChanged = 20001,

        /// <summary>待处理好友申请变化（收到新申请或处理完毕），无参数；订阅方从 BlokusRuntime.Social.FriendRequests 读取最新缓存。</summary>
        FriendRequestsChanged = 20002,

        /// <summary>好友房间状态变化（建房/加入/席位/就绪/模式/房主变更），无参数；订阅方从 BlokusRuntime.Room.State 读取最新快照。</summary>
        RoomStateChanged = 20003,

        /// <summary>个人对局积分结算到达（服务端 002_007 推送），无参数；订阅方从 BlokusRuntime.Player.LatestRatingSettlement 读取最新结果。</summary>
        BattleRatingSettled = 20004,

        /// <summary>私聊会话列表变化（会话新增/最后一条更新/未读增减），无参数；订阅方从 BlokusRuntime.Chat.Conversations 读取最新缓存（会话列表与红点）。</summary>
        ChatConversationsChanged = 20005,
    }
}
