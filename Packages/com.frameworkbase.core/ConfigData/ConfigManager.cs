using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Framework.Data;
using Framework.Http;
using Framework.Serialization;
using Framework.Storage;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Framework
{
    /// <summary>
    /// 负责加载和缓存普通表配置以及单例 general 配置。
    /// </summary>
    public class ConfigManager : Core.FrameworkComponent<ConfigManager>
    {
        private const string DefaultDatabaseFileName = "config.db";
        private const string DefaultStreamingConfigPath = "RefData/config.db";
        private const string DefaultAddressableConfigAddress = "RefData/config.db";

        private readonly Dictionary<Type, IConfigTable> _tableConfigCache = new Dictionary<Type, IConfigTable>();
        private readonly Dictionary<Type, object> _generalConfigCache = new Dictionary<Type, object>();

        private string _dbPath;
        private bool _isInitialized;

        /// <summary>
        /// 持久化数据库与首包数据库的兼容检查结果。
        /// </summary>
        private struct DatabaseRefreshResult
        {
            /// <summary>
            /// 是否已经用首包数据库替换持久化数据库。
            /// </summary>
            public bool Refreshed;

            /// <summary>
            /// 是否检测到持久化数据库结构落后于首包数据库。
            /// </summary>
            public bool IncompatibleDetected;

            /// <summary>
            /// 是否检测到首包数据库内容基线比持久化数据库更新。
            /// </summary>
            public bool PackagedBaselineUpdated;
        }

        /// <summary>
        /// SQLite 表名查询行。
        /// </summary>
        private sealed class DatabaseTableNameRow
        {
            /// <summary>
            /// SQLite 表名。
            /// </summary>
            public string Name { get; set; }
        }

        /// <summary>
        /// SQLite PRAGMA table_info 的列信息行。
        /// </summary>
        private sealed class DatabaseColumnInfoRow
        {
            /// <summary>
            /// SQLite 列名。
            /// </summary>
            public string Name { get; set; }
        }

        /// <summary>
        /// general 配置表的纵向键值行结构，对应 SQLite 中的 Key/ValueType/Value/Comment 四列。
        /// </summary>
        private sealed class GeneralConfigRow
        {
            /// <summary>
            /// 配置字段键，对应生成配置类属性名或 Column 特性名。
            /// </summary>
            public string Key { get; set; }

            /// <summary>
            /// 配置值类型，主要用于排查配置表内容。
            /// </summary>
            public string ValueType { get; set; }

            /// <summary>
            /// 配置值文本，运行时会按目标属性类型转换。
            /// </summary>
            public string Value { get; set; }

            /// <summary>
            /// 配置说明，运行时不参与逻辑，仅保留查询可读性。
            /// </summary>
            public string Comment { get; set; }
        }

        /// <summary>
        /// 框架组件启动时初始化配置管理器。
        /// </summary>
        public override void OnInit()
        {
            Initialize();
        }

        /// <summary>
        /// 框架组件关闭时释放已加载配置。
        /// </summary>
        public override void OnShutdown()
        {
            Dispose();
        }

        /// <summary>
        /// 解析数据库路径，并准备后续配置加载所需状态。
        /// </summary>
        public void Initialize(string dbPath = null)
        {
            if (_isInitialized)
            {
                GameLog.Warning("[ConfigManager] Already initialized; skipping duplicate init.");
                return;
            }

            _dbPath = ResolveDatabasePath(dbPath);
            EnsureDatabaseDirectory(_dbPath);

            if (!File.Exists(_dbPath))
            {
                // 首装设备此时必然无库（LaunchFlow 随后从首包安装），属正常序列而非异常；
                // 真正的"装完仍无可用库"在 EnsureDatabaseReadyAsync 里以 Warning/Error 报告。
                GameLog.Log($"[ConfigManager] Database is not ready yet (fresh install expected): {_dbPath}");
            }
            else
            {
                GameLog.Log($"[ConfigManager] Database path: {_dbPath}");
            }

            _isInitialized = true;
            GameLog.Log("[ConfigManager] Initialization complete.");
        }

        /// <summary>
        /// 确保持久化目录中存在可读数据库，必要时从 StreamingAssets 拷贝。
        /// </summary>
        public async UniTask<bool> EnsureDatabaseReadyAsync(string streamingAssetRelativePath = DefaultStreamingConfigPath)
        {
            EnsureInitialized();

            string[] sourcePaths =
            {
                streamingAssetRelativePath,
                DefaultDatabaseFileName
            };

            if (File.Exists(_dbPath))
            {
                DatabaseRefreshResult refreshResult = await TryRefreshExistingDatabaseFromPackagedAsync(sourcePaths);
                if (refreshResult.Refreshed)
                {
                    GameLog.Log($"[ConfigManager] Database was refreshed from packaged baseline: {_dbPath}");
                    // 首包配置自动替换旧持久化库后，刷新已显示的 TextMeshProEx。
                    Language.Refresh();
                    return true;
                }

                if (refreshResult.IncompatibleDetected)
                {
                    GameLog.Warning($"[ConfigManager] Existing database is incompatible and could not be refreshed automatically: {_dbPath}");
                    return false;
                }

                if (!ValidateDatabaseFile(_dbPath))
                {
                    GameLog.Warning($"[ConfigManager] Existing database is invalid and will be reinstalled: {_dbPath}");
                    DeleteFileQuietly(_dbPath);
                }
                else
                {
                    GameLog.Log($"[ConfigManager] Database is ready: {_dbPath}");
                    // 配置库可能晚于 UI Awake 完成，数据库 ready 后主动刷新一次多语言文本。
                    Language.Refresh();
                    return true;
                }
            }

            EnsureDatabaseDirectory(_dbPath);

            for (int i = 0; i < sourcePaths.Length; i++)
            {
                if (string.IsNullOrEmpty(sourcePaths[i]))
                {
                    continue;
                }

                string sourcePath = PathUtil.GetStreamingAssetsPath(sourcePaths[i]);
                bool copied = await TryCopyStreamingDatabaseAsync(sourcePath, _dbPath);
                if (copied)
                {
                    GameLog.Log($"[ConfigManager] Installed packaged database: {sourcePaths[i]} -> {_dbPath}");
                    // 首包配置首次安装后，刷新已显示的 TextMeshProEx。
                    Language.Refresh();
                    return true;
                }
            }

            GameLog.Warning($"[ConfigManager] No usable database found at persistentDataPath or StreamingAssets/RefData/config.db: {_dbPath}");
            return false;
        }

        /// <summary>
        /// 从 Addressables 下载热更数据库，并以事务方式安装。
        /// <para>
        /// 返回 <see cref="ConfigInstallResult"/>，显式区分：本次发行不包含配置（NotIncluded，正常）/
        /// 安装成功（Installed，旧库备份保留至启动确认点）/ 下载失败 / 校验失败 / 替换失败 / 重载失败。
        /// 调用方（LaunchFlow）必须检查 <see cref="ConfigInstallResult.Succeeded"/>：
        /// 任何失败终态都要中止本次启动更新，禁止继续提交版本状态——
        /// 旧实现的单 bool 返回把"没有配置更新"和"安装失败"混成一类，失败会被静默放行。
        /// </para>
        /// </summary>
        public async UniTask<ConfigInstallResult> UpdateDatabaseFromAddressablesAsync(
            string address = DefaultAddressableConfigAddress,
            bool reloadLoadedConfigs = true)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("[ConfigManager] Address must not be empty.");
                return ConfigInstallResult.Failed(ConfigInstallStatus.DownloadFailed, "配置数据库地址为空");
            }

            // ── 阶段 1：从 Addressables 加载载荷（区分"不存在"与"下载失败"两种终态）──
            byte[] payload;
            AsyncOperationHandle<TextAsset> handle = default;
            try
            {
                handle = Addressables.LoadAssetAsync<TextAsset>(address);
                await handle.Task;

                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    // key 存在但下载/加载失败（网络、bundle 损坏）必须阻断；
                    // 只有 key 不存在（InvalidKeyException）才是"本次发行不包含配置"。
                    if (handle.OperationException != null &&
                        handle.OperationException.GetType().Name == "InvalidKeyException")
                    {
                        GameLog.Log($"[ConfigManager] 本次发行不包含热更配置数据库：{address}");
                        return ConfigInstallResult.NotIncluded();
                    }

                    string reason = handle.OperationException?.Message ?? $"状态={handle.Status}";
                    GameLog.Error($"[ConfigManager] 热更配置数据库下载失败 [{address}]：{reason}");
                    return ConfigInstallResult.Failed(ConfigInstallStatus.DownloadFailed, reason);
                }

                payload = handle.Result.bytes;
            }
            catch (Exception ex) when (ex.GetType().Name == "InvalidKeyException")
            {
                GameLog.Log($"[ConfigManager] 本次发行不包含热更配置数据库（key 不存在）：{address}");
                return ConfigInstallResult.NotIncluded();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[ConfigManager] 热更配置数据库下载异常 [{address}]：{ex.Message}");
                return ConfigInstallResult.Failed(ConfigInstallStatus.DownloadFailed, ex.Message);
            }
            finally
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }

            // ── 阶段 2：事务化安装（备份保留至启动确认点，见 ConfigDatabaseInstaller）──
            ConfigInstallResult installResult = Installer.Install(payload, $"Addressables:{address}");
            if (!installResult.DatabaseChanged)
                return installResult;

            // ── 阶段 3：重载已缓存配置（失败即恢复备份并失败关闭）───────────────
            if (reloadLoadedConfigs)
            {
                var loadedConfigTypes = new List<Type>(_tableConfigCache.Keys);
                loadedConfigTypes.AddRange(_generalConfigCache.Keys);
                if (loadedConfigTypes.Count > 0)
                {
                    try
                    {
                        ReloadConfigs(loadedConfigTypes);
                    }
                    catch (Exception ex)
                    {
                        GameLog.Error($"[ConfigManager] 新配置数据库重载失败，恢复上一份已确认库：{ex.Message}");
                        try
                        {
                            Installer.RestoreLastConfirmed();
                            ReloadConfigs(loadedConfigTypes);
                        }
                        catch (Exception restoreEx)
                        {
                            GameLog.Error($"[ConfigManager] 恢复旧配置数据库失败（备份保留待下次启动）：{restoreEx.Message}");
                        }
                        return ConfigInstallResult.Failed(ConfigInstallStatus.LoadFailed, ex.Message);
                    }
                }
            }

            // 热更配置替换后，刷新可能受 language 表影响的 UI 文本。
            Language.Refresh();
            return installResult;
        }

        // 配置数据库事务化安装器：备份生命周期延伸到统一启动确认点。
        // 所有权边界：installer 由本组件独占持有；dbPath 在 Initialize 后不再变化。
        private ConfigDatabaseInstaller _installer;

        private ConfigDatabaseInstaller Installer =>
            _installer ??= new ConfigDatabaseInstaller(_dbPath, ValidateDatabaseFile, GameLog.Log, GameLog.Error);

        /// <summary>
        /// 是否存在未确认的配置数据库备份。
        /// 存在即说明上次启动安装了新配置但未走到统一确认点（进程被杀 / 启动失败），
        /// 启动早期应调用 <see cref="RestoreLastConfirmedDatabaseIfAny"/> 恢复。
        /// </summary>
        public bool HasUnconfirmedDatabaseBackup
        {
            get
            {
                EnsureInitialized();
                return Installer.HasUnconfirmedBackup;
            }
        }

        /// <summary>
        /// 统一启动确认点动作：本次启动的全部内容（代码/资源/配置）都确认成功后，
        /// 由 LaunchFlow 调用以清理配置数据库备份。确认前禁止调用。
        /// </summary>
        public void ConfirmHotUpdateDatabase()
        {
            EnsureInitialized();
            Installer.ConfirmInstalled();
        }

        /// <summary>
        /// 内容级出厂回退：删除持久化数据库与备份，下次 EnsureDatabaseReadyAsync 会从
        /// StreamingAssets 重新安装出厂基线。仅由内容级崩溃循环恢复路径调用。
        /// </summary>
        public void ResetDatabaseToFactoryBaseline()
        {
            EnsureInitialized();
            UnloadAllConfigs();
            DeleteFileQuietly(_dbPath);
            DeleteFileQuietly(Installer.BackupPath);
            GameLog.Warning("[ConfigManager] 配置数据库已回退出厂基线（持久化库与备份已清除）。");
        }

        /// <summary>
        /// 启动确认前失败的恢复动作：恢复上一份已确认配置数据库（若存在备份）。
        /// 由 LaunchFlow 失败路径或下次启动早期（检测到未确认 Pending 时）调用。
        /// </summary>
        /// <returns>实际发生了恢复返回 true。</returns>
        public bool RestoreLastConfirmedDatabaseIfAny()
        {
            EnsureInitialized();
            try
            {
                return Installer.RestoreLastConfirmed();
            }
            catch (Exception ex)
            {
                // 恢复失败已在 installer 内保留备份供下次启动重试；这里只向上暴露日志，不再抛出中断恢复链。
                GameLog.Error($"[ConfigManager] 配置数据库恢复失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取已缓存配置，或按需加载配置；同时支持表加载器和 general 单例数据类。
        /// </summary>
        public TConfig GetConfig<TConfig>() where TConfig : class, new()
        {
            EnsureInitialized();

            Type configType = typeof(TConfig);
            if (typeof(IConfigTable).IsAssignableFrom(configType))
            {
                return LoadTableConfig<TConfig>(configType);
            }

            if (IsGeneralConfigType(configType))
            {
                return LoadGeneralConfig<TConfig>(configType);
            }

            throw new InvalidOperationException(
                $"Type {configType.Name} does not implement IConfigTable and is not marked with GeneralConfigAttribute.");
        }

        /// <summary>
        /// 将单个配置预加载到缓存中，不向调用方返回实例。
        /// </summary>
        public void PreloadConfig<TConfig>() where TConfig : class, new()
        {
            GetConfig<TConfig>();
        }

        /// <summary>
        /// 批量预加载配置类型，不支持的类型会输出警告并跳过。
        /// </summary>
        public void PreloadConfigs(params Type[] configTypes)
        {
            EnsureInitialized();

            foreach (Type configType in configTypes)
            {
                try
                {
                    if (typeof(IConfigTable).IsAssignableFrom(configType))
                    {
                        LoadTableConfigByType(configType);
                        continue;
                    }

                    if (IsGeneralConfigType(configType))
                    {
                        LoadGeneralConfigByType(configType);
                        continue;
                    }

                    GameLog.Warning($"[ConfigManager] Unsupported config type: {configType.Name}");
                }
                catch (Exception ex)
                {
                    GameLog.Error($"[ConfigManager] Failed to preload {configType.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 卸载单个已缓存配置，普通表配置和 general 配置都会处理。
        /// </summary>
        public void UnloadConfig<TConfig>() where TConfig : class
        {
            Type configType = typeof(TConfig);

            if (_tableConfigCache.TryGetValue(configType, out IConfigTable tableConfig))
            {
                tableConfig.Unload();
                _tableConfigCache.Remove(configType);
                GameLog.Log($"[ConfigManager] Unloaded table config: {configType.Name}");
            }

            if (_generalConfigCache.Remove(configType))
            {
                GameLog.Log($"[ConfigManager] Unloaded general config: {configType.Name}");
            }
        }

        /// <summary>
        /// 卸载所有已缓存配置，并清空两类配置缓存。
        /// </summary>
        public void UnloadAllConfigs()
        {
            foreach (var kvp in _tableConfigCache)
            {
                try
                {
                    kvp.Value.Unload();
                }
                catch (Exception ex)
                {
                    GameLog.Error($"[ConfigManager] Failed to unload {kvp.Key.Name}: {ex.Message}");
                }
            }

            _tableConfigCache.Clear();
            _generalConfigCache.Clear();
            GameLog.Log("[ConfigManager] Cleared all loaded configs.");
        }

        /// <summary>
        /// 通过清除缓存并重新读取数据库来重载单个配置。
        /// </summary>
        public void ReloadConfig<TConfig>() where TConfig : class, new()
        {
            UnloadConfig<TConfig>();
            GetConfig<TConfig>();
        }

        /// <summary>
        /// 从当前数据库中重载所有已缓存配置。
        /// </summary>
        public void ReloadAllConfigs()
        {
            var configTypes = new List<Type>(_tableConfigCache.Keys);
            configTypes.AddRange(_generalConfigCache.Keys);

            UnloadAllConfigs();
            PreloadConfigs(configTypes.ToArray());

            GameLog.Log("[ConfigManager] Reloaded all cached configs.");
        }

        /// <summary>
        /// 判断指定配置类型是否已经加载到缓存。
        /// </summary>
        public bool IsConfigLoaded<TConfig>() where TConfig : class
        {
            Type configType = typeof(TConfig);
            return _tableConfigCache.ContainsKey(configType) || _generalConfigCache.ContainsKey(configType);
        }

        /// <summary>
        /// 返回普通表配置和 general 配置的缓存总数。
        /// </summary>
        public int GetLoadedConfigCount()
        {
            return _tableConfigCache.Count + _generalConfigCache.Count;
        }

        /// <summary>
        /// 返回当前管理器使用的数据库路径。
        /// </summary>
        public string GetDatabasePath()
        {
            return _dbPath;
        }

        /// <summary>
        /// 释放已加载配置，并将管理器标记为未初始化。
        /// </summary>
        public void Dispose()
        {
            UnloadAllConfigs();
            _isInitialized = false;
            GameLog.Log("[ConfigManager] Disposed.");
        }

        /// <summary>
        /// 加载普通 ConfigBase 派生表加载器，并按加载器类型缓存。
        /// </summary>
        private TConfig LoadTableConfig<TConfig>(Type configType) where TConfig : class, new()
        {
            if (_tableConfigCache.TryGetValue(configType, out IConfigTable cachedConfig))
            {
                return (TConfig)cachedConfig;
            }

            try
            {
                var config = (IConfigTable)new TConfig();
                string tableName = string.IsNullOrEmpty(config.TableName)
                    ? GetTableNameFromType(configType)
                    : config.TableName;

                config.Load(_dbPath, tableName);
                _tableConfigCache[configType] = config;

                GameLog.Log($"[ConfigManager] Loaded table config {configType.Name} with {config.Count} rows.");
                return (TConfig)config;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[ConfigManager] Failed to load table config {configType.Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 在只有运行时 Type 的情况下加载普通表配置。
        /// </summary>
        private void LoadTableConfigByType(Type configType)
        {
            if (_tableConfigCache.ContainsKey(configType))
            {
                return;
            }

            IConfigTable config = Activator.CreateInstance(configType) as IConfigTable;
            if (config == null)
            {
                throw new InvalidOperationException($"Could not create table config instance: {configType.Name}");
            }

            string tableName = string.IsNullOrEmpty(config.TableName)
                ? GetTableNameFromType(configType)
                : config.TableName;

            config.Load(_dbPath, tableName);
            _tableConfigCache[configType] = config;

            GameLog.Log($"[ConfigManager] Preloaded table config {configType.Name} with {config.Count} rows.");
        }

        /// <summary>
        /// 加载单例 general 配置数据类，并从 SQLite 纵向键值表组装强类型对象。
        /// </summary>
        private TConfig LoadGeneralConfig<TConfig>(Type configType) where TConfig : class, new()
        {
            if (_generalConfigCache.TryGetValue(configType, out object cachedConfig))
            {
                return (TConfig)cachedConfig;
            }

            try
            {
                string tableName = GetTableName(configType);
                TConfig config;
                using (var db = new SQLiteHelper(_dbPath))
                {
                    // general 配置表保持 Key/ValueType/Value/Comment 纵向结构，运行时再组装为强类型对象。
                    var rows = db.Query<GeneralConfigRow>(
                        $"SELECT [Key], [ValueType], [Value], [Comment] FROM {QuoteSqlIdentifier(tableName)}");
                    config = BuildGeneralConfig<TConfig>(configType, rows);
                }

                if (config == null)
                {
                    throw new InvalidOperationException($"general 配置表 {tableName} 没有任何数据行。");
                }

                _generalConfigCache[configType] = config;
                GameLog.Log($"[ConfigManager] Loaded general config {configType.Name}.");
                return config;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[ConfigManager] Failed to load general config {configType.Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 将 general 配置纵向键值行反射写入生成的强类型配置对象。
        /// </summary>
        private TConfig BuildGeneralConfig<TConfig>(Type configType, List<GeneralConfigRow> rows) where TConfig : class, new()
        {
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            var config = new TConfig();
            var propertyMap = BuildGeneralPropertyMap(configType);

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Key))
                {
                    continue;
                }

                if (!propertyMap.TryGetValue(row.Key, out PropertyInfo property))
                {
                    GameLog.Warning($"[ConfigManager] general config {configType.Name} has no property for key: {row.Key}");
                    continue;
                }

                object parsedValue = ParseGeneralValue(row.Value, property.PropertyType);
                property.SetValue(config, parsedValue);
            }

            return config;
        }

        /// <summary>
        /// 建立 general 配置属性索引，同时支持属性名和 SQLite Column 特性名。
        /// </summary>
        private Dictionary<string, PropertyInfo> BuildGeneralPropertyMap(Type configType)
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in properties)
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                map[property.Name] = property;

                var columnAttr = Attribute.GetCustomAttribute(property, typeof(SQLite.ColumnAttribute)) as SQLite.ColumnAttribute;
                if (columnAttr != null && !string.IsNullOrEmpty(columnAttr.Name))
                {
                    map[columnAttr.Name] = property;
                }
            }

            return map;
        }

        /// <summary>
        /// 将 general 表 Value 文本转换为生成配置类属性需要的运行时类型。
        /// </summary>
        private object ParseGeneralValue(string value, Type targetType)
        {
            Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (string.IsNullOrWhiteSpace(value))
            {
                return actualType.IsValueType ? Activator.CreateInstance(actualType) : null;
            }

            if (actualType == typeof(string))
            {
                return value;
            }

            if (actualType == typeof(bool))
            {
                string boolText = value.Trim().ToLowerInvariant();
                return boolText == "true" || boolText == "1" || boolText == "yes" || boolText == "y" || boolText == "是";
            }

            if (actualType.IsEnum)
            {
                return Enum.Parse(actualType, value, ignoreCase: true);
            }

            if (actualType.IsArray)
            {
                Type elementType = actualType.GetElementType();
                string[] parts = SplitGeneralCollection(value);
                Array array = Array.CreateInstance(elementType, parts.Length);

                for (int i = 0; i < parts.Length; i++)
                {
                    array.SetValue(ParseGeneralValue(parts[i], elementType), i);
                }

                return array;
            }

            if (actualType.IsGenericType && actualType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elementType = actualType.GetGenericArguments()[0];
                string[] parts = SplitGeneralCollection(value);
                var list = (IList)Activator.CreateInstance(actualType);

                foreach (string part in parts)
                {
                    list.Add(ParseGeneralValue(part, elementType));
                }

                return list;
            }

            MethodInfo parseMethod = actualType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (parseMethod != null)
            {
                return parseMethod.Invoke(null, new object[] { value });
            }

            return Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 拆分 general 配置中的数组或列表文本。
        /// </summary>
        private string[] SplitGeneralCollection(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            string normalized = value.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return normalized.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 在只有运行时 Type 的情况下加载 general 配置。
        /// </summary>
        private void LoadGeneralConfigByType(Type configType)
        {
            if (_generalConfigCache.ContainsKey(configType))
            {
                return;
            }

            var method = typeof(ConfigManager)
                .GetMethod(nameof(LoadGeneralConfigByReflection), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.MakeGenericMethod(configType);

            if (method == null)
            {
                throw new InvalidOperationException("Could not build the general config loader method.");
            }

            method.Invoke(this, null);
        }

        /// <summary>
        /// PreloadConfigs 预加载 general 配置类型时使用的反射桥接方法。
        /// </summary>
        private void LoadGeneralConfigByReflection<TConfig>() where TConfig : class, new()
        {
            LoadGeneralConfig<TConfig>(typeof(TConfig));
        }

        /// <summary>
        /// 在管理器尚未初始化时抛出异常，避免误用。
        /// </summary>
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("ConfigManager has not been initialized.");
            }
        }

        /// <summary>
        /// 优先使用调用方传入路径，否则使用默认持久化数据库路径。
        /// </summary>
        private string ResolveDatabasePath(string dbPath)
        {
            if (!string.IsNullOrEmpty(dbPath))
            {
                return PathUtil.NormalizePath(dbPath);
            }

            return PathUtil.GetPersistentDataPath(DefaultDatabaseFileName);
        }

        /// <summary>
        /// 当数据库目录不存在时创建目录。
        /// </summary>
        private void EnsureDatabaseDirectory(string dbPath)
        {
            string directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// 当持久化数据库结构落后于首包数据库时，自动用首包数据库刷新本地库。
        /// </summary>
        private async UniTask<DatabaseRefreshResult> TryRefreshExistingDatabaseFromPackagedAsync(string[] sourcePaths)
        {
            var result = new DatabaseRefreshResult();
            if (sourcePaths == null || sourcePaths.Length == 0)
            {
                return result;
            }

            for (int i = 0; i < sourcePaths.Length; i++)
            {
                if (string.IsNullOrEmpty(sourcePaths[i]))
                {
                    continue;
                }

                string sourcePath = PathUtil.GetStreamingAssetsPath(sourcePaths[i]);

#if UNITY_ANDROID && !UNITY_EDITOR
                string tempSourcePath = _dbPath + ".packaged";
                byte[] sourceBytes = await TryReadStreamingDatabaseBytesAsync(sourcePath);
                if (sourceBytes == null || sourceBytes.Length == 0)
                {
                    continue;
                }

                try
                {
                    File.WriteAllBytes(tempSourcePath, sourceBytes);
                    bool installed = TryInstallPackagedDatabaseIfRefreshNeeded(tempSourcePath, $"StreamingAssets:{sourcePaths[i]}", ref result);
                    if (installed || !result.IncompatibleDetected)
                    {
                        return result;
                    }
                }
                finally
                {
                    DeleteFileQuietly(tempSourcePath);
                }
#else
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                bool installed = TryInstallPackagedDatabaseIfRefreshNeeded(sourcePath, $"StreamingAssets:{sourcePaths[i]}", ref result);
                if (installed || !result.IncompatibleDetected)
                {
                    return result;
                }
#endif
            }

            await UniTask.CompletedTask;
            return result;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// 读取 Android 包内 StreamingAssets 数据库字节。
        /// </summary>
        private async UniTask<byte[]> TryReadStreamingDatabaseBytesAsync(string sourcePath)
        {
            string sourceUrl = PathUtil.GetFileUrl(sourcePath);
            HttpResponse response = await HttpClients.Shared.SendAsync(HttpRequest.Get(sourceUrl));
            if (!response.Succeeded)
            {
                GameLog.Warning($"[ConfigManager] Failed to read packaged database: {sourceUrl}, {response.Error}");
                return null;
            }

            return response.Data;
        }
#endif

        /// <summary>
        /// 如果持久化数据库缺少首包数据库已有结构，或首包内容基线更新，则安装首包数据库。
        /// </summary>
        private bool TryInstallPackagedDatabaseIfRefreshNeeded(string packagedDbPath, string sourceName, ref DatabaseRefreshResult result)
        {
            if (!ValidateDatabaseFile(packagedDbPath))
            {
                return false;
            }

            bool currentDatabaseValid = ValidateDatabaseFile(_dbPath);
            if (currentDatabaseValid
                && IsDatabaseCompatibleWithPackaged(_dbPath, packagedDbPath)
                && !ShouldRefreshFromNewerPackagedBaseline(packagedDbPath))
            {
                return false;
            }

            if (!currentDatabaseValid || !IsDatabaseCompatibleWithPackaged(_dbPath, packagedDbPath))
            {
                result.IncompatibleDetected = true;
                GameLog.Warning($"[ConfigManager] Existing database schema is older than packaged baseline. Reinstalling from {sourceName}.");
            }
            else
            {
                result.PackagedBaselineUpdated = true;
                GameLog.Warning($"[ConfigManager] Packaged database baseline is newer than persistent database. Reinstalling from {sourceName}.");
            }

            try
            {
                EnsureDatabaseDirectory(_dbPath);
                File.Copy(packagedDbPath, _dbPath, overwrite: true);
                result.Refreshed = ValidateDatabaseFile(_dbPath);
                return result.Refreshed;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[ConfigManager] Failed to refresh database from packaged baseline: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判断首包数据库文件是否比当前持久化数据库更新，且本地没有安装更高资源版本的热更库。
        /// </summary>
        private bool ShouldRefreshFromNewerPackagedBaseline(string packagedDbPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android 包内 StreamingAssets 没有可靠文件时间戳，保留结构兼容刷新和 Addressables 热更刷新。
            return false;
#else
            if (string.IsNullOrEmpty(packagedDbPath)
                || !File.Exists(packagedDbPath)
                || !File.Exists(_dbPath))
            {
                return false;
            }

            DateTime packagedWriteTimeUtc = File.GetLastWriteTimeUtc(packagedDbPath);
            DateTime currentWriteTimeUtc = File.GetLastWriteTimeUtc(_dbPath);
            if (packagedWriteTimeUtc <= currentWriteTimeUtc)
            {
                return false;
            }

            if (HasInstalledResourceVersionNewerThanPackaged())
            {
                GameLog.Log("[ConfigManager] Persistent database belongs to a newer resource hot-update; keep current database.");
                return false;
            }

            return true;
#endif
        }

        /// <summary>
        /// 判断持久化版本号是否高于首包版本号，用于避免首包基线覆盖已热更配置库。
        /// </summary>
        private bool HasInstalledResourceVersionNewerThanPackaged()
        {
            HotUpdate.UpdateInfo persistentVersion = TryReadVersionInfo(Path.Combine(Application.persistentDataPath, "version.json"));
            HotUpdate.UpdateInfo packagedVersion = TryReadVersionInfo(Path.Combine(Application.streamingAssetsPath, "version.json"));

            if (persistentVersion == null || packagedVersion == null)
            {
                return false;
            }

            if (!string.Equals(persistentVersion.AppVersion, Application.version, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(packagedVersion.AppVersion, Application.version, StringComparison.Ordinal))
            {
                return false;
            }

            return persistentVersion.ResourceVersion > packagedVersion.ResourceVersion;
        }

        /// <summary>
        /// 安静读取版本文件；读取失败时返回 null，让调用方按保守策略继续判断。
        /// </summary>
        private HotUpdate.UpdateInfo TryReadVersionInfo(string versionPath)
        {
            if (string.IsNullOrEmpty(versionPath) || !FileStorages.Shared.FileExists(versionPath))
            {
                return null;
            }

            try
            {
                string json = FileStorages.Shared.ReadText(versionPath);
                return JsonSerializers.Shared.FromJson<HotUpdate.UpdateInfo>(json);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[ConfigManager] Failed to read version info: {versionPath}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 判断持久化数据库是否包含首包数据库中的全部表和列。
        /// </summary>
        private bool IsDatabaseCompatibleWithPackaged(string currentDbPath, string packagedDbPath)
        {
            try
            {
                using (var currentDb = new SQLiteHelper(currentDbPath))
                using (var packagedDb = new SQLiteHelper(packagedDbPath))
                {
                    List<DatabaseTableNameRow> packagedTables = packagedDb.Query<DatabaseTableNameRow>(
                        "SELECT name AS Name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");

                    foreach (DatabaseTableNameRow table in packagedTables)
                    {
                        if (table == null || string.IsNullOrEmpty(table.Name))
                        {
                            continue;
                        }

                        if (!DatabaseTableExists(currentDb, table.Name))
                        {
                            GameLog.Warning($"[ConfigManager] Existing database missing table: {table.Name}");
                            return false;
                        }

                        HashSet<string> packagedColumns = GetDatabaseColumnNames(packagedDb, table.Name);
                        HashSet<string> currentColumns = GetDatabaseColumnNames(currentDb, table.Name);
                        foreach (string columnName in packagedColumns)
                        {
                            if (!currentColumns.Contains(columnName))
                            {
                                GameLog.Warning($"[ConfigManager] Existing database missing column: {table.Name}.{columnName}");
                                return false;
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[ConfigManager] Failed to compare database schema: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判断 SQLite 数据库中是否存在指定表。
        /// </summary>
        private bool DatabaseTableExists(SQLiteHelper db, string tableName)
        {
            int count = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?",
                tableName);
            return count > 0;
        }

        /// <summary>
        /// 获取指定 SQLite 表的列名集合。
        /// </summary>
        private HashSet<string> GetDatabaseColumnNames(SQLiteHelper db, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<DatabaseColumnInfoRow> rows = db.Query<DatabaseColumnInfoRow>($"PRAGMA table_info({QuoteSqlIdentifier(tableName)})");
            foreach (DatabaseColumnInfoRow row in rows)
            {
                if (row != null && !string.IsNullOrEmpty(row.Name))
                {
                    columns.Add(row.Name);
                }
            }

            return columns;
        }

        /// <summary>
        /// 将 StreamingAssets 中随包携带的数据库拷贝到持久化目录。
        /// </summary>
        private async UniTask<bool> TryCopyStreamingDatabaseAsync(string sourcePath, string targetPath)
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath))
            {
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            string sourceUrl = PathUtil.GetFileUrl(sourcePath);
            HttpResponse response = await HttpClients.Shared.SendAsync(HttpRequest.Get(sourceUrl));
            if (!response.Succeeded)
            {
                GameLog.Warning($"[ConfigManager] Failed to read packaged database: {sourceUrl}, {response.Error}");
                return false;
            }

            FileStorages.Shared.WriteBytes(targetPath, response.Data);
            return FileStorages.Shared.FileExists(targetPath);
#else
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            await UniTask.CompletedTask;
            return File.Exists(targetPath);
#endif
        }

        // 说明：旧的 InstallDatabaseBytes（临时文件→校验→备份→替换→立即删备份）已收敛进
        // ConfigDatabaseInstaller。行为差异：备份不再在安装成功后立即删除，而是保留到统一启动
        // 确认点（ConfirmHotUpdateDatabase），使配置回滚与代码槽回滚保持一致的事务边界。

        /// <summary>
        /// 打开数据库文件，并校验 SQLite 能否读取其表结构。
        /// </summary>
        private bool ValidateDatabaseFile(string dbPath)
        {
            try
            {
                using (var db = new SQLiteHelper(dbPath))
                {
                    db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table'");
                }

                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[ConfigManager] Invalid database file {dbPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在数据库替换后重载指定配置类型。
        /// </summary>
        private void ReloadConfigs(List<Type> configTypes)
        {
            UnloadAllConfigs();
            PreloadConfigs(configTypes.ToArray());
        }

        /// <summary>
        /// 静默删除临时文件；删除失败只记录日志，不中断调用流程。
        /// </summary>
        private void DeleteFileQuietly(string path)
        {
            if (string.IsNullOrEmpty(path) || !FileStorages.Shared.FileExists(path))
            {
                return;
            }

            try
            {
                FileStorages.Shared.DeleteFile(path);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[ConfigManager] Failed to delete temp file {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// 从表加载器类型或其数据行类型解析 SQLite 表名。
        /// </summary>
        private string GetTableNameFromType(Type configType)
        {
            Type baseType = configType.BaseType;
            if (baseType != null && baseType.IsGenericType)
            {
                Type valueType = baseType.GetGenericArguments()[1];
                return GetTableName(valueType);
            }

            return ConvertToSnakeCase(configType.Name);
        }

        /// <summary>
        /// 优先从 TableAttribute 解析 SQLite 表名，否则使用类型名兜底。
        /// </summary>
        private string GetTableName(Type type)
        {
            var tableAttr = Attribute.GetCustomAttribute(type, typeof(SQLite.TableAttribute)) as SQLite.TableAttribute;
            if (tableAttr != null && !string.IsNullOrEmpty(tableAttr.Name))
            {
                return tableAttr.Name;
            }

            return ConvertToSnakeCase(type.Name);
        }

        /// <summary>
        /// 转义 SQLite 标识符，避免 Key 等保留字或特殊字符影响 general 查询。
        /// </summary>
        private string QuoteSqlIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("SQL 标识符不能为空", nameof(name));
            }

            return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
        }

        /// <summary>
        /// 判断数据类是否标记为 general 单例配置。
        /// </summary>
        private bool IsGeneralConfigType(Type configType)
        {
            return Attribute.IsDefined(configType, typeof(GeneralConfigAttribute));
        }

        /// <summary>
        /// 将 PascalCase 类型名转换为 snake_case 表名兜底值。
        /// </summary>
        private string ConvertToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            var result = new System.Text.StringBuilder();
            result.Append(char.ToLower(input[0]));

            for (int i = 1; i < input.Length; i++)
            {
                if (char.IsUpper(input[i]))
                {
                    result.Append('_');
                    result.Append(char.ToLower(input[i]));
                }
                else
                {
                    result.Append(input[i]);
                }
            }

            return result.ToString();
        }
    }
}
