#requires -Version 5.1
# FrameworkBase 全链路一键演示：真服务端 + 真客户端 + 真热更，全程无人值守。
#
# 演示的是「一个完整游戏的骨架」，分五段，每段落 ASCII 哨兵：
#   1. 服务端起来并自证（心跳探针 + 会话探针，不需要 Unity）
#   2. 客户端真连服务端：游客登录 → TCP 长连接 → SessionBind 握手 → 校时/RTT → Echo →
#      服务端权威档案（020_001，userId 由服务端按会话绑定回答，客户端自报不作数）
#   3. 冷启动令牌重绑：再跑一次，userId 必须与上次相同（不产生新游客）
#   4. 服务端重启后客户端自愈：令牌全失效 → 静默降级到游客登录，无人工干预重新走完全链路
#      （身份是否跨重启延续单列观察，不作判据——见段 4 注释）
#   5. 热更闭环：同一个 IL2CPP Player 上 v1 → v2 数值翻倍 → 回滚复原
#
# 段 2~4 三次运行只改 AppConfig 一个资产（UseNetworkLogin），不动任何客户端代码——
# 这正是 ServerBase 验收清单 2.5 的判据。
#
# 用法（工程须先关闭 Unity 编辑器；ServerBase 默认取本仓库的同级目录）：
#   .\Tools\demo\run-demo.ps1 [-ServerRepo <path>] [-UnityPath <Unity.exe>] [-SkipHotUpdate]
# 退出码：0 = 全段通过；非 0 = 任一段未通过。
# 耗时：不含热更约 6 分钟；含热更约 30 分钟（热更段要构建 IL2CPP Player 并跑三次）。
[CmdletBinding()]
param(
    [string]$ServerRepo,
    [string]$UnityPath,
    [switch]$SkipHotUpdate,
    # 演示结束后保留服务端进程，便于手工继续把玩。
    [switch]$KeepServerRunning
)

$ErrorActionPreference = "Stop"
$projectPath = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 定位 ServerBase（默认同级目录，便于别人 clone 两个仓库并排放）────────────
if (-not $ServerRepo) { $ServerRepo = Join-Path (Split-Path $projectPath -Parent) "ServerBase" }
if (-not (Test-Path (Join-Path $ServerRepo "src\Server.Host\Server.Host.csproj"))) {
    throw "未找到 ServerBase 仓库：$ServerRepo（传 -ServerRepo 指定，或把两个仓库放同级目录）"
}

# ── 定位 Unity（与 run-ci.ps1 同规则）────────────────────────────────────────
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

$logsDir         = Join-Path $projectPath "Logs\demo"
$appConfigPath   = Join-Path $projectPath "Assets\Resources\AppConfig.asset"
$appConfigBackup = Join-Path $logsDir "AppConfig.asset.orig"
$serverExe       = Join-Path $ServerRepo "src\Server.Host\bin\Debug\net10.0\Server.Host.exe"
New-Item -ItemType Directory -Force $logsDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$script:serverProc = $null

function Write-Section([string]$text) {
    Write-Host ""
    Write-Host "======== $text ========"
}

# ── 服务端进程管理 ──────────────────────────────────────────────────────────
# 直接跑已构建的 exe 而不是 `dotnet run`：后者是「宿主进程 + 真实服务端子进程」两层，
# 杀父留子，下一段重启时端口仍被占，症状是「服务端起不来」而根因是上一段没清干净。
function Start-DemoServer([string]$tag) {
    $log = Join-Path $logsDir "server-$tag.log"
    # 必须显式给环境名：ServerBase 的 P0 启动硬栏在非 Development 环境下拒绝以随机令牌密钥 +
    # 纯内存会话启动（ADR-S005）。仓库把 Development 写在 launchSettings.json 里，那份配置
    # 只对 `dotnet run` 生效，直接跑 exe 会落进 Production 并在启动时抛
    # ProductionStartupBlockedException。
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $script:serverProc = Start-Process -FilePath $serverExe `
        -WorkingDirectory (Join-Path $ServerRepo "src\Server.Host") `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    for ($i = 0; $i -lt 60; $i++) {
        $tcp = New-Object System.Net.Sockets.TcpClient
        try { $tcp.Connect("127.0.0.1", 9000); $tcp.Dispose(); return $log }
        catch { $tcp.Dispose() }
        Start-Sleep -Milliseconds 500
    }
    throw "服务端未能在 30 秒内监听 :9000，详见 $log"
}

