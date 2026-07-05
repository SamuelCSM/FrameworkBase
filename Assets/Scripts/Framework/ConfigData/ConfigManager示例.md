# ConfigManager 配置管理器示例

## 概述

本文档展示如何使用SQLite-net在实际项目中实现配置表管理器。

## 完整实现

### 1. 配置管理器

```csharp
using SQLite;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Framework.Data
{
    /// <summary>
    /// 配置表管理器
    /// 使用SQLite-net管理游戏配置数据
    /// </summary>
    public class ConfigManager
    {
        private static ConfigManager _instance;
        private SQLiteConnection _db;
        private readonly string _dbPath;

        public static ConfigManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ConfigManager();
                }
                return _instance;
            }
        }

        private ConfigManager()
        {
            _dbPath = Path.Combine(Application.persistentDataPath, "config.db");
            Initialize();
        }

        /// <summary>
        /// 初始化配置数据库
        /// </summary>
        private void Initialize()
        {
            try
            {
                // 创建数据库连接
                _db = new SQLiteConnection(_dbPath);
                
                // 创建所有配置表
                CreateTables();
                
                // 导入初始数据（如果需要）
                ImportInitialData();
                
                Debug.Log($"[ConfigManager] 配置数据库初始化完成: {_dbPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ConfigManager] 初始化失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 创建所有配置表
        /// </summary>
        private void CreateTables()
        {
            _db.CreateTable<ItemConfig>();
            _db.CreateTable<SkillConfig>();
            _db.CreateTable<LevelConfig>();
            _db.CreateTable<MonsterConfig>();
            
            // 创建索引
            _db.Execute("CREATE INDEX IF NOT EXISTS idx_item_type ON items(type)");
            _db.Execute("CREATE INDEX IF NOT EXISTS idx_skill_type ON skills(type)");
            
            Debug.Log("[ConfigManager] 配置表创建完成");
        }

        /// <summary>
        /// 导入初始数据
        /// </summary>
        private void ImportInitialData()
        {
            // 检查是否已有数据
            int itemCount = _db.Table<ItemConfig>().Count();
            if (itemCount > 0)
            {
                Debug.Log("[ConfigManager] 配置数据已存在，跳过导入");
                return;
            }

            // 导入初始数据
            _db.RunInTransaction(() =>
            {
                // 导入物品配置
                _db.InsertAll(new List<ItemConfig>
                {
                    new ItemConfig { Id = 1001, Type = 1, Name = "铁剑", Quality = 1, Icon = "sword_01", Description = "一把普通的铁剑" },
                    new ItemConfig { Id = 1002, Type = 1, Name = "钢剑", Quality = 2, Icon = "sword_02", Description = "一把锋利的钢剑" },
                    new ItemConfig { Id = 2001, Type = 2, Name = "布甲", Quality = 1, Icon = "armor_01", Description = "简单的布制护甲" },
                });

                // 导入技能配置
                _db.InsertAll(new List<SkillConfig>
                {
                    new SkillConfig { Id = 3001, Type = 1, Name = "火球术", Level = 1, ManaCost = 10, Damage = 50, Description = "发射一个火球" },
                    new SkillConfig { Id = 3002, Type = 1, Name = "冰冻术", Level = 1, ManaCost = 15, Damage = 40, Description = "冰冻敌人" },
                });
            });

            Debug.Log("[ConfigManager] 初始数据导入完成");
        }

        #region 物品配置

        /// <summary>
        /// 获取所有物品配置
        /// </summary>
        public List<ItemConfig> GetAllItems()
        {
            return _db.Table<ItemConfig>().OrderBy(x => x.Id).ToList();
        }

        /// <summary>
        /// 根据ID获取物品配置
        /// </summary>
        public ItemConfig GetItemById(int id)
        {
            return _db.Get<ItemConfig>(id);
        }

        /// <summary>
        /// 根据类型获取物品配置
        /// </summary>
        public List<ItemConfig> GetItemsByType(int type)
        {
            return _db.Table<ItemConfig>()
                .Where(x => x.Type == type)
                .OrderBy(x => x.Quality)
                .ToList();
        }

        /// <summary>
        /// 获取高品质物品
        /// </summary>
        public List<ItemConfig> GetHighQualityItems(int minQuality)
        {
            return _db.Table<ItemConfig>()
                .Where(x => x.Quality >= minQuality)
                .OrderByDescending(x => x.Quality)
                .ThenBy(x => x.Id)
                .ToList();
        }

        /// <summary>
        /// 搜索物品（按名称）
        /// </summary>
        public List<ItemConfig> SearchItems(string keyword)
        {
            return _db.Query<ItemConfig>(
                "SELECT * FROM items WHERE name LIKE ? ORDER BY quality DESC",
                $"%{keyword}%"
            );
        }

        #endregion

        #region 技能配置

        /// <summary>
        /// 获取所有技能配置
        /// </summary>
        public List<SkillConfig> GetAllSkills()
        {
            return _db.Table<SkillConfig>().OrderBy(x => x.Id).ToList();
        }

        /// <summary>
        /// 根据ID获取技能配置
        /// </summary>
        public SkillConfig GetSkillById(int id)
        {
            return _db.Get<SkillConfig>(id);
        }

        /// <summary>
        /// 根据类型获取技能配置
        /// </summary>
        public List<SkillConfig> GetSkillsByType(int type)
        {
            return _db.Table<SkillConfig>()
                .Where(x => x.Type == type)
                .OrderBy(x => x.Level)
                .ToList();
        }

        /// <summary>
        /// 获取指定等级范围的技能
        /// </summary>
        public List<SkillConfig> GetSkillsByLevelRange(int minLevel, int maxLevel)
        {
            return _db.Table<SkillConfig>()
                .Where(x => x.Level >= minLevel && x.Level <= maxLevel)
                .OrderBy(x => x.Level)
                .ToList();
        }

        #endregion

        #region 等级配置

        /// <summary>
        /// 获取所有等级配置
        /// </summary>
        public List<LevelConfig> GetAllLevels()
        {
            return _db.Table<LevelConfig>().OrderBy(x => x.Level).ToList();
        }

        /// <summary>
        /// 根据等级获取配置
        /// </summary>
        public LevelConfig GetLevelConfig(int level)
        {
            return _db.Table<LevelConfig>()
                .Where(x => x.Level == level)
                .FirstOrDefault();
        }

        /// <summary>
        /// 根据经验值获取等级
        /// </summary>
        public int GetLevelByExperience(long experience)
        {
            var config = _db.Table<LevelConfig>()
                .Where(x => x.RequiredExp <= experience)
                .OrderByDescending(x => x.Level)
                .FirstOrDefault();

            return config?.Level ?? 1;
        }

        #endregion

        #region 怪物配置

        /// <summary>
        /// 获取所有怪物配置
        /// </summary>
        public List<MonsterConfig> GetAllMonsters()
        {
            return _db.Table<MonsterConfig>().OrderBy(x => x.Id).ToList();
        }

        /// <summary>
        /// 根据ID获取怪物配置
        /// </summary>
        public MonsterConfig GetMonsterById(int id)
        {
            return _db.Get<MonsterConfig>(id);
        }

        /// <summary>
        /// 根据等级范围获取怪物
        /// </summary>
        public List<MonsterConfig> GetMonstersByLevelRange(int minLevel, int maxLevel)
        {
            return _db.Table<MonsterConfig>()
                .Where(x => x.Level >= minLevel && x.Level <= maxLevel)
                .OrderBy(x => x.Level)
                .ToList();
        }

        #endregion

        /// <summary>
        /// 关闭数据库连接
        /// </summary>
        public void Close()
        {
            _db?.Close();
            Debug.Log("[ConfigManager] 配置数据库已关闭");
        }
    }
}
```

