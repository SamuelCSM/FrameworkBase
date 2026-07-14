# FrameworkBase

可复用的 Unity 纯底层框架 + 最小可跑壳工程，用于「引地基 + 写业务」快速起新商业项目。
定位：移动端、强联网、热更新、长期运营；框架主干不包含任何业务概念（背包/货币/任务等）。

- **Unity 版本**：2022.3.62f3（须与之一致，见 `ProjectSettings/ProjectVersion.txt`）
- **形态**：**嵌入式 UPM 包**（`Packages/com.frameworkbase.core`，semver + CHANGELOG）
  + 最小壳工程（协议模板程序集 / 冒烟示例 / 工程配置），壳工程同时充当新项目模板。
- **分发**：本仓库内框架以 embedded package 形式演进；新项目经 git URL /
  本地路径引用 `com.frameworkbase.core` 获得只读版本化依赖，修复经版本发布回流，不再源码分叉。
- **来源**：ClientBase 的 `Assets/Scripts/Framework/` 单向抽取（复制，非 submodule 反向引用）。

## 目录

```
Packages/com.frameworkbase.core/  框架包（版本见 package.json，变更记录见 CHANGELOG.md）
├── (运行时)                 Audio/Camera/ConfigData/Core/Event/Input/Localization/
│                            Network/Resource/Save/Scene/Stage/Timer/Tips/UI/Utils/HotUpdate
├── Editor/                  通用工具链（发布/构建/配置表/协议安装/清单签名/CI 入口）
└── Tests/EditMode/          单元测试（事件/封包/对象池/校时/定时器/热更安全等，
                             经 manifest.json 的 testables 接入 Test Runner）
Assets/Scripts/GameProtocol/ 协议模板程序集（Google.Protobuf 生成物 + 路由伴生 partial，随项目走）
Assets/Scripts/HotUpdate/    热更业务壳；Clicker 为参考垂直切片，起正式项目时删除/替换
Assets/Tests/EditMode/       壳工程/参考样例专属测试（不得反向进入框架包）
Assets/_Sample/              最小冒烟示例（FrameworkSmoke）
Assets/Packages/             插件 DLL（Google.Protobuf 3.28.3 / SQLite / ExcelDataReader，走 NuGetForUnity，已入库）
proto/                       协议源（.proto，主号 001 为框架保留段：心跳等系统协议）
Tools/ProtoGen/              一键双端协议生成器（gen-proto.bat 触发，详见其 README）
Tools/ci/                    本地 CI 门禁脚本（run-ci.ps1）与 CI 说明
.github/workflows/ci.yml     GitHub Actions 门禁：编译 + EditMode 测试（GameCI）
```

## 核心能力现状

| 能力 | 状态 | 入口 |
|---|---|---|
| 启动流程 | ✅ 九步序列 + 重试 + 强更闸门 + 阶段埋点 | `Core/LaunchFlow.cs` |
| 热更（资源+代码） | ✅ HybridCLR + Addressables 三版本闭环 | `HotUpdate/` |
| 热更安全 | ✅ prod 强制 HTTPS / 清单 RSA 签名 / 补丁强制 MD5 / 构建期门禁 | `HotUpdate/UpdateSecurity.cs` |
| 网络 | ✅ TCP + 心跳 + 指数退避重连 + 重连后重鉴权 + TLS 指纹固定选项 | `Network/` |
| 协议 | ✅ proto-first，Google.Protobuf（AOT 安全二进制路径）+ 路由生成 | `proto/` + `Tools/ProtoGen` |
| 配置表 | ✅ Excel → SQLite 导出/校验/代码生成，首包/热更库兼容检查 | `Editor/ExcelTool` + `ConfigData/` |
| 存档 | ✅ AES+HMAC、账号目录隔离、原子写、按文件锁 | `Save/SaveManager.cs` |
| UI | ✅ 层级/导航栈/对象池/遮罩/动画 + LoopScroll/TabGroup | `UI/` |
| 运营接入 | ✅ 平台 SDK 抽象 + 埋点事件管道/Schema + 用户维度远配与功能开关 | `Sdk/` + `Analytics/` + `RemoteConfig/` |
| 遥测 | ✅ 崩溃回捞（本地+可选上报）+ 启动耗时 + 业务埋点管道 | `Core/Telemetry/` + `Analytics/` |
| CI/构建 | ✅ 本地与远端双门禁；batchmode Player 构建入口 | `Tools/ci/` + `Editor/BuildEntry.cs` |

