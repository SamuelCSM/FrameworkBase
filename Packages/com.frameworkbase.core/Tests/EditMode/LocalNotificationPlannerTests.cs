using System;
using System.Collections.Generic;
using Framework.Notifications;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 本地通知排程器单测：去重覆盖、过期过滤、免打扰平移（含跨午夜）、上限裁剪、结算不动注册表。
    /// 排错时段的通知（半夜弹窗）是真实差评来源，平移规则必须准。
    /// </summary>
    public class LocalNotificationPlannerTests
    {
        // 固定基准：2026-07-17 12:00 +08:00（午间，不落任何免打扰窗口）
        private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.FromHours(8));

        [Test]
        public void 注册去重_同id覆盖_注销生效()
        {
            var planner = new LocalNotificationPlanner();

            planner.Register("energy", "旧", "旧文案", Noon.AddHours(1));
            planner.Register("energy", "新", "新文案", Noon.AddHours(2));
            planner.Register("daily", "签到", "回来签到", Noon.AddHours(3));
            Assert.AreEqual(2, planner.Count, "同 id 覆盖不新增");

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            Assert.AreEqual("新", plan[0].Title, "覆盖后以后者为准");

            Assert.IsTrue(planner.Unregister("daily"));
            Assert.IsFalse(planner.Unregister("daily"), "重复注销返回 false");
            Assert.AreEqual(1, planner.BuildPlan(Noon).Count);
        }

        [Test]
        public void 空id_直接抛()
        {
            var planner = new LocalNotificationPlanner();
            Assert.Throws<ArgumentException>(() => planner.Register("", "t", "b", Noon));
        }

        [Test]
        public void 过期项_结算时过滤_注册表保留()
        {
            var planner = new LocalNotificationPlanner();

            planner.Register("past", "已过期", "", Noon.AddMinutes(-1));
            planner.Register("future", "将来", "", Noon.AddMinutes(30));

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual("future", plan[0].Id);
            Assert.AreEqual(2, planner.Count, "结算不修改注册表——时间推进后重结算语义一致");
        }

        [Test]
        public void 结算清单_按触发时间升序()
        {
            var planner = new LocalNotificationPlanner();

            planner.Register("c", "", "", Noon.AddHours(3));
            planner.Register("a", "", "", Noon.AddHours(1));
            planner.Register("b", "", "", Noon.AddHours(2));

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            Assert.AreEqual(new[] { "a", "b", "c" },
                new[] { plan[0].Id, plan[1].Id, plan[2].Id });
        }

        [Test]
        public void 超出上限_保留最近的()
        {
            var planner = new LocalNotificationPlanner(maxScheduled: 2);

            planner.Register("far", "", "", Noon.AddHours(9));
            planner.Register("near", "", "", Noon.AddHours(1));
            planner.Register("mid", "", "", Noon.AddHours(5));

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual("near", plan[0].Id);
            Assert.AreEqual("mid", plan[1].Id, "裁掉的是最远的——近提醒对拉回最有效");
        }

        [Test]
        public void 免打扰_同日窗口_平移到窗口结束()
        {
            var planner = new LocalNotificationPlanner();
            planner.SetQuietHours(13, 15); // 午休 13:00~15:00

            planner.Register("in", "", "", Noon.AddHours(2));   // 14:00 落窗口内
            planner.Register("out", "", "", Noon.AddHours(4));  // 16:00 窗口外

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            Assert.AreEqual(15, plan[0].FireAt.ToOffset(Noon.Offset).Hour, "窗口内平移到 15:00");
            Assert.AreEqual("in", plan[0].Id);
            Assert.AreEqual(16, plan[1].FireAt.ToOffset(Noon.Offset).Hour, "窗口外不动");
        }

        [Test]
        public void 免打扰_跨午夜窗口_前后半夜均平移到次日早晨()
        {
            var planner = new LocalNotificationPlanner();
            planner.SetQuietHours(22, 8); // 22:00~次日 08:00

            planner.Register("late", "", "", Noon.AddHours(11));   // 23:00（前半夜）
            planner.Register("early", "", "", Noon.AddHours(18));  // 次日 06:00（后半夜）

            List<LocalNotificationRequest> plan = planner.BuildPlan(Noon);
            foreach (LocalNotificationRequest item in plan)
            {
                DateTimeOffset local = item.FireAt.ToOffset(Noon.Offset);
                Assert.AreEqual(8, local.Hour, $"[{item.Id}] 应平移到 08:00");
                Assert.AreEqual(18, local.Day, $"[{item.Id}] 应是次日（7-18）");
            }
        }

        [Test]
        public void 免打扰_窗口内已错过的时刻_平移后仍有效()
        {
            // 平移先于过期过滤：22:10 在 now(23:00) 之前，但它落在免打扰窗口内，
            // 平移到次日 08:00 后回到将来——次日早 8 点补提醒是符合预期的行为，这里锁死该语义。
            var night = new DateTimeOffset(2026, 7, 17, 23, 0, 0, TimeSpan.FromHours(8));
            var planner = new LocalNotificationPlanner();
            planner.SetQuietHours(22, 8);

            planner.Register("missed", "", "", night.AddMinutes(-50)); // 22:10
            List<LocalNotificationRequest> plan = planner.BuildPlan(night);

            Assert.AreEqual(1, plan.Count, "窗口内已错过的时刻平移到窗口结束后仍有效");
            Assert.AreEqual(8, plan[0].FireAt.ToOffset(night.Offset).Hour);
        }

        [Test]
        public void 免打扰关闭_不平移()
        {
            var planner = new LocalNotificationPlanner();
            planner.SetQuietHours(6, 6); // start == end 视为关闭

            planner.Register("n", "", "", Noon.AddHours(18)); // 次日 06:00
            Assert.AreEqual(6, planner.BuildPlan(Noon)[0].FireAt.ToOffset(Noon.Offset).Hour);
        }

        [Test]
        public void 非法免打扰参数_直接抛()
        {
            var planner = new LocalNotificationPlanner();
            Assert.Throws<ArgumentOutOfRangeException>(() => planner.SetQuietHours(24, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => planner.SetQuietHours(0, -1));
        }
    }
}
