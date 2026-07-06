# SQLiteHelper 使用指南

## 概述

SQLiteHelper是基于SQLite-net封装的工具类，提供了简化的数据库操作接口，包括Query/QueryFirst/Execute方法和完整的事务支持。

## 特点

- ✅ **简化接口** - 提供Query/QueryFirst/Execute等常用方法
- ✅ **事务支持** - BeginTransaction/Commit/Rollback
- ✅ **自动映射** - 查询结果自动映射到C#对象
- ✅ **LINQ查询** - 支持SQLite-net的LINQ查询
- ✅ **完整CRUD** - Insert/Update/Delete等操作
- ✅ **错误处理** - 完整的异常处理和日志记录
- ✅ **资源管理** - 实现IDisposable，支持using语句

## 快速开始

### 1. 创建连接

```csharp
using Framework.Data;
using System.IO;

string dbPath = Path.Combine(Application.persistentDataPath, "game.db");

using (var db = new SQLiteHelper(dbPath))
{
    // 数据库操作
}
// 自动关闭连接
```

### 2. 创建表

```csharp
using (var db = new SQLiteHelper(dbPath))
{
    // 创建表（基于类定义）
    db.CreateTable<ItemConfig>();
    db.CreateTable<PlayerData>();
}
```

### 3. 基本操作

```csharp
using (var db = new SQLiteHelper(dbPath))
{
    // 插入
    db.Insert(new ItemConfig { Type = 1, Name = "铁剑", Quality = 2 });
    
    // 查询
    var items = db.Query<ItemConfig>("SELECT * FROM items WHERE type = ?", 1);
    
    // 查询单条
    var item = db.QueryFirst<ItemConfig>("SELECT * FROM items WHERE id = ?", 1001);
    
    // 更新
    item.Quality = 3;
    db.Update(item);
    
    // 删除
    db.Delete(item);
}
```

## API参考

### 查询方法

#### Query<T>

查询多条记录。

```csharp
public List<T> Query<T>(string sql, params object[] args) where T : new()
```

**示例：**
```csharp
// 查询所有武器
var weapons = db.Query<ItemConfig>("SELECT * FROM items WHERE type = ?", 1);

// 查询高品质物品
var highQualityItems = db.Query<ItemConfig>(
    "SELECT * FROM items WHERE quality >= ? ORDER BY quality DESC", 
    3
);
```

#### QueryFirst<T>

查询单条记录。

```csharp
public T QueryFirst<T>(string sql, params object[] args) where T : new()
```

**示例：**
```csharp
// 查询指定ID的物品
var item = db.QueryFirst<ItemConfig>("SELECT * FROM items WHERE id = ?", 1001);

// 查询第一个高品质物品
var firstHighQuality = db.QueryFirst<ItemConfig>(
    "SELECT * FROM items WHERE quality >= ? ORDER BY quality DESC LIMIT 1", 
    3
);
```

#### Table<T>

获取LINQ查询对象。

```csharp
public TableQuery<T> Table<T>() where T : new()
```

**示例：**
```csharp
// LINQ查询
var items = db.Table<ItemConfig>()
    .Where(x => x.Type == 1 && x.Quality >= 2)
    .OrderByDescending(x => x.Quality)
    .ToList();
```

#### Get<T>

根据主键获取记录。

```csharp
public T Get<T>(object pk) where T : new()
```

**示例：**
```csharp
// 根据主键获取
var item = db.Get<ItemConfig>(1001);
```

#### Find<T>

尝试根据主键获取记录（不存在返回null）。

```csharp
public T Find<T>(object pk) where T : new()
```

**示例：**
```csharp
// 查找记录
var item = db.Find<ItemConfig>(1001);
if (item != null)
{
    Debug.Log($"找到物品: {item.Name}");
}
```

### 执行方法

#### Execute

执行SQL命令（INSERT、UPDATE、DELETE等）。

```csharp
public int Execute(string sql, params object[] args)
```

**示例：**
```csharp
// 更新
int affected = db.Execute("UPDATE items SET quality = ? WHERE id = ?", 3, 1001);

// 删除
int deleted = db.Execute("DELETE FROM items WHERE quality < ?", 2);

// 创建索引
db.Execute("CREATE INDEX IF NOT EXISTS idx_item_type ON items(type)");
```

#### ExecuteScalar<T>

执行标量查询（返回单个值）。

```csharp
public T ExecuteScalar<T>(string sql, params object[] args)
```

**示例：**
```csharp
// 查询总数
int count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM items");

// 查询最大值
int maxQuality = db.ExecuteScalar<int>("SELECT MAX(quality) FROM items");
```

### 插入方法

#### Insert

