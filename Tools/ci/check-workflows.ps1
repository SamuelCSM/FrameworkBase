# FrameworkBase workflow 静态门禁（纯文本校验，不需要 Unity / License，秒级）。
#
# 目的：把"CI 必须是合并前门禁、发布必须有真实部署目标"焊成静态强约束，
# 防止未来的修改把 pull_request 触发器、required job 或部署目标闸门悄悄删掉。
#
# 校验规则：
#   W1 ci.yml 必须包含 pull_request 触发器（合并前门禁，PR required checks 的事实来源）。
#   W2 ci.yml 必须包含全部 required job：workflow-gate / reproducibility / asmdef-gate /
#      tests / asset-gate / build-impact / android-player / ios-xcode-project。
#      （发布端到端演练需 Unity batchmode 多次调用，作为本地/nightly 门禁 release-rehearsal.ps1，
#        不放进无 Unity 的 ubuntu CI job；CI 内的发布状态机覆盖由 tests job 的 EditMode
#        ReleaseStoreTests / ReleasePublishingTests 保证。见 W7。）
#   W3 ci.yml 移动端 job 的路径过滤必须覆盖构建关键路径（Assets/Packages/ProjectSettings）。
#   W4 release.yml 必须向 ReleaseBatchEntry 传 -uploadRoot 与 -releaseMode（部署目标注入闭环）。
#   W5 release.yml 必须包含部署目标闸门（RELEASE_E_STORE_NOT_CONFIGURED）与
#      签名私钥闸门（RELEASE_E_SIGNING_KEY_MISSING）。
#   W6 ReleaseProfiles：所有环境 RequireManifestSignature=true；
#      prod/staging 必须 RequireHttps=true 且 AllowPlayerPrefsOverride=false。
#      （example.com 占位与空 UploadRoot 由发布时 ReleaseProfileGate 失败关闭，
#        属于运行时门禁并有单测覆盖，此处不重复；模板仓库允许携带占位配置。）
#
# 用法：
#   pwsh -File Tools/ci/check-workflows.ps1     # 校验，违规则 exit 1
#
# 退出码：0 = 全部通过；1 = 存在违规（明细打印到 stdout）。
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

# Tools\ci → 工程根
$root = (Get-Item $PSScriptRoot).Parent.Parent.FullName

$violations = New-Object System.Collections.Generic.List[string]
function Add-Violation([string]$rule, [string]$msg) {
    $script:violations.Add(("[{0}] {1}" -f $rule, $msg))
}

Write-Host "== workflow 静态门禁 =="

# ── W1/W2/W3: ci.yml ─────────────────────────────────────────────────────────
$ciPath = Join-Path $root ".github\workflows\ci.yml"
if (-not (Test-Path $ciPath)) {
    Add-Violation "W1" "缺少 .github/workflows/ci.yml"
}
else {
    $ci = [IO.File]::ReadAllText($ciPath)

    if ($ci -notmatch "(?m)^\s*pull_request\s*:") {
        Add-Violation "W1" "ci.yml 缺少 pull_request 触发器——CI 退化为合并后检查，PR 门禁失效。"
    }

    $requiredJobs = @(
        "workflow-gate", "reproducibility", "asmdef-gate", "tests",
        "asset-gate", "build-impact",
        "android-player", "ios-xcode-project"
    )
    foreach ($job in $requiredJobs) {
        if ($ci -notmatch ("(?m)^\s{2}" + [regex]::Escape($job) + "\s*:")) {
            Add-Violation "W2" ("ci.yml 缺少 required job: {0}" -f $job)
        }
    }

    foreach ($critical in @("Assets/", "Packages/", "ProjectSettings/")) {
        if ($ci.IndexOf($critical, [StringComparison]::Ordinal) -lt 0) {
            Add-Violation "W3" ("ci.yml 构建影响过滤未覆盖关键路径: {0}（该路径的修改不允许跳过移动端构建验证）" -f $critical)
        }
    }
}

