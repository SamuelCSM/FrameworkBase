# ConfigBase 配置表基础结构

## 快速开始

ConfigBase 是一个用于管理游戏配置表的泛型基类，提供了从 SQLite 数据库加载和查询配置数据的统一接口。

### 1. 定义配置数据类

```csharp
using SQLite;

[Table("item_config")]
public class ItemConfigData
{
    [PrimaryKey]
    public int Id { get; set; }
    
    public string Name { get; set; }
    public int Type { get; set; }
    public int Quality { get; set; }
}
```

### 2. 创建配置表类

```csharp
public class ItemConfigTable : ConfigBase<int, ItemConfigData>
{
    protected override int GetKey(ItemConfigData item)
    {
        return item.Id;
    }
}
```

### 3. 使用配置表

```csharp
var itemConfig = new ItemConfigTable();
itemConfig.Load(dbPath, "item_config");

// 根据主键查询
var item = itemConfig.GetByKey(1001);

// 获取所有配置
var allItems = itemConfig.GetAll();

// 条件查询
var weapons = itemConfig.GetList(item => item.Type == 1);

// 获取第一条数据
var firstItem = itemConfig.GetFirst();

// 获取第一个符合条件的配置
var firstWeapon = itemConfig.GetFirst(item => item.Type == 1);

// 获取最后一条数据
var lastItem = itemConfig.GetLast();

// 获取最后一个符合条件的配置
var lastWeapon = itemConfig.GetLast(item => item.Type == 1);
```

## 特性说明

### SQLite-net 提供的 ORM 特性

这些特性来自 `SQLite` 命名空间，用于对象关系映射：

- `[Table("table_name")]` - 指定表名
- `[PrimaryKey]` - 标记主键
- `[Column("column_name")]` - 指定列名
- `[Indexed]` - 创建索引
- `[AutoIncrement]` - 自增字段
- `[MaxLength(n)]` - 字符串最大长度

### Framework.Data 提供的校验特性

这些特性主要用于 Excel 导出工具的数据校验：

- `[Range(min, max)]` - 数值范围校验
- `[ForeignKey(typeof(T))]` - 外键引用校验

## 查询方法

- `GetByKey(key)` - 根据主键查询
- `GetAll()` - 获取所有配置
- `GetList(predicate)` - 条件查询
- `GetFirst(predicate = null)` - 获取第一个符合条件的配置，predicate 为 null 时返回第一条数据
- `GetLast(predicate = null)` - 获取最后一个符合条件的配置，predicate 为 null 时返回最后一条数据
- `ContainsKey(key)` - 检查主键是否存在

## 文件说明

- `ConfigBase.cs` - 配置表基类
- `ConfigValidationAttributes.cs` - 校验特性定义
- `ConfigExample.cs` - 示例配置表
- `ConfigBase使用指南.md` - 详细使用文档
- `ConfigBase实现说明.md` - 实现细节说明

## 依赖

- SQLite-net (sqlite-net-pcl) - ORM 库
- SQLiteHelper - SQLite 数据库操作封装

## 注意事项

1. 配置数据类必须有无参数的公共构造函数
2. ConfigBase 不是线程安全的，应在主线程使用
3. Range 和 ForeignKey 特性的校验在 Excel 导出工具中执行
4. 不再使用的配置表应调用 Unload() 释放内存

## 下一步

- 任务 24：实现 Excel 转 SQLite 工具（使用校验特性）
- 任务 25：实现玩家数据管理
- 任务 26：实现 DataManager 数据管理器