插入单条记录。

```csharp
public int Insert(object obj)
```

**示例：**
```csharp
var item = new ItemConfig 
{ 
    Type = 1, 
    Name = "铁剑", 
    Quality = 2 
};
db.Insert(item);

// 如果有AutoIncrement主键，会自动填充Id
Debug.Log($"插入的ID: {item.Id}");
```

#### InsertOrReplace

插入或替换记录。

```csharp
public int InsertOrReplace(object obj)
```

**示例：**
```csharp
// 如果主键存在则更新，否则插入
db.InsertOrReplace(item);
```

#### InsertAll

批量插入记录。

```csharp
public int InsertAll(System.Collections.IEnumerable objects)
```

**示例：**
```csharp
var items = new List<ItemConfig>
{
    new ItemConfig { Type = 1, Name = "铁剑", Quality = 2 },
    new ItemConfig { Type = 1, Name = "钢剑", Quality = 3 },
    new ItemConfig { Type = 2, Name = "布甲", Quality = 1 }
};
db.InsertAll(items);
```

### 更新方法

#### Update

更新记录。

```csharp
public int Update(object obj)
```

**示例：**
```csharp
var item = db.Get<ItemConfig>(1001);
item.Quality = 3;
db.Update(item);
```

#### UpdateAll

批量更新记录。

```csharp
public int UpdateAll(System.Collections.IEnumerable objects)
```

**示例：**
```csharp
var items = db.Table<ItemConfig>().Where(x => x.Type == 1).ToList();
foreach (var item in items)
{
    item.Quality += 1;
}
db.UpdateAll(items);
```

### 删除方法

#### Delete

删除记录。

```csharp
public int Delete(object obj)
```

**示例：**
```csharp
var item = db.Get<ItemConfig>(1001);
db.Delete(item);
```

#### Delete<T>

根据主键删除记录。

```csharp
public int Delete<T>(object pk)
```

**示例：**
```csharp
db.Delete<ItemConfig>(1001);
```

#### DeleteAll<T>

删除所有记录。

```csharp
public int DeleteAll<T>()
```

**示例：**
```csharp
int count = db.DeleteAll<ItemConfig>();
Debug.Log($"删除了 {count} 条记录");
```

### 表操作

#### CreateTable<T>

创建表。

```csharp
public void CreateTable<T>() where T : new()
```

**示例：**
```csharp
db.CreateTable<ItemConfig>();
db.CreateTable<PlayerData>();
```

#### DropTable<T>

删除表。

```csharp
public void DropTable<T>() where T : new()
```

**示例：**
```csharp
db.DropTable<ItemConfig>();
```

### 事务支持

#### BeginTransaction

开始事务。

```csharp
public void BeginTransaction()
```

#### Commit

提交事务。

```csharp
public void Commit()
```

#### Rollback

回滚事务。

```csharp
public void Rollback()
```

**示例：**
```csharp
db.BeginTransaction();
try
{
    db.Insert(item1);
    db.Insert(item2);
    db.Update(item3);
    
    db.Commit();
}
catch (Exception ex)
{
    db.Rollback();
    Debug.LogError($"事务失败: {ex.Message}");
}
```

#### RunInTransaction

在事务中执行操作（自动提交或回滚）。

```csharp
public void RunInTransaction(Action action)
```

**示例：**
```csharp
db.RunInTransaction(() =>
{
    db.Insert(item1);
    db.Insert(item2);
    db.Update(item3);
});
// 自动提交，如果出错自动回滚
```

## 完整示例

### 配置管理器

```csharp
using Framework.Data;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class ConfigManager : MonoBehaviour
{
    private SQLiteHelper _db;

    void Start()
    {
        InitializeDatabase();
        LoadConfigs();
    }

    void InitializeDatabase()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, "config.db");
        _db = new SQLiteHelper(dbPath);

        // 创建表
        _db.CreateTable<ItemConfig>();
        _db.CreateTable<SkillConfig>();

        // 导入初始数据
        ImportInitialData();
    }

    void ImportInitialData()
    {
        // 检查是否已有数据
        int count = _db.ExecuteScalar<int>("SELECT COUNT(*) FROM items");
        if (count > 0)
        {
            Debug.Log("配置数据已存在");
            return;
        }

        // 使用事务批量插入
        _db.RunInTransaction(() =>
        {
            _db.InsertAll(new List<ItemConfig>
            {
                new ItemConfig { Id = 1001, Type = 1, Name = "铁剑", Quality = 2 },
                new ItemConfig { Id = 1002, Type = 1, Name = "钢剑", Quality = 3 },
                new ItemConfig { Id = 2001, Type = 2, Name = "布甲", Quality = 1 }
            });
        });

        Debug.Log("初始数据导入完成");
    }

    void LoadConfigs()
    {
        // 使用Query方法
        var allItems = _db.Query<ItemConfig>("SELECT * FROM items ORDER BY id");
        Debug.Log($"加载了 {allItems.Count} 个物品配置");

        // 使用QueryFirst方法
        var firstItem = _db.QueryFirst<ItemConfig>("SELECT * FROM items WHERE id = ?", 1001);
        if (firstItem != null)
        {
            Debug.Log($"第一个物品: {firstItem.Name}");
        }

        // 使用LINQ查询
        var weapons = _db.Table<ItemConfig>()
            .Where(x => x.Type == 1)
            .OrderByDescending(x => x.Quality)
            .ToList();
        Debug.Log($"找到 {weapons.Count} 个武器");
    }

    void OnDestroy()
    {
        _db?.Close();
    }
}

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
}

[Table("skills")]
public class SkillConfig
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int Type { get; set; }

    [MaxLength(50)]
    public string Name { get; set; }

    public int Level { get; set; }
}
```

