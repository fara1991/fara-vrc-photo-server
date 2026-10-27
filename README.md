# fara-vrc-photo-extension

VRChatのログを監視し、写真を撮ってファイルが作成されたとき、選択中のアバター情報をファイルに追記します。

追記した情報は、写真ブログ用のデスクトップアプリ（[fara-vrc-photo-album](https://github.com/fara1991/fara-vrc-photo-album)）が、写真の取り込み時に読みます。

- 仕様: [docs/spec.md](docs/spec.md)（第 1 版・案。ログの読み取り・履歴・写真への追記・アプリ側の読み取りの取り決め）
- 動作環境: Windows（.NET 10 のタスクトレイの常駐ソフトの予定）
- VRChat 本体への改造・注入は行いません。ログの読み取り・写真フォルダの見張り・写真ファイルへの追記だけです（任意で OSC の受信）。

状況: E3（タスクトレイの常駐ソフト）まで実装済み。写真ブログのアプリ側の読み取り（E4）はこれからです。

## 使い方（常駐ソフト）

```powershell
dotnet run --project src/FaraVrcPhotoExtension.App
```

タスクトレイに常駐します。初回は設定の画面が開くので、VRChat の表示名を入れて保存してください（空のあいだは、何も記録しません）。写真のフォルダ（既定は、ピクチャの下の VRChat）に新しい写真ができると、そのときのアバターを写真に追記します。メニューから、設定・最近の処理・フォルダを開く・終了ができます。

実行ファイルにするとき: `dotnet publish src/FaraVrcPhotoExtension.App -c Release -r win-x64 --self-contained false`（.NET 10 のランタイムが要ります）。

## 使い方（確認用の CLI）

```powershell
dotnet run --project src/FaraVrcPhotoExtension.Cli -- scan --name "<自分の VRChat の表示名>"
dotnet run --project src/FaraVrcPhotoExtension.Cli -- show
dotnet run --project src/FaraVrcPhotoExtension.Cli -- show --at "2026-10-28 01:50:00"
```

VRChat のログ（約 1 日で消える）を読んで、`%LOCALAPPDATA%FaraVrcPhotoExtensionhistory.jsonl` に追記します（同じイベントは二重に書きません）。表示名は、ログから自分の行を選ぶためにだけ使い、履歴には書きません。

```powershell
# 写真に、撮影日時のアバターを追記する（--dry-run なら書かない。--backup で、書く前の元を残す）
dotnet run --project src/FaraVrcPhotoExtension.Cli -- tag "<写真.png>" --backup "<控えのフォルダ>"

# 写真フォルダ（既定はピクチャの下の VRChat）を見張り、新しい写真に追記する。Ctrl+C で終了
dotnet run --project src/FaraVrcPhotoExtension.Cli -- watch --name "<自分の VRChat の表示名>"
```

写真に書くのは、`faraxml` の項目（アバター名・ID）だけで、元の XMP は変えません。すでに `faraxml` の項目がある写真は、書き換えません。

```powershell
dotnet test
```
