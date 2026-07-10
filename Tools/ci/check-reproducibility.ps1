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
        & git ls-files --error-unmatch -- $path *> $null
        if ($LASTEXITCODE -ne 0) {
            $errors.Add("必要文件尚未纳入 Git：$path")
        }
    }
}

& git check-ignore -q -- ProjectSettings/ProjectVersion.txt
if ($LASTEXITCODE -eq 0) {
    $errors.Add("ProjectSettings 仍被 .gitignore 排除")
}

$versionLine = Get-Content "ProjectSettings/ProjectVersion.txt" |
    Where-Object { $_ -match "^m_EditorVersion:" } |
    Select-Object -First 1
if (-not $versionLine) {
    $errors.Add("ProjectVersion.txt 缺少 m_EditorVersion")
}

$hybridText = Get-Content "ProjectSettings/HybridCLRSettings.asset" -Raw
if ($hybridText -match "Blokus|Inventory|Battle|Skill|Buff") {
    $errors.Add("HybridCLRSettings 包含疑似具体游戏业务程序集配置")
}

if ($errors.Count -gt 0) {
    Write-Host "[Reproducibility] FAIL"
    $errors | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "[Reproducibility] PASS"
exit 0
