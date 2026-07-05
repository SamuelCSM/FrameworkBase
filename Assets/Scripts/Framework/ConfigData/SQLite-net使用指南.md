# SQLite-net 完整使用指南

## 概述

SQLite-net是一个轻量级的ORM（对象关系映射）库，提供LINQ风格的查询API，让你可以用C#代码操作SQLite数据库，无需编写SQL字符串。

## 核心概念

### 1. 数据模型定义

使用C#类和特性定义数据库表结构：

```csharp
using SQLite;

[Table("items")]
public class ItemConfig
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int Type { get; set; }

    [MaxLength(50)]
    public string Name { get; set; }

    public int Quality { get; set; }

    public string Icon { get; set; }

    [MaxLength(200)]
    public string Description { get; set; }

    // 忽略此属性（不存储到数据库）
    [Ignore]
    public bool IsLoaded { get; set; }
}
```

### 2. 数据库连接

```csharp
using SQLite;
using System.IO;

// 创建数据库路径
string dbPath = Path.Combine(Application.persistentDataPath, "game.db");

// 创建连接
var db = new SQLiteConnection(dbPath);

// 使用完后关闭
db.Close();

// 或使用using语句自动关闭
using (var db = new SQLiteConnection(dbPath))
{
    // 数据库操作
}
```

## 特性标注

### 表级特性

```csharp
// 指定表名
[Table("item_configs")]
public class ItemConfig { }
```

### 列级特性

```csharp
public class ItemConfig
{
    // 主键
    [PrimaryKey]
    public int Id { get; set; }

    // 自动递增主键
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    // 索引
    [Indexed]
    public int Type { get; set; }

    // 唯一索引
    [Indexed(Unique = true)]
    public string Code { get; set; }

    // 最大长度
    [MaxLength(50)]
    public string Name { get; set; }

    // 指定列名
    [Column("item_name")]
    public string Name { get; set; }

    // 不为空
    [NotNull]
    public string Name { get; set; }

    // 默认值（通过C#属性初始化器）
    public int Quality { get; set; } = 1;

    // 忽略此属性
    [Ignore]
    public bool IsLoaded { get; set; }

    // 唯一约束
    [Unique]
    public string Code { get; set; }
}
```

## 表操作

### 创建表

```csharp
// 创建单个表
db.CreateTable<ItemConfig>();

// 创建多个表
db.CreateTables<ItemConfig, PlayerData, SkillConfig>();

// 如果表已存在，不会重复创建
// 如果表结构改变，会自动添加新列（但不会删除旧列）
```

### 删除表

```csharp
// 删除表
db.DropTable<ItemConfig>();
```

### 检查表是否存在

```csharp
var tableInfo = db.GetTableInfo("items");
bool tableExists = tableInfo.Count > 0;
```

## CRUD操作

### 插入（Create）

```csharp
// 插入单条
var item = new ItemConfig 
{ 
    Type = 1, 
    Name = "铁剑", 
    Quality = 2 
};
db.Insert(item);

// 插入后，如果有AutoIncrement主键，会自动填充Id
Debug.Log($"插入的ID: {item.Id}");

// 插入或替换（如果主键存在则更新）
db.InsertOrReplace(item);

// 批量插入
var items = new List<ItemConfig>
{
    new ItemConfig { Type = 1, Name = "铁剑", Quality = 2 },
    new ItemConfig { Type = 1, Name = "钢剑", Quality = 3 },
    new ItemConfig { Type = 2, Name = "布甲", Quality = 1 }
};
db.InsertAll(items);

// 批量插入（使用事务，性能更好）
db.RunInTransaction(() =>
{
    foreach (var i in items)
    {
        db.Insert(i);
    }
});
```

### 查询（Read）

