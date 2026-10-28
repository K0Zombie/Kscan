# Kscan

二维码与条码识别/生成、截图、滚动截图、标注与贴图工具。本工作分支 feature/region-recording 另包含 Windows GIF、视频和音频录制，以及命名规则与时间标注。

- 用户指南：[手册目录](docs/manual/index.md)
- 录制：[操作指南](docs/manual/录制.md)
- 命名与程序员扩展：[规则参考](docs/manual/命名规则.md)
- 构建与边界：[版本说明](docs/manual/版本说明.md)
- 内部设计：[DESIGN.md](docs/DESIGN.md)

Windows 便携包运行 kscan-gui.exe，CLI 使用 kscan.exe help。录制另外配置 FFmpeg。项目需要 .NET 10 SDK；测试和发布前请阅读对应分支说明，不将录制分支功能误写成 main 已有功能。
