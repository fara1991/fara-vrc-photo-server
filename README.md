# fara-vrc-photo-server

**FaraVRCPhotoServer** — VRChat のログを監視し、写真を撮ってファイルが作成されたとき、選択中のアバター情報を写真に追記します。

追記した情報は、写真ブログ用のデスクトップアプリ（[fara-vrc-photo-album](https://github.com/fara1991/fara-vrc-photo-album)）が、写真の取り込み時に読みます（新しい記事の写真に、撮影時のアバターが自動で入ります）。

- 仕様: [docs/spec.md](docs/spec.md)（ログの読み取り・履歴・写真への追記・アプリ側の読み取りの取り決め）
- 動作環境: Windows 10 / 11（x64）。.NET のランタイムは、実行ファイルに含まれているので、別に入れる必要はありません。
- VRChat 本体への改造・注入は行いません。ログの読み取り・写真フォルダの見張り・写真ファイルへの追記だけです（任意で OSC の受信）。

## インストール

1. `FaraVRCPhotoServer-Setup-<版>.exe`（セットアップ）を実行します。管理者の権限は要りません（現在のユーザーの `AppData\Local\Programs` に入ります）。
2. 「Windows の起動時に自動で起動する」を選ぶと、PC を起動するたびに、タスクトレイに常駐します（あとから、設定の画面でも変えられます）。
3. 初回は設定の画面が開くので、**VRChat の表示名**を入れて保存してください（空のあいだは、何も記録しません）。

タスクトレイ（画面右下の「^」の中）に、紫のカメラのアイコンが出ます。Windows 11 は、新しいアイコンを「^」の中にしまうので、常に出したいときは、アイコンをタスクバー側へドラッグするか、「設定 → 個人用設定 → タスクバー → その他のシステムトレイアイコン」で、オンにしてください。

- 写真のフォルダ（既定は、ピクチャの下の `VRChat`）に新しい写真ができると、そのときのアバターを写真に追記します。
- アイコンのメニュー: 状態／設定／最近の処理／写真のフォルダを開く／履歴のフォルダを開く／終了。
- 設定と履歴は `%LOCALAPPDATA%\FaraVrcPhotoServer\`（`settings.json`・`history.jsonl`）に置きます。アンインストールしても消えません。
- 写真に書くのは、`faraxml` の項目（アバター名・ID）だけで、元の XMP は変えません。すでに `faraxml` の項目がある写真は、書き換えません。

## 配布物を作る（開発者向け）

```powershell
powershell -File build/publish.ps1
```

- `dist\publish\FaraVrcPhotoServer.exe` … .NET のランタイムを含む、単体の実行ファイル（約 50 MB）
- `dist\FaraVRCPhotoServer-Setup-<版>.exe` … セットアップ（約 45 MB。[Inno Setup 6](https://jrsoftware.org/isinfo.php) が要ります。`winget install JRSoftware.InnoSetup`）
- 版は、`src/FaraVrcPhotoServer.App/FaraVrcPhotoServer.App.csproj` の `<Version>`。セットアップの定義は `installer/FaraVRCPhotoServer.iss`。
- アイコン（`src/FaraVrcPhotoServer.App/app.ico`）を作り直すとき: `dotnet run --project src/FaraVrcPhotoServer.App -- --write-icon src/FaraVrcPhotoServer.App/app.ico`

開発中に、ビルドして動かすとき: `dotnet run --project src/FaraVrcPhotoServer.App`

## 確認用の CLI

```powershell
dotnet run --project src/FaraVrcPhotoServer.Cli -- scan --name "<自分の VRChat の表示名>"
dotnet run --project src/FaraVrcPhotoServer.Cli -- show
dotnet run --project src/FaraVrcPhotoServer.Cli -- show --at "2026-10-28 01:50:00"
```

VRChat のログ（約 1 日で消える）を読んで、`%LOCALAPPDATA%\FaraVrcPhotoServer\history.jsonl` に追記します（同じイベントは二重に書きません）。表示名は、ログから自分の行を選ぶためにだけ使い、履歴には書きません。

```powershell
# 写真に、撮影日時のアバターを追記する（--dry-run なら書かない。--backup で、書く前の元を残す）
dotnet run --project src/FaraVrcPhotoServer.Cli -- tag "<写真.png>" --backup "<控えのフォルダ>"

# 写真フォルダ（既定はピクチャの下の VRChat）を見張り、新しい写真に追記する。Ctrl+C で終了
dotnet run --project src/FaraVrcPhotoServer.Cli -- watch --name "<自分の VRChat の表示名>"
```

## テスト

```powershell
dotnet test
```