```csharp
// 查询所有
var allItems = db.Table<ItemConfig>().ToList();

// 条件查询
var swords = db.Table<ItemConfig>()
    .Where(x => x.Type == 1)
    .ToList();

// 复杂查询
var highQualityItems = db.Table<ItemConfig>()
    .Where(x => x.Quality >= 3 && x.Type == 1)
    .OrderByDescending(x => x.Quality)
    .ThenBy(x => x.Name)
    .Take(10)
    .ToList();

// 查询单条
var item = db.Table<ItemConfig>()
    .Where(x => x.Id == 1001)
    .FirstOrDefault();

// 根据主键查询
var item = db.Get<ItemConfig>(1001);

// 查询第一条
var firstItem = db.Table<ItemConfig>().First();

// 查询第一条或默认值
var firstItem = db.Table<ItemConfig>().FirstOrDefault();

// 计数
int count = db.Table<ItemConfig>().Count();
int swordCount = db.Table<ItemConfig>().Count(x => x.Type == 1);

// 分页查询
int pageSize = 20;
int pageIndex = 0;
var items = db.Table<ItemConfig>()
    .Skip(pageIndex * pageSize)
    .Take(pageSize)
    .ToList();

// 使用原始SQL查询
var items = db.Query<ItemConfig>("SELECT * FROM items WHERE type = ?", 1);

// 查询单个值
int count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM items");
```

### 更新（Update）

```csharp
// 更新对象
var item = db.Get<ItemConfig>(1001);
item.Name = "精铁剑";
item.Quality = 3;
db.Update(item);

// 批量更新
var items = db.Table<ItemConfig>().Where(x => x.Type == 1).ToList();
foreach (var i in items)
{
    i.Quality += 1;
}
db.UpdateAll(items);

// 使用原始SQL更新
db.Execute("UPDATE items SET quality = quality + 1 WHERE type = ?", 1);
```

### 删除（Delete）

```csharp
// 删除对象
var item = db.Get<ItemConfig>(1001);
db.Delete(item);

// 根据主键删除
db.Delete<ItemConfig>(1001);

// 删除所有
db.DeleteAll<ItemConfig>();

// 条件删除（使用原始SQL）
db.Execute("DELETE FROM items WHERE quality < ?", 2);
```

## 事务处理

### 基本事务

```csharp
db.BeginTransaction();
try
{
    db.Insert(item1);
    db.Update(item2);
    db.Delete(item3);
    
    db.Commit();
}
catch (System.Exception ex)
{
    db.Rollback();
    Debug.LogError($"事务失败: {ex.Message}");
    throw;
}
```

### RunInTransaction

```csharp
// 更简洁的事务处理
db.RunInTransaction(() =>
{
    db.Insert(item1);
    db.Update(item2);
    db.Delete(item3);
});

// 如果lambda中抛出异常，会自动回滚
```

## 高级查询

### LINQ查询

```csharp
// Where
var items = db.Table<ItemConfig>()
    .Where(x => x.Type == 1 && x.Quality >= 2)
    .ToList();

// OrderBy / OrderByDescending
var items = db.Table<ItemConfig>()
    .OrderBy(x => x.Type)
    .ThenByDescending(x => x.Quality)
    .ToList();

// Take / Skip
var items = db.Table<ItemConfig>()
    .Take(10)
    .ToList();

// First / FirstOrDefault
var item = db.Table<ItemConfig>()
    .Where(x => x.Id == 1001)
    .FirstOrDefault();

// Count
int count = db.Table<ItemConfig>()
    .Where(x => x.Type == 1)
    .Count();

// Any
bool hasItems = db.Table<ItemConfig>()
    .Where(x => x.Type == 1)
    .Any();

// Select（投影）
var names = db.Table<ItemConfig>()
    .Select(x => x.Name)
    .ToList();
```

### 原始SQL查询

```csharp
// 查询对象列表
var items = db.Query<ItemConfig>(
    "SELECT * FROM items WHERE type = ? AND quality >= ?", 
    1, 2
);

// 查询单个值
int count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM items");

// 执行SQL命令
db.Execute("UPDATE items SET quality = quality + 1");

// 带参数的SQL命令
db.Execute("DELETE FROM items WHERE id = ?", 1001);
```

## 数据类型支持

### 基本类型

```csharp
public class DataTypes
{
    public int IntValue { get; set; }           // INTEGER
    public long LongValue { get; set; }         // INTEGER (64位)
    public float FloatValue { get; set; }       // REAL
    public double DoubleValue { get; set; }     // REAL
    public bool BoolValue { get; set; }         // INTEGER (0/1)
    public string StringValue { get; set; }     // TEXT
    public byte[] BlobValue { get; set; }       // BLOB
}
```