function Stop-DemoServer {
    if (-not $script:serverProc) { return }
    try { Stop-Process -Id $script:serverProc.Id -Force -ErrorAction SilentlyContinue } catch { }
    $script:serverProc = $null
    # 端口交还内核需要一瞬；不等会让紧随其后的重启探针连到正在关闭的旧监听。
    Start-Sleep -Milliseconds 800
}

# 还原被本脚本改动的一切。任何路径退出都必须经过这里。
function Restore-Demo {
    if (Test-Path $appConfigBackup) { Copy-Item $appConfigBackup $appConfigPath -Force }
    if (-not $KeepServerRunning) { Stop-DemoServer }
}

# 跑一次客户端长连接验收器，回传 @{ Ok=<bool>; UserId=<string>; Line=<string> }。
function Invoke-ClientCheck([string]$tag) {
    $log = Join-Path $logsDir "client-$tag.log"
    if (Test-Path $log) { Remove-Item $log -Force }

    $env:GAMESERVER_SELFCHECK = "1"
    $proc = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
        "-batchmode", "-projectPath", $projectPath,
        "-executeMethod", "Game.Editor.GameServerSessionPlayCheck.Run",
        "-logFile", $log)
    if (-not $proc.WaitForExit(300000)) {
        Write-Host "[$tag] Unity 超时未退出，强制结束。"
        try { $proc.Kill() } catch { }
    }

    # batchmode 退出码不可靠，一律以日志哨兵为准。
    $line = Select-String -Path $log -Pattern "GAME_SERVER_SESSION_CHECK_(OK|FAIL|SKIP).*" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $line) { return @{ Ok = $false; UserId = ""; Line = "未见哨兵行（详见 $log）" } }

    $text = $line.Matches[0].Value
    $userId = if ($text -match "userId=(\S+)") { $Matches[1] } else { "" }
    return @{ Ok = $text.StartsWith("GAME_SERVER_SESSION_CHECK_OK"); UserId = $userId; Line = $text }
}

