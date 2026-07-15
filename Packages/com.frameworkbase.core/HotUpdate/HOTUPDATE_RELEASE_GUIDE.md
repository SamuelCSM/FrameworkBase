# 热更发布与运维指南

面向发布/运维人员，说明热更下载链路上四类新增运维旋钮如何配置、行为边界，以及排障。
设计取舍与安全不变量见 `ARCHITECTURE_DECISIONS.md` 的 **ADR-005**；本文只讲「怎么配、怎么排障」。

配置全部落在 `AppConfig`（`Kernel/AppConfigAsset.cs`），随 **Player 包体** 出包——
下述任何 Host / 环境相关字段都**不能**由 RemoteConfig 等未签名通道在运行时覆盖。

---

## 1. 更新服务器与可信多 CDN 回退

### 1.1 主更新服务器

```
AppConfig.UpdateServerUrl = "https://cdn-a.example.com/Updates/prod/android/default"
```

- prod 环境**必须 HTTPS**；禁止 UserInfo（`user:pass@`）、Query（`?...`）、Fragment（`#...`）与非规范化转义。
- 路径必须包含**独立环境路径段**（如 `prod` / `qa`）——门禁据此防止环境交叉。

### 1.2 备用 CDN 端点（可选，按顺序回退）

```
AppConfig.UpdateCdnEndpoints = [
  { Name = "cdn-b", AppEnv = "prod", BaseUrl = "https://cdn-b.example.net/Updates/prod/android/default" },
  { Name = "cdn-c", AppEnv = "prod", BaseUrl = "https://cdn-c.example.org/Updates/prod/android/default" },
]
```

**配置规则（不满足则构建失败，见 §5）**：

| 规则 | 说明 |
|---|---|
| 端点总数 ≤ 8 | 含主端点在内 |
| `Name` 唯一且合法 | 用于日志、熔断状态、故障定位 |
| `AppEnv` 逐字符匹配主环境 | **禁止 prod/qa 交叉回退** |
| 每个端点独立传输 Origin | scheme + 主机 + 端口三元组去重；共享 Origin 不构成独立故障域 |
| BaseUrl 含独立环境路径段 | 同 §1.1 |
| 全链路 HTTP(S)、无凭据/Query/Fragment | 同 §1.1 |

> **为什么 Host 不能走 RemoteConfig**：热更下载的是将被执行的远程代码。把备用 Host 放到未签名下发通道，
> 等于给出「不改包、只改配置就能把代码下载重定向到任意站点」的开关。Host 变更必须走
> 构建 → 安全门禁 → 发版。

### 1.3 回退与熔断行为（运行时，无需配置，了解即可）

- **信任判定独立于 Host**：任一 CDN 的产物都要过同一把「ManifestId + 相对路径 + Size + SHA-256」尺子，
  校验发生在下载**之后**。换 Host 不降低任何校验标准。
- **不跨 Host 断点续传**：换端点前删除半成品，全量重下，杜绝把两个不同对象拼在一起。
- **按失败类型分级熔断**（进程内、按 Host）：
  - 传输失败：连续 2 次 → 隔离该 Host **30 秒**。
  - 完整性失败（哈希/长度不符）：隔离 **5 分钟**（更可能是投毒/错配，隔离更久）。
  - 安全类失败（如本地信任根缺失）：**立即失败关闭**，不再回退。
- 所有 Host 都在隔离期时，本轮下载返回「所有可信 CDN Host 均处于隔离期」。

---

## 2. 磁盘空间预检（失败关闭）

安装在 **写盘之前** 做一次预检，空间不足或**查询失败**都会中止本次安装，绝不半途写坏。

**所需空间预算**（`StorageBudgetPolicy`，默认值）：

```
required = Payload + 固定开销(4 MiB) + max( 最低保留(64 MiB), Payload × 10% )
```

| 场景 | 结果 | 是否放行 |
|---|---|---|
| 可用空间 ≥ required | `Sufficient` | ✅ 放行 |
| 可用空间 < required | `Insufficient`（`STORAGE_E_INSUFFICIENT_SPACE`） | ❌ 中止 |
| 卷空间无法查询 | `Unknown`（`STORAGE_E_SPACE_UNKNOWN`） | ❌ **中止（失败关闭）** |

- 平台查询：Android 用 `StatFs`，其它平台用目标卷 `DriveInfo`；任何异常 / 卷未就绪 → `Unknown`。
- **`Unknown` 一律当作不可安装**，绝不假设空间充足。日志会带 `path` 与失败原因。

