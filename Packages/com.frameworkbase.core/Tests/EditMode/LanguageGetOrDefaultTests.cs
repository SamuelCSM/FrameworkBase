using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// <see cref="Language.GetOrDefault"/> 单测：配表未加载 / 缺 key 时回退源语言默认值、
    /// 缺 key 不把 key 吐给玩家、null 兜底安全、伪本地化下兜底仍带界标（区分「已接入本地化」与「写死」）。
    ///
    /// 说明：EditMode 无 GameEntry.RefData，language 表不可达，TryGet 恒失败 → 恰好覆盖
    /// 「启动早期配表尚未加载」这条真实路径：此时必须出可读兜底文案，绝不显示 key 或抛异常。
    /// </summary>
    public class LanguageGetOrDefaultTests
    {
        [TearDown]
        public void ResetPseudo()
        {
            // 伪本地化是静态开关，测试改动后必须复位，避免污染其它测试。
            PseudoLocalizer.Enabled = false;
        }

        [Test]
        public void 缺key且伪本地化关_返回内联源语言默认值()
        {
            PseudoLocalizer.Enabled = false;
            string result = Language.GetOrDefault("#1_launch_reading_version_nonexistent", "正在读取版本信息...");
            Assert.AreEqual("正在读取版本信息...", result);
        }

        [Test]
        public void 缺key_绝不把key吐给玩家()
        {
            PseudoLocalizer.Enabled = false;
            const string key = "#1_launch_definitely_missing_key";
            string result = Language.GetOrDefault(key, "兜底文案");
            StringAssert.DoesNotContain(key, result, "缺 key 必须回退兜底文案，绝不把原始 key 显示给玩家");
        }

        [Test]
        public void null兜底_返回空串不抛异常()
        {
            PseudoLocalizer.Enabled = false;
            Assert.AreEqual(string.Empty, Language.GetOrDefault("#1_x_missing", null));
        }

        [Test]
        public void 伪本地化开_兜底文案仍被界标包裹()
        {
            PseudoLocalizer.Enabled = true;
            string result = Language.GetOrDefault("#1_login_authenticating_missing", "正在登录...");

            StringAssert.StartsWith("⟦", result, "已走本地化通道的兜底文案必须带 ⟦ 界标，与写死文本区分");
            StringAssert.EndsWith("⟧", result);
            StringAssert.Contains("正在登录", result, "中文兜底内容保留，只加界标与填充");
        }
    }
}
