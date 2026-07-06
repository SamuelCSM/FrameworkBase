# Excel工具 - 使用指南

## 概述

Excel工具用于将Excel配置表导出为SQLite数据库，并自动生成对应的C#配置类代码。

## Excel格式规范

### 标准格式

Excel文件应遵循以下格式：

```
第0行（注释行）：字段说明
第1行（字段名行）：字段名称（对应C#属性名）
第2行及以后（数据行）：实际配置数据
```

### 示例

| ID（主键） | 名称 | 类型 | 品质 | 图标路径 | 描述 |
|-----------|------|------|------|----------|------|
| Id | Name | Type | Quality | Icon | Description |
| 1001 | 铁剑 | 1 | 1 | icon_sword_001 | 一把普通的铁剑 |
| 1002 | 钢剑 | 1 | 2 | icon_sword_002 | 一把锋利的钢剑 |

## 支持的数据类型

### 基础类型

- **int**: 整数类型
- **long**: 长整数类型
- **float**: 单精度浮点数
- **double**: 双精度浮点数
- **string**: 字符串类型
- **bool**: 布尔类型（支持：true/false, 1/0, yes/no, 是/否）

### 枚举类型

枚举可以使用名称或数值：

```csharp
public enum ItemType
{
    Weapon = 1,
    Armor = 2,
    Consumable = 3
}
```

Excel中可以填写：
- 名称：`Weapon`
- 数值：`1`

### 数组类型

数组使用逗号或分号分隔，可选方括号：

```
[1,2,3]
1,2,3
1;2;3
```

### 自定义类型（JSON）

复杂对象使用JSON格式：

```json
{"x":100,"y":200}
```

## ExcelReader类

### 基本用法

```csharp
using Editor.ExcelTool;

// 创建读取器
var reader = new ExcelReader();

// 读取Excel文件
var sheets = reader.ReadExcel("path/to/config.xlsx");

// 遍历所有工作表
foreach (var sheet in sheets)
{
    Debug.Log($"表名: {sheet.SheetName}");
    Debug.Log($"字段数: {sheet.FieldNames.Count}");
    Debug.Log($"数据行数: {sheet.DataRows.Count}");
    
    // 遍历数据行
    foreach (var row in sheet.DataRows)
    {
        foreach (var field in sheet.FieldNames)
        {
            var value = row[field];
            Debug.Log($"{field}: {value}");
        }
    }
}
```

### 自定义格式

如果Excel格式不同，可以自定义：

```csharp
var format = new ExcelReader.ExcelFormat
{
    CommentRowIndex = 0,      // 注释行索引
    FieldNameRowIndex = 1,    // 字段名行索引
    DataStartRowIndex = 2     // 数据起始行索引
};

var reader = new ExcelReader(format);
```

### 类型解析

使用`ParseCellValue`方法解析单元格值：

```csharp
// 解析为int
int id = (int)ExcelReader.ParseCellValue(cellValue, typeof(int));

// 解析为枚举
ItemType type = (ItemType)ExcelReader.ParseCellValue(cellValue, typeof(ItemType));

// 解析为数组
int[] values = (int[])ExcelReader.ParseCellValue("[1,2,3]", typeof(int[]));

// 解析为自定义类型
Vector2 pos = (Vector2)ExcelReader.ParseCellValue("{\"x\":100,\"y\":200}", typeof(Vector2));
```

## 数据结构

### ExcelSheetData

表示一个Excel工作表的数据：

```csharp
public class ExcelSheetData
{
    public string SheetName { get; set; }              // 表名
    public List<string> FieldNames { get; set; }       // 字段名列表
    public List<string> Comments { get; set; }         // 注释列表
    public List<Dictionary<string, object>> DataRows { get; set; }  // 数据行
}
```

### ExcelFormat

定义Excel文件的格式：

```csharp
public class ExcelFormat
{
    public int CommentRowIndex { get; set; } = 0;      // 注释行索引
    public int FieldNameRowIndex { get; set; } = 1;    // 字段名行索引
    public int DataStartRowIndex { get; set; } = 2;    // 数据起始行索引
}
```

## 依赖包

ExcelReader依赖以下NuGet包：

- **ExcelDataReader** (3.7.0): 核心Excel读取库
- **ExcelDataReader.DataSet** (3.7.0): DataSet扩展
- **System.Text.Encoding.CodePages** (7.0.0): 编码支持

这些包已在`Assets/packages.config`中配置。

## 注意事项

1. **文件格式**：仅支持`.xlsx`格式（Excel 2007及以上）
2. **空行处理**：自动跳过完全为空的数据行
3. **空字段**：空字段名的列会被跳过
4. **类型转换**：类型转换失败时返回默认值并记录警告
5. **编码**：自动注册编码提供程序以支持中文等字符

## 错误处理

ExcelReader会记录详细的错误日志：

- 文件不存在：抛出`FileNotFoundException`
- 读取失败：抛出异常并记录错误日志
- 类型解析失败：返回默认值并记录警告日志

## 下一步

ExcelReader是Excel工具的第一部分，后续还需要实现：

1. **数据校验器**：校验主键、类型、外键、范围等
2. **代码生成器**：根据Excel生成C#配置类
3. **导出器**：将Excel数据导出为SQLite数据库

详见任务列表中的其他子任务。
