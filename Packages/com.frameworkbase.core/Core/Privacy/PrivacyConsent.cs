using UnityEngine;

namespace Framework.Core.Privacy
{
    /// <summary>
    /// 隐私协议同意状态管理（版本化）。
    ///
    /// 合规要点：同意是**对某个版本的协议**给出的——协议改版后旧同意失效，
    /// 必须重新征得。因此存的不是布尔而是"已同意的协议版本号"，
    /// 业务把当前协议版本传入 <see cref="IsAccepted"/> 判定。
    ///
    /// 典型接线（合规市场）：
    /// <code>
    /// const int PolicyVersion = 3; // 协议改版时 +1
    /// if (!PrivacyConsent.IsAccepted(PolicyVersion))
    /// {
    ///     GameEntry.Analytics.CollectionEnabled = false;   // 同意前数据不出设备
    ///     bool ok = await ShowPrivacyDialogAsync();        // 业务弹窗（或走 Sdk.Privacy）
    ///     if (ok) { PrivacyConsent.Accept(PolicyVersion); GameEntry.Analytics.CollectionEnabled = true; }
    /// }
    /// </code>
    /// </summary>
    public static class PrivacyConsent
    {
        private const string PrefKey = "Privacy.AcceptedPolicyVersion";

        /// <summary>已同意的协议版本号（0 = 从未同意）。</summary>
        public static int AcceptedPolicyVersion
            => PlayerPrefs.GetInt(PrefKey, 0);

        /// <summary>当前协议版本是否已被同意（协议改版后旧同意失效）。</summary>
        public static bool IsAccepted(int currentPolicyVersion)
        {
            return AcceptedPolicyVersion >= currentPolicyVersion && currentPolicyVersion > 0;
        }

        /// <summary>记录同意（传当前协议版本号），并广播 <see cref="GameMessage.PrivacyConsentChanged"/>。</summary>
        public static void Accept(int policyVersion)
        {
            if (policyVersion <= 0)
            {
                GameLog.Error($"[PrivacyConsent] 协议版本号必须为正数，收到 {policyVersion}，忽略");
                return;
            }

            PlayerPrefs.SetInt(PrefKey, policyVersion);
            PlayerPrefs.Save();
            GameLog.Log($"[PrivacyConsent] 已记录同意 协议版本={policyVersion}");
            GameEntry.Event?.Publish(GameMessage.PrivacyConsentChanged, policyVersion);
        }

        /// <summary>撤回同意（用户在设置里撤回，或 RTBF 抹除后）。业务应同步关闭采集闸门。</summary>
        public static void Revoke()
        {
            PlayerPrefs.DeleteKey(PrefKey);
            PlayerPrefs.Save();
            GameLog.Log("[PrivacyConsent] 同意已撤回");
            GameEntry.Event?.Publish(GameMessage.PrivacyConsentChanged, 0);
        }
    }
}
