# 隐私合规接线指南

框架提供三块合规基础设施，业务按目标市场法规（GDPR / CCPA / 个保法）接线。

## 1. 同意管理（PrivacyConsent，版本化）

同意是**对某个版本协议**给出的——协议改版后旧同意失效，必须重新征得。
所以存的是"已同意的协议版本号"而非布尔：

```csharp
const int PolicyVersion = 3;                      // 协议改版时 +1

if (!PrivacyConsent.IsAccepted(PolicyVersion))
{
    GameEntry.Analytics.CollectionEnabled = false; // 同意前数据不出设备
    bool ok = await ShowPrivacyDialogAsync();      // 业务弹窗，或 GameEntry.Sdk.Privacy.ShowPrivacyPolicyAsync()
    if (!ok) { Application.Quit(); return; }       // 按法规与商店政策决定拒绝后的行为

    PrivacyConsent.Accept(PolicyVersion);
    GameEntry.Analytics.CollectionEnabled = true;
}
```

状态变化广播 `GameMessage.PrivacyConsentChanged`（参数 int 版本号，0=撤回）。
iOS ATT / 个性化广告授权走渠道能力：`GameEntry.Sdk.Privacy.RequestTrackingConsentAsync()`。

## 2. 采集闸门（AnalyticsManager.CollectionEnabled）

- `false` 时 `Track` **直接丢弃**（数据根本不产生，不是缓存后补发——缓存再补发
  等于同意前就采集了，审核不认）；`FlushAsync` 不出网。
- 默认 `true`（非合规市场行为不变）；合规市场在启动早期、同意判定前置 `false`。
- 更稳的接线方式：在 `AppConfig` 开启 `RequirePrivacyConsentForAnalytics` 并填写
  `PrivacyPolicyVersion`。这样 `AnalyticsManager` 初始化时会先按当前协议版本关闸；
  未同意时不会读取/补发旧的 `analytics_pending.jsonl`。
- 三方 SDK（含渠道埋点）同理：同意前不要初始化，放在 Accept 之后再
  `GameEntry.Sdk.InitializeAsync()`。

## 3. 数据抹除（PrivacyCompliance，RTBF）

用户行使"删除我的数据"时：

```csharp
GameEntry.Analytics.CollectionEnabled = false;
GameEntry.Network.Disconnect();

var report = PrivacyCompliance.EraseAllLocalUserData();  // 逐项报告可展示给用户
// 引导重启：各管理器内存态不保证全部回滚
```

覆盖：埋点队列与落盘快照、远程配置缓存、全部账号加密存档、PlayerPrefs
（语言与同意状态一并清空——抹除后按未同意处理，语义正确）、崩溃记录、
启动指标快照、文件日志目录。逐项异常隔离，失败项如实进报告。

**边界（审核陈述必须如实）**：本编排只清**设备本地**数据。服务端侧删除
（账号注销、采集端按 device_id / user_id 清库）走业务后台流程，两段合起来
才构成完整的 RTBF 响应。
