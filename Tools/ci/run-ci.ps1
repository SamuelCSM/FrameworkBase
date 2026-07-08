# FrameworkBase 本地 CI 门禁：编译 + EditMode 测试 + PlayMode 冒烟。
# 与 .github/workflows/ci.yml 跑同一套门禁，提交前在本机自查用。
#
# 用法（PowerShell，工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\run-ci.ps1                     # 自动定位 Unity（Hub 常见安装位置）
#   .\Tools\ci\run-ci.ps1 -UnityPath "H:\Hub\2022.3.62f3\Editor\Unity.exe"
#   .\Tools\ci\run-ci.ps1 -SkipPlayMode       # 只跑 EditMode（快速自查）
#   $env:UNITY_EDITOR_PATH = "...\Unity.exe"; .\Tools\ci\run-ci.ps1
#
# 退出码：0 = 全部通过；非 0 = 编译失败或有测试未通过。
param(
    [string]$UnityPath,
    [switch]$SkipPlayMode
)

$ErrorActionPreference = "Stop"

# Tools\ci → 工程根
$projectPath = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 读取工程要求的 Unity 版本 ─────────────────────────────────────────────
$versionLine = Get-Content (Join-Path $projectPath "ProjectSettings\ProjectVersion.txt") |
    Where-Object { $_ -match "^m_EditorVersion:" } | Select-Object -First 1
$unityVersion = ($versionLine -split "\s+")[1]

# ── 定位 Unity ────────────────────────────────────────────────────────────
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
    throw "未找到 Unity $unityVersion。请传 -UnityPath 或设置环境变量 UNITY_EDITOR_PATH。"
}

# ── batchmode 需要独占工程 ────────────────────────────────────────────────
if (Test-Path (Join-Path $projectPath "Temp\UnityLockfile")) {
    throw "工程正被 Unity 编辑器占用（Temp\UnityLockfile 存在）。请关闭编辑器后重试。"
}

$artifacts = Join-Path $projectPath "Logs\ci"
New-Item -ItemType Directory -Force $artifacts | Out-Null

# ── 单个测试平台的执行 + 结果解析 ─────────────────────────────────────────
function Invoke-UnityTests([string]$platform) {
    $tag = $platform.ToLowerInvariant()
    $resultsPath = Join-Path $artifacts "$tag-results.xml"
    $logPath     = Join-Path $artifacts "$tag.log"
    if (Test-Path $resultsPath) { Remove-Item $resultsPath -Force }

    Write-Host ""
    Write-Host "── $platform 测试 ──────────────────────────────"

    & $UnityPath -batchmode -nographics `
        -projectPath $projectPath `
        -runTests -testPlatform $platform `
        -testResults $resultsPath `
        -logFile $logPath
    $exit = $LASTEXITCODE

    if (Test-Path $resultsPath) {
        [xml]$xml = Get-Content $resultsPath
        $run = $xml."test-run"
        Write-Host ("{0} 结果: total={1} passed={2} failed={3} skipped={4}" -f `
            $platform, $run.total, $run.passed, $run.failed, $run.skipped)

        if ([int]$run.failed -gt 0) {
            Write-Host "-- 失败用例 --"
            $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
                Write-Host (" [FAIL] {0}" -f $_.fullname)
                $msg = $_.SelectSingleNode("failure/message")
                if ($null -ne $msg) { Write-Host ("        {0}" -f $msg.InnerText.Trim()) }
            }
        }
    }
    else {
        Write-Host "未生成 $platform 结果文件（大概率编译失败），详见日志: $logPath"
        Write-Host "-- 日志中的编译错误 --"
        Get-Content $logPath -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "error CS|Compilation failed" } |
            Select-Object -First 20
    }

    return $exit
}

Write-Host "== FrameworkBase CI =="
Write-Host "Unity   : $UnityPath"
Write-Host "Project : $projectPath"
Write-Host ("步骤    : 编译 + EditMode 测试" + $(if ($SkipPlayMode) { "" } else { " + PlayMode 冒烟" }) + "（产物 $artifacts）")

# ── 依次跑两条跑道（EditMode 先行：编译失败/逻辑用例挂了就不必再起 PlayMode）──
$finalExit = Invoke-UnityTests "EditMode"

if ($finalExit -eq 0 -and -not $SkipPlayMode) {
    $finalExit = Invoke-UnityTests "PlayMode"
}
elseif ($finalExit -ne 0) {
    Write-Host ""
    Write-Host "EditMode 未通过，跳过 PlayMode。"
}

Write-Host ""
if ($finalExit -eq 0) {
    Write-Host "== CI 通过 =="
}
else {
    Write-Host "== CI 未通过（Unity 退出码 $finalExit）=="
}
exit $finalExit