### 2. 数据模型定义

```csharp
using SQLite;

namespace Framework.Data
{
    /// <summary>
    /// 物品配置表
    /// </summary>
    [Table("items")]
    public class ItemConfig
    {
        [PrimaryKey]
        public int Id { get; set; }

        [Indexed]
        public int Type { get; set; }

        [MaxLength(50), NotNull]
        public string Name { get; set; }

        public int Quality { get; set; }

        [MaxLength(50)]
        public string Icon { get; set; }

        [MaxLength(200)]
        public string Description { get; set; }
    }

    /// <summary>
    /// 技能配置表
    /// </summary>
    [Table("skills")]
    public class SkillConfig
    {
        [PrimaryKey]
        public int Id { get; set; }

        [Indexed]
        public int Type { get; set; }

        [MaxLength(50), NotNull]
        public string Name { get; set; }

        public int Level { get; set; }

        public int ManaCost { get; set; }

        public int Damage { get; set; }

        [MaxLength(200)]
        public string Description { get; set; }
    }

    /// <summary>
    /// 等级配置表
    /// </summary>
    [Table("levels")]
    public class LevelConfig
    {
        [PrimaryKey]
        public int Level { get; set; }

        public long RequiredExp { get; set; }

        public int MaxHp { get; set; }

        public int MaxMp { get; set; }

        public int Attack { get; set; }

        public int Defense { get; set; }
    }

    /// <summary>
    /// 怪物配置表
    /// </summary>
    [Table("monsters")]
    public class MonsterConfig
    {
        [PrimaryKey]
        public int Id { get; set; }

        [MaxLength(50), NotNull]
        public string Name { get; set; }

        public int Level { get; set; }

        public int Hp { get; set; }

        public int Attack { get; set; }

        public int Defense { get; set; }

        public int ExpReward { get; set; }

        [MaxLength(50)]
        public string Model { get; set; }
    }
}
```

### 3. 使用示例

