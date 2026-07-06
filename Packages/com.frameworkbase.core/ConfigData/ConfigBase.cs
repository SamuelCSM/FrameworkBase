using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Framework.Data
{
    /// <summary>
    /// 配置表基类接口（非泛型）
    /// 用于ConfigManager统一管理
    /// </summary>
    public interface IConfigTable
    {
        /// <summary>
        /// 加载配置表
        /// </summary>
        void Load(string dbPath, string tableName);

        /// <summary>
        /// 卸载配置表
        /// </summary>
        void Unload();

        /// <summary>
        /// 获取配置项数量
        /// </summary>
        int Count { get; }

        /// <summary>
        /// 是否已加载
        /// </summary>
        bool IsLoaded { get; }

        /// <summary>
        /// 获取表名
        /// </summary>
        string TableName { get; }
    }

    /// <summary>
    /// 配置表基类
    /// 提供配置表的加载和查询功能
    /// </summary>
    /// <typeparam name="TKey">主键类型</typeparam>
    /// <typeparam name="TValue">配置项类型</typeparam>
    public abstract class ConfigBase<TKey, TValue> : IConfigTable where TValue : class, new()
    {
        /// <summary>
        /// 配置数据字典（主键 -> 配置项）
        /// </summary>
        protected Dictionary<TKey, TValue> _dataDict;

        /// <summary>
        /// 是否已加载
        /// </summary>
        protected bool _isLoaded;

        /// <summary>
        /// 数据库路径
        /// </summary>
        protected string _dbPath;

        /// <summary>
        /// 表名
        /// </summary>
        protected string _tableName;

        /// <summary>
        /// 构造函数
        /// </summary>
        public ConfigBase()
        {
            _dataDict = new Dictionary<TKey, TValue>();
            _isLoaded = false;
        }

        /// <summary>
        /// 获取配置项数量
        /// </summary>
        public int Count => _dataDict.Count;

        /// <summary>
        /// 是否已加载
        /// </summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>
        /// 获取表名
        /// </summary>
        public string TableName => _tableName;

        /// <summary>
        /// 加载配置表
        /// </summary>
        /// <param name="dbPath">数据库文件路径</param>
        /// <param name="tableName">表名</param>
        public virtual void Load(string dbPath, string tableName)
        {
            if (string.IsNullOrEmpty(dbPath))
            {
                throw new ArgumentNullException(nameof(dbPath));
            }

            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            if (_isLoaded)
            {
                Debug.LogWarning($"[ConfigBase] 配置表 {tableName} 已经加载，跳过重复加载");
                return;
            }

            _dbPath = dbPath;
            _tableName = tableName;

            try
            {
                using (var db = new SQLiteHelper(dbPath))
                {
                    // 使用SQLite-net的Table方法查询所有数据
                    var allData = db.QueryConfigTable<TValue>(tableName);

                    Debug.Log($"[ConfigBase] 从表 {tableName} 加载了 {allData.Count} 条配置数据");

                    // 将数据加载到字典中
                    _dataDict.Clear();
                    foreach (var item in allData)
                    {
                        TKey key = GetKey(item);
                        if (key == null)
                        {
                            Debug.LogWarning($"[ConfigBase] 配置项的主键为null，跳过该项");
                            continue;
                        }

                        if (_dataDict.ContainsKey(key))
                        {
                            Debug.LogWarning($"[ConfigBase] 主键重复: {key}，将覆盖旧值");
                        }

                        _dataDict[key] = item;
                    }

                    _isLoaded = true;
                    Debug.Log($"[ConfigBase] 配置表 {tableName} 加载完成，共 {_dataDict.Count} 条有效数据");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConfigBase] 加载配置表失败: {tableName}, 错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 根据主键获取配置项
        /// </summary>
        /// <param name="key">主键</param>
        /// <returns>配置项，如果不存在返回null</returns>
        public TValue GetByKey(TKey key)
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return null;
            }

            if (key == null)
            {
                Debug.LogWarning("[ConfigBase] 查询的主键为null");
                return null;
            }

            if (_dataDict.TryGetValue(key, out TValue value))
            {
                return value;
            }

            Debug.LogWarning($"[ConfigBase] 未找到主键为 {key} 的配置项");
            return null;
        }

        /// <summary>
        /// 获取所有配置项
        /// </summary>
        /// <returns>所有配置项的列表</returns>
        public List<TValue> GetAll()
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return new List<TValue>();
            }

            return _dataDict.Values.ToList();
        }

        /// <summary>
        /// 根据条件查询配置项列表
        /// </summary>
        /// <param name="predicate">查询条件</param>
        /// <returns>符合条件的配置项列表</returns>
        public List<TValue> GetList(Func<TValue, bool> predicate)
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return new List<TValue>();
            }

            if (predicate == null)
            {
                Debug.LogWarning("[ConfigBase] 查询条件为null，返回所有数据");
                return GetAll();
            }

            try
            {
                return _dataDict.Values.Where(predicate).ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConfigBase] 条件查询失败: {ex.Message}");
                return new List<TValue>();
            }
        }

        /// <summary>
        /// 根据条件获取第一个配置项
        /// </summary>
        /// <param name="predicate">查询条件，如果为null则返回第一条数据</param>
        /// <returns>第一个符合条件的配置项，如果不存在返回null</returns>
        public TValue GetFirst(Func<TValue, bool> predicate = null)
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return null;
            }

            try
            {
                if (predicate == null)
                {
                    // 如果条件为null，返回第一条数据
                    return _dataDict.Values.FirstOrDefault();
                }

                return _dataDict.Values.FirstOrDefault(predicate);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConfigBase] 查询第一个配置项失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据条件获取最后一个配置项
        /// </summary>
        /// <param name="predicate">查询条件，如果为null则返回最后一条数据</param>
        /// <returns>最后一个符合条件的配置项，如果不存在返回null</returns>
        public TValue GetLast(Func<TValue, bool> predicate = null)
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return null;
            }

            try
            {
                if (predicate == null)
                {
                    // 如果条件为null，返回最后一条数据
                    return _dataDict.Values.LastOrDefault();
                }

                return _dataDict.Values.LastOrDefault(predicate);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConfigBase] 查询最后一个配置项失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查是否包含指定主键
        /// </summary>
        /// <param name="key">主键</param>
        /// <returns>如果包含返回true，否则返回false</returns>
        public bool ContainsKey(TKey key)
        {
            if (!_isLoaded)
            {
                Debug.LogError("[ConfigBase] 配置表尚未加载，请先调用Load方法");
                return false;
            }

            if (key == null)
            {
                return false;
            }

            return _dataDict.ContainsKey(key);
        }

        /// <summary>
        /// 卸载配置表
        /// </summary>
        public virtual void Unload()
        {
            _dataDict.Clear();
            _isLoaded = false;
            Debug.Log($"[ConfigBase] 配置表 {_tableName} 已卸载");
        }

        /// <summary>
        /// 获取配置项的主键（子类必须实现）
        /// </summary>
        /// <param name="item">配置项</param>
        /// <returns>主键值</returns>
        protected abstract TKey GetKey(TValue item);
    }
}
