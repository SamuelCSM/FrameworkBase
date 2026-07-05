# Excel配置表工具 - 完整使用指南

## 概述

Excel配置表工具是Unity客户端框架的一部分，用于将Excel配置表转换为SQLite数据库，并自动生成C#配置类代码。

## 工具组成

Excel工具由以下几个部分组成：

1. **ExcelReader（Excel读取器）** - 读取Excel文件并解析数据
2. **DataValidator（数据校验器）** - 校验数据完整性和正确性
3. **CodeGenerator（代码生成器）** - 生成C#配置类代码
4. **Exporter（导出器）** - 导出数据到SQLite数据库

## 快速开始

### 1. 安装依赖

首先需要安装ExcelDataReader库：

1. 打开Unity菜单：`Tools > Excel > Install ExcelDataReader`
2. 按照提示安装NuGet包
3. 重启Unity编辑器
4. 运行 `Tools > Excel > Check ExcelDataReader Installation` 验证安装

### 2. 准备Excel文件

创建Excel文件（.xlsx格式），遵循以下格式：

```
第0行：字段注释（中文说明）
第1行：字段名称（C#属性名）
第2行及以后：数据行
```

示例：

| ID（主键） | 名称 | 类型 | 品质 | 价格 | 描述 |
|-----------|------|------|------|------|------|
| Id | Name | Type | Quality | Price | Description |
| 1001 | 铁剑 | 1 | 1 | 100 | 一把普通的铁剑 |
| 1002 | 钢剑 | 1 | 2 | 500 | 一把锋利的钢剑 |

### 3. 测试读取

使用菜单测试Excel读取：

1. 打开 `Tools > Excel > Test Read Excel`
2. 选择你的Excel文件
3. 查看Console输出，确认数据正确读取

## Excel格式规范

### 标准格式

- **第0行（注释行）**：字段的中文说明，用于文档和代码注释
- **第1行（字段名行）**：字段的英文名称，必须是有效的C#标识符
- **第2行及以后（数据行）**：实际的配置数据

### 字段命名规则

字段名必须遵循C#命名规范：

- 以字母或下划线开头
- 只包含字母、数字、下划线
- 不能是C#关键字
- 建议使用PascalCase命名（首字母大写）

示例：
- ✓ `Id`, `Name`, `MaxValue`, `IsActive`
- ✗ `1Id`, `名称`, `max-value`, `class`

### 空行和空列处理

- 完全为空的数据行会被自动跳过
- 字段名为空的列会被忽略
- 可以使用空列作为视觉分隔

## 支持的数据类型

### 基础类型

| C#类型 | Excel示例 | 说明 |
|--------|-----------|------|
| int | 123 | 整数 |
| long | 123456789 | 长整数 |
| float | 123.45 | 单精度浮点数 |
| double | 123.456789 | 双精度浮点数 |
| decimal | 123.45 | 高精度小数 |
| string | Hello | 字符串 |
| bool | true, 1, yes, 是 | 布尔值 |

### 枚举类型

定义枚举：

```csharp
public enum ItemType
{
    Weapon = 1,
    Armor = 2,
    Consumable = 3
}
```

Excel中可以使用：
- 枚举名称：`Weapon`
- 枚举值：`1`

### 数组类型

数组使用逗号或分号分隔，可选方括号：

| 格式 | 示例 | 说明 |
|------|------|------|
| 方括号 | [1,2,3] | 推荐格式 |
| 逗号 | 1,2,3 | 简洁格式 |
| 分号 | 1;2;3 | 备选格式 |

支持的数组类型：
- `int[]`: `[1,2,3]`
- `float[]`: `[1.1,2.2,3.3]`
- `string[]`: `[a,b,c]`
- `bool[]`: `[true,false,true]`

### 自定义类型（JSON）

复杂对象使用JSON格式：

```json
{"x":100,"y":200}
```

示例：

```csharp
[Serializable]
public class Vector2Data
{
    public float x;
    public float y;
}
```

Excel中填写：`{"x":100,"y":200}`

## 配置表特性

使用特性来定义配置表结构和校验规则：

### Table特性

指定表名：

```csharp
[Table("item_config")]
public class ItemConfig
{
    // ...
}
```

### PrimaryKey特性

标记主键字段：

```csharp
[PrimaryKey]
public int Id { get; set; }
```

### Column特性

指定列名（可选）：

```csharp
[Column("item_name")]
public string Name { get; set; }
```

### Index特性

创建索引以提高查询性能：

