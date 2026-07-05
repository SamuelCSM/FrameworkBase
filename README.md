# FrameworkBase

从 **ClientBase（Blokus）** 剥离出的可复用 Unity 底层地基 + 最小可跑壳工程，用于「引地基 + 写业务」快速起新项目。

- **Unity 版本**：2022.3.62f3（须与之一致）
- **形态**：最小 Unity 壳工程，内含 `Framework` asmdef（运行时地基）+ 复用所需的 package/插件；同时充当新项目模板。
- **来源**：ClientBase 的 `Assets/Scripts/Framework/` 单向抽取（复制，非 submodule 反向引用）。ClientBase 保持不动，二者从此各自演进。

## 目录

```
Assets/Scripts/Framework/   地基运行时（Audio/Camera/ConfigData/Core/Event/Input/
                            Localization/Network/Resource/Save/Scene/Stage/Timer/Tips/UI/Utils/HotUpdate）
Assets/Packages/            插件 DLL（protobuf-net 2.4.6 / SQLite / ExcelDataReader ...，走 NuGetForUnity）
Packages/ ProjectSettings/  与 ClientBase 一致的 Unity 版本与包版本
```

## 现状与路线

- [x] **A1** Framework 运行时 + 配置照搬入壳（已移除 URP/timeline/visualscripting/2d.sprite）
- [x] **A2** 通用 Editor 工具移入 `Framework/Editor/` + `Framework.Editor.asmdef`（AddressablesSetup/ProtobufInstaller/HotUpdatePublisher/HybridCLRStreamingAssetsSync/FullPackage*/AppConfigAssetMenu/ExcelTool；ProtoGenerator 等协议绑定工具不迁）
- [x] **B** 纯框架启动能力（热更总开关 `EnableHotUpdate`）+ 最小冒烟 sample（`Assets/_Sample/`）+ 离线 `Resources/AppConfig.asset`。**已在 Unity 冒烟通过**：9 个 Manager 全启动 + Timer 回调运转（裸场景两条 UI 相关 Error 属预期）。登录/心跳联网冒烟需服务端，另行安排。
- [ ] **C** 序列化库替换：`protobuf-net` → `Google.Protobuf`（AOT 主因，`.proto` + protoc 双端生成 + 路由伴生 partial）

### 约定
- **日志类名为 `Framework.GameLog`（不叫 `Logger`）**：`UnityEngine` 里有 `Logger` 类型，`Framework` 命名空间外的文件一旦同时 `using Framework;` + `using UnityEngine;` 就会 CS0104 二义。改名 `GameLog` 后任意文件直接 `GameLog.Log/Warning/Error/Exception` 均无冲突，**勿再引入名为 `Logger` 的类型**。API 与原 `Logger` 完全一致。

### 已知「模板卫生」待清理（不阻断编译）
- `Framework/Event/GameMessage.cs`：含 Blokus 专有事件枚举（20000-20999），新项目应替换为自身业务事件。
- `Framework/HotUpdate/VersionManager.cs`：默认热更程序集名硬编码为 Blokus.Core/GameProtocol/HotUpdate，新项目经 `AppConfig` 覆盖或改默认。
