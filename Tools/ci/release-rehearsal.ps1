# FrameworkBase 发布演练（release rehearsal）：发布端 → 客户端 端到端安全网。
#
# 流程（切片 B 起为四跳）：
#   1. 生成一次性 dev RSA 密钥对，经 FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 注入；
#   2. batchmode 真实发布 code=2、code=3 两个 release（buildTarget=Win64——必须用 Unity
#      官方命令行平台名，该参数会先被 Unity 启动器解析）到本地 uploadRoot；
#   3. batchmode 执行 RollbackRelease 一键回滚（指针回切到 code=2，产物不重建）；
#   4. EditMode 集成测试（ReleaseRehearsalTests）消费真实产物：指针验签+历史链 → 清单验签
#      → 准入（平台/渠道映射）→ 逐文件校验 → 事务槽安装确认 + 三个故障注入。
#
# v1 约束：不含真实网络跳（客户端按不可变相对路径直接读 uploadRoot）；
#          只发代码热更（仓库 Addressables 组为空，-publishResource 待有真实远程资源后开启）。
#
# 用法（工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\release-rehearsal.ps1 [-UnityPath <Unity.exe>] [-KeepArtifacts]
# 退出码：0 = 演练全绿；非 0 = 任一发布/回滚/契约测试未通过。
param(
    [string]$UnityPath,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$projectPath = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 定位 Unity（与 run-ci.ps1 同规则）───────────────────────────────────────
$versionLine = Get-Content (Join-Path $projectPath "ProjectSettings\ProjectVersion.txt") |
    Where-Object { $_ -match "^m_EditorVersion:" } | Select-Object -First 1
$unityVersion = ($versionLine -split "\s+")[1]
if (-not $UnityPath) { $UnityPath = $env:UNITY_EDITOR_PATH }
if (-not $UnityPath) {
    $candidates = @("C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe")
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem)) {
        $candidates += Join-Path $drive.Root "Hub\$unityVersion\Editor\Unity.exe"
        $candidates += Join-Path $drive.Root "Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
    }
    $UnityPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    throw "未找到 Unity $unityVersion。请传 -UnityPath 或设置 UNITY_EDITOR_PATH。"
}
if (Test-Path (Join-Path $projectPath "Temp\UnityLockfile")) {
    throw "工程正被 Unity 编辑器占用（Temp\UnityLockfile 存在）。请关闭编辑器后重试。"
}

$rehearsalRoot = Join-Path $projectPath "Artifacts\Rehearsal"
$uploadRoot    = Join-Path $rehearsalRoot "uploadRoot"
$logsDir       = Join-Path $projectPath "Logs\ci"
New-Item -ItemType Directory -Force $logsDir | Out-Null
if (Test-Path $rehearsalRoot) { Remove-Item $rehearsalRoot -Recurse -Force }
New-Item -ItemType Directory -Force $uploadRoot | Out-Null

# ── 一次性 dev 密钥对（私钥只存在于本次进程环境，绝不落库）───────────────────
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
try {
    $privateXml = $rsa.ToXmlString($true)
    $publicXml  = $rsa.ToXmlString($false)
}
finally { $rsa.Dispose() }
$env:FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 =
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($privateXml))

# ── 发布参数：appVersion 取工程当前 bundleVersion；KeyId 取 dev Profile 的 SigningKeyRef ──
$bundleLine = Get-Content (Join-Path $projectPath "ProjectSettings\ProjectSettings.asset") |
    Where-Object { $_ -match "^\s*bundleVersion:\s*(\S+)" } | Select-Object -First 1
if (-not ($bundleLine -match "bundleVersion:\s*(\S+)")) { throw "ProjectSettings.asset 中未找到 bundleVersion。" }
$appVersion = $Matches[1]
$devProfile = Get-Content (Join-Path $projectPath "ReleaseProfiles\dev.json") -Raw | ConvertFrom-Json
$keyId = $devProfile.SigningKeyRef
if (-not $keyId) { throw "ReleaseProfiles/dev.json 缺少 SigningKeyRef。" }

