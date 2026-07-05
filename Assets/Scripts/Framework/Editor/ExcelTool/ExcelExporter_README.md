# Excel 导出器使用指南

## 概述

Excel 导出器负责将 Excel 配置表数据导出到 SQLite 数据库，支持单个文件导出和批量导出。

## 功能特性

- 单个 Excel 文件导出
- 批量 Excel 文件导出
- 数据校验（可选）
- 导出进度显示
- 导出错误日志
- 支持覆盖/不覆盖已存在的表
- 详细日志输出（可选）
- 支持客户端/服务端表级导出目标过滤
- 服务端 TSV 与服务端生成类内容未变化时不会重新写入文件

## 使用方式

### 方式一：使用可视化窗口

1. 打开导出器窗口
   - 菜单：`Tools > Excel > Excel 导出器`

2. 选择文件范围
   - **Single**：导出单个 Excel 文件（日常改表）
   - **Batch**：导出 `Assets/RefData_Excel` 下全部 Excel；**每个 xlsx 会导出其全部工作表**（如 `首包配表.xlsx` 内含 `language` + `loading_tips`）

3. 选择输出目标
   - **StreamingAssetsOnly（仅首包）**：只写 `StreamingAssets/RefData/config.db`，不触发热更资源
   - **HotUpdateOnly（仅热更）**：只写 `ResourcesOut/RefData/config.db.bytes`，首包不变；热更库不存在时会先从首包拷贝基线
   - **Both（两者同步）**：先写首包，再将整库同步到热更 `.bytes`

4. 推荐搭配
   - 日常改单表：`Single` + `HotUpdateOnly`
   - 发版定首包：`Batch` + `StreamingAssetsOnly`
   - 首包与热更同时更新：`Batch` + `Both`

5. 配置导出选项
   - **首包数据库 / 热更数据库**：按输出目标显示对应路径
   - **覆盖已存在的表**：是否覆盖数据库中同名表（表级覆盖，非清空整个 RefData）
   - **启用数据校验**：是否在导出前进行数据校验
   - **显示详细日志**：是否输出详细的导出日志
   - **同时导出服务端 TSV**：按导出目标规则输出服务端 `.txt` 和服务端配置类

6. 点击"开始导出"按钮

7. 查看导出结果
   - 成功：显示表名和行数
   - 失败：显示错误消息
   - 警告：显示数据校验警告

## 导出目标规则

配表默认按客户端和服务端双端导出，不需要逐表配置。少数例外表在
`Assets/Editor/ExcelTool/ConfigExportRules.asset` 中维护：

- `Both`：客户端 SQLite 与服务端 TSV 都导出，默认值
- `ClientOnly`：只导出客户端，不生成服务端 `.txt` 与服务端配置类
- `ServerOnly`：只导出服务端，跳过客户端 SQLite 写入

当前默认规则中，`language`、`loading_tips`、`ui_wnd_res` 为 `ClientOnly`。

## 文件写入策略

服务端 TSV 导出会先比较目标文件内容；内容完全一致时不会写入文件，也不会刷新文件修改时间。

服务端配置类生成时会忽略头部 `// 生成时间:` 行做内容比较；如果除了生成时间以外没有变化，也会跳过写入，避免产生只有时间戳变化的无意义 diff。

### 方式二：使用代码

```csharp
using Editor.ExcelTool;

// 创建导出配置
var config = new ExcelExporter.ExportConfig
{
    OutputDbPath = "Assets/StreamingAssets/RefData/config.db",
    AddressableBytesOutputPath = "Assets/ResourcesOut/RefData/config.db.bytes",
    OutputTarget = ExcelExporter.DatabaseOutputTarget.HotUpdateOnly,
    OverwriteExistingTables = true,
    EnableValidation = true,
    VerboseLogging = true
};

// 创建导出器
var exporter = new ExcelExporter(config);

// 导出单个文件
var result = exporter.ExportExcel("Assets/RefData_Excel/ItemConfig.xlsx");

// 检查结果
if (result.Success)
{
    Debug.Log($"导出成功: {result.TableName}, 行数: {result.RowCount}");
}
else
{
    Debug.LogError($"导出失败: {result.ErrorMessage}");
}
```

## 导出配置

### ExportConfig 属性

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| OutputDbPath | string | StreamingAssets/RefData/config.db | 首包数据库路径 |
| AddressableBytesOutputPath | string | ResourcesOut/RefData/config.db.bytes | 热更数据库路径 |
| OutputTarget | DatabaseOutputTarget | HotUpdateOnly | 仅首包 / 仅热更 / 两者同步 |
| OverwriteExistingTables | bool | true | 是否覆盖已存在的表 |
| EnableValidation | bool | true | 是否启用数据校验 |
| VerboseLogging | bool | false | 是否显示详细日志 |

### ExportResult 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| Success | bool | 是否导出成功 |
| TableName | string | 导出的表名 |
| RowCount | int | 导出的行数 |
| ErrorMessage | string | 错误消息 |
| Warnings | List<string> | 警告消息列表 |

## 导出流程

```
1. 读取 Excel 文件
   ↓
2. 解析表结构（字段名、类型、注释）
   ↓
3. 数据校验（可选）
   - 主键重复检查
   - 类型检查
   - 外键检查
   - Range 范围校验
   ↓
4. 创建 SQLite 表
   ↓
5. 插入数据
   ↓
6. 返回导出结果
```