## 最佳实践

### 1. 使用using语句

```csharp
// ✅ 推荐 - 自动关闭连接
using (var db = new SQLiteHelper(dbPath))
{
    // 数据库操作
}

// ❌ 不推荐 - 需要手动关闭
var db = new SQLiteHelper(dbPath);
// 操作...
db.Close();
```

### 2. 使用参数化查询

```csharp
// ✅ 安全 - 参数化查询
var items = db.Query<ItemConfig>("SELECT * FROM items WHERE name = ?", userName);

// ❌ 危险 - SQL注入风险
var items = db.Query<ItemConfig>($"SELECT * FROM items WHERE name = '{userName}'");
```

### 3. 批量操作使用事务

```csharp
// ✅ 快 - 使用事务
db.RunInTransaction(() =>
{
    for (int i = 0; i < 1000; i++)
    {
        db.Insert(new ItemConfig { Name = $"Item{i}" });
    }
});

// ❌ 慢 - 每次都提交
for (int i = 0; i < 1000; i++)
{
    db.Insert(new ItemConfig { Name = $"Item{i}" });
}
```

### 4. 正确处理事务

```csharp
// ✅ 推荐 - 完整的异常处理
db.BeginTransaction();
try
{
    db.Insert(item1);
    db.Update(item2);
    db.Commit();
}
catch (Exception ex)
{
    db.Rollback();
    Debug.LogError($"事务失败: {ex.Message}");
    throw;
}

// ✅ 更简洁 - 使用RunInTransaction
db.RunInTransaction(() =>
{
    db.Insert(item1);
    db.Update(item2);
});
```

## 性能优化

### 1. 创建索引

```csharp
db.Execute("CREATE INDEX IF NOT EXISTS idx_item_type ON items(type)");
db.Execute("CREATE INDEX IF NOT EXISTS idx_item_quality ON items(quality)");
```

### 2. 使用批量操作

```csharp
// 批量插入
var items = new List<ItemConfig>();
for (int i = 0; i < 1000; i++)
{
    items.Add(new ItemConfig { Name = $"Item{i}" });
}
db.InsertAll(items);
```

### 3. 限制结果集

```csharp
// 使用LIMIT
var items = db.Query<ItemConfig>("SELECT * FROM items LIMIT 100");

// 使用LINQ
var items = db.Table<ItemConfig>().Take(100).ToList();
```

## 常见问题

### Q: Query和Table有什么区别？

**A:**
- `Query<T>()` - 使用原始SQL查询，灵活性高
- `Table<T>()` - 使用LINQ查询，类型安全

### Q: 何时使用事务？

**A:** 
- 批量操作（插入/更新/删除多条记录）
- 需要保证原子性的操作
- 性能要求高的场景

### Q: 如何处理大量数据？

**A:**
- 使用分页查询
- 创建索引
- 使用事务批量操作

## 总结

SQLiteHelper提供了：

- ✅ **简化接口** - Query/QueryFirst/Execute
- ✅ **事务支持** - BeginTransaction/Commit/Rollback
- ✅ **完整CRUD** - Insert/Update/Delete
- ✅ **LINQ查询** - 支持SQLite-net的LINQ
- ✅ **错误处理** - 完整的日志记录

满足任务22的所有要求！

## 相关文档

- 📄 [SQLite-net使用指南.md](./SQLite-net使用指南.md) - SQLite-net完整文档
- 📄 [ConfigManager示例.md](./ConfigManager示例.md) - 实际项目示例
- 📄 [最佳实践.md](./最佳实践.md) - 性能优化和常见问题