---

## 3. 缓存治理（高低水位 + 磁盘缺口双触发）

长期运营中下载缓存会累积。清理是**确定性**的，且**永不误删事务槽**。

**默认策略**（`CacheRetentionPolicy`）：

| 参数 | 默认 | 含义 |
|---|---|---|
| `MaxCacheBytes` | 512 MiB | 缓存配额上限 |
| `HighWatermarkRatio` | 0.90 | 超过则触发清理 |
| `LowWatermarkRatio` | 0.70 | 清理的目标回落水位 |

- **双触发**：磁盘空间缺口（安装需要）**或** 缓存超过高水位，取二者所需释放量的较大值。
- **受保护槽永不删除**：Active（当前生效）、Pending（待确认）、LKG（上一已确认）、提交中槽——
  它们参与容量统计，但绝不进入删除计划。
- **删除顺序**（确定）：按类别 Temporary → OrphanStaging（孤儿暂存）→ ObsoleteRelease（过期版本）→
  Diagnostic，同类按最旧写入优先，再按路径。
- 清理后**以真实卷空间重新查询**决定能否安装——不相信「计划释放量」，只认删除完成后的事实。
- 查询为 `Unknown` 时**不做破坏性清理**（避免在盲区里误删）。

---

## 4. 网络生命周期（前后台切换与断线恢复）

长连接在切后台、切网络时的恢复行为，两个可调项：

```
AppConfig.NetworkBackgroundGraceSeconds       = 10   // 短后台旧连接保留宽限（秒）
AppConfig.NetworkForegroundProbeTimeoutSeconds = 5   // 回前台主动探活超时（秒）
```

- **短后台**（≤ 宽限）回前台：先对旧连接**主动探活**；探活超时视为半开 TCP，立即切新连接并重新鉴权。
- **长后台**或**网络代际变化**（Wi-Fi↔蜂窝、接口/路由指纹变化）：**废弃旧连接（Epoch）**，串行重连 + 重鉴权。
- 会话过期（服务端判定）为**永久失败**，停止拿过期令牌空转重试，走重新登录。
- 后台期间暂停心跳、请求计时与重连退避，避免后台被系统冻结时空耗预算。

> 会话级请求语义、离线队列、错误码拦截见 `Network/NETWORK_MANAGER_GUIDE.md`。

---

## 5. 构建门禁

上述 CDN / 环境配置在**出包时**由 `Editor/HotUpdateSecurityBuildCheck` 静态校验，
不合规**直接构建失败**（`BuildFailedException`），不会带病出包。运行时与门禁共用同一套校验规则
（`UpdateSecurity.ValidateCdnEndpointConfiguration`），杜绝「只在某一侧放宽 Host 边界」。

---

## 6. 排障速查

| 现象 / 日志关键字 | 可能原因 | 处理 |
|---|---|---|
| 构建失败：`可信 CDN 配置未通过安全准入` | §1.2 某条规则不满足（环境不符 / Origin 重复 / 非 HTTPS / 缺环境段） | 按提示 `reason` 修正 `UpdateCdnEndpoints` |
| `STORAGE_E_INSUFFICIENT_SPACE` | 目标卷可用空间 < 预算 | 引导玩家清理空间；或评估 Payload 体积 |
| `STORAGE_E_SPACE_UNKNOWN` | 卷空间查询失败（权限/平台/卷未就绪） | 查平台适配与目标路径；失败关闭是预期行为 |
| `所有可信 CDN Host 均处于隔离期` | 所有端点近期连续失败被熔断 | 查 CDN 健康；传输失败 30s、完整性失败 5min 后自动恢复 |
| `[TrustedCDN] 端点校验失败 kind=Integrity` | 某 Host 返回的字节哈希/长度不符 | 查该 CDN 是否缓存了旧版本 / 被投毒；框架已自动回退并隔离 |
| `[TrustedCDN] ... kind=Security` | 本地信任根缺失等安全配置错误 | 与 Host 无关，回退也不能恢复；查签名公钥环配置 |
| 回前台后长时间无响应再重连 | 半开 TCP 探活超时 | 属预期恢复路径；可调 `NetworkForegroundProbeTimeoutSeconds` |

---

**相关文档**：`ARCHITECTURE_DECISIONS.md`（ADR-005 安全边界与不变量）、
`Network/NETWORK_MANAGER_GUIDE.md`（长连接 API）、`HotUpdate/HybridCLR_Installation_Guide.md`（热更代码接入）。
