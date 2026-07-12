# FrameworkBase asmdef 依赖门禁（纯静态校验，不需要 Unity / License，秒级）。
#
# 把 ADR-001/002/004 固化的分层与热更拓扑约束焊成 CI 强约束，防止依赖方向回退：
#   R1 无环：项目自有 asmdef 依赖图不得成环。
#   R2 分层单向：Framework 核心层只能向下引用（Foundation/Protocol.Abstractions < Kernel < Framework < Editor）；
#      核心层不得引用测试 / 热更 / 业务程序集。
#   R3 热更协议保持轻量（ADR-004）：被其他热更程序集依赖的“协议层”热更程序集不得引用重型 Framework
#      （只能引 Protocol.Abstractions / Foundation / Kernel 等轻量层）。
#   R4 热更清单一致：HybridCLRSettings.hotUpdateAssemblies 中每个名字都须存在对应工程 asmdef；
#      AppConfig.HotUpdateAssemblyFiles 非空时，其（去 .dll.bytes 后）集合须与 HybridCLRSettings 一致；
#      有效热更入口程序集（AppConfig.HotUpdateEntryAssembly 优先，回退 VersionManager 默认）须为热更清单成员。
#   R5 测试程序集 autoReferenced=false（避免被 Assembly-CSharp 自动引用污染）。
#
# 用法：
#   pwsh -File Tools/ci/check-asmdef-deps.ps1          # 校验，违规则 exit 1
#
# 退出码：0 = 全部通过；1 = 存在违规（明细打印到 stdout）。
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

# Tools\ci → 工程根
$root = (Get-Item $PSScriptRoot).Parent.Parent.FullName

# ── 框架自有分层（名字稳定、框架自持，故在门禁内硬编码 rank；业务/热更名一律从数据读取）──
$tierRank = @{
    "Framework.Foundation"            = 0
    "Framework.Protocol.Abstractions" = 0
    "Framework.Kernel"                = 1
    "Framework"                       = 2
    "Framework.Editor"                = 3
}
$runtimeCoreAsm = "Framework"   # 重型运行时主包（拖 Addressables/HybridCLR/TMP）

$violations = New-Object System.Collections.Generic.List[string]
function Add-Violation([string]$rule, [string]$msg) {
    $script:violations.Add(("[{0}] {1}" -f $rule, $msg))
}

# ── 1) 收集工程自有 asmdef（排除 Library/PackageCache/Temp 下的外部包）──────────
$asmFiles = Get-ChildItem -Path $root -Recurse -Filter *.asmdef -File |
    Where-Object {
        $_.FullName -notmatch "[\\/]Library[\\/]" -and
        $_.FullName -notmatch "[\\/]Temp[\\/]"
    }

$asms = @{}        # name -> record
$nameByGuid = @{}  # meta guid -> asm name

foreach ($f in $asmFiles) {
    $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $name = [string]$json.name
    if ([string]::IsNullOrWhiteSpace($name)) { continue }

    $refs = @()
    if ($null -ne $json.references) { $refs = @($json.references) }

    $constraints = @()
    if ($null -ne $json.defineConstraints) { $constraints = @($json.defineConstraints) }

    $autoRef = $true
    if ($null -ne $json.autoReferenced) { $autoRef = [bool]$json.autoReferenced }

    $rec = [pscustomobject]@{
        Name        = $name
        Path        = $f.FullName
        Refs        = $refs
        Constraints = $constraints
        AutoRef     = $autoRef
    }
    if ($asms.ContainsKey($name)) {
        Add-Violation "DUP" "程序集名重复：$name（$($f.FullName) 与 $($asms[$name].Path)）"
    }
    $asms[$name] = $rec

    $metaPath = "$($f.FullName).meta"
    if (Test-Path $metaPath) {
        $g = (Get-Content $metaPath | Where-Object { $_ -match "^guid:\s*([0-9a-fA-F]+)" } | Select-Object -First 1)
        if ($g -and $g -match "guid:\s*([0-9a-fA-F]+)") { $nameByGuid[$Matches[1]] = $name }
    }
}

$projectNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($k in $asms.Keys) { [void]$projectNames.Add($k) }

# 引用名解析：支持 "GUID:xxxx" 与明文名；只保留解析到“工程自有程序集”的边（外部包忽略）。
function Resolve-ProjectRefs($rec) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($r in $rec.Refs) {
        $rn = [string]$r
        if ($rn -match "^GUID:([0-9a-fA-F]+)$") {
            $g = $Matches[1]
            if ($nameByGuid.ContainsKey($g)) { $rn = $nameByGuid[$g] } else { continue }
        }
        if ($projectNames.Contains($rn)) { [void]$out.Add($rn) }
    }
    return ,$out
}

