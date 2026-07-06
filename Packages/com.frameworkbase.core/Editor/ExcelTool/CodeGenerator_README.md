# CodeGenerator - 代码生成器

## 概述

CodeGenerator 是 Excel 转 SQLite 工具的代码生成组件，能够根据 Excel 表结构自动生成 C# 配置类代码和配置表加载类。

## 功能特性

### 1. 自动生成配置类
- 根据 Excel 字段名生成属性
- 自动推断属性类型
- 生成 SQLite 特性标记
- 生成 XML 文档注释

### 2. 自动生成加载类
- 继承自 `ConfigBase<TKey, TValue>`
- 实现 `GetKey` 方法
- 提供构造函数模板

### 3. 灵活配置
- 自定义命名空间
- 控制注释生成
- 控制特性生成
- 自定义缩进格式

## 使用方法

### 基本用法

```csharp
using Editor.ExcelTool;

// 1. 读取 Excel 文件
var excelReader = new ExcelReader();
var sheets = excelReader.ReadExcel("path/to/excel.xlsx");

// 2. 创建代码生成器
var generator = new CodeGenerator();

// 3. 生成配置类代码
var code = generator.GenerateConfigClass(sheets[0]);

// 4. 保存到文件
File.WriteAllText("ItemConfig.cs", code);
```

### 自定义配置

```csharp
var config = new CodeGenerator.GeneratorConfig
{
    Namespace = "MyGame.Data",           // 自定义命名空间
    GenerateComments = true,              // 生成注释
    UseSQLiteAttributes = true,           // 使用 SQLite 特性
    GenerateSerializable = true,          // 生成 Serializable 特性
    Indent = "    "                       // 使用 4 个空格缩进
};

var generator = new CodeGenerator(config);
var code = generator.GenerateConfigClass(sheetData);
```

### 批量生成

```csharp
var excelReader = new ExcelReader();
var sheets = excelReader.ReadExcel("path/to/excel.xlsx");

var generator = new CodeGenerator();
var codeDict = generator.GenerateConfigClasses(sheets);

foreach (var kvp in codeDict)
{
    var className = kvp.Key;
    var code = kvp.Value;
    File.WriteAllText($"{className}.cs", code);
}
```

## 生成的代码示例

### Excel 表结构

| Id (物品ID) | Name (名称) | Type (类型) | Quality (品质) | Icon (图标) |
|------------|-----------|-----------|--------------|-----------|
| 1001       | 铁剑      | 1         | 2            | icon_sword |
| 1002       | 木盾      | 2         | 1            | icon_shield |

### 生成的配置类

```csharp
// ==========================================
// 自动生成的配置类: ItemConfig
// 来源表: ItemConfig
// 生成时间: 2024-03-02 15:30:00
// 警告: 请勿手动修改此文件！
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework.Data;

namespace HotUpdate.Config
{
    /// <summary>
    /// ItemConfig 配置类
    /// </summary>
    [Table("ItemConfig")]
    [Serializable]
    public class ItemConfig
    {
        /// <summary>
        /// 物品ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 名称
        /// </summary>
        [Column("Name")]
        public string Name { get; set; }

        /// <summary>
        /// 类型
        /// </summary>
        [Column("Type")]
        public int Type { get; set; }

        /// <summary>
        /// 品质
        /// </summary>
        [Column("Quality")]
        public int Quality { get; set; }

        /// <summary>
        /// 图标
        /// </summary>
        [Column("Icon")]
        public string Icon { get; set; }

    }

    /// <summary>
    /// ItemConfig 配置表加载器
    /// </summary>
    public class ItemConfigTable : ConfigBase<int, ItemConfig>
    {
        /// <summary>
        /// 构造函数
        /// </summary>
        public ItemConfigTable()
        {
            // 可以在这里指定数据库路径和表名
            // Load(dbPath, "ItemConfig");
        }

        /// <summary>
        /// 获取配置项的主键
        /// </summary>
        protected override int GetKey(ItemConfig item)
        {
            return item.Id;
        }
    }
}
```

## 类型推断规则

代码生成器会根据字段名自动推断属性类型：

| 字段名包含 | 推断类型 | 示例 |
|-----------|---------|------|
| id, count, num, level, type | `int` | ItemId, Count, Level |
| rate, percent, ratio | `float` | DropRate, Percent |
| is, enable, flag | `bool` | IsActive, EnableFlag |
| name, desc, text, icon, path | `string` | Name, Description |
| 其他 | `string` | 默认类型 |

### 自定义类型推断

如果自动推断不准确，可以在生成后手动修改代码，或者扩展 `InferPropertyType` 方法：

```csharp
private string InferPropertyType(string fieldName)
{
    var lowerName = fieldName.ToLower();
    
    // 添加自定义规则
    if (lowerName.Contains("reward"))
    {
        return "ItemReward";  // 自定义类型
    }
    
    // 原有规则...
}
```

