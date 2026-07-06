namespace Framework.Save
{
    /// <summary>
    /// 玩家轻量本地设置的 PlayerPrefs Key 常量表。
    /// 所有 Key 集中在此处定义，避免魔法字符串散落各处。
    ///
    /// 使用示例：
    ///   // 写
    ///   SaveManager.Instance.SetPref(PlayerSettings.MusicOn, false);
    ///   SaveManager.Instance.SetPref(PlayerSettings.MusicVolume, 0.8f);
    ///   SaveManager.Instance.SetPref(PlayerSettings.Language, "zh-CN");
    ///
    ///   // 读
    ///   bool  musicOn = SaveManager.Instance.GetPref(PlayerSettings.MusicOn, defaultValue: true);
    ///   float vol     = SaveManager.Instance.GetPref(PlayerSettings.MusicVolume, defaultValue: 1f);
    ///   string lang   = SaveManager.Instance.GetPref(PlayerSettings.Language, defaultValue: "zh-CN");
    ///
    /// 规范：
    ///   - Key 命名：pref_{模块}_{含义}，全小写 + 下划线
    ///   - 只存 primitive 类型（int / float / string / bool）
    ///   - 游戏存档数据（角色信息、道具、进度）请用 SaveManager.SaveAsync，不要放 PlayerPrefs
    /// </summary>
    public static class PlayerSettings
    {
        // ── 音频 ─────────────────────────────────────────────────────────────
        /// <summary>背景音乐开关（bool）</summary>
        public const string MusicOn     = "pref_audio_music_on";
        /// <summary>音效开关（bool）</summary>
        public const string SfxOn       = "pref_audio_sfx_on";
        /// <summary>背景音乐音量（float 0..1）</summary>
        public const string MusicVolume = "pref_audio_music_vol";
        /// <summary>音效音量（float 0..1）</summary>
        public const string SfxVolume   = "pref_audio_sfx_vol";

        // ── 语言 ─────────────────────────────────────────────────────────────
        /// <summary>当前语言（string）  "zh-CN" | "en-US" | "ja-JP" ...</summary>
        public const string Language = "pref_i18n_language";

        // ── 图形 ─────────────────────────────────────────────────────────────
        /// <summary>画质档次（int）  0=Low  1=Medium  2=High</summary>
        public const string GraphicsQuality = "pref_gfx_quality";
        /// <summary>帧率上限（int）  30 | 60 | 120</summary>
        public const string FrameRateCap    = "pref_gfx_fps_cap";

        // ── 游戏体验 ──────────────────────────────────────────────────────────
        /// <summary>是否首次启动（bool）</summary>
        public const string IsFirstLaunch = "pref_sys_first_launch";
        /// <summary>上次使用的存档槽位（int）0 / 1 / 2</summary>
        public const string LastSaveSlot  = "pref_sys_last_slot";
        /// <summary>振动开关（bool）</summary>
        public const string VibrationOn   = "pref_sys_vibration_on";

        // ── 通知 ─────────────────────────────────────────────────────────────
        /// <summary>推送通知开关（bool）</summary>
        public const string PushNotification = "pref_notify_push_on";
    }
}
