# 分支保护配置说明（P0-5 CI 合并前门禁）

> 本文说明 20 人以上团队协作所需的 GitHub 分支保护配置。CI 的 `pull_request` 触发器只是提供了
> required checks 的**事实来源**；真正把"未过门禁不得合并"变成硬约束，必须在 GitHub 仓库设置里
> 开启分支保护。本地 `pre-push` hook 只是开发体验优化，可被 `--no-verify` 跳过，**不能**作为团队质量安全边界。

## 一、需要在 GitHub 启用的规则（Settings → Branches → Branch protection rules，模式 `master`）

- [x] **Require a pull request before merging**（禁止直接 push master）
  - [x] Require approvals：至少 1（大团队建议 2）
  - [x] Dismiss stale pull request approvals when new commits are pushed
  - [x] Require review from Code Owners（若配置了 CODEOWNERS）
- [x] **Require status checks to pass before merging**
  - [x] Require branches to be up to date before merging
  - 勾选以下 required checks（名称须与 ci.yml job `name:` 一致）：
    - `workflow 静态门禁（防触发器/required job 退化）`
    - `干净副本可复现性预检`
    - `asmdef 依赖门禁（分层/热更拓扑）`
    - `编译与 EditMode/PlayMode 测试`
    - `资源与工程质量门禁`
    - `Android Player/IL2CPP 构建验证`（见下节：变更影响分层）
    - `iOS Xcode 工程生成验证`（见下节）
- [x] **Require conversation resolution before merging**
- [x] **Block force pushes**
- [x] **Restrict deletions**（禁止删除受保护分支）
- [ ] **Do not allow bypassing the above settings** —— 是否允许管理员绕过必须由团队显式决定：
  - 生产项目建议**勾选**（管理员也不能绕过），把绕过行为收敛到临时、可审计的例外流程；
  - 引导期若需管理员救火，可暂不勾选，但须在团队约定中显式记录并定期复查。

## 二、移动端构建的分层与 required 语义

`ci.yml` 的 `build-impact` job 做变更影响分析：

- **PR** 上仅当变更触及构建关键路径（`Assets/`、`Packages/`、`ProjectSettings/`、`HybridCLRData/`、
  `Tools/ci/`、`.github/workflows/`）时，才执行 `android-player` / `ios-xcode-project`；
  纯文档/脚本类 PR 会跳过移动端构建（这两个 check 显示为 skipped，视作通过）。
- **master push 与 workflow_dispatch** 恒为全量：移动端构建始终执行，保证发布基线经过移动端验证。

因此把 `android-player` / `ios-xcode-project` 设为 required 是安全的：它们要么真实执行、要么被
`build-impact` 明确判定为无关而跳过。**影响构建/HybridCLR/Addressables/Packages/ProjectSettings 的
修改不会绕过移动端验证**——这是 `build-impact` 的路径过滤保证的，`Tools/ci/check-workflows.ps1` 的 W3
规则又防止该过滤被删。

## 三、可选：脚本化配置分支保护

如果希望脚本化下发（而非手点），可用 `gh` CLI（**Token 绝不入库**，由执行者本地提供）：

```bash
# 需要具备 repo admin 权限的 token，通过环境变量注入，切勿写进仓库或 CI 明文
gh api -X PUT "repos/<owner>/<repo>/branches/master/protection" \
  --input branch-protection.json
```

`branch-protection.json` 的字段结构见 GitHub REST API 文档
`PUT /repos/{owner}/{repo}/branches/{branch}/protection`。仓库不提供带 Token 的自动执行脚本，
避免把管理员凭证的使用固化进代码。

## 四、Environment 审批（发布链路，配合 release.yml）

发布流水线 `release.yml` 的 job 绑定 GitHub Environment（dev / qa / prod）：

- 在 **Settings → Environments → prod** 配置 **Required reviewers**，形成正式发布人工审批门；
- 每个 Environment 配置：
  - Secret `MANIFEST_PRIVATE_KEY_XML_BASE64`（清单签名私钥，按环境隔离，绝不入库）；
  - Variable `RELEASE_UPLOAD_ROOT`（部署目标根；缺失时 Publish/Promote/Rollback 会被
    `RELEASE_E_STORE_NOT_CONFIGURED` 失败关闭）；
- 正式环境优先使用 GitHub OIDC / 短期角色凭证 / 公司内部发布身份，不建议长期 AccessKey。
