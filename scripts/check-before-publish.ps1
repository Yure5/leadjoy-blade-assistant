# 发布前自检：个人路径、蓝牙地址、令牌、超大文件
# 用法： powershell -ExecutionPolicy Bypass -File scripts\check-before-publish.ps1
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Continue'
Set-Location $Root

$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path (Join-Path $Root 'tools\git\cmd\git.exe'))) {
    $git = Join-Path $Root 'tools\git\cmd\git.exe'
}

Write-Host "== 检查目录：$Root ==" -ForegroundColor Cyan

$trackedList = @()
if ($git) {
    $trackedList = @(& $git -c core.quotepath=false ls-files 2>$null)
}

Write-Host "`n== 1. 敏感文件是否入库 ==" -ForegroundColor Cyan
$mustIgnore = @('settings.json', 'profiles', 'save', 'logs', 'startup.log', 'crash.log',
                'HANDOFF.md', 'HANDOFF-QUICK.md', 'HANDOFF-2026-11-18.md', 'HANDOFF-SWITCH-REMOTE.md')
$leaks = @()
foreach ($name in $mustIgnore) {
    foreach ($t in $trackedList) {
        if ($t -eq $name -or $t -like "$name/*") { $leaks += $name; break }
    }
}
if ($leaks.Count -gt 0) {
    Write-Host "[!] 以下内容已被 git 跟踪，请执行 git rm --cached 移除：" -ForegroundColor Red
    $leaks | ForEach-Object { Write-Host "    $_" }
} else {
    Write-Host "[OK] 个人配置 / 日志 / 交接文档未入库"
}

Write-Host "`n== 2. 硬编码路径与令牌 ==" -ForegroundColor Cyan
$patterns = @(
    'C:\\Users\\[A-Za-z0-9_\-\.]+',
    'Administrator',
    'ghp_[A-Za-z0-9]{20,}',
    'github_pat_',
    'Bearer\s+[A-Za-z0-9_\-\.]{20,}'
)
$skip = '\\(bin|obj|tools|third_party|backups|dist|build|tmp|logs|appdata|localappdata|nuget|dotnet-home|previews|analysis)\\'
$textFiles = $trackedList |
    Where-Object { $_ -match '\.(cs|xaml|md|json|ps1|csproj|yml|txt)$' -and $_ -ne 'scripts/check-before-publish.ps1' } |
    ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_ -and $_.FullName -notmatch $skip }

$hits = @()
foreach ($f in $textFiles) {
    $content = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    foreach ($p in $patterns) {
        if ($content -match $p) {
            $hits += [pscustomobject]@{ File = $f.FullName.Replace($Root + '\', ''); Pattern = $p }
            break
        }
    }
}
if ($hits.Count -gt 0) {
    Write-Host "[!] 发现可疑内容（请人工确认）" -ForegroundColor Yellow
    $hits | Format-Table -AutoSize
} else {
    Write-Host "[OK] 未发现本机路径 / 令牌"
}

Write-Host "`n== 3. 大文件（> 5 MB）==" -ForegroundColor Cyan
$big = Get-ChildItem -Recurse -File |
    Where-Object { $_.Length -gt 5MB -and $_.FullName -notmatch $skip }
if ($big.Count -gt 0) {
    $big | Select-Object @{n='文件';e={$_.FullName.Replace($Root + '\', '')}}, @{n='大小MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
} else {
    Write-Host "[OK] 没有超过 5 MB 的待提交文件"
}

Write-Host "`n== 4. 仓库规模 ==" -ForegroundColor Cyan
if ($trackedList.Count -gt 0) {
    $total = 0
    foreach ($t in $trackedList) {
        $item = Get-Item -LiteralPath $t -ErrorAction SilentlyContinue
        if ($item) { $total += $item.Length }
    }
    "已跟踪文件：{0} 个，合计 {1:N1} MB" -f $trackedList.Count, ($total / 1MB)
} else {
    "尚未 git init 或没有已跟踪文件"
}

Write-Host "`n完成。请人工再看一遍上面标黄 / 标红的内容。" -ForegroundColor Green
