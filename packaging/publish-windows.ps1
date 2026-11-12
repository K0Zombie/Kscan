param([string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Destination) { $Destination = Join-Path $projectRoot 'artifacts' }
[xml]$props = Get-Content (Join-Path $projectRoot 'Directory.Build.props')
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$name = "kscan-$version-win-x64-portable"
$output = Join-Path $Destination $name
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($project in @('CLI/Kscan.Cli.csproj', 'GUI/Kscan.App.csproj')) {
    # ReadyToRun：Avalonia 依赖大量 JIT，预编译后常驻内存更小、启动更快（实测 GUI 约省 8 MB）
    & dotnet publish (Join-Path $projectRoot $project) -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
[IO.File]::WriteAllText((Join-Path $output 'portable'), '')
Copy-Item -LiteralPath (Join-Path $projectRoot 'GUI/Assets/portable-defaults.json') -Destination (Join-Path $output 'portable-defaults.json')

# ---- 用户手册 + 示例脚本（给用户看的那份，一起打包）----
$docsOut = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/使用说明.md') -Destination (Join-Path $docsOut '使用手册.md') -Force
# 手册里引用了 DESIGN.md（架构 / 设计），一起带上，链接才不会断
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/DESIGN.md') -Destination (Join-Path $docsOut 'DESIGN.md') -Force
$examplesOut = Join-Path $docsOut '示例脚本'
New-Item -ItemType Directory -Path $examplesOut -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs/examples') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $examplesOut $_.Name) -Force
}

$readme = @"
Kscan $version - Windows x64 portable

解压后运行 kscan-gui.exe。无需安装 .NET，无需管理员权限。
kscan.exe 是命令行工具，执行 kscan.exe help 查看用法。
支持扫码、生成码、截图识别、图片及 Markdown 文本贴图、可自定义全局热键。

== 使用手册 ==
docs\使用手册.md      完整说明书（Markdown，记事本 / VS Code / Typora 都能打开）
docs\示例脚本\        「执行命令」热键可直接用的脚本（微信文件归档、截图写笔记等）

便携配置：程序旁 data/settings.json；移动文件夹即可带走配置。
首次启动默认：F1 纯截图；Ctrl+F1 截图识别；F3 图片/Markdown 贴图；Ctrl+Alt+F5 显示主窗口；Ctrl+Alt+Backspace 框选排除应用。
默认值来自 portable-defaults.json，仅在 data/settings.json 不存在时使用；已有配置优先（包括主动清空的快捷键）。
静默启动默认关闭；最小化到托盘默认关闭；点击 X 隐藏到托盘默认开启。均可在设置页独立切换。
托盘图标可恢复窗口；托盘菜单或设置页的“退出程序”会彻底结束应用。
截图到剪贴板保存图片；截图并识别在单结果时复制识别内容。
不要只复制 exe，整个文件夹的 DLL 和 native 库必须一起保留。
删除 portable 标记并重启后，将使用系统用户配置目录。
同一用户同时只允许运行一个 kscan 实例（再次启动会唤醒已运行的实例）。
"@
[IO.File]::WriteAllText((Join-Path $output 'README.txt'), $readme, [Text.UTF8Encoding]::new($true))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $Destination "$name.zip"
if (Test-Path -LiteralPath $zip) { throw "Archive already exists: $zip" }
[IO.Compression.ZipFile]::CreateFromDirectory($output, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$sha = [Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($zip)
try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
finally { $stream.Dispose(); $sha.Dispose() }
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
Get-Item -LiteralPath $zip | Select-Object FullName, Length
Write-Output "SHA256 $hash"
