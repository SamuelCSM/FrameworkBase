# Tips 轻提示系统使用规范

## 定位

`Tips` 系统用于全局非阻塞轻提示，例如网络超时、保存成功、复制成功、普通操作失败和应用内通知。业务层只描述提示内容，不直接创建 UI 节点或打开提示窗口。

## 推荐入口

```csharp
GameEntry.Tips.ShowLang("#1_copy_success");
GameEntry.Tips.ShowSuccess("#1_save_success");
GameEntry.Tips.ShowWarning("#1_room_invite_expired");
GameEntry.Tips.ShowError("#1_network_error");
GameEntry.Tips.ShowRaw(serverMessage, TipStyle.Error);
```

## 多语言规则

- 代码主动控制的提示使用 `#1_xxx`，通过 `ShowLang`、`ShowSuccess`、`ShowWarning`、`ShowError` 展示。
- 服务端返回文本、玩家名、版本号、数字等不应翻译的内容使用 `ShowRaw`。
- 找不到多语言 key 时由 `Language.Get` 回退原 key，不应阻断业务流程。

## 调度规则

- 同屏最多展示 3 条。
- 待展示队列最多保留 20 条。
- 相同 `DedupeKey` 在 1.5 秒内只展示一次。
- 队列满时低优先级提示先丢弃，高优先级提示可挤掉低优先级提示。
- 普通和成功提示默认停留 2 秒，警告和错误提示默认停留 2.5 秒。

## 不适合 Tips 的场景

- 强制更新、封号、登录过期、断线重连失败：使用系统弹窗或遮罩流程。
- 需要玩家确认的操作：使用 Dialog/Popup。
- 玩法内世界空间飘字、战斗伤害数字、棋盘特效：使用场景 VFX。
- 长文本说明、教程解释：使用 Guide/Tooltip。

## 架构约束

- Framework 层只保留 `TipManager`、`TipRequest` 和调度规则，不依赖 HotUpdate Prefab。
- HotUpdate 层负责展示：`TipsWindow` 必须通过 `ui_wnd_res` 注册为 Addressables UI 窗口。
- `TipsWindow` 内部重复提示条使用 `UIItemPool<TipItemView>` 管理，不使用场景子预制 Host，也不逐条 Addressables 加载。
- 业务代码不直接引用 `TipsWindow` 或 `TipItemView`。
- 新增提示样式时优先扩展 `TipStyle` 和展示层映射，不要在业务侧写颜色、动画或层级逻辑。
