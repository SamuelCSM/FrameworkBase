# 发布环境配置（ReleaseProfiles）

发布环境文件只保存团队共享的非敏感策略；机器路径和私钥由发布机或 CI 注入。Editor 窗口与
`ReleaseBatchEntry` 使用同一套门禁和流水线。

| 文件 | 环境 | HTTPS | 清单签名 |
|---|---|---|---|
| `dev.json` | 本机开发 | 可选 | 强制，使用开发密钥 |
| `qa.json` | 测试 | 可选 | 强制，使用 QA 密钥 |
| `staging.json` | 预发 | 强制 | 强制 |
| `prod.json` | 生产 | 强制 | 强制 |

## 字段

- `BaseUrl`：客户端更新服务根 URL。代码补丁 URL 写入已签名清单，并必须与该根同源、位于其路径下。
- `UploadRoot`：发布目标文件系统根目录。正式环境通常在 CI 通过 `-uploadRoot` 覆盖，不把机器路径提交到仓库。
- `RequireHttps`：是否强制 HTTPS；staging/prod 必须为 true。
- `RequireManifestSignature`：所有环境必须为 true。开发环境也不能跳过远程代码信任边界。
- `SigningKeyRef`：稳定 KeyId，只是密钥引用名，不包含私钥材料。
- `AllowPlayerPrefsOverride`：是否允许交互式 Editor 使用本机覆盖；正式环境应关闭。

## 发布纪律

1. staging/prod 的 `example.com` 是占位域名，正式发布门禁会直接拒绝。
2. 私钥绝不进入仓库、AppConfig 或 Player。CI 使用：
   - `FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64`，或
   - `FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_PATH`。
3. 每次发布先写本地 staging 和发布台账，再复制载荷，最后提交 `version.json.sig` 与 `version.json`。
4. 代码补丁使用包含 AppVersion、CodeVersion 和摘要前缀的不可变 URL；禁止覆盖旧清单仍在引用的对象。
