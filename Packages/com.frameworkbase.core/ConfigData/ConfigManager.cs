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
    public partial class ConfigManager : Core.FrameworkComponent<ConfigManager>
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

                config.Load(ResolveTableDbPath(tableName), tableName);
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

            config.Load(ResolveTableDbPath(tableName), tableName);
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
                using (var db = new SQLiteHelper(ResolveTableDbPath(tableName)))
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

                object parsedValue = GeneralConfigValueParser.Parse(row.Value, property.PropertyType);
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
                Type[] genericArguments = baseType.GetGenericArguments();
                // ConfigBase<TKey,TValue> 的行类型位于最后一项；ConfigListBase<TValue> 只有一项。
                Type valueType = genericArguments[genericArguments.Length - 1];
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
