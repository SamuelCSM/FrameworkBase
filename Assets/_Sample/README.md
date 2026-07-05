# _Sample —— 地基冒烟

`FrameworkSmoke.cs`：验证地基能在本壳工程内真正启动（Manager 初始化 + Timer 运转）。

## 冒烟步骤（Unity 内）
1. 新建空场景（File → New Scene，Basic/Empty 均可）。
2. 建一个空 GameObject（命名如 `_GameEntry`）。
3. 给它挂 `GameEntry` 组件 + `FrameworkSmoke` 组件。
4. 按 Play。Console 期望：
   - `[GameEntry] 框架初始化完成`
   - `[FrameworkSmoke] ✅ Framework OK ...`
   - 0.5s 后 `[FrameworkSmoke] ✅ Timer 0.5s 回调触发 ...`
5. 预期噪声（两条 Error，均正常）：`[UIManager] GetLayerRoot: bootstrap 未注入` + `[GameEntry] _loadingViewPrefab 未赋值`——纯框架裸场景没接 UI 基础设施（UIBootstrap/Loading 预制体），Manager 已在 Awake 全部初始化，不影响地基。真实项目接上即消失。

## 说明
- `Resources/AppConfig.asset` 已配成离线纯框架：`EnableHotUpdate=0`（跳过 HybridCLR 热更）、`UseNetworkLogin=0`（Mock 登录）。
- 真实项目：把 `EnableHotUpdate` 打开、拖入 Loading/Login 预制体、按业务接管即可。
- 本目录属 Sample，可随时整目录删除，不影响地基。
