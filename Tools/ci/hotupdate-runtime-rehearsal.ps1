#requires -Version 5.1
# 热更运行时演练：真 Player 经 HybridCLR 运行时加载热更 dll，跑出新玩法数值。
#
# 与 release-rehearsal.ps1 的分工：那条链路验证「发布端产物 + 客户端契约」（EditMode 消费产物），
# 但**编辑器不实际热加载**——它直接用已编译程序集，因此"改了代码真能在运行时生效"从未被证过。
# 本脚本补上这一跳：构建 Windows IL2CPP 独立 Player（HybridCLR 只在 IL2CPP 下生效），
# 让它从本地更新服务真下载、装载、执行热更代码，并以玩法数值证伪。
#
# 五跳：
#   1. 生成一次性 dev RSA 密钥对，公钥写入 AppConfig（构建期烘入）、私钥只在本进程用于签名；
#   2. 构建一次 Development + IL2CPP Player（此后 Player 不再重建——这是关键：
#      后续数值变化只可能来自热更 dll，不可能来自重新构建的宿主）；
#   3. 发布 v1 → 起本地 HTTP 服务托管 uploadRoot → 跑 Player → 记下 ClickGain；
#   4. 改一行玩法（ClickGain ×2）发布 v2 → 同一个 Player 再跑 → 断言数值翻倍；
#   5. 一键回滚到 v1 → 同一个 Player 再跑 → 断言数值回到初值。
#
# 判定一律以 Player 日志的 ASCII 哨兵为准（batchmode/Player 退出码均不可靠）：
#   GAMEPLAY_SELFCHECK_OK click(+N)
#
# 用法（工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\hotupdate-runtime-rehearsal.ps1 [-UnityPath <Unity.exe>] [-SkipBuild] [-KeepArtifacts]
# 退出码：0 = 三跳数值全部符合预期；非 0 = 任一跳未通过。
[CmdletBinding()]
param(
    [string]$UnityPath,
    [switch]$SkipBuild,
    [switch]$KeepArtifacts,
    [int]$Port = 8099,
    # 须 >= Player 内烘入的本地 resourceVersion，否则热更清单会被"拒绝资源版本降级"挡下。
    [int]$ResourceVersion = 2
)

$ErrorActionPreference = "Stop"
$projectPath = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 定位 Unity（与 run-ci.ps1 / release-rehearsal.ps1 同规则）────────────────
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

$rehearsalRoot = Join-Path $projectPath "Artifacts\RuntimeRehearsal"
$uploadRoot    = Join-Path $rehearsalRoot "uploadRoot"
$buildDir      = Join-Path $projectPath "Builds\HotUpdateRehearsal"
$playerExe     = Join-Path $buildDir "Game.exe"
$logsDir       = Join-Path $projectPath "Logs\ci"
$appConfigPath = Join-Path $projectPath "Assets\Resources\AppConfig.asset"
# 发布产物按 {env}/{platform}/{channel} 落盘；UpdateServerUrl 必须直指该渠道根——
# 热更安全门禁要求 URL 自带独立 env 路径段（防各环境共用 CDN 根），构建期即校验。
$channelRootUrl = "http://127.0.0.1:$Port/dev/windows/default"
$modelPath     = Join-Path $projectPath "Assets\Scripts\HotUpdate\Clicker\ClickerModel.cs"
# 发布端把 BaseUrl 烘进清单的补丁 URL；客户端校验补丁 URL 必须与更新根同源同路径根。
# 演练期改指本地服务，否则清单里写的是 dev profile 的 127.0.0.1:80/Updates，实测被
# "补丁 URL 不属于受信任更新根" 拒绝。退出时还原。
$devProfilePath = Join-Path $projectPath "ReleaseProfiles\dev.json"

New-Item -ItemType Directory -Force $logsDir | Out-Null

$bundleLine = Get-Content (Join-Path $projectPath "ProjectSettings\ProjectSettings.asset") |
    Where-Object { $_ -match "^\s*bundleVersion:\s*(\S+)" } | Select-Object -First 1
if (-not ($bundleLine -match "bundleVersion:\s*(\S+)")) { throw "ProjectSettings.asset 中未找到 bundleVersion。" }
$appVersion = $Matches[1]
if (Test-Path $uploadRoot) { Remove-Item $uploadRoot -Recurse -Force }
New-Item -ItemType Directory -Force $uploadRoot | Out-Null

$appConfigBackup = Join-Path $rehearsalRoot "AppConfig.asset.orig"
$modelBackup     = Join-Path $rehearsalRoot "ClickerModel.cs.orig"
$devProfileBackup = Join-Path $rehearsalRoot "dev.json.orig"
Copy-Item $appConfigPath $appConfigBackup -Force
Copy-Item $modelPath $modelBackup -Force
Copy-Item $devProfilePath $devProfileBackup -Force

