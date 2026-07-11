# FrameworkBase 发布演练（release rehearsal）：发布端 → 客户端 端到端安全网。
#
# 流程：
#   1. 生成一次性 dev RSA 密钥对，经 FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 注入；
#   2. batchmode 执行 ReleaseBatchEntry.PublishHotUpdate（buildTarget=Win64——必须用 Unity
#      官方命令行平台名，该参数会先被 Unity 启动器解析）发布到本地 uploadRoot；
#   3. 写 rehearsal.json 供 EditMode 集成测试（ReleaseRehearsalTests）消费真实发布产物：
#      原始字节验签 → KeyId 信封 → 字段级准入（平台/渠道映射）→ 逐文件校验 → 事务槽安装确认；
#   4. 三个故障注入必须红：篡改 DLL、过期清单、连续 3 次未确认启动回退出厂。
#
# v1 约束：不含真实网络跳（客户端按不可变 payloads 相对路径直接读 uploadRoot）；
#          只发代码热更（仓库 Addressables 组为空，-publishResource 待有真实远程资源后开启）。
#
# 用法（工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\release-rehearsal.ps1 [-UnityPath <Unity.exe>] [-KeepArtifacts]
# 退出码：0 = 演练全绿；非 0 = 发布失败或任一契约测试未通过。
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

$resourceVersion = 1   # v1 不发资源：与出厂基线一致即可通过防降级
$codeVersion     = 2   # 高于出厂基线 1，触发完整代码补丁集契约

Write-Host "== FrameworkBase 发布演练 =="
Write-Host "Unity      : $UnityPath"
Write-Host "AppVersion : $appVersion  Code: $codeVersion  KeyId: $keyId"
Write-Host "UploadRoot : $uploadRoot"

# ── 第 1 跳：真实发布（batchmode，以 RELEASE_RESULT 哨兵判定，不信进程退出码）──
$publishLog = Join-Path $logsDir "release-rehearsal-publish.log"
if (Test-Path $publishLog) { Remove-Item $publishLog -Force }
$releaseId = [guid]::NewGuid().ToString("N")

& $UnityPath -batchmode -nographics `
    -projectPath $projectPath `
    -executeMethod Framework.Editor.Release.ReleaseBatchEntry.PublishHotUpdate `
    -releaseId $releaseId `
    -releaseEnv dev `
    -buildTarget Win64 `
    -appVersion $appVersion `
    -resourceVersion $resourceVersion `
    -codeVersion $codeVersion `
    -publishResource false `
    -publishCode true `
    -uploadRoot $uploadRoot `
    -allowDirtyRelease `
    -logFile $publishLog | Out-Null

$verdict = $null
for ($i = 0; $i -lt 300; $i++) {
    if (Test-Path $publishLog) {
        $endLine = Get-Content $publishLog -Encoding UTF8 -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "RELEASE_RESULT\s+exit=(\d+)" } | Select-Object -Last 1
        if ($endLine -and $endLine -match "RELEASE_RESULT\s+exit=(\d+)") {
            $verdict = [int]$Matches[1]
            break
        }
    }
    if ($i -gt 0 -and $i % 25 -eq 0) { Write-Host "等待发布结论落盘... $([int]($i / 5))s" }
    Start-Sleep -Milliseconds 200
}

Get-Content $publishLog -Encoding UTF8 -ErrorAction SilentlyContinue |
    Where-Object { $_ -match "\[ReleaseBatch\]|\[环境校验\]|error CS|Exception" } |
    Select-Object -Last 40 | ForEach-Object { Write-Host $_ }

if ($verdict -ne 0) {
    Write-Host "发布演练第 1 跳失败（RELEASE_RESULT=$verdict），详见日志: $publishLog"
    Remove-Item Env:\FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 -ErrorAction SilentlyContinue
    exit 1
}
$channelManifest = Join-Path $uploadRoot (($channelRelative -replace '/', '\') + "\version.json")
if (-not (Test-Path $channelManifest)) {
    Write-Host "发布声称成功但渠道根缺少清单别名：$channelManifest，判定失败。"
    exit 1
}
Write-Host "第 1 跳（真实发布）通过。"

# ── 第 2 跳：写 rehearsal.json，跑客户端契约集成测试 ─────────────────────────
@{
    PublicKeyXml    = $publicXml
    KeyId           = $keyId
    UploadRoot      = $uploadRoot
    BaseUrl         = $devProfile.BaseUrl
    ChannelRelative = $channelRelative
    AppVersion      = $appVersion
    ResourceVersion = $resourceVersion
    CodeVersion     = $codeVersion
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
    if ([int]$run.failed -eq 0 -and [int]$run.passed -ge 7) { $exitCode = 0 }
    elseif ([int]$run.failed -eq 0) { Write-Host "契约测试通过数不足（可能整组被跳过），判定失败。" }
}
else {
    Write-Host "未生成契约测试结果（大概率编译失败），详见日志: $testLog"
    Get-Content $testLog -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "error CS|Compilation failed" } | Select-Object -First 20
}

# ── 收尾：清理进程内私钥。演练成功默认清理产物（传 -KeepArtifacts 保留）；
#    失败时一律保留——uploadRoot 与 ledger 是排查第一手证据。
Remove-Item Env:\FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 -ErrorAction SilentlyContinue
if (-not $KeepArtifacts -and $exitCode -eq 0) {
    Remove-Item $rehearsalRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) { Write-Host "== 发布演练全绿 ==" } else { Write-Host "== 发布演练未通过 ==" }
exit $exitCode
