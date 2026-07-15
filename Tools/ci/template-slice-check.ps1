# 模板切片 Play 验收跑道（切片 F）：batchmode 驱动两个无人值守 Play 验收器，
# 以日志 ASCII 哨兵判定（batchmode 退出码不可靠，沿用 run-ci 门禁同款约定）：
#   - ClickerPlayCheck    → CLICKER_PLAY_CHECK_OK / _FAIL（启动壳/配表/玩法循环/存档往返）
#   - LoginSlicePlayCheck → LOGIN_SLICE_CHECK_OK / _FAIL（真实 HTTP 登录/A-B 切号存档隔离/互踢）
#
# 用法（工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\template-slice-check.ps1                     # 两个验收器全跑
#   .\Tools\ci\template-slice-check.ps1 -Only Clicker       # 只跑玩法切片
#   .\Tools\ci\template-slice-check.ps1 -Only Login         # 只跑登录切片
#   .\Tools\ci\run-ci.ps1 -TemplateSlice                    # 挂入 run-ci（nightly/手动触发）
#
# 退出码：0 = 全部验收器 OK；1 = 任一 FAIL / 超时 / 未产出哨兵。
# 注意：验收器进 Play 模式，Unity 进程由验收器自身 EditorApplication.Exit 结束；
# 本脚本只轮询哨兵并在超时时兜底强杀（只杀本工程的 batchmode，不误伤其它 Unity）。
param(
    [string]$UnityPath,
    [ValidateSet("All", "Clicker", "Login")]
    [string]$Only = "All",
    [int]$TimeoutSec = 480
)

$ErrorActionPreference = "Stop"

# Tools\ci → 工程根
$projectPath = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 读取工程要求的 Unity 版本并定位 Unity（与 run-ci 同款）────────────────
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
    throw "未找到 Unity $unityVersion。请传 -UnityPath 或设置环境变量 UNITY_EDITOR_PATH。"
}

# 锁文件检查带等待窗口：挂在 run-ci 后面时，前一个 batchmode Unity（EditMode/门禁）刚退出，
# UnityLockfile 可能残留数秒；立即判占用会误伤。真被编辑器长期占用时 60s 后仍会明确阻断。
$lockPath = Join-Path $projectPath "Temp\UnityLockfile"
for ($i = 0; $i -lt 12 -and (Test-Path $lockPath); $i++) {
    if ($i -eq 0) { Write-Host "检测到 UnityLockfile，等待前一个 Unity 进程释放（最多 60s）..." }
    Start-Sleep -Seconds 5
}
if (Test-Path $lockPath) {
    throw "工程正被 Unity 编辑器占用（Temp\UnityLockfile 存在）。请关闭编辑器后重试。"
}

$artifacts = Join-Path $projectPath "Logs\ci"
New-Item -ItemType Directory -Force $artifacts | Out-Null

# ── 单个 Play 验收器：起 batchmode（不带 -quit，验收器自会 Exit）→ 轮询哨兵 ──
function Invoke-PlayCheck([string]$name, [string]$method, [string]$okSentinel, [string]$failSentinel) {
    $logPath = Join-Path $artifacts "$name.log"
    if (Test-Path $logPath) { Remove-Item $logPath -Force }

    Write-Host ""
    Write-Host "── 模板切片验收：$name（$method）──────────────"

    # 不带 -nographics：验收器要驱动真实 UGUI（历次人工验收均以此形态跑绿，保持一致）。
    $argLine = "-batchmode -projectPath `"$projectPath`" -executeMethod $method -logFile `"$logPath`""
    $proc = Start-Process -FilePath $UnityPath -ArgumentList $argLine -PassThru -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $exitedAt = $null
    while ($true) {
        Start-Sleep -Seconds 5

        # 哨兵优先：进程退出后日志仍可能延迟落盘，只要哨兵出现即可判定。
        if (Test-Path $logPath) {
            $line = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match [regex]::Escape($okSentinel) -or $_ -match [regex]::Escape($failSentinel) } |
                Select-Object -Last 1
            if ($line) {
                Write-Host $line
                # 哨兵已见：给进程最多 60s 自然退出，残留则兜底强杀。
                for ($i = 0; $i -lt 12 -and -not $proc.HasExited; $i++) { Start-Sleep -Seconds 5 }
                if (-not $proc.HasExited) {
                    Write-Host "$name 哨兵已产出但 Unity 进程未退出，强制结束。"
                    try { $proc.Kill() } catch { }
                }
                if ($line -match [regex]::Escape($okSentinel)) {
                    Write-Host "$name 验收通过。"
                    return 0
                }
                Write-Host "$name 验收未通过，详见日志: $logPath"
                return 1
            }
        }

        # 进程已退出但尚无哨兵：宽限 30s 等日志冲刷，仍无哨兵按失败（编译失败/异常中止）。
        if ($proc.HasExited) {
            if ($null -eq $exitedAt) { $exitedAt = Get-Date }
            elseif ((Get-Date) -gt $exitedAt.AddSeconds(30)) {
                Write-Host "$name 进程已退出且未产出哨兵（大概率编译失败或异常中止），详见日志: $logPath"
                Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
                    Where-Object { $_ -match "error CS|Compilation failed|Exception" } |
                    Select-Object -First 20 |
                    ForEach-Object { Write-Host $_ }
                return 1
            }
        }

        if ((Get-Date) -gt $deadline) {
            Write-Host "$name 超时（${TimeoutSec}s）未产出哨兵，强制结束 Unity 进程。日志: $logPath"
            try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
            return 1
        }
    }
}

Write-Host "== 模板切片验收（Template Slice Check）=="
Write-Host "Unity   : $UnityPath"
Write-Host "Project : $projectPath"
Write-Host "范围    : $Only（产物 $artifacts）"

$checks = @()
if ($Only -eq "All" -or $Only -eq "Clicker") {
    $checks += @{ Name = "clicker-play-check"; Method = "Game.Editor.ClickerPlayCheck.Run";
                  Ok = "CLICKER_PLAY_CHECK_OK"; Fail = "CLICKER_PLAY_CHECK_FAIL" }
}
if ($Only -eq "All" -or $Only -eq "Login") {
    $checks += @{ Name = "login-slice-check"; Method = "Game.Editor.LoginSlicePlayCheck.Run";
                  Ok = "LOGIN_SLICE_CHECK_OK"; Fail = "LOGIN_SLICE_CHECK_FAIL" }
}

$finalExit = 0
foreach ($check in $checks) {
    $result = Invoke-PlayCheck $check.Name $check.Method $check.Ok $check.Fail
    if ($result -ne 0) { $finalExit = 1 }
}

# Play 验收可能把运行时产物烘回工作区（历史上出现过 Launch.unity/预制体被重写）。
# 只提示不判红：是否丢弃由人/上层流程决定，避免 CI 静默改动工作区。
$dirty = git -C $projectPath status --porcelain -- "Assets/Scenes" "Assets/FrameworkTemplate" 2>$null
if ($dirty) {
    Write-Host ""
    Write-Host "注意：Play 验收后工作区存在场景/预制体改动（运行时烘焙产物，非意图改动，建议 git checkout -- 丢弃）："
    $dirty | ForEach-Object { Write-Host "  $_" }
}

Write-Host ""
Write-Host "TEMPLATE_SLICE_RESULT exit=$finalExit"
if ($finalExit -eq 0) {
    Write-Host "== 模板切片验收通过 =="
}
else {
    Write-Host "== 模板切片验收未通过 =="
}
exit $finalExit
