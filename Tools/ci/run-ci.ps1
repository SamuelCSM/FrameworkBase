# FrameworkBase 本地 CI 门禁：编译 + EditMode 测试 + 资源门禁 + PlayMode 冒烟。
# 与 .github/workflows/ci.yml 跑同一套门禁，提交前在本机自查用。
#
# 用法（PowerShell，工程须先关闭 Unity 编辑器）：
#   .\Tools\ci\run-ci.ps1                     # 自动定位 Unity（Hub 常见安装位置）
#   .\Tools\ci\run-ci.ps1 -UnityPath "H:\Hub\2022.3.62f3\Editor\Unity.exe"
#   .\Tools\ci\run-ci.ps1 -SkipPlayMode       # 只跑 EditMode + 资源门禁（快速自查）
#   .\Tools\ci\run-ci.ps1 -SkipAssetGate      # 跳过资源门禁（Addressables/字体）
#   .\Tools\ci\run-ci.ps1 -StrictFonts        # 字体缺字从告警升级为阻断
#   .\Tools\ci\run-ci.ps1 -TemplateSlice      # 附加模板切片 Play 验收（nightly/手动；PR 门禁默认不跑）
#   $env:UNITY_EDITOR_PATH = "...\Unity.exe"; .\Tools\ci\run-ci.ps1
#
# 退出码：0 = 全部通过；非 0 = 编译失败 / 有测试未通过 / 资源门禁被阻断。
param(
    [string]$UnityPath,
    [switch]$SkipPlayMode,
    [switch]$SkipAssetGate,
    [switch]$StrictFonts,
    [switch]$TemplateSlice,
    [string]$BuildSizeDir,
    [switch]$BuildSizeUpdateBaseline,
    [switch]$BuildSizeWarnOnly,
    [double]$BuildSizeBudgetMB = 0
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
    if ($null -eq $exit) { $exit = 1 }

    # batchmode 下 Unity 进程返回可能早于结果文件落盘（退出码不可靠的同类问题），必须轮询等待结果文件出现。
    # 窗口取 300s：EditMode 套件随用例增长而变长，叠加本机许可握手重试的启动波动，60s 会误判「未生成」。
    # 注：GitHub Linux CI 用 game-ci/unity-test-runner，自带更宽超时，本窗口只作用于本地 / pre-push 门禁。
    for ($i = 0; $i -lt 1500 -and -not (Test-Path $resultsPath); $i++) {
        if ($i -gt 0 -and $i % 25 -eq 0) {
            Write-Host "等待 $platform 结果文件落盘... $([int]($i / 5))s"
        }
        Start-Sleep -Milliseconds 200
    }

    if (Test-Path $resultsPath) {
        [xml]$xml = $null
        for ($i = 0; $i -lt 300; $i++) {
            try {
                [xml]$candidate = Get-Content -Encoding UTF8 -Raw $resultsPath
                if ($null -ne $candidate.DocumentElement) {
                    $xml = $candidate
                    break
                }
            }
            catch {
            }

            if ($i -gt 0 -and $i % 25 -eq 0) {
                Write-Host "等待 $platform 结果文件写入完成... $([int]($i / 5))s"
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $xml) {
            Write-Host "无法解析 $platform 结果文件，详见日志: $logPath"
            return 1
        }

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

        # 判定按计数、不看顶层 result 串：有失败或 Inconclusive 即红，并要求至少一个用例真正通过
        # （挡住「整组没跑 / 全跳过」）。skipped(Assert.Ignore) 不判红——ReleaseRehearsalTests 在无
        # Artifacts/Rehearsal/rehearsal.json（gitignored 演练产物）时整组 Ignore，令顶层
        # result=Skipped:Ignored；按其文档「不影响常规 CI」这属自愿跳过。旧逻辑用
        # result -notlike "Passed*" 会把这种自愿跳过误判为失败，使全新 clone 上 run-ci 假红。
        if ([int]$run.failed -gt 0 -or [int]$run.inconclusive -gt 0 -or [int]$run.passed -le 0) {
            $exit = 1
        }
        else {
            if ([int]$run.skipped -gt 0) {
                Write-Host ("注意：{0} 个用例被跳过（如 ReleaseRehearsalTests 无演练 fixture 时 Assert.Ignore），按设计不判失败。" -f $run.skipped)
            }
            if ($exit -ne 0) {
                Write-Host "$platform 测试报告已通过，但 Unity 进程退出码为 $exit；按测试报告判定通过。"
            }
            $exit = 0
        }
    }
    else {
        Write-Host "未生成 $platform 结果文件（大概率编译失败），详见日志: $logPath"
        Write-Host "-- 日志中的编译错误 --"
        Get-Content $logPath -ErrorAction SilentlyContinue |
            Where-Object { $_ -match "error CS|Compilation failed" } |
            Select-Object -First 20

        if ($exit -eq 0) {
            $exit = 1
        }
    }

    return $exit
}

# ── 资源门禁（Addressables 校验 + 字体缺字），batchmode 执行 CiGate ──────────
function Invoke-AssetGate {
    $logPath = Join-Path $artifacts "asset-gate.log"
    if (Test-Path $logPath) { Remove-Item $logPath -Force }

    Write-Host ""
    Write-Host "── 资源门禁（Addressables + 字体缺字）──────────────"

    $gateArgs = @(
        "-batchmode", "-nographics",
        "-projectPath", $projectPath,
        "-executeMethod", "Framework.Editor.CiGate.RunAssetGate",
        "-logFile", $logPath
    )
    if ($StrictFonts) { $gateArgs += "-strictFonts" }

    & $UnityPath @gateArgs
    $procExit = $LASTEXITCODE

    # 以 CiGate 落日志的 ASCII 结论哨兵为准，而非 Unity 进程退出码：
    # batchmode 下即便 EditorApplication.Exit(0)，Unity 进程仍可能返回非 0（与测试跑道同款现象）。
    # 关键：Unity 日志在进程退出后仍有落盘延迟（末尾几行可能还没冲刷），必须像测试跑道等结果
    # 文件那样轮询等待哨兵出现，否则会读到不含结论的半截日志而误判「未产出结论」。
    # 纯 ASCII 匹配 + UTF8 读取，免受日志中文编码影响。
    $verdict = $null
    for ($i = 0; $i -lt 150; $i++) {
        if (Test-Path $logPath) {
            $endLine = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match "\[CiGate\]\s+GATE_RESULT\s+exit=(\d+)" } | Select-Object -Last 1
            if ($endLine -and $endLine -match "GATE_RESULT\s+exit=(\d+)") {
                $verdict = [int]$Matches[1]
                break
            }
        }
        if ($i -gt 0 -and $i % 25 -eq 0) {
            Write-Host "等待资源门禁结论落盘... $([int]($i / 5))s"
        }
        Start-Sleep -Milliseconds 200
    }

    # 摘出门禁关键行（此时日志已完整）。必须走 Write-Host：否则这些行会并入函数返回值，
    # 污染 $gateExit（PowerShell 函数返回全部未捕获输出，而非仅 return 值）。
    Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "\[CiGate\]|\[AddressablesValidator\]|\[FontCoverage\]" } |
        Select-Object -Last 40 |
        ForEach-Object { Write-Host $_ }

    if ($null -ne $verdict) {
        if ($verdict -ne 0) {
            Write-Host "资源门禁未通过（CiGate exit=$verdict），详见日志: $logPath"
        }
        elseif ($procExit -ne 0) {
            Write-Host "资源门禁通过（CiGate exit=0；Unity 进程退出码 $procExit 已忽略，按门禁结论判定）。"
        }
        else {
            Write-Host "资源门禁通过。"
        }
        return $verdict
    }

    # 无门禁结论 = 未跑完（编译失败/异常）。
    Write-Host "资源门禁未产出结论（大概率编译失败或异常），详见日志: $logPath"
    Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "error CS|Compilation failed|Exception" } |
        Select-Object -First 20 |
        ForEach-Object { Write-Host $_ }
    if ($procExit -eq 0) { return 1 } else { return $procExit }
}