# 渠道作用域（切片 A 布局）：{env}/{platform}/{channel}。platform 与发布端 GetPlatformId(Win64)
# 一致为 windows；channel 与发布端同源取 AppConfig.AppChannel（缺省 default）。
$channelLine = Get-Content (Join-Path $projectPath "Assets\Resources\AppConfig.asset") |
    Where-Object { $_ -match "^\s*AppChannel:\s*(\S+)" } | Select-Object -First 1
$channel = if ($channelLine -match "AppChannel:\s*(\S+)") { $Matches[1] } else { "default" }
$channelRelative = "dev/windows/$channel"

Write-Host "== FrameworkBase 发布演练 =="
Write-Host "Unity      : $UnityPath"
Write-Host "AppVersion : $appVersion  KeyId: $keyId  Channel: $channelRelative"
Write-Host "UploadRoot : $uploadRoot"

# ── batchmode 调用与哨兵判定（不信进程退出码）────────────────────────────────
function Invoke-ReleaseBatch([string]$stepName, [string]$logName, [string[]]$unityArgs) {
    $logPath = Join-Path $logsDir $logName
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    & $UnityPath -batchmode -nographics -projectPath $projectPath @unityArgs -logFile $logPath | Out-Null

    $verdict = $null
    for ($i = 0; $i -lt 300; $i++) {
        if (Test-Path $logPath) {
            $endLine = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match "RELEASE_RESULT\s+exit=(\d+)" } | Select-Object -Last 1
            if ($endLine -and $endLine -match "RELEASE_RESULT\s+exit=(\d+)") {
                $verdict = [int]$Matches[1]
                break
            }
        }
        if ($i -gt 0 -and $i % 25 -eq 0) { Write-Host "等待 $stepName 结论落盘... $([int]($i / 5))s" }
        Start-Sleep -Milliseconds 200
    }

    Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "\[ReleaseBatch\]|\[环境校验\]|error CS|Exception" } |
        Select-Object -Last 30 | ForEach-Object { Write-Host $_ }

    if ($verdict -ne 0) {
        Write-Host "$stepName 失败（RELEASE_RESULT=$verdict），详见日志: $logPath"
        return $false
    }
    Write-Host "$stepName 通过。"
    return $true
}

function Invoke-Publish([int]$codeVersion, [string]$releaseId, [string]$logName) {
    return Invoke-ReleaseBatch "发布 code=$codeVersion" $logName @(
        "-executeMethod", "Framework.Editor.Release.ReleaseBatchEntry.PublishHotUpdate",
        "-releaseId", $releaseId,
        "-releaseEnv", "dev",
        "-buildTarget", "Win64",
        "-appVersion", $appVersion,
        "-resourceVersion", "1",
        "-codeVersion", "$codeVersion",
        "-publishResource", "false",
        "-publishCode", "true",
        "-uploadRoot", $uploadRoot,
        "-allowDirtyRelease"
    )
}

$releaseIdV2 = [guid]::NewGuid().ToString("N")
$releaseIdV3 = [guid]::NewGuid().ToString("N")

# ── 第 1/2 跳：真实发布两个 release ─────────────────────────────────────────
$cleanup = { Remove-Item Env:\FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 -ErrorAction SilentlyContinue }
if (-not (Invoke-Publish 2 $releaseIdV2 "release-rehearsal-publish-v2.log")) { & $cleanup; exit 1 }
if (-not (Invoke-Publish 3 $releaseIdV3 "release-rehearsal-publish-v3.log")) { & $cleanup; exit 1 }

# ── 第 3 跳：一键回滚（指针回切到 code=2，产物不重建）───────────────────────
$rollbackOk = Invoke-ReleaseBatch "一键回滚" "release-rehearsal-rollback.log" @(
    "-executeMethod", "Framework.Editor.Release.ReleaseBatchEntry.RollbackRelease",
    "-releaseEnv", "dev",
    "-buildTarget", "Win64",
    "-uploadRoot", $uploadRoot,
    "-switchedBy", "release-rehearsal"
)
if (-not $rollbackOk) { & $cleanup; exit 1 }

