using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Serialization;
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
    /// min_version 低于当前版本时关闭（老包别开它跑不动的功能）。
    ///
    /// 后端选择：默认按 AppConfig.RemoteConfigUrl——非空用 <see cref="HttpRemoteConfigBackend"/>，
    /// 留空不拉取（只用缓存与默认值）；对接三方平台经 <see cref="SetBackend"/> 注入扩展包实现。
    /// </summary>
    public class RemoteConfigManager : FrameworkComponent
    {
        /// <summary>磁盘缓存文件名（原样保存最近一次拉取成功的 JSON）。</summary>
        private const string CacheFileName = "remote_config_cache.json";

        private readonly Dictionary<string, object> _defaults = new Dictionary<string, object>();
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
            if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
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
            if (value is long l) return l;
            if (value is string s &&
                float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed;
            return defaultValue;
        }

        /// <summary>
        /// 功能开关判定。布尔值直读；条件对象按 enabled / min_version / rollout 依次过滤
        /// （见类注释）。键不存在或值不可判定时返回 defaultValue。
        /// 同一设备同一键的判定结果稳定（设备分桶哈希），不会本次开下次关。
        /// </summary>
        public bool IsFeatureEnabled(string key, bool defaultValue = false)
        {
            if (!TryGetValue(key, out object value) || value == null)
                return defaultValue;

            if (value is bool b) return b;
            if (value is string s && bool.TryParse(s, out bool parsed)) return parsed;
            if (value is Dictionary<string, object> flag) return EvaluateFlag(key, flag);
            return defaultValue;
        }

        /// <summary>清除磁盘缓存与已激活值（测试隔离 / 合规抹除用），代码默认值保留。</summary>
        public void ClearCache()
        {
            _active = null;
            FetchedThisSession = false;
            try
            {
                if (File.Exists(_cachePath))
                    File.Delete(_cachePath);
            }
            catch
            {
                // 删除失败无碍：下次拉取成功会覆盖写
            }
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>条件开关对象判定：enabled 关 → 关；版本不够 → 关；否则按 rollout 分桶。</summary>
        private bool EvaluateFlag(string key, Dictionary<string, object> flag)
        {
            if (flag.TryGetValue("enabled", out object enabled) && enabled is bool e && !e)
                return false;

            if (flag.TryGetValue("min_version", out object minVer) && minVer is string mv &&
                !string.IsNullOrEmpty(mv) &&
                HotUpdate.VersionManager.CompareVersion(_appVersion, mv) < 0)
                return false;

            if (flag.TryGetValue("rollout", out object rollout))
            {
                long percent = CoerceToLong(rollout, 100);
                if (percent <= 0)
                    return false;
                if (percent < 100)
                    return StableHash.Bucket($"{_deviceId}:{key}") < percent;
            }
            return true;
        }

        private static long CoerceToLong(object value, long fallback)
        {
            if (value is long l) return l;
            if (value is double d) return (long)d;
            if (value is string s &&
                long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                return parsed;
            return fallback;
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
                File.WriteAllText(_cachePath, json);
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
                if (!File.Exists(_cachePath))
                    return;

                string json = File.ReadAllText(_cachePath);
                if (JsonObjectParser.TryParseObject(json, out var values))
                    _active = values;
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