# ── 包体门禁（构建后置：需已有产物目录），batchmode 执行 BuildSizeCiGate ─────
# 仅当传入 -BuildSizeDir 时执行；本仓库 run-ci 不出包，故默认跳过。
function Invoke-BuildSizeGate([string]$buildDir) {
    $logPath = Join-Path $artifacts "build-size-gate.log"
    if (Test-Path $logPath) { Remove-Item $logPath -Force }

    Write-Host ""
    Write-Host "── 包体回归门禁（产物：$buildDir）──────────────"

    $gateArgs = @(
        "-batchmode", "-nographics",
        "-projectPath", $projectPath,
        "-executeMethod", "Framework.Editor.BuildSize.BuildSizeCiGate.RunBuildSizeGate",
        "-buildSizeDir", $buildDir,
        "-logFile", $logPath
    )
    if ($BuildSizeUpdateBaseline) { $gateArgs += "-buildSizeUpdateBaseline" }
    if ($BuildSizeWarnOnly) { $gateArgs += "-buildSizeWarnOnly" }
    if ($BuildSizeBudgetMB -gt 0) { $gateArgs += @("-buildSizeBudgetMB", "$BuildSizeBudgetMB") }

    & $UnityPath @gateArgs
    $procExit = $LASTEXITCODE

    # 与资源门禁同款：以 ASCII 结论哨兵为准（batchmode 退出码不可靠），轮询等日志落盘。
    $verdict = $null
    for ($i = 0; $i -lt 150; $i++) {
        if (Test-Path $logPath) {
            $endLine = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match "\[BuildSizeGate\]\s+GATE_RESULT\s+exit=(\d+)" } | Select-Object -Last 1
            if ($endLine -and $endLine -match "GATE_RESULT\s+exit=(\d+)") {
                $verdict = [int]$Matches[1]
                break
            }
        }
        if ($i -gt 0 -and $i % 25 -eq 0) {
            Write-Host "等待包体门禁结论落盘... $([int]($i / 5))s"
        }
        Start-Sleep -Milliseconds 200
    }

    # 摘关键行（走 Write-Host，避免并入返回值污染 $gateExit）。
    Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "\[BuildSizeGate\]" } |
        Select-Object -Last 40 |
        ForEach-Object { Write-Host $_ }

    if ($null -ne $verdict) {
        if ($verdict -ne 0) {
            Write-Host "包体门禁未通过（BuildSizeGate exit=$verdict），详见日志: $logPath"
        }
        else {
            Write-Host "包体门禁通过。"
        }
        return $verdict
    }

    Write-Host "包体门禁未产出结论（大概率编译失败或异常），详见日志: $logPath"
    if ($procExit -eq 0) { return 1 } else { return $procExit }
}

