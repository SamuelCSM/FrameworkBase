param(
    [switch]$AllowUntracked
)

$ErrorActionPreference = "Stop"
$projectRoot = (Get-Item $PSScriptRoot).Parent.Parent.FullName
Set-Location $projectRoot

$required = @(
    "ProjectSettings/ProjectVersion.txt",
    "ProjectSettings/ProjectSettings.asset",
    "ProjectSettings/EditorBuildSettings.asset",
    "ProjectSettings/EditorSettings.asset",
    "ProjectSettings/QualitySettings.asset",
    "ProjectSettings/GraphicsSettings.asset",
    "ProjectSettings/HybridCLRSettings.asset",
    "Assets/AddressableAssetsData/AddressableAssetSettings.asset",
    "Assets/Resources/AppConfig.asset",
    "Packages/manifest.json",
    "Packages/packages-lock.json"
)

$errors = New-Object System.Collections.Generic.List[string]
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        $errors.Add("缺少必要文件：$path")
        continue
    }

    if (-not $AllowUntracked) {
        # 不用 --error-unmatch：它把结论写到 stderr，Windows PowerShell 5.1 在
        # $ErrorActionPreference=Stop 下会把 stderr 包装成终止错误，导致首个未入库文件
        # 直接中断脚本、打不出完整 FAIL 清单。ls-files 输出为空即未跟踪，纯 stdout 判定。
        $tracked = & git ls-files -- $path
        if (-not $tracked) {
            $errors.Add("必要文件尚未纳入 Git：$path")
        }
    }
}

& git check-ignore -q -- ProjectSettings/ProjectVersion.txt
if ($LASTEXITCODE -eq 0) {
    $errors.Add("ProjectSettings 仍被 .gitignore 排除")
}

# 内容级检查前先确认文件存在：文件缺失已在上方记录，这里不能因 Get-Content 抛异常
# 打断脚本，导致 FAIL 清单不完整。
if (Test-Path -LiteralPath "ProjectSettings/ProjectVersion.txt") {
    $versionLine = Get-Content "ProjectSettings/ProjectVersion.txt" |
        Where-Object { $_ -match "^m_EditorVersion:" } |
        Select-Object -First 1
    if (-not $versionLine) {
        $errors.Add("ProjectVersion.txt 缺少 m_EditorVersion")
    }
}

if (Test-Path -LiteralPath "ProjectSettings/HybridCLRSettings.asset") {
    $hybridText = Get-Content "ProjectSettings/HybridCLRSettings.asset" -Raw
    if ($hybridText -match "Blokus|Inventory|Battle|Skill|Buff") {
        $errors.Add("HybridCLRSettings 包含疑似具体游戏业务程序集配置")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "[Reproducibility] FAIL"
    $errors | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "[Reproducibility] PASS"
exit 0