$httpJob = $null
$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "──── [$text] ────"
}

# 还原一切被本脚本改动的工作区文件。任何路径退出都必须经过这里，
# 否则会把演练用的 AppConfig（含一次性公钥 + EnableHotUpdate=1）留在工作区。
function Restore-Workspace {
    if (Test-Path $appConfigBackup) { Copy-Item $appConfigBackup $appConfigPath -Force }
    if (Test-Path $modelBackup) { Copy-Item $modelBackup $modelPath -Force }
    if (Test-Path $devProfileBackup) { Copy-Item $devProfileBackup $devProfilePath -Force }
    if ($script:httpJob) {
        Stop-Job $script:httpJob -ErrorAction SilentlyContinue
        Remove-Job $script:httpJob -Force -ErrorAction SilentlyContinue
        $script:httpJob = $null
    }
}

# ── dev 密钥对（私钥绝不落库，只写到构建产物目录供 -SkipBuild 复用）─────────────
# 公钥在构建期烘进 Player，私钥用于发布签名，二者必须成对。-SkipBuild 复用旧 Player 时
# 若重新生成密钥，Player 会以"清单验签失败"拒绝更新——那是配置事故，不是热更缺陷。
# 故密钥随 Player 一起留存：重建时新生成，复用时读回。
$keyFile = Join-Path $buildDir "rehearsal-key.xml"
if ($SkipBuild -and (Test-Path $playerExe) -and (Test-Path $keyFile)) {
    $privateXml = [IO.File]::ReadAllText($keyFile)
    $rsaLoad = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
    try { $rsaLoad.FromXmlString($privateXml); $publicXml = $rsaLoad.ToXmlString($false) }
    finally { $rsaLoad.Dispose() }
    Write-Host "复用既有 Player 对应的密钥对（$keyFile）。"
}
elseif ($SkipBuild -and (Test-Path $playerExe)) {
    throw "指定了 -SkipBuild 但找不到 $keyFile：既有 Player 烘入的公钥无从得知，无法签出它认的清单。请去掉 -SkipBuild 重建。"
}
else {
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
    try { $privateXml = $rsa.ToXmlString($true); $publicXml = $rsa.ToXmlString($false) }
    finally { $rsa.Dispose() }
    New-Item -ItemType Directory -Force $buildDir | Out-Null
    [IO.File]::WriteAllText($keyFile, $privateXml)
}
$env:FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 =
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($privateXml))

