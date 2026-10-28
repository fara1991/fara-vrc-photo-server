# リリースの説明文（Markdown）を作る。前のリリースのタグから今までに取り込んだ変更（マージした PR の題。PR が無ければ、コミットの題）を並べる。
#   pwsh build/release-notes.ps1 -Version 1.0.1 [-PreviousTag v1.0.0] > notes.md
# 前のタグを渡さなければ、最初のリリースとして、これまでのすべての変更を並べる。
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PreviousTag = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$range = if ($PreviousTag) { "$PreviousTag..HEAD" } else { 'HEAD' }

# マージした PR: 「Merge pull request #N from …」の本文の最初の行が、PR の題
$items = New-Object System.Collections.Generic.List[string]
$merges = git log $range --merges --format='%s%x1f%b%x1e' | Out-String
foreach ($entry in ($merges -split [char]0x1e)) {
    $parts = $entry.Trim() -split [char]0x1f, 2
    if ($parts.Count -lt 2) { continue }
    # develop → master の PR（master への merge commit）は、リリースする本体なので、変更の項目に入れない
    if ($parts[0] -match ' from [^/\s]+/develop$') { continue }
    if ($parts[0] -match '^Merge pull request #(\d+)') {
        $title = ($parts[1] -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        if ($title) { $items.Add("- $($title.Trim()) (#$($Matches[1]))") }
    }
}
# PR が無いとき（直接のコミットだけ）は、マージでないコミットの題
if ($items.Count -eq 0) {
    git log $range --no-merges --format='%s' | ForEach-Object { if ($_.Trim()) { $items.Add("- $($_.Trim())") } }
}

$body = New-Object System.Collections.Generic.List[string]
$body.Add("## FaraVRCPhotoServer $Version")
$body.Add('')
if (-not $PreviousTag) { $body.Add('最初のリリースです。'); $body.Add('') }
$body.Add('### 変更内容')
$body.Add('')
if ($items.Count -gt 0) { $items | ForEach-Object { $body.Add($_) } } else { $body.Add('- （変更の記録がありません）') }
$body.Add('')
$body.Add('### インストール')
$body.Add('')
$body.Add("1. 下の「Assets」の ``FaraVRCPhotoServer-Setup-$Version.exe`` をダウンロードして実行します（管理者の権限は要りません）。")
$body.Add('2. 初回は設定の画面が開くので、VRChat の表示名を入れて保存します。')
$body.Add('')
$body.Add('動作環境: Windows 10 / 11（x64）。.NET のランタイムは、インストーラーに含まれています。')
$body -join "`n"
