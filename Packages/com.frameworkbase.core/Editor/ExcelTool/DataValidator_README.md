# DataValidator - 数据校验器

## 概述

DataValidator 是 Excel 转 SQLite 工具的数据校验组件，负责在导出前校验 Excel 数据的完整性和正确性。

## 功能特性

### 1. 主键重复检查
- 检测配置表中是否存在重复的主键值
- 支持通过 `[PrimaryKey]` 特性标记主键字段
- 如果没有标记，默认使用名为 "Id" 的字段作为主键

### 2. 类型检查
- 验证 Excel 单元格的值是否能正确转换为目标类型
- 支持所有基本类型：int, long, float, double, string, bool 等
- 支持复杂类型：枚举、数组、自定义类型（JSON）

### 3. 外键检查
- 验证外键引用的完整性
- 通过 `[ForeignKey(typeof(T))]` 特性标记外键字段
- 检查外键值是否存在于引用表的主键中

### 4. 范围校验
- 验证数值是否在指定范围内
- 通过 `[Range(min, max)]` 特性标记需要范围校验的字段
- 支持所有数值类型

## 使用方法

### 基本用法

```csharp
using Editor.ExcelTool;

// 1. 读取 Excel 文件
var excelReader = new ExcelReader();
var sheets = excelReader.ReadExcel("path/to/excel.xlsx");

// 2. 创建数据校验器
var validator = new DataValidator();

// 3. 校验数据
var errors = validator.Validate(sheets[0], typeof(YourConfigClass));

// 4. 处理校验结果
if (errors.Count == 0)
{
    Debug.Log("校验通过");
}
else
{
    foreach (var error in errors)
    {
        Debug.LogError(error.ToString());
    }
}
```

### 外键校验

外键校验需要传入所有表的数据：

```csharp
// 准备所有表数据
var allSheetData = new Dictionary<string, ExcelReader.ExcelSheetData>();
foreach (var sheet in sheets)
{
    allSheetData[sheet.SheetName] = sheet;
}

// 校验（包含外键检查）
var errors = validator.Validate(sheet, typeof(YourConfigClass), allSheetData);
```

## 配置类示例

### 基本配置类

```csharp
using SQLite;
using Framework.Data;

[Table("item_config")]
public class ItemConfig
{
    [PrimaryKey]
    [Column("id")]
    public int Id { get; set; }
    
    [Column("name")]
    public string Name { get; set; }
    
    [Column("type")]
    public int Type { get; set; }
    
    [Column("quality")]
    [Range(1, 5)]  // 品质范围：1-5
    public int Quality { get; set; }
}
```

### 带外键的配置类

```csharp
[Table("skill_config")]
public class SkillConfig
{
    [PrimaryKey]
    [Column("id")]
    public int Id { get; set; }
    
    [Column("name")]
    public string Name { get; set; }
    
    [Column("item_id")]
    [ForeignKey(typeof(ItemConfig))]  // 引用 ItemConfig 表
    public int ItemId { get; set; }
    
    [Column("damage")]
    [Range(0, 10000)]  // 伤害范围：0-10000
    public int Damage { get; set; }
}
```

## 校验错误类型

### ValidationErrorType 枚举

- **DuplicatePrimaryKey**: 主键重复
- **TypeError**: 类型转换错误
- **InvalidForeignKey**: 外键无效
- **RangeError**: 数值超出范围

### ValidationError 类

校验错误信息包含以下字段：

- `ErrorType`: 错误类型
- `SheetName`: 表名
- `RowIndex`: 行号（从1开始，包含注释行和字段名行）
- `FieldName`: 字段名
- `ErrorValue`: 错误值
- `Message`: 错误消息

## 错误处理

### 错误输出格式

```
[DuplicatePrimaryKey] 表:ItemConfig, 行:5, 字段:Id, 值:1001, 消息:主键值 '1001' 重复
[TypeError] 表:ItemConfig, 行:8, 字段:Quality, 值:abc, 消息:类型转换失败: 无法将 'abc' 转换为 Int32
[RangeError] 表:ItemConfig, 行:10, 字段:Quality, 值:10, 消息:值 10 超出范围 [1, 5]
[InvalidForeignKey] 表:SkillConfig, 行:6, 字段:ItemId, 值:9999, 消息:外键值 '9999' 在引用表 'ItemConfig' 中不存在
```

### 批量校验

对于多个 Excel 文件的批量校验，建议：

1. 第一遍：读取所有 Excel 文件，收集所有表数据
2. 第二遍：对每个表进行校验（包含外键校验）
3. 汇总所有错误，按类型分组输出

参考 `DataValidatorExample.cs` 中的 `ValidateBatch()` 方法。

## 注意事项

1. **主键检测**：
   - 优先使用 `[PrimaryKey]` 特性标记的字段
   - 如果没有标记，默认使用名为 "Id" 的字段
   - 如果都没有，主键检查将被跳过

2. **空值处理**：
   - 所有校验都会跳过 null 或 DBNull 值
   - 如果字段不允许为空，需要在其他地方进行非空校验

3. **外键校验**：
   - 需要引用表已经被读取并包含在 `allSheetData` 中
   - 引用表必须定义主键
   - 外键类型必须与引用表的主键类型兼容

4. **范围校验**：
   - 只对数值类型有效
   - 非数值类型的字段添加 `[Range]` 特性会被忽略

5. **性能考虑**：
   - 外键校验的时间复杂度为 O(n*m)，n 为当前表行数，m 为引用表行数
   - 对于大型配置表，建议在引用表中建立索引（在实际导出时）

## 集成到导出流程

在 Excel 导出工具中集成数据校验：

```csharp
public class ExcelExporter
{
    public void Export(string excelPath, string dbPath)
    {
        // 1. 读取 Excel
        var reader = new ExcelReader();
        var sheets = reader.ReadExcel(excelPath);
        
        // 2. 校验数据
        var validator = new DataValidator();
        var allErrors = new List<DataValidator.ValidationError>();
        
        foreach (var sheet in sheets)
        {
            var configType = GetConfigType(sheet.SheetName);
            var errors = validator.Validate(sheet, configType);
            allErrors.AddRange(errors);
        }
        
        // 3. 如果有错误，停止导出
        if (allErrors.Count > 0)
        {
            LogErrors(allErrors);
            throw new Exception($"数据校验失败，共 {allErrors.Count} 个错误");
        }
        
        // 4. 导出到 SQLite
        ExportToDatabase(sheets, dbPath);
    }
}
```

## 扩展

如果需要添加自定义校验规则：

1. 在 `ValidationErrorType` 枚举中添加新的错误类型
2. 在 `DataValidator` 类中添加新的校验方法
3. 在 `Validate()` 方法中调用新的校验方法

示例：

```csharp
// 添加唯一性校验
private List<ValidationError> ValidateUnique(
    ExcelReader.ExcelSheetData sheetData,
    PropertyInfo[] properties)
{
    var errors = new List<ValidationError>();
    
    foreach (var prop in properties)
    {
        var uniqueAttr = prop.GetCustomAttribute<UniqueAttribute>();
        if (uniqueAttr == null) continue;
        
        // 实现唯一性检查逻辑...
    }
    
    return errors;
}
```

## 相关文件

- `DataValidator.cs`: 数据校验器实现
- `DataValidatorExample.cs`: 使用示例
- `ExcelReader.cs`: Excel 读取器
- `ConfigValidationAttributes.cs`: 校验特性定义（Range, ForeignKey）

## 版本历史

- v1.0.0 (2024-03-02): 初始版本
  - 实现主键重复检查
  - 实现类型检查
  - 实现外键检查
  - 实现范围校验