# ── 2) 解析 HybridCLRSettings 热更程序集集合 ───────────────────────────────────
$hotUpdate = [System.Collections.Generic.HashSet[string]]::new()
$hybridPath = Join-Path $root "ProjectSettings/HybridCLRSettings.asset"
if (Test-Path $hybridPath) {
    $lines = Get-Content $hybridPath
    $inList = $false
    foreach ($ln in $lines) {
        if ($ln -match "^\s*hotUpdateAssemblies:\s*(\[\])?\s*$") {
            $inList = ($ln -notmatch "\[\]")  # 显式 [] 表示空
            continue
        }
        if ($inList) {
            if ($ln -match "^\s*-\s*(.+?)\s*$") { [void]$hotUpdate.Add($Matches[1].Trim()) }
            elseif ($ln -match "^\S") { $inList = $false }  # 缩进结束
        }
    }
}
else {
    Add-Violation "R4" "未找到 ProjectSettings/HybridCLRSettings.asset。"
}

# ── 3) 解析 AppConfig.HotUpdateAssemblyFiles（空则回退框架默认，不做等值比较）────
$appCfgFiles = New-Object System.Collections.Generic.List[string]
$appCfgHasList = $false
$appCfgPath = Join-Path $root "Assets/Resources/AppConfig.asset"
if (Test-Path $appCfgPath) {
    $lines = Get-Content $appCfgPath
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        if ($ln -match "^\s*HotUpdateAssemblyFiles:\s*(\[\])?\s*$") {
            if ($ln -notmatch "\[\]") {
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    if ($lines[$j] -match "^\s*-\s*(.+?)\s*$") {
                        $appCfgHasList = $true
                        $appCfgFiles.Add($Matches[1].Trim())
                    }
                    elseif ($lines[$j] -match "^\S") { break }
                    else { break }
                }
            }
            break
        }
    }
}

function Strip-BytesSuffix([string]$n) {
    $n = $n -replace "\.bytes$", ""
    $n = $n -replace "\.dll$", ""
    return $n
}

# ── 3b) 解析热更入口程序集名（配置优先，回退 VersionManager 的 C# 默认）──────────
# AppConfig.HotUpdateEntryAssembly（标量，空即回退）
$appCfgEntry = ""
if (Test-Path $appCfgPath) {
    $m = Select-String -Path $appCfgPath -Pattern "^\s*HotUpdateEntryAssembly:\s*(\S.*?)\s*$" | Select-Object -First 1
    if ($m) { $appCfgEntry = $m.Matches[0].Groups[1].Value.Trim() }
}
# VersionManager.DefaultCodePatchFileName（单行 const，作为默认入口来源；解析不到则跳过 C# 侧校验）
$vmDefaultEntry = ""
$vmPath = Join-Path $root "Packages/com.frameworkbase.core/HotUpdate/VersionManager.cs"
if (Test-Path $vmPath) {
    $m = Select-String -Path $vmPath -Pattern 'DefaultCodePatchFileName\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($m) { $vmDefaultEntry = Strip-BytesSuffix ($m.Matches[0].Groups[1].Value) }
}

# ── R1 无环 ───────────────────────────────────────────────────────────────────
$WHITE = 0; $GRAY = 1; $BLACK = 2
$color = @{}
foreach ($n in $projectNames) { $color[$n] = $WHITE }
$cycleReported = $false
function Visit-Node([string]$n, [System.Collections.Generic.List[string]]$stack) {
    if ($script:cycleReported) { return }
    $script:color[$n] = $GRAY
    $stack.Add($n)
    foreach ($m in (Resolve-ProjectRefs $asms[$n])) {
        if ($script:color[$m] -eq $GRAY) {
            $idx = $stack.IndexOf($m)
            $cyc = ($stack.GetRange($idx, $stack.Count - $idx) + $m) -join " -> "
            Add-Violation "R1" "检测到环依赖：$cyc"
            $script:cycleReported = $true
            return
        }
        elseif ($script:color[$m] -eq $WHITE) {
            Visit-Node $m $stack
            if ($script:cycleReported) { return }
        }
    }
    $stack.RemoveAt($stack.Count - 1)
    $script:color[$n] = $BLACK
}
foreach ($n in $projectNames) {
    if ($color[$n] -eq $WHITE) { Visit-Node $n (New-Object System.Collections.Generic.List[string]) }
}

# ── R2 分层单向 + 核心层不得引用测试/热更/业务 ────────────────────────────────
function Test-IsTestAsm($rec) {
    if ($rec.Constraints -contains "UNITY_INCLUDE_TESTS") { return $true }
    foreach ($r in $rec.Refs) {
        if ($r -eq "UnityEngine.TestRunner" -or $r -eq "UnityEditor.TestRunner") { return $true }
    }
    if ($rec.Name -match "\.Tests(\.|$)") { return $true }
    return $false
}