try {
    # ── 跳 1：把演练配置烘进 AppConfig（Player 构建期读取，运行时不可改）──────
    Write-Step "跳 1/5 配置演练档（EnableHotUpdate=1 + 本地更新服务 + 一次性公钥）"
    $config = [IO.File]::ReadAllText($appConfigPath)
    $config = $config -replace "(?m)^  EnableHotUpdate: 0$", "  EnableHotUpdate: 1"
    $config = $config -replace "(?m)^  UpdateServerUrl:\s*$", "  UpdateServerUrl: $channelRootUrl"
    $config = $config -replace "(?m)^  UpdateServerUrl: .*$", "  UpdateServerUrl: $channelRootUrl"
    $config = [regex]::Replace(
        $config,
        "(- KeyId: dev_hotupdate_manifest\r?\n    PublicKeyXml: )[^\r\n]*",
        { param($m) $m.Groups[1].Value + $publicXml },
        [Text.RegularExpressions.RegexOptions]::None)
    # PS 5.1 的 -Encoding utf8 会写 BOM；Unity 资产是无 BOM UTF-8，故走 .NET 显式无 BOM 写入。
    [IO.File]::WriteAllText($appConfigPath, $config, (New-Object Text.UTF8Encoding $false))
    $devProfile = [IO.File]::ReadAllText($devProfilePath)
    $devProfile = $devProfile -replace '"BaseUrl":\s*"[^"]*"', ('"BaseUrl": "http://127.0.0.1:' + $Port + '"')
    [IO.File]::WriteAllText($devProfilePath, $devProfile, (New-Object Text.UTF8Encoding $false))
    Write-Host "AppConfig 与 dev 发布档已切演练档（退出时自动还原）。"

    # ── 跳 2：构建 Player（只此一次）──────────────────────────────────────────
    Write-Step "跳 2/5 构建 Windows IL2CPP Development Player"
    if ($SkipBuild -and (Test-Path $playerExe)) {
        Write-Host "已存在 Player 且指定 -SkipBuild，复用：$playerExe"
    }
    else {
        # 构建前置：把 HybridCLR 的 AOT 补充元数据同步进 StreamingAssets 随包走。
        # 漏这步，Player 能下载装载热更 dll，却会在加载泛型补充元数据时报
        # "未找到 AOT 元数据: mscorlib.dll.bytes" 并整轮回滚——症状出现在运行期，根因在打包期。
        $aotLog = Join-Path $logsDir "runtime-rehearsal-aot-sync.log"
        $aotProc = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
            "-batchmode", "-projectPath", $projectPath, "-buildTarget", "Win64",
            "-executeMethod", "Framework.Editor.HybridCLRStreamingAssetsSync.SyncForBuildBatch",
            "-logFile", $aotLog)
        $aotProc.WaitForExit()
        if (-not (Select-String -Path $aotLog -Pattern "AOT_METADATA_SYNC_OK" -Quiet -ErrorAction SilentlyContinue)) {
            throw "AOT 元数据同步失败，详见 $aotLog"
        }
        Write-Host "AOT 补充元数据已同步进 StreamingAssets。"

        $buildLog = Join-Path $logsDir "runtime-rehearsal-build.log"
        New-Item -ItemType Directory -Force $buildDir | Out-Null
        $proc = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
            "-batchmode", "-projectPath", $projectPath, "-buildTarget", "Win64",
            "-executeMethod", "Framework.Editor.BuildEntry.BuildPlayer",
            "-outputPath", $playerExe, "-development", "-logFile", $buildLog)
        $proc.WaitForExit()
        if (-not (Test-Path $playerExe)) {
            throw "Player 构建失败，未产出 $playerExe（详见 $buildLog）"
        }
        Write-Host "Player 构建完成：$playerExe"
    }

    # ── 起本地更新服务（托管 uploadRoot）─────────────────────────────────────
    Write-Step "起本地更新服务 http://127.0.0.1:$Port"
    $httpJob = Start-Job -ScriptBlock {
        param($root, $port)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$port/")
        $listener.Start()
        while ($listener.IsListening) {
            try {
                $ctx = $listener.GetContext()
                $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath.TrimStart('/'))
                $file = Join-Path $root ($rel -replace '/', '\')
                if (Test-Path $file -PathType Leaf) {
                    $bytes = [IO.File]::ReadAllBytes($file)
                    $ctx.Response.ContentLength64 = $bytes.Length
                    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                }
                else { $ctx.Response.StatusCode = 404 }
                $ctx.Response.Close()
            }
            catch { }
        }
    } -ArgumentList $uploadRoot, $Port
    # 就绪探针：HttpListener 在 Job 里绑定失败（端口被上一轮演练占用是最常见原因）时
    # 不会抛到主脚本，若不探测就会一路跑到 Player 报"传输失败"，把配置事故误读成热更缺陷。
    # 用 TCP 连接判定而非 HTTP 状态码——根路径本就无文件，HTTP 语义在此只会引入误判。
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        $tcp = New-Object System.Net.Sockets.TcpClient
        try { $tcp.Connect("127.0.0.1", $Port); $ready = $true }
        catch { }
        finally { $tcp.Dispose() }
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        $jobErr = (Receive-Job $httpJob -Keep 2>&1 | Out-String).Trim()
        throw "本地更新服务未能在 :$Port 就绪（端口可能被上一轮演练残留的 powershell 作业占用）。Job 输出：$jobErr"
    }
    Write-Host "更新服务已就绪（Job $($httpJob.Id)），根目录 $uploadRoot"

    # ── 发布与运行的两个原语 ─────────────────────────────────────────────────

    # 发布一个 release code 到 uploadRoot（真跑 Unity batchmode 发布管线，含 dll 重编译与签名）。
    function Invoke-Publish([int]$code, [string]$tag) {
        $publishLog = Join-Path $logsDir "runtime-rehearsal-publish-$tag.log"
        $proc = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
            "-batchmode", "-projectPath", $projectPath, "-buildTarget", "Win64",
            "-executeMethod", "Framework.Editor.Release.ReleaseBatchEntry.PublishHotUpdate",
            "-releaseId", ([guid]::NewGuid().ToString("N")),
            "-releaseEnv", "dev",
            "-appVersion", $appVersion,
            "-resourceVersion", "$ResourceVersion",
            "-codeVersion", "$code",
            "-publishResource", "false",
            "-publishCode", "true",
            "-uploadRoot", $uploadRoot,
            "-allowDirtyRelease",
            "-logFile", $publishLog)
        $proc.WaitForExit()
        # batchmode 退出码不可靠，以日志结论行为准。
        $verdictLine = Get-Content $publishLog -Encoding UTF8 -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "RELEASE_RESULT\s+exit=(\d+)" } | Select-Object -Last 1
        if (-not ($verdictLine -match "RELEASE_RESULT\s+exit=0")) {
            $script:failures.Add("发布 $tag（code=$code）未通过，详见 $publishLog")
        }
        return $publishLog
    }

    # 跑一次 Player，回传它自检打出的 ClickGain（取不到返回 -1）。
    function Invoke-PlayerRun([string]$tag) {
        $playerLog = Join-Path $logsDir "runtime-rehearsal-player-$tag.log"
        if (Test-Path $playerLog) { Remove-Item $playerLog -Force }

        $env:CLICKER_SELFCHECK = "1"
        $env:CLICKER_SELFCHECK_QUIT = "1"
        $proc = Start-Process -FilePath $playerExe -PassThru -ArgumentList @(
            "-logFile", $playerLog, "-screen-width", "640", "-screen-height", "480")

        # Player 自检完成后自行 Application.Quit；给足下载+装载+自检的时间再强杀。
        if (-not $proc.WaitForExit(180000)) {
            Write-Host "[$tag] Player 超时未退出，强制结束。"
            try { $proc.Kill() } catch { }
        }

        if (-not (Test-Path $playerLog)) { return -1 }
        $line = Select-String -Path $playerLog -Pattern "GAMEPLAY_SELFCHECK_OK click\(\+(\d+)\)" |
            Select-Object -First 1
        if (-not $line) { return -1 }
        return [int]$line.Matches[0].Groups[1].Value
    }

    # ── 跳 3：发布 v1 → 跑 Player → 记基准数值 ───────────────────────────────
    Write-Step "跳 3/5 发布 v1（code=2）并运行 Player"
    Invoke-Publish -code 2 -tag "v1" | Out-Null
    $gainV1 = Invoke-PlayerRun -tag "v1"
    Write-Host "v1 ClickGain = $gainV1"
    if ($gainV1 -le 0) { $failures.Add("v1 未取到 ClickGain 哨兵") }

    # ── 跳 4：改玩法（×2）发布 v2 → 同一 Player 再跑 ─────────────────────────
    Write-Step "跳 4/5 改玩法 ClickGain×2，发布 v2（code=3）并运行同一个 Player"
    $model = [IO.File]::ReadAllText($modelPath)
    $patched = $model -replace "(?m)^(\s*)public int ClickGain\s*=>\s*(.+?);\s*$", '$1public int ClickGain => ($2) * 2;'
    if ($patched -eq $model) {
        $failures.Add("未能在 ClickerModel 中定位 ClickGain 属性，v2 玩法改动未生效")
    }
    else {
        [IO.File]::WriteAllText($modelPath, $patched, (New-Object Text.UTF8Encoding $false))
    }
    Invoke-Publish -code 3 -tag "v2" | Out-Null
    $gainV2 = Invoke-PlayerRun -tag "v2"
    Write-Host "v2 ClickGain = $gainV2（期望 $($gainV1 * 2)）"
    if ($gainV1 -gt 0 -and $gainV2 -ne ($gainV1 * 2)) {
        $failures.Add("v2 数值未翻倍：期望 $($gainV1 * 2)，实得 $gainV2（热更 dll 未真正生效）")
    }

    # ── 跳 5：回滚到 v1 → 同一 Player 再跑 ───────────────────────────────────
    Write-Step "跳 5/5 一键回滚到 v1 并运行同一个 Player"
    $rollbackLog = Join-Path $logsDir "runtime-rehearsal-rollback.log"
    $proc = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
        "-batchmode", "-projectPath", $projectPath, "-buildTarget", "Win64",
        "-executeMethod", "Framework.Editor.Release.ReleaseBatchEntry.RollbackRelease",
        "-releaseEnv", "dev", "-uploadRoot", $uploadRoot,
        "-switchedBy", "runtime-rehearsal", "-logFile", $rollbackLog)
    $proc.WaitForExit()

    $gainRollback = Invoke-PlayerRun -tag "rollback"
    Write-Host "回滚后 ClickGain = $gainRollback（期望 $gainV1）"
    if ($gainV1 -gt 0 -and $gainRollback -ne $gainV1) {
        $failures.Add("回滚后数值未回到 v1：期望 $gainV1，实得 $gainRollback")
    }
}
finally {
    Restore-Workspace
    if (-not $KeepArtifacts -and (Test-Path $rehearsalRoot)) {
        Remove-Item $rehearsalRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    $env:FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64 = $null
    $env:CLICKER_SELFCHECK = $null
    $env:CLICKER_SELFCHECK_QUIT = $null
}

Write-Host ""
Write-Host "==== 热更运行时演练结果 ===="
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "  [FAIL] $f" }
    Write-Host "HOTUPDATE_RUNTIME_REHEARSAL_FAIL"
    exit 1
}
Write-Host "  [OK] v1 → v2 数值翻倍 → 回滚复原，同一个 Player 全程未重建。"
Write-Host "HOTUPDATE_RUNTIME_REHEARSAL_OK"
exit 0
