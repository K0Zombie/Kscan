# Save as UTF-8 with BOM when invoking with Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manual = Join-Path $root 'docs/manual'
$cliSource = [IO.File]::ReadAllText((Join-Path $root 'CLI/Program.cs'))
$usage = [regex]::Match($cliSource, 'private const string Usage = """\r?\n([\s\S]*?)\r?\n\s*""";').Groups[1].Value
if (-not $usage) { throw 'Cannot locate CLI help.' }
$usage = [regex]::Replace($usage, '(?m)^        ', '')
$reference = Join-Path $manual '命令行参考.md'
$content = [IO.File]::ReadAllText($reference)
$marker = '## 当前命令完整帮助'
$at = $content.IndexOf($marker)
if ($at -lt 0) { throw 'CLI help marker missing.' }
$generated = $content.Substring(0, $at) + $marker + "`n`n下方由同一份 CLI 源码的 help 文本生成。`n`n" + '```text' + "`n" + $usage + "`n" + '```' + "`n"
[IO.File]::WriteAllText($reference, $generated, [Text.UTF8Encoding]::new($false))
function Inline([string]$value) {
    $value = [Net.WebUtility]::HtmlEncode($value)
    $value = [regex]::Replace($value, '`([^`]+)`', '<code>$1</code>')
    $value = [regex]::Replace($value, '\[([^\]]+)\]\(([^)]+)\)', {
        param($match)
        $target = $match.Groups[2].Value -replace '\.md(?=$|#)', '.html'
        '<a href="' + $target + '">' + $match.Groups[1].Value + '</a>'
    })
    return $value
}
$css = 'body{font:16px/1.8 system-ui,"Microsoft YaHei",sans-serif;color:#20242a;background:#fafafa;margin:0}main{max-width:1000px;margin:auto;padding:28px}a{color:#0869ba}nav{position:sticky;top:0;padding:10px 24px;background:#edf2f7;border-bottom:1px solid #b8c5d0}pre{overflow:auto;padding:16px;background:#e8edf2;border-radius:8px;white-space:pre-wrap}code{font-family:Consolas,monospace}table{border-collapse:collapse;width:100%;display:block;overflow-x:auto}th,td{padding:8px 12px;border:1px solid #b8c5d0;text-align:left}h1,h2,h3{line-height:1.4} @media(prefers-color-scheme:dark){body{color:#eceff4;background:#191d24}a{color:#85c5ff}nav,pre{background:#282f3b}th,td{border-color:#526071}}'
foreach ($file in Get-ChildItem -LiteralPath $manual -Filter '*.md' -File) {
    $body = [Text.StringBuilder]::new()
    $code = $false; $table = $false; $list = ''
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        if ($line.StartsWith('```')) {
            if ($code) { [void]$body.AppendLine('</code></pre>') } else { [void]$body.AppendLine('<pre><code>') }
            $code = -not $code; continue
        }
        if ($code) { [void]$body.AppendLine([Net.WebUtility]::HtmlEncode($line)); continue }
        if ($table -and -not $line.StartsWith('|')) { [void]$body.AppendLine('</tbody></table>'); $table = $false }
        $nextList = if ($line -match '^\s*- ') { 'ul' } elseif ($line -match '^\d+\. ') { 'ol' } else { '' }
        if ($list -and $list -ne $nextList) { [void]$body.AppendLine("</$list>"); $list = '' }
        if ($nextList) {
            if (-not $list) { $list = $nextList; [void]$body.AppendLine("<$list>") }
            [void]$body.AppendLine('<li>' + (Inline ($line -replace '^\s*(?:-|\d+\.)\s+', '')) + '</li>'); continue
        }
        if ($line.StartsWith('|')) {
            if ($line -match '^\|[\s:|\-]+\|$') { continue }
            if (-not $table) { [void]$body.AppendLine('<table><tbody>'); $table = $true }
            $cells = $line.Trim('|').Split('|') | ForEach-Object { '<td>' + (Inline $_.Trim()) + '</td>' }
            [void]$body.AppendLine('<tr>' + ($cells -join '') + '</tr>'); continue
        }
        if ($line -match '^(#{1,4})\s+(.+)$') {
            $level = $Matches[1].Length; [void]$body.AppendLine("<h$level>" + (Inline $Matches[2]) + "</h$level>"); continue
        }
        if ($line.Trim()) { [void]$body.AppendLine('<p>' + (Inline $line) + '</p>') }
    }
    if ($list) { [void]$body.AppendLine("</$list>") }; if ($table) { [void]$body.AppendLine('</tbody></table>') }
    $title = if ($file.BaseName -eq 'index') { '使用手册' } else { [Net.WebUtility]::HtmlEncode($file.BaseName) }
    $html = '<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Kscan - ' + $title + '</title><style>' + $css + '</style></head><body><nav><a href="index.html">Kscan 使用手册</a></nav><main>' + $body.ToString() + '</main></body></html>'
    [IO.File]::WriteAllText((Join-Path $manual ($file.BaseName + '.html')), $html, [Text.UTF8Encoding]::new($false))
}
Write-Output ('Offline help generated: ' + $manual)
