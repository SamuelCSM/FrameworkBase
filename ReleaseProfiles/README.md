# 发布环境配置（ReleaseProfiles）

发布机据此决定"本次发到哪、走不走 HTTPS、要不要签名"。发布工具（Framework → Hot Update Publisher /
Full Package Publisher）顶部的「发布环境」下拉选中哪个，就用哪份。

| 文件 | 环境 | HTTPS | 强制签名 |
|---|---|---|---|
| `dev.json` | 本机开发 | 否 | 否 |
| `qa.json` | 测试 | 否 | 否 |
| `staging.json` | 预发 | 是 | 是 |
| `prod.json` | 生产 | 是 | 是 |

## 字段

- `BaseUrl`：该环境客户端更新服务器根 URL（对应运行时 `AppConfig.UpdateServerUrl`）。补丁下载地址运行时
  由客户端 `UpdateServerUrl` 派生，一份签名清单各环境通用。
- `UploadRoot`：部署产物写入目标根，**机器相关**，留空由本机填写，不作团队权威值。
- `RequireHttps` / `RequireManifestSignature`：环境准入。prod/staging 恒为 true。
- `SigningKeyRef`：签名私钥的**引用名**（非私钥本体），供人工核对本机登记的是否为该环境密钥。

## 两条纪律

1. **占位 URL 必须改**：`staging.json` / `prod.json` 里的 `*.example.com` 是占位符，上线前替换为真实
   CDN 域名。发布前校验会拦下 prod 明文 HTTP，但不会替你判断域名是否写对。
2. **私钥绝不进库**：这里只存引用名。真正的 RSA 私钥保存在工程目录外，路径登记在本机 EditorPrefs
   （菜单 Framework → Hot Update Security → Generate Signing Key Pair / Set Private Key Path）。
   `staging` / `prod` 要求签名——本机未登记可用私钥时，发布前校验会**阻断发布**。
