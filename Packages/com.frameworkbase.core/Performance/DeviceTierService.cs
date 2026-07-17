using UnityEngine;

namespace Framework.Performance
{
    /// <summary>
    /// 设备分级服务：启动时按 <see cref="DeviceTierClassifier"/> 把本机分为低/中/高三档，
    /// 映射到项目的 Quality Level（低端→最低档、高端→最高档、中端→中间档），
    /// 并把档位暴露给业务（粒子密度、分辨率缩放、可选特效开关等按档取值）。
    ///
    /// 玩家手动选画质走 <see cref="SetOverride"/>：持久化到 PlayerPrefs，此后自动分级只算不用，
    /// 传 null 清除覆盖回到自动档。由 GameEntry 在启动早期调用 <see cref="Initialize"/>（Inspector 可关
    /// 画质映射，只分级不动 QualitySettings）。
    /// </summary>
    public static class DeviceTierService
    {
        private const string OverrideKey = "framework.device_tier_override";

        /// <summary>是否已完成分级（未初始化时 <see cref="Tier"/> 恒为 Mid）。</summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>当前生效档位（玩家覆盖优先，其次自动分级）。</summary>
        public static DeviceTier Tier { get; private set; } = DeviceTier.Mid;

        /// <summary>自动分级结果（不含玩家覆盖），设置界面可展示"推荐档位"。</summary>
        public static DeviceTier AutoTier { get; private set; } = DeviceTier.Mid;

        /// <summary>是否存在玩家手动覆盖。</summary>
        public static bool HasOverride => PlayerPrefs.GetInt(OverrideKey, -1) >= 0;

        /// <summary>
        /// 读取本机画像并分级。幂等，可重复调用（阈值调整后重算）。
        /// </summary>
        /// <param name="applyQuality">是否把档位映射到 QualitySettings。</param>
        /// <param name="thresholds">项目自定义阈值，null 用默认。</param>
        public static void Initialize(bool applyQuality, DeviceTierThresholds thresholds = null)
        {
            var profile = new DeviceProfile(
                SystemInfo.systemMemorySize,
                SystemInfo.graphicsMemorySize,
                SystemInfo.processorCount);

            AutoTier = DeviceTierClassifier.Classify(profile, thresholds);

            int overrideValue = PlayerPrefs.GetInt(OverrideKey, -1);
            Tier = overrideValue >= (int)DeviceTier.Low && overrideValue <= (int)DeviceTier.High
                ? (DeviceTier)overrideValue
                : AutoTier;
            IsInitialized = true;

            Debug.Log($"[DeviceTier] 内存 {profile.SystemMemoryMb}MB 显存 {profile.GraphicsMemoryMb}MB " +
                      $"核数 {profile.ProcessorCount} → 自动 {AutoTier}，生效 {Tier}" +
                      (HasOverride ? "（玩家覆盖）" : ""));

            if (applyQuality)
                ApplyQuality(Tier);
        }

        /// <summary>
        /// 设置玩家手动档位覆盖（持久化）；传 null 清除覆盖回到自动分级。
        /// </summary>
        public static void SetOverride(DeviceTier? tier, bool applyQuality = true)
        {
            if (tier.HasValue)
                PlayerPrefs.SetInt(OverrideKey, (int)tier.Value);
            else
                PlayerPrefs.DeleteKey(OverrideKey);
            PlayerPrefs.Save();

            Tier = tier ?? AutoTier;
            if (applyQuality)
                ApplyQuality(Tier);
        }

        /// <summary>
        /// 档位映射 Quality Level：低端→0、高端→最高、中端→中间档（向下取整）。
        /// 项目的 Quality Levels 须按从低到高排布（Unity 默认约定）。
        /// </summary>
        public static void ApplyQuality(DeviceTier tier)
        {
            int count = QualitySettings.names.Length;
            if (count <= 0)
                return;

            int index = tier switch
            {
                DeviceTier.Low => 0,
                DeviceTier.High => count - 1,
                _ => count / 2,
            };

            // applyExpensiveChanges：启动期一次性调用，抗锯齿等重开销变更此时最便宜
            QualitySettings.SetQualityLevel(index, applyExpensiveChanges: true);
            Debug.Log($"[DeviceTier] QualityLevel → {index}（{QualitySettings.names[index]}）");
        }
    }
}