Write-Host "== FrameworkBase CI =="
Write-Host "Unity   : $UnityPath"
Write-Host "Project : $projectPath"
Write-Host ("步骤    : 编译 + EditMode 测试" + `
    $(if ($SkipAssetGate) { "" } else { " + 资源门禁" }) + `
    $(if ($SkipPlayMode) { "" } else { " + PlayMode 冒烟" }) + `
    $(if ($TemplateSlice) { " + 模板切片 Play 验收" } else { "" }) + "（产物 $artifacts）")

# ── 干净副本可复现性预检：关键输入缺失或未纳入 Git 时直接阻断 ──────────
& (Join-Path $PSScriptRoot "check-reproducibility.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "工程可复现性预检未通过，跳过 Unity 测试。"
    exit 1
}

# ── asmdef 依赖门禁：分层/热更拓扑违规直接阻断（纯静态，先于 Unity）──────────
& (Join-Path $PSScriptRoot "check-asmdef-deps.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "asmdef 依赖门禁未通过，跳过 Unity 测试。"
    exit 1
}

# ── 依次跑（EditMode 先行：编译失败/逻辑用例挂了就不必再起后续）──
$finalExit = Invoke-UnityTests "EditMode"

# 资源门禁：EditMode 通过后执行（独立于测试，被阻断则整体失败）。
if ($finalExit -eq 0 -and -not $SkipAssetGate) {
    $gateExit = Invoke-AssetGate
    if ($gateExit -ne 0) { $finalExit = $gateExit }
}

# 包体门禁：仅当传入 -BuildSizeDir 时执行（构建后置检查，本仓库默认不出包故跳过）。
# batchmode 的 CWD 不确定，相对目录先解析成绝对路径再传，避免 Unity 侧找不到产物。
if ($finalExit -eq 0 -and $BuildSizeDir) {
    $absBuildDir = if ([System.IO.Path]::IsPathRooted($BuildSizeDir)) { $BuildSizeDir }
                   else { Join-Path $projectPath $BuildSizeDir }
    $sizeExit = Invoke-BuildSizeGate $absBuildDir
    if ($sizeExit -ne 0) { $finalExit = $sizeExit }
}

if ($finalExit -eq 0 -and -not $SkipPlayMode) {
    $finalExit = Invoke-UnityTests "PlayMode"
}
elseif ($finalExit -ne 0) {
    Write-Host ""
    Write-Host "前序步骤未通过，跳过 PlayMode。"
}

# 模板切片 Play 验收（切片 F）：整机端到端（启动壳/配表/玩法/存档 + 真实登录/切号隔离/互踢）。
# 耗时较长（两次 Unity 启动进 Play），仅 -TemplateSlice 显式开启（nightly/手动触发；PR 门禁默认不含）。
if ($finalExit -eq 0 -and $TemplateSlice) {
    & (Join-Path $PSScriptRoot "template-slice-check.ps1") -UnityPath $UnityPath
    if ($LASTEXITCODE -ne 0) { $finalExit = 1 }
}
elseif ($TemplateSlice -and $finalExit -ne 0) {
    Write-Host ""
    Write-Host "前序步骤未通过，跳过模板切片验收。"
}

Write-Host ""
if ($finalExit -eq 0) {
    Write-Host "== CI 通过 =="
}
else {
    Write-Host "== CI 未通过（Unity 退出码 $finalExit）=="
}
if ($null -eq $finalExit -or $finalExit -eq 0) { exit 0 }
exit 1
