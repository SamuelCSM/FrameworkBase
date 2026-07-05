# Excel 数据校验器使用指南

## 概述

ExcelDataValidator 是一个专门用于校验 Excel 配置表数据正确性的工具。它可以在导出到 SQLite 之前发现数据问题，避免运行时错误。

## 功能特性

### 1. 表结构校验
- 检查是否有字段定义
- 检查类型定义是否完整
- 检查注释是否完整
- 检查字段名是否重复
- 检查字段名是否为空

### 2. 主键校验
- 检查主键是否为空
- 检查主键是否重复

### 3. 数据类型校验
- 校验 int, long, float, double 类型
- 校验 bool 类型（支持多种格式）
- 校验数组类型
- 校验字符串类型

### 4. 空值校验
- 检查整行是否为空
- 检查部分字段是否为空
- 提供详细的空值警告

### 5. 范围校验（可选）
- 校验数值是否在指定范围内
- 支持自定义范围规则

### 6. 外键校验（可选）
- 校验外键引用是否存在
- 支持自定义外键规则

## 使用方式

### 方式一：独立使用

```csharp
using Editor.ExcelTool;

// 1. 读取 Excel
var reader = new ExcelReader();
var sheets = reader.ReadExcel("path/to/config.xlsx");

// 2. 创建校验器
var validator = new ExcelDataValidator();

// 3. 校验数据
var result = validator.ValidateSheet(sheets[0]);

// 4. 检查结果
if (result.IsValid)
{
    Debug.Log("✓ 数据校验通过");
    
    // 显示警告（如果有）
    foreach (var warning in result.Warnings)
    {
        Debug.LogWarning(warning);
    }
}
else
{
    Debug.LogError("✗ 数据校验失败");
    
    // 显示错误
    foreach (var error in result.Errors)
    {
        Debug.LogError(error);
    }
}
```

### 方式二：集成到导出器

ExcelExporter 已经集成了 ExcelDataValidator，只需启用校验选项：

```csharp
var config = new ExcelExporter.ExportConfig
{
    OutputDbPath = "Assets/StreamingAssets/RefData/config.db",
    EnableValidation = true,  // 启用数据校验
    VerboseLogging = true
};

var exporter = new ExcelExporter(config);
var result = exporter.ExportExcel("path/to/config.xlsx");

if (!result.Success)
{
    Debug.LogError($"导出失败: {result.ErrorMessage}");
}
```

### 方式三：使用菜单命令

在 Unity 菜单中选择：
- `Tools > Excel > Examples > 校验 Excel 数据`
- `Tools > Excel > Examples > 校验并显示详细信息`
- `Tools > Excel > Examples > 校验数据范围`

## 校验结果

### ValidationResult 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| IsValid | bool | 是否通过校验 |
| Errors | List<string> | 错误列表（导致校验失败） |
| Warnings | List<string> | 警告列表（不影响导出） |

### 错误 vs 警告

- **错误**：严重问题，会导致 `IsValid = false`，阻止导出
  - 主键为空
  - 主键重复
  - 字段名重复
  - 字段名为空

- **警告**：潜在问题，不阻止导出，但需要注意
  - 类型定义不完整
  - 注释不完整
  - 数据类型不匹配
  - 字段值为空
  - 数值超出范围
  - 外键引用不存在

## 高级功能

### 1. 范围校验

```csharp
var validator = new ExcelDataValidator();
var result = validator.ValidateSheet(sheetData);

// 定义范围规则
var ranges = new Dictionary<string, (double min, double max)>
{
    { "Quality", (1, 5) },      // 品质范围 1-5
    { "Level", (1, 100) },      // 等级范围 1-100
    { "Price", (0, 999999) }    // 价格范围 0-999999
};

// 校验范围
validator.ValidateRanges(sheetData, ranges, result);

// 检查结果
if (result.Warnings.Count > 0)
{
    Debug.LogWarning($"发现 {result.Warnings.Count} 个范围警告");
}
```

### 2. 外键校验

```csharp
var validator = new ExcelDataValidator();
var result = validator.ValidateSheet(sheetData);

// 定义外键规则
var foreignKeys = new Dictionary<string, HashSet<string>>
{
    { "ItemId", new HashSet<string> { "1001", "1002", "1003" } },
    { "SkillId", new HashSet<string> { "2001", "2002", "2003" } }
};

// 校验外键
validator.ValidateForeignKeys(sheetData, foreignKeys, result);

// 检查结果
if (result.Warnings.Count > 0)
{
    Debug.LogWarning($"发现 {result.Warnings.Count} 个外键警告");
}
```

## 校验规则

### 数据类型校验规则

