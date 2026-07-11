# FrameworkBase 发布系统目标设计

> 状态：目标设计（Target Design）。实施按切片 A→B→C→D 在主干推进，每切片必须先过
> `Tools/ci/release-rehearsal.ps1` 端到端演练再提交。本文是设计原则的唯一事实源；
> 实施中发现与现实冲突时，先回到本文对齐再动代码，不允许代码悄悄偏离设计。

## 1. 设计原则

1. **产物不可变（immutable releases）**：任何已写入产物仓库的版本目录永不修改、永不复用路径。
   发布、回滚、晋级都不重建产物，只移动指针。
2. **唯一可变对象是签名指针**：整个仓库中只有 `current.json`（及其伴生签名）允许被覆盖写。
   其余对象一经写入即冻结，冲突即失败。
3. **回滚 = 指针回切**：不重新构建、不重新上传 payload，只把 `current.json` 指回上一个
   已验证 releaseId。回滚耗时与包体大小无关。
4. **晋级（promote）= 同产物重签**：qa 验证过的 releaseId 晋级 prod 时，payload 逐字节复用，
   仅用 prod 密钥重签清单并切 prod 指针。"测过的就是发出去的"，禁止 prod 单独重建。
5. **切指针前必须回读校验**：`current.json` 切换之前，从 CDN/存储侧回读本 release 全部文件，
   逐文件比对 SHA-256 与 ledger 一致；回读失败或哈希不一致时指针不动，发布判失败。
6. **状态机可审计**：每个 releaseId 的生命周期状态持久化在 ledger，任何状态迁移都有
   时间戳、操作者与产物哈希，可回答"谁在何时把哪个 Commit 的产物推到了哪个环境"。

## 2. 产物仓库布局

```
{uploadRoot}/
  {env}/                        # dev / qa / prod（环境隔离，密钥、审批链均按环境）
    {platform}/                 # android / ios / windows …（与 UpdateSecurity.GetRuntimePlatformId 一致）
      {channel}/                # default / 渠道包 / 审核包
        current.json            # ★ 唯一可变对象：签名指针
        current.json.sig
        releases/
          {appVersion}/         # 整包兼容边界，如 1.2.0
            {releaseId}/        # 不可变版本目录（GUID）
              version.json      # 本 release 的已签名清单（冻结）
              version.json.sig
              payloads/…        # 按内容寻址的补丁对象（冻结）
              addressables/…    # 资源产物（冻结）
              ledger.json       # 发布台账：Git Commit、Unity 版本、逐文件 SHA-256、状态机记录
```

- 路径片段一律经 `SanitizePathSegment` 白名单化；`platform` 标识与客户端运行时映射
  **共用同一份代码**（发布端 `GetPlatformId` 与客户端 `GetRuntimePlatformId` 的映射表
  已由发布演练锁定一致性）。
- 同一 releaseId 目录写入过程中若发现同路径已存在且内容不同 → 立即失败（不可变性被破坏）。

## 3. current.json 指针契约

```json
{
  "SchemaVersion": 1,
  "Env": "prod",
  "Platform": "android",
  "Channel": "default",
  "AppVersion": "1.2.0",
  "ReleaseId": "8f3a…",
  "ManifestPath": "releases/1.2.0/8f3a…/version.json",
  "PreviousReleaseId": "77b1…",
  "SwitchedAtUnixSeconds": 0,
  "SwitchedBy": "release.yml run 1234 / operator"
}
```

- `current.json` 与清单同一密钥体系签名（KeyId 公钥环），客户端先验指针签名再跳转清单，
  清单本身再验一次签——两层都失败关闭。
- `PreviousReleaseId` 形成可回溯历史链；回滚时新指针的 `PreviousReleaseId` 指向被回滚者，
  链条不断裂。
- **迁移兼容**：切片 B 落地后的过渡期内，发布端在指针旁**双写** `version.json` 别名
  （复制当前 active 清单，保持旧客户端可用）；切片 C 完成、线上客户端全部支持指针跳转后，
  别名双写由 ReleaseProfile 开关关闭。

## 4. releaseId 状态机

```
Staged ──发布上传完成──▶ Published ──CDN 回读逐文件 SHA-256 全对──▶ Verified
                                                        │
                                              切 current.json 指针
                                                        ▼
     Superseded ◀──被更新 release 替换────────────── Active
     RolledBack ◀──指针回切离开──────────────────────┘
```

- 只有 `Verified` 状态的 releaseId 允许成为指针目标（回滚目标也必须是历史 `Verified+`）。
- 状态迁移事件追加写入该 release 的 `ledger.json`（append-only），不覆盖历史。
- `Staged→Published` 失败走既有 Saga 补偿；`Published→Verified` 失败保留现场供排查，
  指针从未移动，线上无感。

## 5. 实施切片（主干推进，不留长分支）

| 切片 | 内容 | 客户端影响 |
|---|---|---|
| A | 发布写入 `{env}/{platform}/{channel}/releases/{appVersion}/{releaseId}/` 不可变目录；`ResolveTrustedPatchUrl` 的同源路径契约同步（发布端与客户端**同切片原子落地**） | Patch URL 前缀变化，同源校验规则同步 |
| B | `current.json` 指针+签名；指针切换前 CDN 回读逐文件 SHA-256；一键回滚 CLI 与 workflow；过渡期双写 version.json 别名 | 客户端优先走指针跳转，失败回退别名 |
| C | promote 晋级（qa→prod 同产物重签切指针）；release.yml 增加 `kind=promote/rollback` | 无 |
| D | Release Center 控制台（见 §6） | 无 |

每个切片的验收标准统一：`release-rehearsal.ps1` 全绿 + `run-ci.ps1` 全绿。

## 6. Release Center 控制台（切片 D）

1. **视图层**：按 env/platform/channel 列出 `releases/` 与 ledger（releaseId、状态机状态、
   版本号、Git Commit、发布人、时间）；高亮 `current.json` 当前指针，可沿
   `PreviousReleaseId` 回溯历史链。
2. **操作分级**：dev 环境保留本地发布/回滚（复用同一 `IReleaseStep` 管线）；qa/prod 的
   发布/promote/回滚按钮改为经 `gh workflow run` 触发 release.yml——本机不执行，
   审批链不可绕过。
3. **通用操作**：上传后校验重跑、打开 ledger、dry-run（只跑门禁不落产物）。
4. **铁律**：面板与 CLI 永远共用同一组管线步骤，禁止面板独有逻辑；prod 在面板上只读+发起，
   不能本机直发。UI 只做编排与展示，可测逻辑全部留在管线层。

## 7. 与现状的差异清单（实施时逐项消除）

- 现状 `AtomicPublishArtifacts` 直接把 staging 平铺到 uploadRoot 根（manifest-last）；
  切片 A 改为写入不可变 releases/ 目录，切片 B 起 manifest-last 语义由指针切换承接。
- 现状客户端 `CheckUpdateAsync` 直取 `{root}/version.json`；切片 B 增加指针跳转，
  别名兼容期不破坏旧客户端。
- 现状回滚只能"再发一次旧版本"；切片 B 后回滚为指针回切，秒级完成。
- 现状 qa/prod 各自构建；切片 C 后 prod 产物必须来自 qa 已验证 releaseId。
