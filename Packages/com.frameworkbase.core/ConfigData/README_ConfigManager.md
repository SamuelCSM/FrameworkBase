# ConfigManager 配置管理器

## 概述

ConfigManager 是一个通用的配置管理器，提供了配置表的按需加载、自动缓存和统一访问接口。

## 核心功能

### 1. 按需加载

配置表在首次访问时自动加载，避免启动时加载所有配置：

```csharp
// 第一次访问：从数据库加载
var itemConfig = configManager.GetConfig<ItemConfigTable>();

// 第二次访问：从缓存获取（不会重新加载）
var itemConfig2 = configManager.GetConfig<ItemConfigTable>();
```

### 2. 自动缓存

加载过的配置表会自动缓存，提高访问性能：

```csharp
// 所有对同一配置表的访问都返回同一个实例
Debug.Log(ReferenceEquals(itemConfig, itemConfig2)); // True
```

### 3. 预加载

支持在游戏启动时预加载常用配置表：

```csharp
// 预加载单个配置表
configManager.PreloadConfig<ItemConfigTable>();

// 批量预加载
configManager.PreloadConfigs(
    typeof(ItemConfigTable),
    typeof(SkillConfigTable),
    typeof(LevelConfigTable)
);
```

### 4. 热更新

支持重新加载配置表，用于配置热更新：

```csharp
// 重新加载单个配置表
configManager.ReloadConfig<ItemConfigTable>();

// 重新加载所有配置表
configManager.ReloadAllConfigs();

// 从 Addressables 应用热更配置数据库
await configManager.UpdateDatabaseFromAddressablesAsync("RefData/config.db");
```

### 5. 内存管理

支持卸载不再使用的配置表以释放内存：

```csharp
// 卸载单个配置表
configManager.UnloadConfig<ItemConfigTable>();

// 卸载所有配置表
configManager.UnloadAllConfigs();
```

## 使用流程

### 1. 初始化

```csharp
var configManager = new ConfigManager();
configManager.Initialize(); // 使用默认路径
await configManager.EnsureDatabaseReadyAsync(); // 首次运行时从 StreamingAssets/RefData 拷贝 config.db

// 或指定数据库路径
// configManager.Initialize("/path/to/config.db");
```

### 2. 定义配置表

```csharp
// 配置数据类
[Table("item_config")]
public class ItemConfigData
{
    [PrimaryKey]
    public int Id { get; set; }
    public string Name { get; set; }
    public int Type { get; set; }
}

// 配置表类
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
// 获取配置表
var itemConfig = configManager.GetConfig<ItemConfigTable>();

// 查询配置
var item = itemConfig.GetByKey(1001);
var allItems = itemConfig.GetAll();
var weapons = itemConfig.GetList(item => item.Type == 1);
```

## API 参考

### 初始化方法

- `Initialize(string dbPath = null)` - 初始化配置管理器
- `EnsureDatabaseReadyAsync(string streamingAssetRelativePath = "RefData/config.db")` - 确保运行时数据库可用，首次运行时从 StreamingAssets 拷贝默认库
- `UpdateDatabaseFromAddressablesAsync(string address = "RefData/config.db", bool reloadLoadedConfigs = true)` - 从 Addressables 加载热更配置库并写入运行时路径

### 加载方法

- `GetConfig<TConfig>()` - 获取配置表（按需加载，自动缓存）
- `PreloadConfig<TConfig>()` - 预加载单个配置表
- `PreloadConfigs(params Type[] configTypes)` - 批量预加载配置表

### 卸载方法

- `UnloadConfig<TConfig>()` - 卸载单个配置表
- `UnloadAllConfigs()` - 卸载所有配置表

### 重新加载方法

- `ReloadConfig<TConfig>()` - 重新加载单个配置表
- `ReloadAllConfigs()` - 重新加载所有配置表

### 查询方法

- `IsConfigLoaded<TConfig>()` - 检查配置表是否已加载
- `GetLoadedConfigCount()` - 获取已加载的配置表数量
- `GetDatabasePath()` - 获取配置数据库路径

### 清理方法

- `Dispose()` - 清理资源

## 设计特点

### 1. 泛型设计

使用泛型确保类型安全：