```csharp
[Index("idx_type")]
public int Type { get; set; }
```

### Range特性

数值范围校验：

```csharp
[Range(1, 100)]
public int Level { get; set; }
```

### ForeignKey特性

外键引用校验：

```csharp
[ForeignKey(typeof(ItemConfig))]
public int ItemId { get; set; }
```

## 完整示例

### Excel文件

| ID（主键） | 名称 | 类型 | 品质 | 价格 | 属性加成 | 描述 |
|-----------|------|------|------|------|----------|------|
| Id | Name | Type | Quality | Price | Attributes | Description |
| 1001 | 铁剑 | 1 | 1 | 100 | [10,0,0] | 一把普通的铁剑 |
| 1002 | 钢剑 | 1 | 2 | 500 | [25,0,0] | 一把锋利的钢剑 |

### C#配置类

```csharp
using Framework.Data;
using SQLite;

namespace HotUpdate.Config
{
    [Table("item_config")]
    public class ItemConfig
    {
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        [Column("Name")]
        public string Name { get; set; }

        [Column("Type")]
        [Range(1, 10)]
        public int Type { get; set; }

        [Column("Quality")]
        [Range(1, 5)]
        public int Quality { get; set; }

        [Column("Price")]
        [Range(0, 999999)]
        public int Price { get; set; }

        [Column("Attributes")]
        public string Attributes { get; set; }  // 存储为JSON字符串

        [Column("Description")]
        public string Description { get; set; }
    }

    public class ItemConfigTable : ConfigBase<int, ItemConfig>
    {
        protected override int GetKey(ItemConfig item)
        {
            return item.Id;
        }
    }
}
```

### 使用配置表

```csharp
using Framework.Core;
using HotUpdate.Config;

// 加载配置表
var itemConfig = new ItemConfigTable();
itemConfig.Load(dbPath, "item_config");

// 根据主键查询
var item = itemConfig.GetByKey(1001);
Debug.Log($"物品名称: {item.Name}");

// 条件查询
var weapons = itemConfig.GetList(x => x.Type == 1);
Debug.Log($"武器数量: {weapons.Count}");

// 获取所有数据
var allItems = itemConfig.GetAll();
```

## 工作流程

完整的Excel配置表工作流程：

```
1. 策划编辑Excel配置表
   ↓
2. 运行导出工具
   ↓
3. Excel读取器读取数据
   ↓
4. 数据校验器校验数据
   ↓
5. 代码生成器生成C#类（首次或结构变更时）
   ↓
6. 导出器写入SQLite数据库
   ↓
7. 游戏运行时加载配置表
```

## 菜单命令

| 菜单路径 | 功能 |
|---------|------|
| Tools > Excel > Install ExcelDataReader | 显示安装指南 |
| Tools > Excel > Check ExcelDataReader Installation | 检查安装状态 |
| Tools > Excel > Test Read Excel | 测试读取Excel文件 |
| Tools > Excel > Test Type Parsing | 测试类型解析 |

## 常见问题

### Q: 为什么只支持.xlsx格式？

A: ExcelDataReader库只支持Excel 2007及以上版本的.xlsx格式。旧的.xls格式不支持。

### Q: 中文字段名可以吗？

A: 字段名必须是有效的C#标识符，不能使用中文。中文说明应该放在注释行（第0行）。

### Q: 如何处理空值？

A: 空单元格会被解析为对应类型的默认值（数值为0，字符串为空，布尔为false）。

### Q: 数组中可以包含空格吗？

A: 可以，解析器会自动去除元素两端的空格。例如：`[1, 2, 3]` 和 `[1,2,3]` 效果相同。

### Q: 如何调试解析错误？

A: 查看Unity Console，ExcelReader会记录详细的警告和错误日志，包括行号和字段名。

## 性能建议

1. **索引优化**：为常用查询字段添加Index特性
2. **延迟加载**：只在需要时加载配置表
3. **预加载**：在启动时预加载常用配置表
4. **缓存查询**：缓存频繁查询的结果

## 下一步

当前已完成ExcelReader（任务24.1），后续还需要实现：

- [ ] 24.2 数据校验器
- [ ] 24.3 代码生成器
- [ ] 24.4 导出器
- [ ] 24.5 单元测试

## 参考资料

- [ExcelDataReader GitHub](https://github.com/ExcelDataReader/ExcelDataReader)
- [SQLite-net GitHub](https://github.com/praeclarum/sqlite-net)
- [Unity客户端框架设计文档](../../.kiro/specs/unity-client-framework/design.md)
