using System;
using System.Collections.Generic;

namespace Framework.Notifications
{
    /// <summary>单条本地通知请求（纯数据）。时间一律带偏移的绝对时间，杜绝时区歧义。</summary>
    public struct LocalNotificationRequest
    {
        /// <summary>业务唯一 id（如 "energy_full" / "daily_reward"）。同 id 重复注册后者覆盖前者。</summary>
        public string Id;

        public string Title;
        public string Body;

        /// <summary>触发时刻（绝对时间）。</summary>
        public DateTimeOffset FireAt;
    }

    /// <summary>
    /// 本地通知排程器（纯逻辑，可单测）：业务在游玩期间随时注册/注销"将来要提醒的事"
    /// （体力满、签到重置、活动开抢），<see cref="BuildPlan"/> 在切后台时把注册表结算成
    /// 真正交给系统的排程清单：
    /// <list type="bullet">
    /// <item>过滤已过期项（触发时刻不晚于当前时刻的不再提醒）；</item>
    /// <item>免打扰时段平移：落在免打扰窗口内的通知推迟到窗口结束（半夜弹"体力满了"
    ///   是差评来源，宁可晚提醒不可吵醒）；</item>
    /// <item>按触发时间升序、裁剪到条数上限（iOS 待触发上限 64，超限静默丢弃后面的），
    ///   保留最近的——越近的提醒对拉回越有效。</item>
    /// </list>
    /// 注册表与结算分离：BuildPlan 不修改注册状态，切后台可反复结算。
    /// </summary>
    public sealed class LocalNotificationPlanner
    {
        private readonly Dictionary<string, LocalNotificationRequest> _registered =
            new Dictionary<string, LocalNotificationRequest>();

        private readonly int _maxScheduled;
        private int _quietStartHour;
        private int _quietEndHour;
        private bool _quietEnabled;

        /// <summary>当前注册条数（含可能已过期的，过期在结算时过滤）。</summary>
        public int Count => _registered.Count;

        /// <param name="maxScheduled">单次结算交给系统的条数上限（iOS 系统上限 64，默认留余量）。</param>
        public LocalNotificationPlanner(int maxScheduled = 50)
        {
            _maxScheduled = Math.Max(1, maxScheduled);
        }

        /// <summary>注册一条通知。同 id 覆盖；id 为空属接线错误直接抛。</summary>
        public void Register(string id, string title, string body, DateTimeOffset fireAt)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("本地通知 id 不能为空——同 id 覆盖语义依赖它", nameof(id));

            _registered[id] = new LocalNotificationRequest
            {
                Id = id,
                Title = title ?? string.Empty,
                Body = body ?? string.Empty,
                FireAt = fireAt,
            };
        }

        /// <summary>注销一条通知（如体力被消耗后"体力满"不再成立）。返回是否存在。</summary>
        public bool Unregister(string id)
        {
            return !string.IsNullOrEmpty(id) && _registered.Remove(id);
        }

        /// <summary>清空注册表（登出/切账号）。</summary>
        public void Clear()
        {
            _registered.Clear();
        }

        /// <summary>
        /// 设置免打扰时段（小时，[start, end)，支持跨午夜如 22→8）。start == end 视为关闭。
        /// 判定使用触发时刻在 <see cref="BuildPlan"/> 传入 now 的时区偏移下的本地小时。
        /// </summary>
        public void SetQuietHours(int startHour, int endHour)
        {
            if (startHour < 0 || startHour > 23 || endHour < 0 || endHour > 23)
                throw new ArgumentOutOfRangeException(nameof(startHour), "免打扰小时须在 0~23");

            _quietStartHour = startHour;
            _quietEndHour = endHour;
            _quietEnabled = startHour != endHour;
        }

        /// <summary>
        /// 结算排程清单：过期过滤 → 免打扰平移 → 升序 → 截断上限。不修改注册表。
        /// </summary>
        /// <param name="now">当前时刻；其 Offset 同时定义免打扰时段的"本地"时区。</param>
        public List<LocalNotificationRequest> BuildPlan(DateTimeOffset now)
        {
            var plan = new List<LocalNotificationRequest>();

            foreach (LocalNotificationRequest request in _registered.Values)
            {
                LocalNotificationRequest item = request;
                if (_quietEnabled)
                    item.FireAt = ShiftOutOfQuietHours(item.FireAt.ToOffset(now.Offset));

                if (item.FireAt <= now)
                    continue;

                plan.Add(item);
            }

            plan.Sort((a, b) => a.FireAt.CompareTo(b.FireAt));

            if (plan.Count > _maxScheduled)
                plan.RemoveRange(_maxScheduled, plan.Count - _maxScheduled);

            return plan;
        }

        /// <summary>落在免打扰窗口内的时刻平移到窗口结束（同日或次日）。</summary>
        private DateTimeOffset ShiftOutOfQuietHours(DateTimeOffset local)
        {
            int hour = local.Hour;

            bool inQuiet = _quietStartHour < _quietEndHour
                ? hour >= _quietStartHour && hour < _quietEndHour
                : hour >= _quietStartHour || hour < _quietEndHour; // 跨午夜窗口

            if (!inQuiet)
                return local;

            DateTimeOffset windowEnd = new DateTimeOffset(
                local.Year, local.Month, local.Day, _quietEndHour, 0, 0, local.Offset);

            // 已过当日窗口结束点（跨午夜窗口的前半夜），推到次日结束点
            if (windowEnd <= local)
                windowEnd = windowEnd.AddDays(1);

            return windowEnd;
        }
    }
}
