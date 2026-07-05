# ExcelDataReader 安装指南

## 重要提示

ExcelReader需要ExcelDataReader库才能正常工作。请按照以下步骤安装。

## 安装步骤

### 方法1：使用NuGet for Unity（推荐）

1. **安装NuGet for Unity插件**
   - 从GitHub下载：https://github.com/GlitchEnzo/NuGetForUnity/releases
   - 或从Unity Asset Store安装
   - 将.unitypackage导入到项目中

2. **安装ExcelDataReader包**
   - 打开Unity菜单：`Window > NuGet > Manage NuGet Packages`
   - 搜索并安装以下包：
     - `ExcelDataReader` (版本 3.7.0)
     - `ExcelDataReader.DataSet` (版本 3.7.0)
     - `System.Text.Encoding.CodePages` (版本 7.0.0)

3. **重启Unity编辑器**

4. **验证安装**
   - 运行 `Tools > Excel > Check ExcelDataReader Installation`
   - 如果显示"✓ ExcelDataReader已正确安装"，则安装成功

### 方法2：手动安装DLL

1. **下载NuGet包**
   
   从NuGet.org下载以下包：
   - https://www.nuget.org/packages/ExcelDataReader/3.7.0
   - https://www.nuget.org/packages/ExcelDataReader.DataSet/3.7.0
   - https://www.nuget.org/packages/System.Text.Encoding.CodePages/7.0.0

2. **解压并提取DLL**
   
   - 将.nupkg文件重命名为.zip
   - 解压文件
   - 找到 `lib/netstandard2.0` 或 `lib/netstandard2.1` 文件夹
   - 复制其中的DLL文件

3. **放置DLL**
   
   将DLL文件复制到以下位置之一：
   - `Assets/Plugins/ExcelDataReader/`（推荐）
   - `Assets/Plugins/`

   需要的DLL文件：
   - ExcelDataReader.dll
   - ExcelDataReader.DataSet.dll
   - System.Text.Encoding.CodePages.dll

4. **重启Unity编辑器**

5. **验证安装**
   
   - 运行 `Tools > Excel > Check ExcelDataReader Installation`

## 验证安装

安装完成后，可以通过以下方式验证：

1. **检查编译错误**
   - 打开 `Assets/Editor/ExcelTool/ExcelReader.cs`
   - 确保没有编译错误

2. **运行测试**
   - 运行 `Tools > Excel > Test Read Excel`
   - 选择一个.xlsx文件
   - 查看Console输出

3. **检查安装状态**
   - 运行 `Tools > Excel > Check ExcelDataReader Installation`

## 常见问题

### Q: 安装后仍然报错？

A: 请检查：
1. 是否重启了Unity编辑器
2. DLL文件是否放在正确的位置
3. Unity版本是否支持.NET Standard 2.1

### Q: 支持哪些Unity版本？

A: Unity 2020.3及以上版本，需要.NET Standard 2.1支持。

### Q: 可以使用其他版本的ExcelDataReader吗？

A: 建议使用3.7.0版本，这是经过测试的稳定版本。

## 卸载

如果需要卸载ExcelDataReader：

1. 删除DLL文件（如果手动安装）
2. 或使用NuGet for Unity卸载包
3. 重启Unity编辑器

## 技术支持

如果遇到问题，请：

1. 查看Unity Console的错误日志
2. 检查ExcelDataReader的GitHub Issues：https://github.com/ExcelDataReader/ExcelDataReader/issues
3. 查看NuGet for Unity的文档：https://github.com/GlitchEnzo/NuGetForUnity

## 下一步

安装完成后，可以：

1. 阅读 `Excel工具使用指南.md` 了解如何使用
2. 运行 `Tools > Excel > Test Read Excel` 测试功能
3. 查看 `README.md` 了解ExcelReader的API

---

**注意**：ExcelDataReader仅支持.xlsx格式（Excel 2007及以上），不支持旧的.xls格式。
