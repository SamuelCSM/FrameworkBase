using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework.Notifications
{
    /// <summary>
    /// 本地通知的平台后端抽象。主干只定接口 + 日志兜底，原生实现
    /// （Unity Mobile Notifications / 厂商通道）进扩展包，经
    /// <see cref="LocalNotifications.SetBackend"/> 注入（与 ICrashBackend / ISdkProvider 同款模式）。
    /// </summary>
    public interface ILocalNotificationBackend
    {
        /// <summary>把结算好的清单交给系统排程（调用方保证已按时间升序且未过期）。</summary>
        void ScheduleAll(IReadOnlyList<LocalNotificationRequest> requests);

        /// <summary>取消全部未触发排程并清掉通知栏里已投递的本应用通知。</summary>
        void CancelAll();
    }

    /// <summary>默认兜底：只打日志不真排程（Editor / 未接原生扩展包时可观察行为）。</summary>
    public sealed class NullLocalNotificationBackend : ILocalNotificationBackend
    {
        public void ScheduleAll(IReadOnlyList<LocalNotificationRequest> requests)
        {
            Debug.Log($"[LocalNotifications] （日志兜底）排程 {requests.Count} 条：" +
                      (requests.Count > 0 ? $"最早 [{requests[0].Id}] {requests[0].FireAt:yyyy-MM-dd HH:mm zzz}" : "空"));
        }

        public void CancelAll()
        {
            Debug.Log("[LocalNotifications] （日志兜底）取消全部本地通知。");
        }
    }

    /// <summary>
    /// 本地通知门面：业务向 <see cref="Planner"/> 注册/注销提醒，本类接管"何时交给系统"：
    /// 切后台/退出时结算排程，回前台时全部取消（玩家人在游戏里，通知栏还挂着"回来玩"是低级错误）。
    /// 生命周期驱动由 <see cref="LocalNotificationRelay"/>（GameEntry 自动挂载）触发。
    /// <para>推送权限申请不在本模块——那是渠道能力，走 <c>ISdkPushService.RequestPermissionAsync</c>；
    /// 未授权时系统静默丢弃排程，不影响本模块调用安全。</para>
    /// </summary>
    public static class LocalNotifications
    {
        private static ILocalNotificationBackend _backend = new NullLocalNotificationBackend();

        /// <summary>注册表与结算策略（免打扰时段、条数上限见 <see cref="LocalNotificationPlanner"/>）。</summary>
        public static LocalNotificationPlanner Planner { get; } = new LocalNotificationPlanner();

        /// <summary>注入平台后端（原生扩展包在 RuntimeInitializeOnLoad 里调用）。传 null 恢复日志兜底。</summary>
        public static void SetBackend(ILocalNotificationBackend backend)
        {
            _backend = backend ?? new NullLocalNotificationBackend();
        }

        /// <summary>
        /// 应用暂停/恢复接线。暂停（含退出）：先清旧排程再按当前注册表重排——排程永远反映
        /// 最新游戏状态，不残留已失效的提醒；恢复：全部取消。
        /// 后端异常隔离：排程失败不该把暂停路径炸穿（暂停回调里抛异常平台行为未定义）。
        /// </summary>
        public static void HandleAppPause(bool paused)
        {
            try
            {
                _backend.CancelAll();
                if (paused)
                    _backend.ScheduleAll(Planner.BuildPlan(DateTimeOffset.Now)); // banned-api-allow: local-time 免打扰时段按玩家本地时钟
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LocalNotifications] 后端排程异常（已隔离）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 生命周期接线组件：把 OnApplicationPause / OnApplicationQuit 转交
    /// <see cref="LocalNotifications.HandleAppPause"/>。由 GameEntry 自动挂载（Inspector 可关）。
    /// </summary>
    public class LocalNotificationRelay : MonoBehaviour
    {
        private void OnApplicationPause(bool paused)
        {
            LocalNotifications.HandleAppPause(paused);
        }

        private void OnApplicationQuit()
        {
            // 被杀进程前最后一次排程机会（OnApplicationPause 在部分平台的杀进程路径上不可靠）
            LocalNotifications.HandleAppPause(paused: true);
        }
    }
}