## 命名规范

### 类名处理
- 移除非法字符（只保留字母、数字、下划线）
- 确保以字母或下划线开头
- 首字母大写（PascalCase）

### 属性名处理
- 移除非法字符
- 确保以字母或下划线开头
- 首字母大写（PascalCase）

### 示例

| Excel 字段名 | 生成的属性名 |
|-------------|------------|
| id | Id |
| item_name | Item_name |
| 123abc | _123abc |
| player-level | Playerlevel |

## 配置选项

### GeneratorConfig 类

```csharp
public class GeneratorConfig
{
    /// <summary>
    /// 命名空间（默认: HotUpdate.Config）
    /// </summary>
    public string Namespace { get; set; }

    /// <summary>
    /// 是否生成注释（默认: true）
    /// </summary>
    public bool GenerateComments { get; set; }

    /// <summary>
    /// 是否使用 SQLite 特性（默认: true）
    /// </summary>
    public bool UseSQLiteAttributes { get; set; }

    /// <summary>
    /// 是否生成 Serializable 特性（默认: true）
    /// </summary>
    public bool GenerateSerializable { get; set; }

    /// <summary>
    /// 缩进字符（默认: 4个空格）
    /// </summary>
    public string Indent { get; set; }
}
```

## 集成到导出流程

```csharp
public class ExcelExporter
{
    public void Export(string excelPath, string dbPath, string codePath)
    {
        // 1. 读取 Excel
        var reader = new ExcelReader();
        var sheets = reader.ReadExcel(excelPath);
        
        // 2. 生成代码
        var generator = new CodeGenerator();
        var codeDict = generator.GenerateConfigClasses(sheets);
        
        // 3. 保存代码文件
        foreach (var kvp in codeDict)
        {
            var className = kvp.Key;
            var code = kvp.Value;
            var filePath = Path.Combine(codePath, $"{className}.cs");
            File.WriteAllText(filePath, code);
        }
        
        // 4. 导出到 SQLite
        ExportToDatabase(sheets, dbPath);
        
        // 5. 刷新 Unity 资源
        AssetDatabase.Refresh();
    }
}
```

## 最佳实践

### 1. 统一命名规范

在 Excel 中使用统一的字段命名规范：
- 使用英文字段名
- 使用 PascalCase 或 snake_case
- 避免特殊字符

### 2. 添加注释

在 Excel 的注释行（第一行）添加详细的字段说明，这些注释会被生成到代码中。

### 3. 版本控制

生成的代码文件应该加入版本控制，方便追踪配置变更。

### 4. 代码审查

虽然是自动生成的代码，但建议在首次生成后进行审查，确保类型推断正确。

### 5. 分离生成和手写代码

- 自动生成的代码放在独立目录（如 `Config/Generated/`）
- 手写的扩展代码使用 partial class 或继承

```csharp
// Generated/ItemConfig.cs (自动生成)
public partial class ItemConfig
{
    // 自动生成的属性...
}

// ItemConfig.Extension.cs (手写扩展)
public partial class ItemConfig
{
    // 手写的方法和属性
    public bool IsWeapon()
    {
        return Type == 1;
    }
}
```

## 注意事项

### 1. 类型推断限制

自动类型推断基于字段名，可能不够准确。建议：
- 使用清晰的字段命名
- 生成后检查类型是否正确
- 必要时手动修改

### 2. 复杂类型

对于复杂类型（如自定义类、数组等），需要手动修改生成的代码：

```csharp
// 生成的代码
public string Rewards { get; set; }

// 手动修改为
public List<ItemReward> Rewards { get; set; }
```

### 3. 主键识别

代码生成器默认将第一个字段作为主键。如果主键不是第一个字段，需要手动调整特性。

### 4. 文件覆盖

每次生成都会覆盖现有文件。如果需要保留手写代码，使用 partial class 或继承。

## 扩展功能

### 1. 自定义模板

可以扩展 `CodeGenerator` 类，使用自定义模板：

```csharp
public class CustomCodeGenerator : CodeGenerator
{
    protected override void GenerateClass(StringBuilder sb, ...)
    {
        // 使用自定义模板生成代码
    }
}
```

### 2. 添加验证特性

可以在生成时添加数据验证特性：

```csharp
[Range(1, 100)]
public int Level { get; set; }

[Required]
public string Name { get; set; }
```

### 3. 生成单元测试

可以扩展生成器，同时生成单元测试代码。

## 相关文件

- `CodeGenerator.cs` - 代码生成器实现
- `ExcelReader.cs` - Excel 读取器
- `DataValidator.cs` - 数据校验器

## 版本历史

- v1.0.0 (2024-03-02): 初始版本
  - 实现基本代码生成功能
  - 支持配置类和加载类生成
  - 支持类型推断
  - 支持批量生成
