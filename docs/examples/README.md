# Kscan 示例与集成

操作手册已统一到 [帮助目录](../manual/index.md)。此目录保留可运行示例，输出请先放到临时目录验证，脚本不会自动查找聊天软件私有目录。

## Windows CLI 自检

在完整便携包根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\cli-demo.ps1 -OutputDirectory .\demo-output
```

脚本生成二维码、SVG、Wi-Fi 码并解码核对；可用 -Kscan 指定命令行程序路径。失败返回非零退出码，不截图、不联网、不修改个人配置。

## 截图识别到笔记

F1 截图后运行下例；-STA 用于 Windows 剪贴板：

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\shot-to-note.ps1 -Note .\notes
```

可加 -Shot 指定图片。结果追加到当天 Markdown，没图或没码不写笔记。可将同样的程序及参数填入自定义命令并绑定热键，路径按实际位置修改，不必管理员运行。

## 文件归档

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\examples\wechat-archive.ps1 -Source .\inbox -Destination .\archive -DryRun
```

先预览，去掉 -DryRun 后默认复制，只有明确 -Move 才移动。按日期和类型分类，同名不同内容追加序号；临时下载文件跳过。脚本名沿用历史名称，来源目录始终由用户指定。

## Linux

```bash
bash docs/examples/shot-to-note.sh -k /path/to/kscan -i code.png -n ./notes
bash docs/examples/wechat-archive.sh -s ./inbox -t ./archive -n
```

需要 Bash 和 GNU 工具；不传 -i 时截图依赖 grim/slurp，不能以 Windows exe 代替 Linux 原生截图。归档 -n 预览，默认复制，-m 明确移动。脚本帮助以 -h 或源码参数说明为准。

## 主题与命名

将 ocean-theme.json 复制到实际配置的 user/themes 中，再到设置选择；它不是 settings.json。命名 JSON 的示例及程序员解析接口见 [命名规则](../manual/命名规则.md)。

Windows ps1 保留 UTF-8 BOM；Linux sh 保留 UTF-8 和 LF。便携包的 docs/examples 与源码示例同源，用户运行产生的文件不纳入分发包。