using Framework.Core.Auth;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 开发期登录调试工具。
    ///
    /// 背景：登录成功后会话经 <see cref="AuthSessionStore"/> 持久化（SecureStorage），
    /// 下次 Play 冷启动静默重登、直接跳过登录界面——这是「记住登录」的正确行为，
    /// 但开发期反复调登录 UI / 登录链路时需要一键回到"未登录"状态。
    /// </summary>
    public static class DevAuthTools
    {
        [MenuItem("Template/Clear Persisted Login Session (清除本机登录会话)")]
        public static void ClearPersistedSession()
        {
            AuthSessionStore.Clear();

            if (Application.isPlaying)
            {
                // Play 中清除只影响磁盘持久化；本次会话的内存态（已登录身份）不受影响，
                // 需退出 Play 再进才会走回登录界面。
                Debug.Log("[DevAuthTools] 已清除持久化登录会话（内存会话不受影响，重进 Play 后回到登录界面）");
            }
            else
            {
                Debug.Log("[DevAuthTools] 已清除持久化登录会话，下次 Play 将回到登录界面");
            }
        }
    }
}
