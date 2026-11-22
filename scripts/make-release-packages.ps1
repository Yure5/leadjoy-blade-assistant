# 生成 GitHub Release 用的两个便携包：clean（无配置）与 sample（示例配置）
# 用法： powershell -ExecutionPolicy Bypass -File scripts\make-release-packages.ps1 -Version 0.8.81
param(
    [string]$Version = "0.8.81",
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$SampleSource = "C:\Users\Administrator\Desktop\风刃手柄助手"
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'build\app\LeadjoyBlade.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先 publish。" }

$outDir = Join-Path $Root 'dist\release'
$common = @(
    @{ Src = 'docs\使用说明.pdf';       Dst = '使用说明.pdf' },
    @{ Src = 'docs\使用说明.md';        Dst = '使用说明.md' },
    @{ Src = 'docs\INPUT-METHOD.md';    Dst = '手柄输入法说明.md' },
    @{ Src = 'docs\SYSTEM-MENU.md';     Dst = '系统菜单说明.md' },
    @{ Src = 'LICENSE';                 Dst = 'LICENSE-GPL-3.0.txt' },
    @{ Src = 'NOTICE.md';               Dst = 'NOTICE.md' },
    @{ Src = 'THIRD-PARTY-NOTICES.md';  Dst = 'THIRD-PARTY-NOTICES.md' },
    @{ Src = 'src\LeadjoyBlade.Binput\LICENSE-Binput-GPL-3.0.txt'; Dst = 'LICENSE-Binput-GPL-3.0.txt' }
)

function New-Package {
    param([string]$Suffix, [switch]$WithSample)

    $name = "LeadjoyBlade-V$Version-win-x64-$Suffix"
    $stage = Join-Path $outDir $name
    $zip = Join-Path $outDir "$name.zip"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Copy-Item $exe (Join-Path $stage 'LeadjoyBlade.exe')
    foreach ($item in $common) {
        $src = Join-Path $Root $item.Src
        if (Test-Path $src) { Copy-Item $src (Join-Path $stage $item.Dst) }
        else { Write-Warning "缺少 $($item.Src)" }
    }
    Get-ChildItem (Join-Path $Root 'docs\images') -Filter *.png -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $stage $_.Name) }

    if ($WithSample) {
        $profiles = Join-Path $stage 'profiles'
        New-Item -ItemType Directory -Path $profiles -Force | Out-Null
        Get-ChildItem (Join-Path $SampleSource 'profiles') -Filter *.json -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $profiles $_.Name) }
        foreach ($f in 'fantasy-library.json', 'macro-library.json') {
            $src = Join-Path $SampleSource $f
            if (Test-Path $src) { Copy-Item $src (Join-Path $stage $f) }
        }
        # 示例配置使用独立设置：不开机自启，默认加载「默认配置」
        $settingsPath = Join-Path $stage 'settings.json'
        $settings = Get-Content (Join-Path $SampleSource 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $settings.StartWithWindows = $false
        $settings.LastProfile = '默认配置'
        $settings.StartWithMappingEnabled = $false
        $json = $settings | ConvertTo-Json -Depth 6
        [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    }
    else {
        # clean 包：带一个空配置，避免首次启动生成默认映射/组合键
        $clean = Join-Path $Root 'scripts\clean-config'
        $profiles = Join-Path $stage 'profiles'
        New-Item -ItemType Directory -Path $profiles -Force | Out-Null
        Get-ChildItem (Join-Path $clean 'profiles') -Filter *.json |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $profiles $_.Name) }
        foreach ($f in 'settings.json', 'fantasy-library.json', 'macro-library.json') {
            Copy-Item (Join-Path $clean $f) (Join-Path $stage $f)
        }
    }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "已生成：$zip"
    "大小：{0:N1} MB  SHA256：{1}" -f ((Get-Item $zip).Length / 1MB), $hash
}

New-Package -Suffix 'clean'
New-Package -Suffix 'sample' -WithSample
