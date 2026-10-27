<#
.SYNOPSIS
生成并识别示例二维码，验证便携版 CLI。
#>
[CmdletBinding()]
param([string]$Kscan, [string]$OutputDirectory = (Join-Path (Get-Location) 'demo-output'))
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
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$payload = 'https://example.com/kscan-demo'
$png = Join-Path $OutputDirectory 'code.png'
& $Kscan gen $payload --size 480 --ecc H -o $png
if ($LASTEXITCODE -ne 0) { throw '二维码生成失败。' }
$decoded = @(& $Kscan decode $png)
if ($LASTEXITCODE -ne 0 -or ($decoded -join "`n").Trim() -ne $payload) { throw '二维码识别结果与原文不一致。' }
& $Kscan gen $payload --svg -o (Join-Path $OutputDirectory 'code.svg')
if ($LASTEXITCODE -ne 0) { throw 'SVG 生成失败。' }
& $Kscan build wifi --ssid DemoWiFi --password example-password -o (Join-Path $OutputDirectory 'wifi.png')
if ($LASTEXITCODE -ne 0) { throw 'Wi-Fi 码生成失败。' }
Write-Output "验证通过。输出目录：$OutputDirectory"
