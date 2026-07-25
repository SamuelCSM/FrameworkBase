using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Framework.Data
{
    /// <summary>
    /// 无主键配置列表基类，适用于关系表、多对多表、掉落明细等允许首列重复的数据。
    /// <para>
    /// 与 <see cref="ConfigBase{TKey,TValue}"/> 一样由 <see cref="ConfigManager"/> 按需加载和缓存，
    /// 但保留 SQLite 中的每一行，不构建主键字典，也不会因重复字段值覆盖记录。
    /// </para>
    /// </summary>
    /// <typeparam name="TValue">单行配置类型。</typeparam>
    public abstract class ConfigListBase<TValue> : IConfigTable where TValue : class, new()
    {
        /// <summary>按数据库查询顺序保存的全部配置行。</summary>
        protected readonly List<TValue> _items = new List<TValue>();

        /// <summary>当前实例是否已完成加载。</summary>
        protected bool _isLoaded;

        /// <summary>当前加载使用的数据库路径。</summary>
        protected string _dbPath;

        /// <summary>当前加载使用的 SQLite 表名。</summary>
        protected string _tableName;

        /// <inheritdoc />
        public int Count => _items.Count;

        /// <inheritdoc />
        public bool IsLoaded => _isLoaded;

        /// <inheritdoc />
        public string TableName => _tableName;

        /// <summary>不复制地读取当前列表；调用方不得修改其中的配置对象。</summary>
        public IReadOnlyList<TValue> Items => _items;

        /// <inheritdoc />
        public virtual void Load(string dbPath, string tableName)
        {
            if (string.IsNullOrEmpty(dbPath))
                throw new ArgumentNullException(nameof(dbPath));
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));
            if (_isLoaded)
            {
                GameLog.Warning($"[ConfigListBase] 配置表 {tableName} 已加载，跳过重复加载");
                return;
            }

            _dbPath = dbPath;
            _tableName = tableName;
            using (var db = new SQLiteHelper(dbPath))
            {
                _items.Clear();
                _items.AddRange(db.QueryConfigTable<TValue>(tableName));
            }

            _isLoaded = true;
            GameLog.Log($"[ConfigListBase] 配置表 {tableName} 加载完成，共 {_items.Count} 行");
        }

        /// <summary>返回全部配置行的副本，避免业务侧改写内部缓存结构。</summary>
        public List<TValue> GetAll()
        {
            EnsureLoaded();
            return new List<TValue>(_items);
        }

        /// <summary>按条件筛选配置行并返回副本。</summary>
        public List<TValue> GetList(Func<TValue, bool> predicate)
        {
            EnsureLoaded();
            if (predicate == null) return GetAll();
            return _items.Where(predicate).ToList();
        }

        /// <summary>返回第一条匹配记录；没有匹配项时返回 null。</summary>
        public TValue GetFirst(Func<TValue, bool> predicate = null)
        {
            EnsureLoaded();
            return predicate == null ? _items.FirstOrDefault() : _items.FirstOrDefault(predicate);
        }

        /// <inheritdoc />
        public virtual void Unload()
        {
            _items.Clear();
            _isLoaded = false;
        }

        private void EnsureLoaded()
        {
            if (!_isLoaded)
                throw new InvalidOperationException("ConfigListBase 尚未加载，请通过 ConfigManager.GetConfig 获取。");
        }
    }
}