# ── W4/W5: release.yml ───────────────────────────────────────────────────────
$relPath = Join-Path $root ".github\workflows\release.yml"
if (-not (Test-Path $relPath)) {
    Add-Violation "W4" "缺少 .github/workflows/release.yml"
}
else {
    $rel = [IO.File]::ReadAllText($relPath)

    if ($rel -notmatch "-uploadRoot") {
        Add-Violation "W4" "release.yml 未向 ReleaseBatchEntry 传 -uploadRoot，部署目标注入链断裂。"
    }
    if ($rel -notmatch "-releaseMode") {
        Add-Violation "W4" "release.yml 未传 -releaseMode，BuildOnly/Publish 模式区分失效。"
    }
    if ($rel -notmatch "RELEASE_E_STORE_NOT_CONFIGURED") {
        Add-Violation "W5" "release.yml 缺少部署目标闸门（RELEASE_E_STORE_NOT_CONFIGURED）——Publish 空目标会静默跳过部署。"
    }
    if ($rel -notmatch "RELEASE_E_SIGNING_KEY_MISSING") {
        Add-Violation "W5" "release.yml 缺少签名私钥闸门（RELEASE_E_SIGNING_KEY_MISSING）。"
    }
}

# ── W6: ReleaseProfiles ──────────────────────────────────────────────────────
$profilesDir = Join-Path $root "ReleaseProfiles"
if (-not (Test-Path $profilesDir)) {
    Add-Violation "W6" "缺少 ReleaseProfiles 目录。"
}
else {
    foreach ($file in Get-ChildItem $profilesDir -Filter *.json -File) {
        try {
            $profile = Get-Content $file.FullName -Raw | ConvertFrom-Json
        }
        catch {
            Add-Violation "W6" ("{0} 不是合法 JSON: {1}" -f $file.Name, $_.Exception.Message)
            continue
        }

        if (-not $profile.RequireManifestSignature) {
            Add-Violation "W6" ("{0}: RequireManifestSignature 必须为 true（所有环境强制验签）。" -f $file.Name)
        }

        $isProduction = $file.BaseName -in @("prod", "staging")
        if ($isProduction) {
            if (-not $profile.RequireHttps) {
                Add-Violation "W6" ("{0}: 正式环境必须 RequireHttps=true（禁止 HTTP 拉取热更 DLL）。" -f $file.Name)
            }
            if ($profile.AllowPlayerPrefsOverride) {
                Add-Violation "W6" ("{0}: 正式环境必须 AllowPlayerPrefsOverride=false（封死本地重定向更新源）。" -f $file.Name)
            }
        }
    }
}

# ── W7: 发布状态机资产存在性（Store 抽象 + 本地/nightly 演练脚本）──────────────
$storeIface = Join-Path $root "Packages\com.frameworkbase.core\Editor\Release\IReleaseArtifactStore.cs"
$storeImpl  = Join-Path $root "Packages\com.frameworkbase.core\Editor\Release\LocalFileSystemReleaseStore.cs"
$rehearsal  = Join-Path $root "Tools\ci\release-rehearsal.ps1"
if (-not (Test-Path $storeIface)) { Add-Violation "W7" "缺少发布存储抽象 IReleaseArtifactStore.cs（Publish/Promote/Rollback 须共用同一 Store）。" }
if (-not (Test-Path $storeImpl))  { Add-Violation "W7" "缺少主干目录型 LocalFileSystemReleaseStore.cs。" }
if (-not (Test-Path $rehearsal))  { Add-Violation "W7" "缺少发布端到端演练脚本 release-rehearsal.ps1（本地/nightly 门禁）。" }

# ── 结论 ─────────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "[WorkflowGate] FAIL —— 发现 $($violations.Count) 项违规："
    foreach ($violation in $violations) {
        Write-Host "  - $violation"
    }
    exit 1
}

Write-Host "[WorkflowGate] PASS —— W1..W6 全部通过。"
exit 0
