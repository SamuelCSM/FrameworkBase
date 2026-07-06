# HybridCLR 集成指南

## 概述

本文档说明如何在Unity项目中集成HybridCLR热更新方案。

## 安装步骤

### 1. 安装HybridCLR包

HybridCLR已通过Unity Package Manager添加到项目中：

```json
"com.code-philosophy.hybridclr": "https://gitee.com/focus-creative-games/hybridclr_unity.git"
```

### 2. 安装HybridCLR编辑器工具

在Unity编辑器中：

1. 打开菜单：`HybridCLR > Installer...`
2. 点击 `Install` 按钮安装il2cpp_plus和libil2cpp
3. 等待安装完成

### 3. 配置热更新程序集

已创建的配置：

- **HotUpdate.asmdef**: 热更新程序集定义文件
  - 位置：`Assets/Scripts/HotUpdate/HotUpdate.asmdef`
  - 引用：Framework, UniTask, Addressables, GameProtocol（Google.Protobuf 为自动引用插件）

### 4. 配置HybridCLR设置

在Unity编辑器中：

1. 打开菜单：`HybridCLR > Settings`
2. 在 `Hot Update Assemblies` 中添加：
   - `Blokus.Core`
   - `GameProtocol`
   - `HotUpdate`
3. 配置其他选项（可选）：
   - `Enable` - 启用HybridCLR
   - `Use Global Il2cpp` - 使用全局il2cpp

### 5. 配置Link.xml

已创建 `Assets/link.xml` 文件，防止代码裁剪。包含：

- HotUpdate程序集
- Framework程序集
- UniTask
- Protobuf
- SQLite
- Unity核心模块

### 6. 生成AOT泛型补充元数据

在打包前：

1. 打开菜单：`HybridCLR > Generate > All`
2. 这将生成：
   - AOT泛型补充元数据
   - 热更新DLL
   - Link.xml补充

### 7. 构建项目

#### 开发模式（编辑器）

在编辑器中直接运行，HybridCLR会自动使用HotUpdate程序集。

#### 发布模式

1. 打开菜单：`File > Build Settings`
2. 选择目标平台（Windows/Android/iOS）
3. 点击 `Build` 或 `Build And Run`
4. HybridCLR会自动处理热更新程序集

## 热更新流程

### 1. 准备热更新包

```bash
# 1. 生成热更新DLL
HybridCLR > Generate > All

# 2. 复制DLL到StreamingAssets
# DLL位置：HybridCLRData/HotUpdateDlls/{Platform}/{AssemblyName}.dll
# 目标位置：Assets/StreamingAssets/{AssemblyName}.dll.bytes
```

### 2. 上传到服务器

将以下文件上传到更新服务器：

- `Blokus.Core.dll.bytes` - 双端同源规则内核
- `GameProtocol.dll.bytes` - 项目协议目录
- `HotUpdate.dll.bytes` - 热更新业务代码入口
- `version.json` - 版本信息
- 其他资源文件

### 3. 客户端更新流程

```csharp
// 1. 检查更新
var updateInfo = await GameEntry.HotUpdate.CheckUpdateAsync(updateUrl);

// 2. 下载补丁
if (updateInfo.PatchFiles.Count > 0)
{
    await GameEntry.HotUpdate.DownloadPatchAsync(updateInfo, progress =>
    {
        Debug.Log($"下载进度: {progress * 100}%");
    });
}

// 3. 加载热更新程序集
GameEntry.HotUpdate.LoadHotUpdateAssembly();

// 4. 加载AOT泛型补充元数据（可选）
GameEntry.HotUpdate.LoadMetadata();

// 5. 启动热更新逻辑
GameEntry.HotUpdate.StartHotfix();
```

## 注意事项

### 1. 热更新限制

- 不能添加新的值类型字段到已有类
- 不能修改已有方法的签名
- 不能添加新的虚方法
- 详见：[HybridCLR限制文档](https://hybridclr.doc.code-philosophy.com/)

### 2. AOT泛型

对于泛型方法，需要在AOT代码中提前实例化：

```csharp
// 在Framework层的某个初始化方法中
public static void InitializeAOTGenerics()
{
    // 示例：预实例化常用泛型
    _ = new List<int>();
    _ = new Dictionary<int, string>();
    // ... 其他泛型类型
}
```

### 3. 反射限制

IL2CPP会裁剪未使用的代码，使用反射时需要：

- 在 `link.xml` 中保留类型
- 或使用 `[Preserve]` 特性标记

### 4. 调试

#### 编辑器调试

在编辑器中，HybridCLR会直接使用HotUpdate程序集，可以正常调试。

#### 真机调试

1. 使用 `Development Build` 选项
2. 启用 `Script Debugging`
3. 使用日志输出调试信息

## 常见问题

### Q1: 找不到HybridCLR菜单

**A**: 确保已正确安装HybridCLR包，重启Unity编辑器。

### Q2: 热更新DLL加载失败

**A**: 检查：
- DLL文件是否存在
- 文件路径是否正确
- DLL是否被正确生成

### Q3: 反射找不到类型

**A**: 在 `link.xml` 中添加对应的程序集和类型。

### Q4: 泛型方法报错

**A**: 在AOT代码中预实例化该泛型类型。

## 参考资料

- [HybridCLR官方文档](https://hybridclr.doc.code-philosophy.com/)
- [HybridCLR GitHub](https://github.com/focus-creative-games/hybridclr)
- [HybridCLR示例项目](https://github.com/focus-creative-games/hybridclr_trial)

## 版本信息

- Unity版本：2022.3.62f3
- HybridCLR版本：最新版（通过Git安装）
- 创建日期：2026-03-04
