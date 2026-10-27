# Kscan 0.1.15 示例

这些是可复制、可运行的示例。所有输出建议先放到临时目录；文件归档默认复制，只有明确添加移动参数才移动原文件。

## 1. 二维码生成与识别自检（Windows）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\cli-demo.ps1 -OutputDirectory .\demo-output
```

脚本自动找到便携包根目录的 `kscan.exe`，生成二维码、SVG、Wi-Fi 码，再读取二维码核对文本，失败时返回非零退出码。可通过 `-Kscan` 指定其他路径。不会截图、联网或修改个人设置。

## 2. 截图识别到当天笔记（Windows）

1. 用 F1 截取带码的图片，或在标注后完成复制。
2. 运行下面的命令。`-STA` 用于访问 Windows 剪贴板。

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\shot-to-note.ps1 -Note .\notes
```

也可直接识别文件：

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\shot-to-note.ps1 -Shot .\demo-output\code.png -Note .\notes
```

笔记写入 `notes/yyyy-MM-dd.md`。没有图片或没有识别结果时不写笔记。Windows 版示例不再调用不受支持的 CLI 框选接口。

在软件的「执行命令」里，程序填 `powershell.exe`，参数填：

```text
-STA -NoProfile -ExecutionPolicy Bypass -File "D:\Tools\Kscan\docs\examples\shot-to-note.ps1" -Note "D:\Notes"
```

将路径改为实际位置，再为这个命令绑定自己的热键。不需要管理员权限。

## 3. 按日期和类型归档（Windows）

脚本文件沿用 `wechat-archive.ps1` 名称，但不猜测微信或下载目录。用 `-Source` 明确指定任意文件目录，目标不能放在来源目录里面。

```powershell
# 只预览，不创建目录、不复制文件
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\wechat-archive.ps1 -Source .\inbox -Destination .\archive -WhatIf

# 默认复制，保留来源
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\wechat-archive.ps1 -Source .\inbox -Destination .\archive

# 明确移动，可先加 -WhatIf 检查
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\wechat-archive.ps1 -Source .\inbox -Destination .\archive -Move
```

输出结构为 `archive/yyyy-MM/类别/文件名`；`-ByDay` 改为按天。类别使用英文目录名，中文文件名保留。重复文件按 SHA-256 内容判断；同名不同内容自动加数字后缀，不覆盖。`.tmp`、`.part`、`.crdownload` 跳过。移动模式遇到已存在相同内容的目标时保留来源，不擅自删除。

## 4. Linux 示例

需要 Bash 4+ 及 GNU 基础工具；截图需要 Wayland 下的 grim、slurp，解码需要可运行的 Linux `kscan`。不适用于直接调用 Windows 版 exe 做 Linux 截图。

```bash
bash docs/examples/shot-to-note.sh -k /path/to/kscan -i code.png -n ./notes
# 不给 -i 时用 grim + slurp 框选
bash docs/examples/shot-to-note.sh -k /path/to/kscan -n ./notes

bash docs/examples/wechat-archive.sh -s ./inbox -t ./archive -n
bash docs/examples/wechat-archive.sh -s ./inbox -t ./archive
bash docs/examples/wechat-archive.sh -s ./inbox -t ./archive -m
```

归档同样默认复制，`-n` 预览，`-m` 明确移动。示例不自动搜索客户端私有目录。

## 5. 用户主题示例

把 [ocean-theme.json](ocean-theme.json) 复制到当前便携配置的 `data/user/themes/`，然后在设置中选择「海蓝」。界面、独立标注及快速标注工具栏使用同一套主题；更改 JSON 会重新应用。不要把文件当作 `settings.json`。

## 文件与执行环境

Windows `.ps1` 使用 UTF-8 BOM，兼容 Windows PowerShell 5.1。Linux `.sh` 使用 UTF-8、LF 换行。源码包附带脚本实现，便携包附带同一份示例，不包含运行时的个人笔记、截图或归档目录。
