# FrameworkBase banned-API 静态门禁（纯文本扫描，无需 Unity，PS 5.1 兼容）。
#
# 扫描范围：框架运行时代码（Packages/com.frameworkbase.core 除 Editor/、Tests/）
#           + 游戏侧运行时（Assets/Scripts）。
# Editor / Tests 不扫：编辑器工具与测试里这些 API 各有正当用途，规则只管跑在玩家设备上的代码。
#
# 规则（新增规则先全量扫描存量、逐处豁免或整改后再入门禁，别上来就红）：
#   local-time   —— DateTime.Now / DateTimeOffset.Now：本地时钟可被玩家改表，跨设备不可比。
#                   逻辑判定用 UtcNow 或 ServerTime；确属本地展示（日志时间戳、免打扰时段）行内豁免。
#   thread-sleep —— Thread.Sleep：主线程卡帧、线程池线程占坑。用定时器/UniTask.Delay；
#                   专用后台线程的节流循环行内豁免。
#   gc-collect   —— GC.Collect：全量 GC 集中卡顿。只允许加载屏/调试命令等明确遮蔽时机，行内豁免。
#
# 豁免：违规行或其上一行包含 `banned-api-allow: <规则id> <理由>`。
# 豁免随代码进评审——理由写不出来的豁免通不过评审，这正是设计意图。

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$rules = @(
    @{ Id = "local-time";   Pattern = '\bDateTime(Offset)?\.Now\b';  Advice = "用 DateTimeOffset.UtcNow 或 ServerTime.Now；确属本地展示则行内豁免" }
    @{ Id = "thread-sleep"; Pattern = '\bThread\.Sleep\s*\(';        Advice = "用定时器 / UniTask.Delay；专用后台线程节流则行内豁免" }
    @{ Id = "gc-collect";   Pattern = '\bGC\.Collect\s*\(';          Advice = "只允许加载屏 / 调试命令等遮蔽时机，行内豁免并写明时机" }
)

# 正斜杠两平台都认（ci.yml 的静态门禁 job 跑在 ubuntu）
$scanRoots = @(
    (Join-Path $repoRoot "Packages/com.frameworkbase.core"),
    (Join-Path $repoRoot "Assets/Scripts")
)

$files = @()
foreach ($root in $scanRoots) {
    if (-not (Test-Path $root)) { continue }
    $files += Get-ChildItem -Path $root -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '[\\/]Editor[\\/]' -and $_.FullName -notmatch '[\\/]Tests[\\/]' }
}

Write-Host "== banned-API 静态门禁 =="
Write-Host ("扫描 {0} 个运行时源文件，规则 {1} 条。" -f $files.Count, $rules.Count)

$violations = 0
foreach ($file in $files) {
    $lines = Get-Content -Path $file.FullName -Encoding UTF8
    if ($null -eq $lines) { continue }
    if ($lines -is [string]) { $lines = @($lines) }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $trimmed = $line.TrimStart()
        if ($trimmed.StartsWith("//")) { continue }  # 注释里提到被禁 API 无害

        foreach ($rule in $rules) {
            if ($line -notmatch $rule.Pattern) { continue }

            $allowToken = "banned-api-allow: " + $rule.Id
            $exempt = $line.Contains($allowToken)
            if (-not $exempt -and $i -gt 0) {
                $exempt = $lines[$i - 1].Contains($allowToken)
            }
            if ($exempt) { continue }

            $relative = $file.FullName.Substring($repoRoot.Length + 1)
            Write-Host ("[banned-api] {0}:{1} 命中 {2}" -f $relative, ($i + 1), $rule.Id)
            Write-Host ("    {0}" -f $line.Trim())
            Write-Host ("    整改：{0}" -f $rule.Advice)
            $violations++
        }
    }
}

if ($violations -gt 0) {
    Write-Host ("banned-API 门禁未通过：{0} 处违规。豁免须行内 banned-api-allow: <规则id> <理由>。" -f $violations)
    exit 1
}

Write-Host "banned-API 门禁通过（PASS）。"
exit 0