```csharp
using UnityEngine;
using Framework.Data;

public class GameManager : MonoBehaviour
{
    void Start()
    {
        // 初始化配置管理器（自动完成）
        var configManager = ConfigManager.Instance;

        // 测试物品配置
        TestItemConfig();

        // 测试技能配置
        TestSkillConfig();

        // 测试等级配置
        TestLevelConfig();
    }

    void TestItemConfig()
    {
        Debug.Log("=== 测试物品配置 ===");

        // 获取所有物品
        var allItems = ConfigManager.Instance.GetAllItems();
        Debug.Log($"总共有 {allItems.Count} 个物品");

        // 获取指定ID的物品
        var item = ConfigManager.Instance.GetItemById(1001);
        if (item != null)
        {
            Debug.Log($"物品: {item.Name}, 品质: {item.Quality}");
        }

        // 获取指定类型的物品
        var weapons = ConfigManager.Instance.GetItemsByType(1);
        Debug.Log($"武器数量: {weapons.Count}");

        // 获取高品质物品
        var highQualityItems = ConfigManager.Instance.GetHighQualityItems(2);
        Debug.Log($"高品质物品数量: {highQualityItems.Count}");

        // 搜索物品
        var searchResults = ConfigManager.Instance.SearchItems("剑");
        Debug.Log($"搜索'剑'找到 {searchResults.Count} 个结果");
    }

    void TestSkillConfig()
    {
        Debug.Log("=== 测试技能配置 ===");

        // 获取所有技能
        var allSkills = ConfigManager.Instance.GetAllSkills();
        Debug.Log($"总共有 {allSkills.Count} 个技能");

        // 获取指定类型的技能
        var magicSkills = ConfigManager.Instance.GetSkillsByType(1);
        Debug.Log($"魔法技能数量: {magicSkills.Count}");

        // 获取指定等级范围的技能
        var lowLevelSkills = ConfigManager.Instance.GetSkillsByLevelRange(1, 5);
        Debug.Log($"1-5级技能数量: {lowLevelSkills.Count}");
    }

    void TestLevelConfig()
    {
        Debug.Log("=== 测试等级配置 ===");

        // 根据经验值获取等级
        long experience = 1000;
        int level = ConfigManager.Instance.GetLevelByExperience(experience);
        Debug.Log($"经验值 {experience} 对应等级: {level}");

        // 获取等级配置
        var levelConfig = ConfigManager.Instance.GetLevelConfig(level);
        if (levelConfig != null)
        {
            Debug.Log($"等级 {level} 配置: HP={levelConfig.MaxHp}, MP={levelConfig.MaxMp}");
        }
    }

    void OnApplicationQuit()
    {
        // 关闭配置管理器
        ConfigManager.Instance.Close();
    }
}
```

## 高级用法

### 1. 缓存优化

```csharp
public class ConfigManager
{
    // 缓存常用配置
    private Dictionary<int, ItemConfig> _itemCache = new Dictionary<int, ItemConfig>();

    public ItemConfig GetItemById(int id)
    {
        // 先从缓存获取
        if (_itemCache.TryGetValue(id, out var item))
        {
            return item;
        }

        // 从数据库查询
        item = _db.Get<ItemConfig>(id);
        
        // 加入缓存
        if (item != null)
        {
            _itemCache[id] = item;
        }

        return item;
    }

    public void ClearCache()
    {
        _itemCache.Clear();
    }
}
```

### 2. 热更新支持

```csharp
public class ConfigManager
{
    /// <summary>
    /// 重新加载配置（用于热更新）
    /// </summary>
    public void ReloadConfigs()
    {
        // 清除缓存
        ClearCache();

        // 重新导入数据
        _db.RunInTransaction(() =>
        {
            // 删除旧数据
            _db.DeleteAll<ItemConfig>();
            _db.DeleteAll<SkillConfig>();

            // 导入新数据
            ImportInitialData();
        });

        Debug.Log("[ConfigManager] 配置重新加载完成");
    }
}
```

### 3. 数据验证

```csharp
public class ConfigManager
{
    /// <summary>
    /// 验证配置数据完整性
    /// </summary>
    public bool ValidateConfigs()
    {
        try
        {
            // 检查物品配置
            var items = _db.Table<ItemConfig>().ToList();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Name))
                {
                    Debug.LogError($"物品 {item.Id} 名称为空");
                    return false;
                }
                if (item.Quality < 1 || item.Quality > 5)
                {
                    Debug.LogError($"物品 {item.Id} 品质超出范围");
                    return false;
                }
            }

            // 检查技能配置
            var skills = _db.Table<SkillConfig>().ToList();
            foreach (var skill in skills)
            {
                if (skill.ManaCost < 0)
                {
                    Debug.LogError($"技能 {skill.Id} 魔法消耗为负数");
                    return false;
                }
            }

            Debug.Log("[ConfigManager] 配置验证通过");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[ConfigManager] 配置验证失败: {ex.Message}");
            return false;
        }
    }
}
```

## 总结

这个ConfigManager示例展示了：

- ✅ 单例模式管理数据库连接
- ✅ 自动创建表和索引
- ✅ 导入初始数据
- ✅ 完整的CRUD操作
- ✅ 类型安全的查询
- ✅ 缓存优化
- ✅ 热更新支持
- ✅ 数据验证

你可以根据项目需求进行扩展和定制。

## 相关文档

- 📄 [SQLite-net使用指南.md](./SQLite-net使用指南.md) - 完整的API文档
- 📄 [快速开始指南.md](./快速开始指南.md) - 5分钟快速配置
- 📄 [最佳实践.md](./最佳实践.md) - 性能优化和常见问题