## 里程碑

- [x] **A1** Framework 运行时 + 配置照搬入壳（已移除 URP/timeline/visualscripting/2d.sprite）
- [x] **A2** 通用 Editor 工具移入 `Framework/Editor/` + `Framework.Editor.asmdef`
- [x] **B** 纯框架启动能力（热更总开关 `EnableHotUpdate`）+ 最小冒烟 sample（`Assets/_Sample/`）
      + 离线 `Resources/AppConfig.asset`，Unity 冒烟通过
- [x] **C** 序列化库替换：`protobuf-net` → `Google.Protobuf`（`.proto` + protoc 双端生成 + 路由伴生 partial，
      仅走二进制路径保 IL2CPP/AOT 安全）
- [x] **D1** 热更供应链安全闭环（URL 准入 / 清单签名 / 补丁强制哈希 / 构建门禁）
- [x] **D2** batchmode 构建入口 + 编译/测试双入口门禁（本地脚本 + GitHub Actions）
- [x] **D3** 主干业务残留清零（演示协议中性化 / 文档去项目专名 / 机器路径出库）
- [x] **E** 分发模型：Framework 迁嵌入式 UPM 包 `com.frameworkbase.core`（semver + CHANGELOG），
      壳工程转正为模板工程
- [x] **F** 运营能力层：平台 SDK 抽象 / 埋点事件管道 / 远程配置与功能开关
- [ ] **G** 参考垂直切片：A～C（启动壳/配表/Clicker）已收口；D～G（真实热更/登录/CI/接入文档）待完成

## 使用须知（新项目起步）

1. **热更安全**：正式发布前必须
   - `AppConfig.AppEnv` 置 `prod`（届时强制 HTTPS，明文 HTTP 会在构建期直接失败）；
   - 菜单 `Framework → Hot Update Security → Generate Signing Key Pair` 生成密钥对，
     公钥填入 `AppConfig.UpdateManifestPublicKey`（私钥留在发布机，勿入库）。
2. **协议**：业务模块主号从 002 起占号（001 为框架保留段）；改 `proto/*.proto` 后跑 `gen-proto.bat`。
   `proto/sample/sample_list.proto` 是模板示例，起步时删除替换。
3. **热更程序集组**：经 `AppConfig.HotUpdateAssemblyFiles` 配置（依赖在前），留空默认
   `GameProtocol → HotUpdate`；无热更程序集的项目关闭 `EnableHotUpdate`。
4. **提交纪律**：提交前跑 `Tools\ci\run-ci.ps1`（需关闭 Unity 编辑器）；push/PR 由 GitHub Actions
   复验（首次启用需配置 Unity 许可 Secrets，见 `Tools/ci/README.md`）。
5. **业务广播消息**：在业务程序集自建枚举从 20000 起占号，订阅/发布时转成 `int` 走
   `EventManager` 重载；`Framework/Event/GameMessage.cs` 仅保留框架系统段（9000-9999）
   与登录段（10000-10999）。

## 约定

- **日志类名为 `Framework.GameLog`（不叫 `Logger`）**：`UnityEngine` 里有 `Logger` 类型，
  同时 `using Framework;` + `using UnityEngine;` 会 CS0104 二义。**勿再引入名为 `Logger` 的类型**。
- **框架主干禁止业务概念**：背包/货币/任务/商店/战斗等一律进业务热更程序集；
  演示代码只允许中性命名（Sample/Echo），并标注"模板示例，起步时删除"。
- **机器级配置不入库**：部署路径、签名私钥路径等存 EditorPrefs；仓库内不允许出现开发机绝对路径。
