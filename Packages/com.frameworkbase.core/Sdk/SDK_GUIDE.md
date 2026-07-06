# 平台 SDK 抽象层使用指南

## 定位与分工

- **框架主干（本目录）**：只有能力契约（`ISdkProvider` 四子服务）、注册机制（`SdkManager`）
  与开发期 Mock 兜底。**不含任何渠道厂商代码**。
- **渠道扩展包（独立仓库/包）**：每个渠道一个实现（如 `com.yourgame.sdk.taptap`），
  引用本包并实现 `ISdkProvider`，含该渠道的原生依赖与构建注入（manifest/plist）。
- **业务组合根**：启动早期选定渠道实现并注册。

与 `Framework.Core.Auth` 的边界：SDK 账号服务产出**渠道凭证**（channelUserId + token）；
游戏会话由 `AuthManager` 拿渠道凭证向游戏服换取。两层不要混。

## 业务侧用法

```csharp
// 1. 组合根注册渠道（不注册则自动 Mock 兜底，正式包会打 Error 醒目暴露）
GameEntry.Sdk.RegisterProvider(new TapTapSdkProvider());   // 来自渠道扩展包
await GameEntry.Sdk.InitializeAsync();

// 2. 登录：渠道凭证 → AuthManager 换游戏会话
var login = await GameEntry.Sdk.Account.LoginAsync();
if (login.Success)
{
    // login.Data.ChannelUserId / Token 交给 Auth 后端向游戏服验真
}
else if (login.Code == SdkErrorCode.UserCancelled)
{
    // 用户取消：静默返回登录页，不弹错误
}

// 3. 支付：查价 → 购买 → 服务端验证发货 → Confirm
var products = await GameEntry.Sdk.Purchase.QueryProductsAsync(new[] { "gem_60" });
var buy = await GameEntry.Sdk.Purchase.PurchaseAsync("gem_60", serverOrderId);
if (buy.Success)
{
    // buy.Data.Receipt 交服务端验证 → 发货成功后：
    await GameEntry.Sdk.Purchase.ConfirmAsync(buy.Data.TransactionId);
}

// 4. 启动补单（服务端验证后逐个 Confirm）
var pending = await GameEntry.Sdk.Purchase.RestorePendingAsync();
```

## 错误处理约定

业务只对 `SdkErrorCode` 分支；渠道原生错误码在 `ChannelCode` 里透传（日志/埋点用）。
`UserCancelled` 一律静默处理。能力可能为 null（渠道不支持），用前判空或查
`Sdk.SupportsPurchase` 等。

## 写一个渠道实现（扩展包作者）

1. 新建 UPM 包，依赖 `com.frameworkbase.core`；
2. 实现 `ISdkProvider`（不支持的能力返回 null）；
3. 渠道回调线程 → 主线程调度自行处理（建议 UniTask.SwitchToMainThread）；
4. 原生错误码映射到 `SdkErrorCode`，原文塞 `ChannelCode/Message`；
5. 构建注入（AndroidManifest / Info.plist）用该包自己的
   `IPostGenerateGradleAndroidProject` / `IPostprocessBuildWithReport`，不改框架。

## 上线检查

- 正式包日志出现 `[SdkManager] 正式构建未注册任何渠道实现` 或
  `[MockSdkProvider] Mock 支付直接成功` = 渠道接入配置错误，禁止提审。