$channelRoot = Join-Path $uploadRoot ($channelRelative -replace '/', '\')
foreach ($required in @("version.json", "version.json.sig", "current.json", "current.json.sig")) {
    if (-not (Test-Path (Join-Path $channelRoot $required))) {
        Write-Host "回滚后渠道根缺少 $required，判定失败。"
        & $cleanup; exit 1
    }
}

# ── 第 4 跳：写 rehearsal.json，跑客户端契约集成测试 ─────────────────────────
@{
    PublicKeyXml              = $publicXml
    KeyId                     = $keyId
    UploadRoot                = $uploadRoot
    BaseUrl                   = $devProfile.BaseUrl
    ChannelRelative           = $channelRelative
    AppVersion                = $appVersion
    ResourceVersion           = 1
    CodeVersion               = 2   # 回滚后激活 release 的代码版本
    ExpectedActiveReleaseId   = $releaseIdV2
    ExpectedPreviousReleaseId = $releaseIdV3
    RolledBackCodeVersion     = 3   # 被回滚 release 的代码版本（正本必须原样保留）
} | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $rehearsalRoot "rehearsal.json")

$resultsPath = Join-Path $logsDir "release-rehearsal-results.xml"
$testLog     = Join-Path $logsDir "release-rehearsal-tests.log"
if (Test-Path $resultsPath) { Remove-Item $resultsPath -Force }

& $UnityPath -batchmode -nographics `
    -projectPath $projectPath `
    -runTests -testPlatform EditMode `
    -testFilter "Framework.Tests.ReleaseRehearsalTests" `
    -testResults $resultsPath `
    -logFile $testLog | Out-Null

for ($i = 0; $i -lt 300 -and -not (Test-Path $resultsPath); $i++) { Start-Sleep -Milliseconds 200 }
[xml]$xml = $null
for ($i = 0; $i -lt 300; $i++) {
    try {
        [xml]$candidate = Get-Content -Encoding UTF8 -Raw $resultsPath
        if ($null -ne $candidate.DocumentElement) { $xml = $candidate; break }
    } catch {}
    Start-Sleep -Milliseconds 200
}

$exitCode = 1
if ($null -ne $xml) {
    $run = $xml."test-run"
    Write-Host ("契约测试结果: total={0} passed={1} failed={2} skipped={3}" -f `
        $run.total, $run.passed, $run.failed, $run.skipped)
    if ([int]$run.failed -gt 0) {
        $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
            Write-Host (" [FAIL] {0}" -f $_.fullname)
            $msg = $_.SelectSingleNode("failure/message")
            if ($null -ne $msg) { Write-Host ("        {0}" -f $msg.InnerText.Trim()) }
        }
    }
    # 全绿且确实执行（skipped 全组说明 rehearsal.json 未被识别，同样判失败）。
    if ([int]$run.failed -eq 0 -and [int]$run.passed -ge 8) { $exitCode = 0 }
    elseif ([int]$run.failed -eq 0) { Write-Host "契约测试通过数不足（可能整组被跳过），判定失败。" }
}
else {
    Write-Host "未生成契约测试结果（大概率编译失败），详见日志: $testLog"
    Get-Content $testLog -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "error CS|Compilation failed" } | Select-Object -First 20
}

# ── 收尾：清理进程内私钥。演练成功默认清理产物（传 -KeepArtifacts 保留）；
#    失败时一律保留——uploadRoot 与 ledger 是排查第一手证据。
& $cleanup
if (-not $KeepArtifacts -and $exitCode -eq 0) {
    Remove-Item $rehearsalRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) { Write-Host "== 发布演练全绿 ==" } else { Write-Host "== 发布演练未通过 ==" }
exit $exitCode