try {
    Copy-Item $appConfigPath $appConfigBackup -Force

    # ── 段 1：服务端起来并自证 ───────────────────────────────────────────────
    Write-Section "段 1/5 启动 ServerBase 并做服务端侧自证"
    Write-Host "构建服务端（dotnet build -c Debug）……"
    & dotnet build (Join-Path $ServerRepo "ServerBase.slnx") -c Debug --nologo -v quiet |
        Out-File (Join-Path $logsDir "server-build.log") -Encoding utf8
    if (-not (Test-Path $serverExe)) { throw "服务端构建失败，详见 $logsDir\server-build.log" }

    Start-DemoServer -tag "s1" | Out-Null
    Write-Host "服务端已监听 TCP :9000 / HTTP :9080"

    $hb = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ServerRepo "Tools\probe-heartbeat.ps1") 2>&1 | Out-String
    if ($hb -notmatch "HEARTBEAT_PROBE_OK") { $failures.Add("段1 心跳探针未通过：$hb") }
    else { Write-Host "  [OK] 心跳探针（001_001 回包、ServerTime、序号回显）" }

    $ss = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ServerRepo "Tools\probe-session.ps1") 2>&1 | Out-String
    if ($ss -notmatch "SESSION_PROBE_OK") { $failures.Add("段1 会话探针未通过：$ss") }
    else { Write-Host "  [OK] 会话探针（HTTP 令牌 → TCP SessionBind → Echo 放行）" }

    # ── 段 2：客户端切联网档并真连 ───────────────────────────────────────────
    Write-Section "段 2/5 客户端真连服务端（登录 → 长连接 → 握手 → 校时 → Echo → 服务端档案）"
    # 只改这一个字段：committed 状态是离线默认，保证 clone 下来不起服务端也能跑。
    $config = [IO.File]::ReadAllText($appConfigPath)
    $config = $config -replace "(?m)^  UseNetworkLogin: 0$", "  UseNetworkLogin: 1"
    [IO.File]::WriteAllText($appConfigPath, $config, (New-Object Text.UTF8Encoding $false))
    Write-Host "AppConfig 已切联网档（仅 UseNetworkLogin，退出时还原）。"

    $r1 = Invoke-ClientCheck -tag "first"
    Write-Host "  $($r1.Line)"
    if (-not $r1.Ok) { $failures.Add("段2 首次贯通未通过：$($r1.Line)") }

    # ── 段 3：冷启动令牌重绑 ─────────────────────────────────────────────────
    Write-Section "段 3/5 冷启动令牌重绑（同一 userId，不产生新游客）"
    $r2 = Invoke-ClientCheck -tag "rebind"
    Write-Host "  $($r2.Line)"
    if (-not $r2.Ok) { $failures.Add("段3 重绑后贯通未通过：$($r2.Line)") }
    elseif ($r1.Ok -and $r2.UserId -ne $r1.UserId) {
        $failures.Add("段3 冷启动后 userId 变了：$($r1.UserId) -> $($r2.UserId)（应命中持久化会话重绑）")
    }
    else { Write-Host "  [OK] userId 保持 $($r2.UserId)" }

    # ── 段 4：服务端重启后客户端自愈 ─────────────────────────────────────────
    Write-Section "段 4/5 服务端重启后客户端自愈（令牌全失效 -> 无人工干预重新贯通）"
    Stop-DemoServer
    Start-DemoServer -tag "s2" | Out-Null
    Write-Host "服务端已重启（既有令牌与会话全部失效）。"

    $r3 = Invoke-ClientCheck -tag "afterrestart"
    Write-Host "  $($r3.Line)"
    # 判据只到「自愈」为止：重绑必然被拒（令牌是进程内内存态），客户端应静默降级到游客登录并
    # 重新走完握手/校时/业务全链路，不报错、不卡在登录页。
    if (-not $r3.Ok) { $failures.Add("段4 服务端重启后未能自愈：$($r3.Line)") }
    else { Write-Host "  [OK] 无人工干预重新贯通（bind/sync/echo/profile 全绿）" }

    # 身份连续性单列观察，不作判据：deviceId→userId 映射由 IGuestIdentityStore 提供，
    # 当前只有内存实现（InMemoryGuestIdentityStore），重启即丢，必然换新游客 id。
    # 「重启后 userId 不变」是持久化实现才具备的性质（ADR-S005：生产必须替换内存存储），
    # 拿它当 dev 判据等于断言一个架构还没兑现的东西。
    if ($r1.Ok -and $r3.UserId -ne $r1.UserId) {
        Write-Host "  [注] 身份未延续：$($r1.UserId) -> $($r3.UserId)"
        Write-Host "       预期内——游客映射当前是纯内存的。要跨重启保号需实现持久化 IGuestIdentityStore。"
    }
    elseif ($r1.Ok) {
        Write-Host "  [注] 身份已延续（$($r3.UserId)）——说明已接入持久化游客存储。"
    }

    # ── 段 5：热更闭环 ───────────────────────────────────────────────────────
    Write-Section "段 5/5 热更闭环（同一个 IL2CPP Player：v1 -> v2 翻倍 -> 回滚复原）"
    if ($SkipHotUpdate) {
        Write-Host "已指定 -SkipHotUpdate，跳过。"
    }
    else {
        # 先把 AppConfig 还原回 committed 状态：热更演练有自己的一整套配置补丁与备份，
        # 让它从干净档起跑，两个脚本的备份互不覆盖。
        Copy-Item $appConfigBackup $appConfigPath -Force
        $hu = & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $projectPath "Tools\ci\hotupdate-runtime-rehearsal.ps1") `
            -UnityPath $UnityPath 2>&1 | Tee-Object -FilePath (Join-Path $logsDir "hotupdate.log") | Out-String
        if ($hu -notmatch "HOTUPDATE_RUNTIME_REHEARSAL_OK") {
            $failures.Add("段5 热更演练未通过，详见 $logsDir\hotupdate.log")
        }
        else { Write-Host "  [OK] v1 -> v2 数值翻倍 -> 回滚复原" }
    }
}
finally {
    Restore-Demo
    $env:GAMESERVER_SELFCHECK = $null
    $env:ASPNETCORE_ENVIRONMENT = $null
}

Write-Host ""
Write-Host "==== 全链路演示结果 ===="
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "  [FAIL] $f" }
    Write-Host "FULL_DEMO_FAIL"
    exit 1
}
$covered = "服务端自证 / 客户端贯通 / 冷启动重绑 / 服务端重启自愈"
if (-not $SkipHotUpdate) { $covered += " / 热更闭环" }
Write-Host "  $covered"
Write-Host "FULL_DEMO_OK"
exit 0
