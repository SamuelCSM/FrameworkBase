using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Serialization;
using Framework.Storage;
using UnityEngine;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 远程配置 / 功能开关客户端：不发包改配置、按设备灰度放量新功能。
    ///
    /// 取值三层回退：本次/缓存拉取值 → 代码默认值（<see cref="SetDefaults"/>）→ 调用方兜底参数。
    /// 磁盘缓存（last-known-good）：拉取成功即落盘，下次启动先用上次的值，断网也有一致行为；
    /// 拉取失败/解析失败一律保留现值，远端配置永远不能把客户端打挂。
    ///
    /// 功能开关值支持两种写法：
    ///   直接布尔        <c>"new_shop_ui": true</c>
    ///   条件对象        <c>"new_shop_ui": { "enabled": true, "rollout": 30, "min_version": "1.2.0" }</c>
    /// 条件对象按 设备稳定分桶 &lt; rollout 百分比 判定灰度命中（放量上调时已命中设备保持命中），
    /// min_version 低于当前版本时关闭（老包别开它跑不动的功能）。三个字段都可缺省，但出现即须合法：
    /// 类型不对、rollout 越界、版本号无法比较时该远端值整份作废并告警，判定退回本地基线
    /// （代码默认值 → 调用方兜底参数），绝不让"配置写错"变成"限制消失"。
    ///
    /// 后端选择：默认按 AppConfig.RemoteConfigUrl——非空用 <see cref="HttpRemoteConfigBackend"/>，
    /// 留空不拉取（只用缓存与默认值）；对接三方平台经 <see cref="SetBackend"/> 注入扩展包实现。
    /// </summary>
    public class RemoteConfigManager : FrameworkComponent<RemoteConfigManager>
    {
        /// <summary>磁盘缓存文件名（原样保存最近一次拉取成功的 JSON）。</summary>
        private const string CacheFileName = "remote_config_cache.json";

        private readonly Dictionary<string, object> _defaults = new Dictionary<string, object>();

        /// <summary>
        /// 已就其配置非法告过警的开关键。开关判定可能每帧发生，据此把同一键的告警压到每份配置一次；
        /// 激活新配置时清空，让修复后仍非法的配置能重新出声。
        /// </summary>
        private readonly HashSet<string> _warnedFlagKeys = new HashSet<string>();

        private Dictionary<string, object> _active;
        private IRemoteConfigBackend _backend;
        private bool _isFetching;
        private bool _warnedNoBackend;

        private string _cachePath;
        private string _deviceId;
        private string _appVersion;
        private string _userId = string.Empty;

        /// <summary>是否已有远端值可用（本次拉取或磁盘缓存）。</summary>
        public bool HasRemoteValues => _active != null;

        /// <summary>本次会话是否成功拉取过（false 表示当前用的是缓存/默认值）。</summary>
        public bool FetchedThisSession { get; private set; }

        public override void OnInit()
        {
            _cachePath = Path.Combine(Application.persistentDataPath, CacheFileName);
            _deviceId = SystemInfo.deviceUniqueIdentifier;
            _appVersion = Application.version;

            LoadCacheFromDisk();
            GameLog.Log($"[RemoteConfigManager] 初始化 缓存值={( _active != null ? _active.Count : 0 )} 项");
        }

        // ── 对外 API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 注册代码默认值（组合根启动早期调用一次）。默认值是断网首装的行为底线，
        /// 每个会被读取的键都应该有默认值，远端只做覆盖。
        /// </summary>
        public void SetDefaults(IReadOnlyDictionary<string, object> defaults)
        {
            if (defaults == null)
                return;
            foreach (var pair in defaults)
                _defaults[pair.Key] = pair.Value;
        }

        /// <summary>登录成功后设置用户维度（服务端按用户定向用）；登出传空。</summary>
        public void SetUserId(string userId)
        {
            _userId = userId ?? string.Empty;
        }

        /// <summary>注入自定义后端（三方平台扩展包）。应在首次拉取前调用。</summary>
        public void SetBackend(IRemoteConfigBackend backend)
        {
            if (backend == null)
            {
                GameLog.Error("[RemoteConfigManager] SetBackend 传入 null，忽略");
                return;
            }
            _backend = backend;
            GameLog.Log($"[RemoteConfigManager] 配置后端: {backend.Name}");
        }

        /// <summary>
        /// 拉取并激活远程配置。成功即整体替换激活值并写磁盘缓存；
        /// 失败（网络/解析/未配置端点）保留现值返回 false——调用方不需要也不应该重试轰炸，
        /// 下次启动或业务关键点再拉即可。
        /// </summary>
        public async UniTask<bool> FetchAndActivateAsync()
        {
            if (_isFetching)
                return false;

            IRemoteConfigBackend backend = Backend();
            if (backend == null)
                return false;

            _isFetching = true;
            try
            {
                var request = new RemoteConfigRequest
                {
                    DeviceId = _deviceId,
                    UserId = _userId,
                    AppVersion = _appVersion,
                    Channel = ChannelName(),
                    Env = AppConfig.Load() != null ? AppConfig.Load().AppEnv : string.Empty
                };

                string json = await backend.FetchAsync(request);
                if (string.IsNullOrEmpty(json))
                {
                    GameLog.Warning("[RemoteConfigManager] 拉取失败，保留现值");
                    return false;
                }

                if (!JsonObjectParser.TryParseObject(json, out var values))
                {
                    GameLog.Warning("[RemoteConfigManager] 配置 JSON 解析失败，保留现值");
                    return false;
                }

                _active = values;
                _warnedFlagKeys.Clear();
                FetchedThisSession = true;
                PersistCache(json);
                GameLog.Log($"[RemoteConfigManager] 远程配置已激活 {values.Count} 项");
                return true;
            }
            finally
            {
                _isFetching = false;
            }
        }

        /// <summary>键是否有值（激活值或默认值）。</summary>
        public bool HasKey(string key)
        {
            return TryGetValue(key, out _);
        }

        /// <summary>取原始值（激活值优先，默认值兜底）。嵌套对象为 Dictionary，数组为 List。</summary>
        public bool TryGetValue(string key, out object value)
        {
            if (_active != null && _active.TryGetValue(key, out value))
                return true;
            return _defaults.TryGetValue(key, out value);
        }

        /// <summary>取字符串（数字/布尔宽容转为不变文化文本）。</summary>
        public string GetString(string key, string defaultValue = "")
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;

            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
            return defaultValue;
        }

        /// <summary>取布尔（接受 bool 与 "true"/"false" 文本）。</summary>
        public bool GetBool(string key, bool defaultValue = false)
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;

            if (value is bool b) return b;
            if (value is string s && bool.TryParse(s, out bool parsed)) return parsed;
            return defaultValue;
        }

        /// <summary>取 int（接受整数/小数截断/数字文本）。</summary>
        public int GetInt(string key, int defaultValue = 0)
        {
            return (int)GetLong(key, defaultValue);
        }

        /// <summary>取 long（接受整数/小数截断/数字文本）。</summary>
        public long GetLong(string key, long defaultValue = 0)
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;
            return CoerceToLong(value, defaultValue);
        }

        /// <summary>取 float（接受整数/小数/数字文本）。</summary>
        public float GetFloat(string key, float defaultValue = 0f)
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;

            if (value is double d) return (float)d;
            if (value is float f) return f;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is string s &&
                float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed;
            return defaultValue;
        }

        /// <summary>
        /// 功能开关判定。布尔值直读；条件对象按 enabled / min_version / rollout 依次过滤
        /// （见类注释）。键不存在或值不可判定时返回 defaultValue。
        /// 同一设备同一键的判定结果稳定（设备分桶哈希），不会本次开下次关。
        /// <para>
        /// 条件对象字段非法（类型不对、rollout 越界、版本号无法比较）时<b>整份远端值作废</b>并告警，
        /// 判定退回本地基线：代码默认值 → 调用方兜底参数。半解析的结果一律不采信——
        /// 那正是"配置写错反而把功能全量放出去"的来源。
        /// </para>
        /// </summary>
        /// <param name="key">开关键。</param>
        /// <param name="defaultValue">无远端值也无代码默认值时的兜底判定。</param>
        /// <returns>该开关对本设备是否开启。</returns>
        public bool IsFeatureEnabled(string key, bool defaultValue = false)
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;

            if (value is bool b) return b;
            if (value is string s && bool.TryParse(s, out bool parsed)) return parsed;
            if (value is Dictionary<string, object> flag)
            {
                return TryEvaluateFlag(key, flag, out bool enabled)
                    ? enabled
                    : LocalFeatureBaseline(key, defaultValue);
            }

            return defaultValue;
        }

        /// <summary>
        /// 远端开关值作废时的本地基线：只看代码默认值，取不到再用调用方兜底参数。
        /// 刻意不走 <see cref="TryGetValue"/>——那会把正在作废的远端值又读回来。
        /// </summary>
        /// <param name="key">开关键。</param>
        /// <param name="defaultValue">调用方兜底参数。</param>
        /// <returns>本地基线判定。</returns>
        private bool LocalFeatureBaseline(string key, bool defaultValue)
        {
            if (_defaults.TryGetValue(key, out object fallback))
            {
                if (fallback is bool b) return b;
                if (fallback is string s && bool.TryParse(s, out bool parsed)) return parsed;
            }

            return defaultValue;
        }

        /// <summary>清除磁盘缓存与已激活值（测试隔离 / 合规抹除用），代码默认值保留。</summary>
        public void ClearCache()
        {
            _active = null;
            _warnedFlagKeys.Clear();
            FetchedThisSession = false;
            FileStorages.Shared.TryDeleteFile(_cachePath);
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 条件开关对象判定：enabled 关 → 关；版本不够 → 关；否则按 rollout 分桶。
        /// <para>
        /// 三个字段都是<b>可选</b>的（缺省即不构成该维度的限制），但<b>一旦出现就必须合法</b>，
        /// 否则整份判定作废（返回 false）交由调用方退回本地基线。宽容地跳过非法字段等于
        /// 让"配置写错"变成"限制消失"，这正是灰度失控的路径。
        /// </para>
        /// </summary>
        /// <param name="key">开关键，同时参与设备分桶哈希。</param>
        /// <param name="flag">条件开关对象。</param>
        /// <param name="enabled">判定成功时返回该开关对本设备是否开启。</param>
        /// <returns>字段全部合法、判定有效时返回 true。</returns>
        private bool TryEvaluateFlag(string key, Dictionary<string, object> flag, out bool enabled)
        {
            enabled = false;

            if (flag.TryGetValue("enabled", out object enabledValue))
            {
                if (!TryCoerceToBool(enabledValue, out bool isEnabled))
                    return RejectFlag(key, "enabled 不是布尔值");
                if (!isEnabled)
                    return true; // 判定有效，结果为关。
            }

            if (flag.TryGetValue("min_version", out object minVersionValue))
            {
                if (!(minVersionValue is string minVersion) || string.IsNullOrWhiteSpace(minVersion))
                    return RejectFlag(key, "min_version 不是非空字符串");
                // 用 TryCompareVersion 而非兼容入口 CompareVersion：后者在格式非法时返回 0（视作版本相等）
                // 从而放行，正是门禁代码必须避免的 fail-open。
                if (!HotUpdate.VersionManager.TryCompareVersion(_appVersion, minVersion, out int comparison))
                    return RejectFlag(key, $"min_version={minVersion} 与当前版本 {_appVersion} 无法可靠比较");
                if (comparison < 0)
                    return true; // 判定有效，老包不开新功能。
            }

            if (flag.TryGetValue("rollout", out object rolloutValue))
            {
                if (!TryCoerceToLong(rolloutValue, out long percent) || percent < 0 || percent > 100)
                    return RejectFlag(key, "rollout 不是 0～100 的整数");
                if (percent <= 0)
                    return true; // 判定有效，结果为关。
                if (percent < 100)
                {
                    enabled = StableHash.Bucket($"{_deviceId}:{key}") < percent;
                    return true;
                }
            }

            enabled = true;
            return true;
        }

        /// <summary>
        /// 判定作废：告警并返回 false。同一键每份配置只告警一次——
        /// <see cref="IsFeatureEnabled"/> 可能被每帧调用，逐次告警会把控制台刷爆并淹没真正的首次报错。
        /// </summary>
        /// <param name="key">开关键。</param>
        /// <param name="reason">非法原因。</param>
        /// <returns>恒为 false，供调用处直接 return。</returns>
        private bool RejectFlag(string key, string reason)
        {
            if (_warnedFlagKeys.Add(key))
                GameLog.Warning($"[RemoteConfigManager] 开关 {key} 配置非法（{reason}），该远端值作废，按本地基线判定");
            return false;
        }

        /// <summary>把远端值收敛为布尔。接受 bool 与 "true"/"false" 文本，与标量取值口径一致。</summary>
        /// <param name="value">远端值。</param>
        /// <param name="result">成功时返回布尔值。</param>
        /// <returns>可收敛返回 true。</returns>
        private static bool TryCoerceToBool(object value, out bool result)
        {
            if (value is bool b)
            {
                result = b;
                return true;
            }

            return bool.TryParse(value as string, out result);
        }

        /// <summary>标量取值口径：无法收敛为整数时退回调用方给的默认值。</summary>
        /// <param name="value">远端值。</param>
        /// <param name="fallback">无法收敛时的返回值。</param>
        /// <returns>收敛后的整数或 <paramref name="fallback"/>。</returns>
        private static long CoerceToLong(object value, long fallback)
            => TryCoerceToLong(value, out long result) ? result : fallback;

        /// <summary>
        /// 把远端值收敛为整数。JSON 数字按解析器约定落为 long 或 double，另接受整数文本；
        /// 非有限的 double（NaN / 无穷）与超出 long 值域的 ulong 都算收敛失败，不做未定义的强转。
        /// </summary>
        /// <param name="value">远端值。</param>
        /// <param name="result">成功时返回整数值。</param>
        /// <returns>可收敛返回 true。</returns>
        private static bool TryCoerceToLong(object value, out long result)
        {
            switch (value)
            {
                case long l: result = l; return true;
                case int i: result = i; return true;
                case short sh: result = sh; return true;
                case byte by: result = by; return true;
                case sbyte sb: result = sb; return true;
                case uint ui: result = ui; return true;
                case ushort us: result = us; return true;
                case ulong ul when ul <= long.MaxValue: result = (long)ul; return true;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d) &&
                                   d >= long.MinValue && d <= long.MaxValue:
                    result = (long)d;
                    return true;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f) &&
                                  f >= long.MinValue && f <= long.MaxValue:
                    result = (long)f;
                    return true;
                case decimal m: result = (long)m; return true;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                    result = parsed;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        /// <summary>取当前后端；未注入时按 AppConfig 惰性选择，未配置端点返回 null（只用缓存与默认值）。</summary>
        private IRemoteConfigBackend Backend()
        {
            if (_backend != null)
                return _backend;

            string url = AppConfig.Load() != null ? AppConfig.Load().RemoteConfigUrl : null;
            if (string.IsNullOrEmpty(url))
            {
                if (!_warnedNoBackend)
                {
                    _warnedNoBackend = true;
                    GameLog.Log("[RemoteConfigManager] 未配置 RemoteConfigUrl 也未注入后端，仅用缓存与代码默认值");
                }
                return null;
            }

            _backend = new HttpRemoteConfigBackend(url);
            GameLog.Log($"[RemoteConfigManager] 默认配置后端: {_backend.Name}");
            return _backend;
        }

        private string ChannelName()
        {
            // GameEntry 未接线（纯单测环境）时渠道维度留空
            return Core.GameEntry.Sdk != null ? Core.GameEntry.Sdk.ChannelName : string.Empty;
        }

        private void PersistCache(string json)
        {
            try
            {
                FileStorages.Shared.WriteText(_cachePath, json);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[RemoteConfigManager] 缓存落盘失败（不影响本次激活值）: {ex.Message}");
            }
        }

        private void LoadCacheFromDisk()
        {
            try
            {
                if (!FileStorages.Shared.FileExists(_cachePath))
                    return;

                string json = FileStorages.Shared.ReadText(_cachePath);
                if (JsonObjectParser.TryParseObject(json, out var values))
                {
                    _active = values;
                    _warnedFlagKeys.Clear();
                }
                else
                    GameLog.Warning("[RemoteConfigManager] 缓存 JSON 解析失败，忽略缓存");
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[RemoteConfigManager] 读取缓存失败: {ex.Message}");
            }
        }
    }
}
