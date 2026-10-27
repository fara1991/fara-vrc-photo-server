# 配布物を作る: 単体の実行ファイル（.NET のランタイムを含む）→ セットアップ（Inno Setup）。
#   powershell -File build/publish.ps1            実行ファイルとセットアップを作る
#   powershell -File build/publish.ps1 -NoInstaller   実行ファイルだけ
# 出力: dist\publish\FaraVrcPhotoServer.exe ・ dist\FaraVRCPhotoServer-Setup-<版>.exe
# 版は、src/FaraVrcPhotoServer.App/FaraVrcPhotoServer.App.csproj の <Version>。
param([switch]$NoInstaller)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\FaraVrcPhotoServer.App\FaraVrcPhotoServer.App.csproj'
$publish = Join-Path $root 'dist\publish'
$version = ([xml](Get-Content -Raw -Encoding UTF8 $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "csproj に <Version> がありません" }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗しました" }
$exe = Join-Path $publish 'FaraVrcPhotoServer.exe'
"実行ファイル: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"

if ($NoInstaller) { return }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 が見つかりません（winget install JRSoftware.InnoSetup）" }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\FaraVRCPhotoServer.iss')
if ($LASTEXITCODE -ne 0) { throw "セットアップのコンパイルが失敗しました" }
"セットアップ: $(Join-Path $root "dist\FaraVRCPhotoServer-Setup-$version.exe")"
