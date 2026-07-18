using System;
using Framework.Security;
using Framework.Serialization;

namespace Framework.Core.Auth
{
    /// <summary>
    /// 会话持久化落点：把登录会话（用户身份 + 会话令牌 + 登录方式）写入
    /// <see cref="Framework.Security.SecureStorage"/>，用于「跨重启静默重登」（冷启动恢复）。
    /// <para>
    /// 安全边界：<b>明文密码从不持久化</b>——账号模式重启后若令牌失效必须回登录界面重新输入。
    /// 令牌等敏感串刻意落 <see cref="Framework.Security.SecureStorage"/>（设备密钥 AES+HMAC）而非普通存档
    /// （<see cref="Framework.Save.SaveManager"/> 是账号数据、非机密保险箱）。
    /// </para>
    /// </summary>
    internal static class AuthSessionStore
    {
        /// <summary>会话持久化键（框架保留命名空间，避免与业务键冲突）。</summary>
        private const string StorageKey = "framework.auth.session";

        /// <summary>持久化会话记录（JSON 落 SecureStorage）。Mode 用 int 存以规避枚举序列化边界。</summary>
        [Serializable]
        internal sealed class AuthSessionRecord
        {
            public int Mode;
            public string UserId;
            public string SessionToken;

            /// <summary>令牌过期时刻（Unix 毫秒；0 = 未提供）。旧记录缺该字段时反序列化为 0，行为不变。</summary>
            public long ExpiresAtMs;

            public string Account;
        }

        /// <summary>用登录结果构造持久化记录（游客不落账号名）。</summary>
        internal static AuthSessionRecord BuildRecord(LoginMode mode, LoginResult result, string account)
        {
            return new AuthSessionRecord
            {
                Mode = (int)mode,
                UserId = result.UserId ?? string.Empty,
                SessionToken = result.SessionToken ?? string.Empty,
                ExpiresAtMs = result.SessionTokenExpiresAtMs,
                Account = mode == LoginMode.Account ? (account ?? string.Empty) : string.Empty,
            };
        }

        /// <summary>写入会话；无有效 UserId 时忽略。任何 IO/序列化异常折算为「未持久化」，不打断登录。</summary>
        internal static void Save(AuthSessionRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.UserId))
                return;

            try
            {
                string json = JsonSerializers.Shared.ToJson(record);
                SecureStorage.Shared.Set(StorageKey, json);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[AuthSessionStore] 会话持久化失败: {ex.Message}");
            }
        }

        /// <summary>读取会话；不存在/损坏/被篡改/无有效 UserId 时返回 false。</summary>
        internal static bool TryLoad(out AuthSessionRecord record)
        {
            record = null;
            if (!SecureStorage.Shared.TryGet(StorageKey, out string json) || string.IsNullOrEmpty(json))
                return false;

            if (!JsonSerializers.Shared.TryFromJson(json, out AuthSessionRecord parsed) ||
                parsed == null || string.IsNullOrEmpty(parsed.UserId))
                return false;

            record = parsed;
            return true;
        }

        /// <summary>清除持久化会话（登出 / 令牌被服务端拒绝时）。</summary>
        internal static void Clear()
        {
            SecureStorage.Shared.Delete(StorageKey);
        }
    }
}