| 类型 | 校验规则 | 示例 |
|------|----------|------|
| int | 可以转换为整数 | 100, 1.0 (Excel 数字) |
| long | 可以转换为长整数 | 1000000 |
| float | 可以转换为浮点数 | 3.14 |
| double | 可以转换为双精度浮点数 | 3.14159 |
| bool | true/false, 1/0, yes/no, 是/否 | true, 1, yes |
| string | 任何值 | "Hello" |
| int[] | 逗号或分号分隔 | 1,2,3 或 1;2;3 |
| float[] | 逗号或分号分隔 | 1.0,2.0,3.0 |
| string[] | 逗号或分号分隔 | a,b,c |

### 主键校验规则

- 主键字段：默认为第一个字段
- 主键不能为空
- 主键不能重复

### 空值校验规则

- 整行为空：警告
- 部分字段为空：警告（主键除外）
- 主键为空：错误

## 错误消息格式

### 结构错误
```
存在重复的字段名: Id, Name
第 3 列的字段名为空
```

### 主键错误
```
第 5 行: 主键 'Id' 为空
第 8 行: 主键重复 (Id=1001)
```

### 类型错误
```
第 10 行, 列 'Price': 无法转换为 int 类型，值: 'abc'
第 12 行, 列 'IsActive': 无法转换为 bool 类型，值: 'maybe'
```

### 空值警告
```
第 15 行: 整行数据为空
第 20 行: 以下字段为空: Description, Icon
```

### 范围警告
```
第 25 行, 列 'Quality': 值 10 超出范围 [1, 5]
第 30 行, 列 'Level': 值 150 超出范围 [1, 100]
```

### 外键警告
```
第 35 行, 列 'ItemId': 外键 '9999' 不存在
第 40 行, 列 'SkillId': 外键 '8888' 不存在
```

## 最佳实践

### 1. 开发阶段始终启用校验

```csharp
var config = new ExcelExporter.ExportConfig
{
    EnableValidation = true,  // 开发阶段启用
    VerboseLogging = true
};
```

### 2. 及时修复警告

虽然警告不会阻止导出，但应该及时修复，避免运行时问题。

### 3. 使用范围校验

为数值字段定义合理的范围，避免异常值：

```csharp
var ranges = new Dictionary<string, (double min, double max)>
{
    { "Quality", (1, 5) },
    { "Level", (1, 100) },
    { "Price", (0, 999999) }
};
```

### 4. 使用外键校验

确保引用的数据存在：

```csharp
// 先读取被引用的表
var itemSheet = reader.ReadExcel("ItemConfig.xlsx")[0];
var itemIds = new HashSet<string>();
foreach (var row in itemSheet.DataRows)
{
    itemIds.Add(row["Id"].ToString());
}

// 校验引用
var foreignKeys = new Dictionary<string, HashSet<string>>
{
    { "ItemId", itemIds }
};
validator.ValidateForeignKeys(rewardSheet, foreignKeys, result);
```

### 5. 批量校验

```csharp
var excelFiles = Directory.GetFiles("Assets/RefData_Excel", "*.xlsx");
var validator = new ExcelDataValidator();
var reader = new ExcelReader();

foreach (var file in excelFiles)
{
    var sheets = reader.ReadExcel(file);
    foreach (var sheet in sheets)
    {
        var result = validator.ValidateSheet(sheet);
        if (!result.IsValid)
        {
            Debug.LogError($"✗ {file} - {sheet.SheetName}: 校验失败");
        }
        else
        {
            Debug.Log($"✓ {file} - {sheet.SheetName}: 校验通过");
        }
    }
}
```

## 常见问题

### Q1: 为什么类型校验会有警告？

A: Excel 中的数字默认是 double 类型，但可以正常转换为 int。如果看到类型警告，检查数据是否真的有问题。

### Q2: 如何自定义主键字段？

A: 当前版本默认第一个字段为主键。如果需要自定义，可以修改 `ValidatePrimaryKey` 方法。

### Q3: 如何禁用某些校验？

A: 可以在导出配置中设置 `EnableValidation = false` 禁用所有校验，或者修改 `ExcelDataValidator` 类来自定义校验逻辑。

### Q4: 校验会影响性能吗？

A: 校验是在内存中进行的，对于普通大小的配置表（几千行）影响很小。如果表非常大，可以考虑在发布版本中禁用校验。

### Q5: 如何添加自定义校验规则？

A: 可以继承 `ExcelDataValidator` 类并添加自定义方法：

```csharp
public class MyCustomValidator : ExcelDataValidator
{
    public void ValidateCustomRule(ExcelReader.ExcelSheetData sheetData, ValidationResult result)
    {
        // 自定义校验逻辑
    }
}
```

## 相关文档

- `ExcelReader_README.md` - Excel 读取器文档
- `ExcelExporter_README.md` - Excel 导出器文档
- `DataValidator_README.md` - 原始数据校验器文档（基于类型）

## 版本历史

- v1.0.0 (2024-03-02): 初始版本
  - 表结构校验
  - 主键校验
  - 数据类型校验
  - 空值校验
  - 范围校验
  - 外键校验
