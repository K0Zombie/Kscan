<#
.SYNOPSIS
按日期和类别归档指定目录，默认复制；-Move 才移动，-WhatIf 仅预览。
.EXAMPLE
./wechat-archive.ps1 -Source ./inbox -Destination ./archive -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [switch]$Move, [switch]$ByDay, [string]$LogPath
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd([char[]]'\/')
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw 'Source 必须是目录。' }
$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd([char[]]'\/')
if ($Destination.Equals($Source, [StringComparison]::OrdinalIgnoreCase) -or
    $Destination.StartsWith($Source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '目标目录不能等于来源目录，也不能放在来源目录里面。'
}
function Get-Category([string]$extension) {
    switch -Regex ($extension.ToLowerInvariant()) {
        '^\.(png|jpg|jpeg|gif|bmp|webp|heic|tiff?)$' { return 'images' }
        '^\.(pdf|docx?|xlsx?|pptx?|txt|md|rtf|csv|odt)$' { return 'documents' }
        '^\.(zip|rar|7z|tar|gz|xz|bz2)$' { return 'archives' }
        '^\.(mp3|wav|flac|m4a|mp4|mov|avi|mkv|webm)$' { return 'media' }
        '^\.(ps1|sh|py|js|ts|cs|json|xml|html|css|ya?ml)$' { return 'code' }
        default { return 'other' }
    }
}
$processed = 0
$skipped = 0
$files = @(Get-ChildItem -LiteralPath $Source -File -Recurse)
foreach ($file in $files) {
    if ($file.Extension -match '^\.(tmp|part|crdownload)$') { $skipped++; continue }
    $date = if ($ByDay) { $file.LastWriteTime.ToString('yyyy-MM-dd') } else { $file.LastWriteTime.ToString('yyyy-MM') }
    $folder = Join-Path (Join-Path $Destination $date) (Get-Category $file.Extension)
    $target = Join-Path $folder $file.Name
    $sourceHash = $null
    $duplicate = $false
    $counter = 0
    while (Test-Path -LiteralPath $target) {
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            if (-not $sourceHash) { $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $sourceHash) { $duplicate = $true; break }
        }
        $counter++
        $target = Join-Path $folder ($file.BaseName + '-' + $counter + $file.Extension)
    }
    if ($duplicate) { $skipped++; continue }
    $operation = if ($Move) { 'Move' } else { 'Copy' }
    if ($PSCmdlet.ShouldProcess($file.FullName, "$operation -> $target")) {
        # 所有移动都使用已解析的绝对来源和显式目标，保持在同一 PowerShell 中执行。
        [void][IO.Directory]::CreateDirectory($folder)
        if ($Move) { Move-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop }
        else { Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop }
        $processed++
        if ($LogPath) {
            $log = [IO.Path]::GetFullPath($LogPath)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($log))
            [IO.File]::AppendAllText($log, "$operation`t$($file.FullName)`t$target`n", [Text.UTF8Encoding]::new($false))
        }
    }
}
Write-Output "处理 $processed 项，跳过 $skipped 项。来源已存在的相同内容不会被额外删除。"
