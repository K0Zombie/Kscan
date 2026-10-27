<#
.SYNOPSIS
读取指定图片或 Windows 剪贴板图片，将识别结果追加到当天笔记。
.EXAMPLE
powershell.exe -STA -NoProfile -File shot-to-note.ps1 -Shot code.png -Note .\notes
#>
[CmdletBinding()]
param([string]$Kscan, [string]$Note = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'notes'), [string]$Shot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
if (-not $Kscan) {
    foreach ($candidate in @((Join-Path $PSScriptRoot '..\..\kscan.exe'), (Join-Path $PSScriptRoot 'kscan.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $Kscan = (Resolve-Path -LiteralPath $candidate).Path; break }
    }
}
if (-not $Kscan) {
    $command = Get-Command kscan.exe -ErrorAction SilentlyContinue
    if ($command) { $Kscan = $command.Source }
}
if (-not $Kscan) { throw '找不到 kscan.exe，请使用 -Kscan 指定。' }
$temporary = $null
try {
    if (-not $Shot) {
        if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw '读取剪贴板需要 powershell.exe -STA。也可以使用 -Shot 指定文件。' }
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $image = [Windows.Forms.Clipboard]::GetImage()
        if ($null -eq $image) { Write-Output '剪贴板没有图片，请先用 F1 截图或指定 -Shot。'; exit 1 }
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('kscan-note-' + [Guid]::NewGuid().ToString('N') + '.png')
        try { $image.Save($temporary, [Drawing.Imaging.ImageFormat]::Png) } finally { $image.Dispose() }
        $Shot = $temporary
    }
    if (-not (Test-Path -LiteralPath $Shot -PathType Leaf)) { throw "图片不存在：$Shot" }
    $lines = @(& $Kscan decode $Shot)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) { Write-Output '没有识别到码，未写入笔记。'; exit 1 }
    $Note = [IO.Path]::GetFullPath($Note)
    [void][IO.Directory]::CreateDirectory($Note)
    $file = Join-Path $Note ((Get-Date -Format 'yyyy-MM-dd') + '.md')
    $quoted = (($lines -join "`n") -split '\r?\n' | ForEach-Object { '> ' + $_ }) -join [Environment]::NewLine
    $entry = '## ' + (Get-Date -Format 'HH:mm:ss') + [Environment]::NewLine + [Environment]::NewLine + $quoted + [Environment]::NewLine + [Environment]::NewLine
    [IO.File]::AppendAllText($file, $entry, [Text.UTF8Encoding]::new($false))
    Write-Output "已追加到：$file"
}
finally {
    if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Force }
}