### 可空类型

```csharp
public class NullableTypes
{
    public int? NullableInt { get; set; }
    public long? NullableLong { get; set; }
    public bool? NullableBool { get; set; }
    public double? NullableDouble { get; set; }
}
```

### 枚举类型

```csharp
public enum ItemType
{
    Weapon = 1,
    Armor = 2,
    Consumable = 3
}

public class ItemConfig
{
    public int Id { get; set; }
    
    // 枚举会自动转换为整数存储
    public ItemType Type { get; set; }
}

// 查询
var weapons = db.Table<ItemConfig>()
    .Where(x => x.Type == ItemType.Weapon)
    .ToList();
```

### 日期时间

```csharp
public class PlayerData
{
    public int Id { get; set; }
    
    // DateTime会自动转换为字符串存储（ISO 8601格式）
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
}

// 查询最近登录的玩家
var recentPlayers = db.Table<PlayerData>()
    .Where(x => x.LastLoginAt > DateTime.Now.AddDays(-7))
    .ToList();
```

## 索引和约束

### 创建索引

```csharp
// 使用特性创建索引
public class ItemConfig
{
    [PrimaryKey]
    public int Id { get; set; }

    // 单列索引
    [Indexed]
    public int Type { get; set; }

    // 唯一索引
    [Indexed(Unique = true)]
    public string Code { get; set; }
}

// 使用SQL创建复合索引
db.Execute("CREATE INDEX IF NOT EXISTS idx_type_quality ON items(type, quality)");
```

### 唯一约束

```csharp
public class ItemConfig
{
    [PrimaryKey]
    public int Id { get; set; }

    // 唯一约束
    [Unique]
    public string Code { get; set; }
}
```

## 性能优化

### 1. 使用事务批量操作

```csharp
// ❌ 慢 - 每次操作都提交
for (int i = 0; i < 1000; i++)
{
    db.Insert(new ItemConfig { Name = $"Item{i}" });
}

// ✅ 快 - 使用事务（快100倍以上）
db.RunInTransaction(() =>
{
    for (int i = 0; i < 1000; i++)
    {
        db.Insert(new ItemConfig { Name = $"Item{i}" });
    }
});

// ✅ 更快 - 使用InsertAll
var items = new List<ItemConfig>();
for (int i = 0; i < 1000; i++)
{
    items.Add(new ItemConfig { Name = $"Item{i}" });
}
db.InsertAll(items);
```

### 2. 创建索引

```csharp
// 为常查询的列创建索引
db.Execute("CREATE INDEX IF NOT EXISTS idx_item_type ON items(type)");
db.Execute("CREATE INDEX IF NOT EXISTS idx_item_quality ON items(quality)");

// 复合索引
db.Execute("CREATE INDEX IF NOT EXISTS idx_type_quality ON items(type, quality)");
```

### 3. 使用编译语句

```csharp
// 对于重复执行的查询，SQLite-net会自动缓存编译后的语句
var query = db.Table<ItemConfig>().Where(x => x.Type == 1);

// 多次执行
var result1 = query.ToList();
var result2 = query.ToList();
```

### 4. 限制结果集

```csharp
// 使用Take限制结果数量
var items = db.Table<ItemConfig>()
    .Take(100)
    .ToList();

// 分页查询
var items = db.Table<ItemConfig>()
    .Skip(pageIndex * pageSize)
    .Take(pageSize)
    .ToList();
```

### 5. 优化数据库设置

```csharp
// 设置同步模式（提高性能，但降低安全性）
db.Execute("PRAGMA synchronous = NORMAL");

// 设置日志模式
db.Execute("PRAGMA journal_mode = WAL");

// 设置缓存大小（单位：页，默认2000页）
db.Execute("PRAGMA cache_size = 10000");

// 设置临时存储位置
db.Execute("PRAGMA temp_store = MEMORY");
```

## 最佳实践

### 1. 使用using语句