```csharp
public TConfig GetConfig<TConfig>()
    where TConfig : IConfigTable, new()
```

### 2. 反射优化

使用反射实现批量预加载，避免重复代码：

```csharp
public void PreloadConfigs(params Type[] configTypes)
{
    // 使用反射动态加载配置表
}
```

### 3. 缓存机制

使用字典缓存配置表实例：

```csharp
private readonly Dictionary<Type, object> _configCache = new Dictionary<Type, object>();
```

### 4. 表名自动推断

支持从 Table 特性或类型名自动推断表名：

```csharp
// 从 Table 特性获取
[Table("item_config")]
public class ItemConfigData { }

// 或使用类型名（自动转换为下划线格式）
public class ItemConfigData { } // 表名: item_config_data
```

## 性能优化

### 1. 按需加载

只加载需要的配置表，减少启动时间和内存占用。

### 2. 自动缓存

避免重复加载，提高访问性能。

### 3. 批量预加载

使用 `PreloadConfigs` 批量加载多个配置表，比逐个加载更高效。

### 4. 及时卸载

不再使用的配置表及时卸载，释放内存。

## 最佳实践

### 1. 启动时预加载常用配置

```csharp
void Start()
{
    configManager.PreloadConfigs(
        typeof(ItemConfigTable),
        typeof(SkillConfigTable),
        typeof(UIConfigTable)
    );
}
```

### 2. 场景切换时卸载不需要的配置

```csharp
void OnSceneUnload()
{
    configManager.UnloadConfig<BattleConfigTable>();
    configManager.UnloadConfig<DungeonConfigTable>();
}
```

### 3. 配置热更资源约定

首包配置数据库：

```text
Assets/StreamingAssets/RefData/config.db
```

远端热更配置数据库建议作为 Addressables 资源发布：

```text
文件名：config.db.bytes
路径：Assets/ResourcesOut/RefData/config.db.bytes
地址：RefData/config.db
Label：remote
```

`ResourcesOut` 同步规则会去掉最后一个扩展名，所以 `config.db.bytes` 的 Addressables 地址是 `RefData/config.db`。启动流程中，`LaunchFlow` 会先确保 `persistentDataPath/config.db` 可用；如果本次发生资源热更，会继续尝试从 Addressables 加载 `RefData/config.db`，校验通过后覆盖本地数据库，并重新加载已缓存的配置表。

编辑器导出（`Tools > Excel > Excel 导出器`）建议：

- 日常改单表：`Single` + `HotUpdateOnly`，只更新 `config.db.bytes`，不动首包
- 发版定首包：`Batch` + `StreamingAssetsOnly`，批量导出到 `StreamingAssets/RefData/config.db`
- 需要首包与热更同时更新：选 `Both`

### 4. 集成到单例管理器

```csharp
public class GameEntry : MonoSingleton<GameEntry>
{
    public static ConfigManager RefData { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        RefData = AddComponent<ConfigManager>();
    }
}

// 使用
var itemConfig = GameEntry.RefData.GetConfig<ItemConfigTable>();
```

## 文件说明

- `ConfigManager.cs` - 配置管理器实现
- `ConfigManager使用指南.md` - 详细使用文档
- `README_ConfigManager.md` - 本文档

## 依赖

- `ConfigBase<TKey, TValue>` - 配置表基类
- `SQLiteHelper` - SQLite 数据库操作封装
- SQLite-net - ORM 库

## 注意事项

1. 使用前必须调用 `Initialize()` 方法
2. 确保配置数据库文件存在于指定路径
3. ConfigManager 不是线程安全的，应在主线程使用
4. 及时卸载不再使用的配置表以释放内存
5. 重新加载配置表会清除缓存，确保所有引用都更新

## 总结

ConfigManager 提供了一个简单而强大的配置管理方案：

- ✅ 按需加载 - 避免启动时加载所有配置
- ✅ 自动缓存 - 提高访问性能
- ✅ 类型安全 - 编译时检查
- ✅ 热更新支持 - 方便配置更新
- ✅ 内存管理 - 支持卸载不需要的配置
- ✅ 易于使用 - 统一的访问接口

通过合理使用 ConfigManager，可以有效管理游戏配置，提高开发效率和运行性能。