foreach ($n in $projectNames) {
    $rec = $asms[$n]
    $isCore = $tierRank.ContainsKey($n)
    foreach ($m in (Resolve-ProjectRefs $rec)) {
        # 分层 rank：核心层只能引用严格更低 rank 的核心层
        if ($isCore -and $tierRank.ContainsKey($m)) {
            if ($tierRank[$m] -ge $tierRank[$n]) {
                Add-Violation "R2" "分层违规：$n(rank $($tierRank[$n])) 不得引用同层或更高层 $m(rank $($tierRank[$m]))"
            }
        }
        # 核心层不得引用热更 / 业务
        if ($isCore -and $hotUpdate.Contains($m)) {
            Add-Violation "R2" "核心层 $n 不得引用热更程序集 $m（主包禁止依赖热更代码）"
        }
        # 核心层不得引用测试程序集
        if ($isCore -and (Test-IsTestAsm $asms[$m])) {
            Add-Violation "R2" "核心层 $n 不得引用测试程序集 $m"
        }
    }
}

# ── R3 热更协议保持轻量：被其他热更程序集依赖的热更程序集不得引用重型 Framework ──
# 计算“被热更集合内其他成员依赖”的成员（协议/共享层），它们必须脱离重型 Framework。
$dependedWithinHot = [System.Collections.Generic.HashSet[string]]::new()
foreach ($n in $hotUpdate) {
    if (-not $asms.ContainsKey($n)) { continue }
    foreach ($m in (Resolve-ProjectRefs $asms[$n])) {
        if ($hotUpdate.Contains($m)) { [void]$dependedWithinHot.Add($m) }
    }
}
foreach ($n in $dependedWithinHot) {
    $refs = Resolve-ProjectRefs $asms[$n]
    if ($refs -contains $runtimeCoreAsm) {
        Add-Violation "R3" "热更协议/共享程序集 $n 不得引用重型 $runtimeCoreAsm（ADR-004：只能引 Framework.Protocol.Abstractions 等轻量层）"
    }
}

# ── R4 热更清单一致 ───────────────────────────────────────────────────────────
foreach ($n in $hotUpdate) {
    if (-not $projectNames.Contains($n)) {
        Add-Violation "R4" "HybridCLRSettings.hotUpdateAssemblies 声明的 $n 没有对应工程 asmdef（可能改名后漏同步）"
    }
}
if ($appCfgHasList) {
    $appSet = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($f in $appCfgFiles) { [void]$appSet.Add((Strip-BytesSuffix $f)) }
    $onlyApp = @($appSet) | Where-Object { -not $hotUpdate.Contains($_) }
    $onlyHy  = @($hotUpdate) | Where-Object { -not $appSet.Contains($_) }
    if ($onlyApp.Count -gt 0 -or $onlyHy.Count -gt 0) {
        Add-Violation "R4" ("AppConfig.HotUpdateAssemblyFiles 与 HybridCLRSettings 不一致：仅 App=[{0}] 仅 Hybrid=[{1}]" -f ($onlyApp -join ","), ($onlyHy -join ","))
    }
}
# 入口程序集一致：有效入口（AppConfig 配置优先，回退 VersionManager 默认）须为热更清单成员，
# 否则加载完成后必反射不到入口类型。解析不到 C# 默认（$vmDefaultEntry 为空）时跳过，交由
# EditMode 的 VersionManagerTests 用编译真相兜底。
if ($appCfgEntry) { $effectiveEntry = $appCfgEntry } else { $effectiveEntry = $vmDefaultEntry }
if ($effectiveEntry -and $hotUpdate.Count -gt 0 -and -not $hotUpdate.Contains($effectiveEntry)) {
    Add-Violation "R4" ("热更入口程序集 {0} 不在 HybridCLRSettings.hotUpdateAssemblies=[{1}] 中（入口须为热更清单成员）" -f $effectiveEntry, (@($hotUpdate) -join ","))
}

# ── R5 测试程序集 autoReferenced=false ────────────────────────────────────────
foreach ($n in $projectNames) {
    $rec = $asms[$n]
    if ((Test-IsTestAsm $rec) -and $rec.AutoRef) {
        Add-Violation "R5" "测试程序集 $n 的 autoReferenced 必须为 false"
    }
}

# ── 结论 ──────────────────────────────────────────────────────────────────────
Write-Host "== asmdef 依赖门禁 =="
Write-Host ("工程自有程序集: {0} 个；热更程序集: [{1}]" -f $projectNames.Count, (@($hotUpdate) -join ", "))
if ($violations.Count -eq 0) {
    Write-Host "[AsmdefGate] PASS —— R1..R5 全部通过。"
    exit 0
}
Write-Host "[AsmdefGate] FAIL —— 违规 $($violations.Count) 项："
foreach ($v in $violations) { Write-Host "  $v" }
exit 1