```csharp
// ✅ 推荐 - 自动关闭连接
using (var db = new SQLiteConnection(dbPath))
{
    // 数据库操作
}

// ❌ 不推荐 - 需要手动关闭
var db = new SQLiteConnection(dbPath);
// 操作...
db.Close();
```

### 2. 单例模式管理连接

```csharp
public class DatabaseManager
{
    private static DatabaseManager _instance;
    private SQLiteConnection _db;

    public static DatabaseManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = new DatabaseManager();
            }
            return _instance;
        }
    }

    private DatabaseManager()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, "game.db");
        _db = new SQLiteConnection(dbPath);
        InitializeTables();
    }

    private void InitializeTables()
    {
        _db.CreateTable<ItemConfig>();
        _db.CreateTable<PlayerData>();
    }

    public SQLiteConnection DB => _db;

    public void Close()
    {
        _db?.Close();
    }
}
```

### 3. 错误处理

```csharp
try
{
    var items = db.Table<ItemConfig>().ToList();
}
catch (SQLiteException ex)
{
    Debug.LogError($"SQLite错误: {ex.Message}");
    // 处理特定的SQLite错误
}
catch (System.Exception ex)
{
    Debug.LogError($"未知错误: {ex.Message}");
}
```

### 4. 数据验证

```csharp
public class ItemConfig
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull, MaxLength(50)]
    public string Name { get; set; }

    public int Quality { get; set; }

    // 在插入前验证
    public bool Validate()
    {
        if (string.IsNullOrEmpty(Name))
            return false;
        if (Quality < 1 || Quality > 5)
            return false;
        return true;
    }
}

// 使用
var item = new ItemConfig { Name = "铁剑", Quality = 2 };
if (item.Validate())
{
    db.Insert(item);
}
```

## 常见问题

### Q: 如何处理表结构变更？

**A:** SQLite-net会自动添加新列，但不会删除旧列。

```csharp
// 方法1：删除并重建表（会丢失数据）
db.DropTable<ItemConfig>();
db.CreateTable<ItemConfig>();

// 方法2：手动迁移
var tableInfo = db.Query<dynamic>("PRAGMA table_info(items)");
bool hasNewColumn = tableInfo.Any(x => x.name == "new_column");

if (!hasNewColumn)
{
    db.Execute("ALTER TABLE items ADD COLUMN new_column INTEGER DEFAULT 0");
}
```

### Q: 如何处理多线程？

**A:** SQLite支持多线程，但建议每个线程使用独立的连接。

```csharp
// 在Unity主线程中使用
var db = new SQLiteConnection(dbPath);

// 如果需要在其他线程中使用，创建新连接
// 注意：Unity的大多数API只能在主线程调用
```

### Q: 如何备份数据库？

**A:** 直接复制数据库文件。

```csharp
string dbPath = Path.Combine(Application.persistentDataPath, "game.db");
string backupPath = dbPath + ".backup";

// 关闭连接
db.Close();

// 复制文件
File.Copy(dbPath, backupPath, true);

// 重新打开连接
db = new SQLiteConnection(dbPath);
```

### Q: 性能如何？

**A:** 使用事务批量操作可以达到每秒数万次插入。

```csharp
// 测试：插入10000条记录
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

db.RunInTransaction(() =>
{
    for (int i = 0; i < 10000; i++)
    {
        db.Insert(new ItemConfig { Name = $"Item{i}", Quality = i % 5 });
    }
});

stopwatch.Stop();
Debug.Log($"插入10000条记录耗时: {stopwatch.ElapsedMilliseconds}ms");
// 通常在100-200ms之间
```

## 总结

SQLite-net是一个优秀的ORM库，适合Unity项目：

- ✅ **简洁易用** - LINQ风格，学习曲线平缓
- ✅ **功能完整** - 支持大部分常用功能
- ✅ **性能良好** - 对于大多数场景足够快
- ✅ **维护活跃** - 社区支持好，更新频繁
- ✅ **跨平台** - 支持所有Unity平台

## 相关文档

- 📄 [快速开始指南.md](./快速开始指南.md) - 5分钟快速配置
- 📄 [ConfigManager示例.md](./ConfigManager示例.md) - 实际项目示例
- 📄 [最佳实践.md](./最佳实践.md) - 性能优化和常见问题
