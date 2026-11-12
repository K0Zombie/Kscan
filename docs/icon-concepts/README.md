# Kscan 图标

- `kscan-icon-user-selected.png`：最终采用的原始图标，`scripts/Build-Icon.ps1` 以它为源。
- `contrast-preview.png`：白、黑、蓝、灰底以及 16/24/32px 下的可读性检查。

`scripts/Build-Icon.ps1` 会从源图生成：

- `GUI/Assets/kscan.png`（1280×1280）
- `GUI/Assets/kscan.ico`（16/24/32/48/64/128/256）
- `packaging/arch/gui-extra/usr/share/icons/hicolor/256x256/apps/kscan.png`
- `docs/icon-concepts/contrast-preview.png`

图标图案为扫描框与二维码，深蓝底板配浅色外轮廓，保证在浅色标题栏和小尺寸下仍然可辨。
