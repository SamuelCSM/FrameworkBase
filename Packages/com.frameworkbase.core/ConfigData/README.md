# SQLite数据库模块

## 概述

本模块使用**SQLite-net**配合**原生SQLite 3.51.2**，为Unity客户端框架提供强大的数据库功能。

## 技术栈

- **SQLite-net** - 轻量级ORM库，提供LINQ风格的查询API
- **原生SQLite 3.51.2** - 最新版本的SQLite数据库引擎
- **SQLiteHelper** - 封装工具类，提供简化的数据库操作接口

## 为什么选择这个组合？

### SQLite-net的优势

- ✅ **LINQ查询** - 类型安全的查询语法，无需编写SQL字符串
- ✅ **自动表创建** - 根据C#类自动创建和更新表结构
- ✅ **特性标注** - 使用特性定义主键、索引、外键等
- ✅ **简洁API** - 代码更简洁，开发效率高
- ✅ **轻量级** - 单个DLL文件，体积小

### 原生SQLite 3.51.2的优势

- ✅ **最新版本** - 支持所有最新特性和性能优化
- ✅ **体积小** - DLL只有~1MB
- ✅ **性能优秀** - 最新的性能改进
- ✅ **更新快** - 可以随时升级到最新版本

### SQLiteHelper的优势

- ✅ **简化接口** - Query/QueryFirst/Execute等常用方法
- ✅ **事务支持** - BeginTransaction/Commit/Rollback/RunInTransaction
- ✅ **完整CRUD** - Insert/Update/Delete及批量操作
- ✅ **资源管理** - 实现IDisposable，支持using语句
- ✅ **错误处理** - 完整的异常处理和日志记录

## 快速开始

### 1. 安装SQLite-net

通过NuGet for Unity安装：

1. 安装 [NuGet for Unity](https://github.com/GlitchEnzo/NuGetForUnity)
2. 打开 `Window > NuGet > Manage NuGet Packages`
3. 搜索 `sqlite-net-pcl`
4. 安装最新版本
5. 重启Unity编辑器

### 2. 下载原生SQLite 3.51.2

1. 访问：https://www.sqlite.org/download.html
2. 下载：`sqlite-dll-win-x64-3510200.zip`（Windows 64位）
3. 解压得到 `sqlite3.dll`
4. 复制到：`Assets/Plugins/SQLite/x86_64/sqlite3.dll`
5. 在Unity中配置DLL：
   - Platform: Windows
   - CPU: x86_64
   - Load on startup: ✅
6. 重启Unity编辑器

### 3. 开始使用

#### 方式1：使用SQLiteHelper（推荐）

```csharp
using Framework.Data;
using Framework.Utils;

string dbPath = PathUtil.Combine(PathUtil.PersistentDataPath, "game.db");

// 使用using语句自动管理连接
using (var db = new SQLiteHelper(dbPath))
{
    // 创建表
    db.CreateTable<ItemConfig>();
    
    // 插入数据
    db.Insert(new ItemConfig { Type = 1, Name = "铁剑", Quality = 2 });
    
    // 查询（SQL方式）
    var items = db.Query<ItemConfig>("SELECT * FROM items WHERE type = ?", 1);
    
    // 查询（LINQ方式）
    var highQualityItems = db.Table<ItemConfig>()
        .Where(x => x.Quality >= 3)
        .ToList();
    
    // 事务支持
    db.RunInTransaction(() =>
    {
        db.Insert(item1);
        db.Update(item2);
    });
}
// 自动关闭连接
```

#### 方式2：直接使用SQLite-net

```csharp
using SQLite;
using Framework.Utils;

string dbPath = PathUtil.Combine(PathUtil.PersistentDataPath, "game.db");

// 创建数据库连接
var db = new SQLiteConnection(dbPath);

// 自动创建表
db.CreateTable<ItemConfig>();

// 插入数据
db.Insert(new ItemConfig 
{ 
    Type = 1, 
    Name = "铁剑", 
    Quality = 2 
});

// LINQ查询
var items = db.Table<ItemConfig>()
    .Where(x => x.Type == 1 && x.Quality >= 2)
    .OrderByDescending(x => x.Quality)
    .ToList();

// 关闭连接
db.Close();
```

## 数据模型定义

使用特性标注表结构：

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

## 文档

- 📄 [快速开始指南](./快速开始指南.md) - 5分钟快速配置
- 📄 [SQLiteHelper使用指南](./SQLiteHelper使用指南.md) - 封装工具类的完整文档
- 📄 [SQLite-net使用指南](./SQLite-net使用指南.md) - 详细的API文档和示例
- 📄 [配置管理器示例](./ConfigManager示例.md) - 实际项目中的使用示例

## 平台支持

- ✅ Windows（编辑器和独立版本）
- ✅ macOS（编辑器和独立版本）
- ✅ Android
- ✅ iOS
- ❌ WebGL（不支持 - 使用IndexedDB替代方案）

## 需求

此实现满足规范中的需求 **2.9.1**：
- ✅ SQLite数据库操作封装
- ✅ 查询方法（LINQ查询）
- ✅ 执行方法用于命令
- ✅ 事务支持
- ✅ 自动类型转换
- ✅ 错误处理和日志记录
- ✅ 跨平台支持