## 批量导出

### 使用代码批量导出

```csharp
// 准备要导出的文件列表
var excelFiles = new List<string>
{
    "Assets/RefData_Excel/ItemConfig.xlsx",
    "Assets/RefData_Excel/SkillConfig.xlsx",
    "Assets/RefData_Excel/MonsterConfig.xlsx"
};

// 批量导出，带进度回调
var results = exporter.ExportBatch(excelFiles, (current, total) =>
{
    Debug.Log($"导出进度: {current}/{total}");
});

// 统计结果
var successCount = results.Count(r => r.Success);
var failCount = results.Count(r => !r.Success);
Debug.Log($"批量导出完成: 成功 {successCount}, 失败 {failCount}");
```

### 使用窗口批量导出

1. 选择"批量模式"
2. 选择包含 Excel 文件的文件夹
3. 点击"开始导出"
4. 查看导出进度和结果

## 数据类型映射

Excel 类型到 SQLite 类型的映射：

| Excel 类型 | SQLite 类型 |
|-----------|------------|
| int, long, short, byte, bool | INTEGER |
| float, double, decimal | REAL |
| string, 数组, 自定义类型 | TEXT |

## 数据校验

导出前会进行以下校验（如果启用）：

1. **主键重复检查**
   - 检查主键字段是否有重复值
   - 主键字段通常是第一个字段

2. **类型检查**
   - 检查数据是否符合字段类型定义
   - 例如：int 字段不能包含非数字值

3. **外键检查**
   - 检查外键引用是否存在
   - 需要在字段上添加 ForeignKey 特性

4. **Range 范围校验**
   - 检查数值是否在指定范围内
   - 需要在字段上添加 Range 特性

## 错误处理

### 常见错误

1. **Excel 文件不存在**
   - 检查文件路径是否正确
   - 确保文件扩展名为 .xlsx

2. **Excel 文件为空**
   - 检查 Excel 文件是否包含数据
   - 确保至少有 4 行（注释、字段名、类型、数据）

3. **数据校验失败**
   - 查看错误消息了解具体问题
   - 修复 Excel 数据后重新导出
   - 或者禁用数据校验

4. **SQLite 数据库错误**
   - 检查输出路径是否有写入权限
   - 确保数据库文件未被其他程序占用

### 错误日志

导出失败时会输出详细的错误日志：

```
[ExcelExporter] 导出失败: ItemConfig
错误: 数据校验失败:
- 第 5 行: 主键重复 (Id=1001)
- 第 8 行: 类型错误 (Price 应为 int，实际为 "abc")
```

## 性能优化

### 批量导出优化

1. **使用事务**
   - 导出器自动使用事务批量插入数据
   - 大幅提升插入性能

2. **禁用详细日志**
   - 在批量导出时禁用 VerboseLogging
   - 减少日志输出开销

3. **合理使用数据校验**
   - 开发阶段启用数据校验
   - 发布阶段可以禁用以提升速度

### 大文件处理

对于包含大量数据的 Excel 文件：

1. 使用批量导出而不是多次单个导出
2. 禁用详细日志
3. 考虑分批导出（分多个小文件）

## 最佳实践

1. **导出前备份数据库**
   - 如果不确定是否覆盖，先备份数据库
   - 或者设置 OverwriteExistingTables = false

2. **使用数据校验**
   - 开发阶段始终启用数据校验
   - 及早发现数据问题

3. **查看导出结果**
   - 检查导出的行数是否正确
   - 查看警告消息

4. **使用版本控制**
   - 将 Excel 文件纳入版本控制
   - 记录每次导出的版本

5. **自动化导出**
   - 使用批量导出功能
   - 编写自动化脚本

## 相关文档

- `ExcelReader_README.md` - Excel 读取器文档
- `DataValidator_README.md` - 数据校验器文档
- `CodeGenerator_README.md` - 代码生成器文档
- `配置表编辑器使用指南.md` - 配置表编辑器文档

## 常见问题

### Q1: 如何导出指定的工作表？

A: 在 ExportExcel 方法中传入工作表名称：

```csharp
var result = exporter.ExportExcel("path/to/file.xlsx", "Sheet1");
```

### Q2: 如何处理导出失败的文件？

A: 检查 ExportResult 的 ErrorMessage 属性，根据错误消息修复问题后重新导出。

### Q3: 数据库文件应该放在哪里？

A: 建议放在 `Assets/StreamingAssets/` 目录下，这样可以在运行时访问。

### Q4: 如何查看导出的数据？

A: 使用 SQLite 客户端工具（如 DB Browser for SQLite）打开数据库文件查看。

### Q5: 批量导出时如何跳过某些文件？

A: 在准备文件列表时过滤掉不需要的文件：

```csharp
var excelFiles = Directory.GetFiles(folder, "*.xlsx")
    .Where(f => !f.Contains("Template"))  // 跳过模板文件
    .ToList();
```

## 版本历史

- v1.0.0 (2024-03-02): 初始版本
  - 支持单个文件导出
  - 支持批量文件导出
  - 支持数据校验
  - 支持导出进度显示
  - 支持导出错误日志
