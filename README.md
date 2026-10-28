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

### 「Windows によって PC が保護されました」（SmartScreen）と出たとき

セットアップを実行すると、次の画面が出ることがあります。**発行元が「不明な発行元」と出るのは、このアプリのセットアップに、有料のコード署名（発行元の証明書）をまだ付けていないため**で、ウイルスなどが見つかったという意味ではありません。

1. 画面に **「実行」** のボタンが出ているときは、それを押します。
2. 「実行」が見えないときは、先に **「詳細情報」** を押すと、「実行」が出ます。
3. 通常の画面（ユーザーアカウント制御）が出たら、「はい」を押します。

安全のため、**セットアップは、このリポジトリの [Releases](https://github.com/fara1991/fara-vrc-photo-server/releases) のページからだけ**ダウンロードしてください（ほかの場所で配られているものは使わないでください）。ソースコードはこのリポジトリにあり、ログの読み取り・写真フォルダの見張り・写真ファイルへの追記以外のことはしません。

タスクトレイ（画面右下の「^」の中）に、紫のカメラのアイコンが出ます。Windows 11 は、新しいアイコンを「^」の中にしまうので、常に出したいときは、アイコンをタスクバー側へドラッグするか、「設定 → 個人用設定 → タスクバー → その他のシステムトレイアイコン」で、オンにしてください。

- 写真のフォルダ（既定は、ピクチャの下の `VRChat`）に新しい写真ができると、そのときのアバターを写真に追記します。
- アイコンのメニュー: 状態／設定／最近の処理／写真のフォルダを開く／履歴のフォルダを開く／終了。
- 設定・履歴・記録は `%LOCALAPPDATA%\FaraVrcPhotoServer\`（`settings.json`・`history.jsonl`・`app.log`）に置きます。**アンインストールすると、このフォルダもまとめて消えます**（元の写真の控えのフォルダなど、自分で選んだフォルダは、写真が入っているので消しません）。
- 写真のフォルダは、ピクチャの下の `VRChat` に決まっています（設定の画面には出しません）。見張りを始められないときは、トレイの状態に理由を出して、10 秒ごとにやり直します。
- 写真に書くのは、`faratag` の項目（`rdf:Description` の中の `faratag:AvatarName`・`faratag:AvatarID`・`faratag:Source`・`faratag:Version`）だけで、元の XMP は変えません。すでにアバターの項目（`faratag`、または v1.0.0 の旧形式 `faraxml`）がある写真は、書き換えません。
- **既定（オフ）では、撮った写真のファイルに、直接タグを書き込みます**（ファイルそのものを残します。書く前に、元の内容を `%LOCALAPPDATA%\FaraVrcPhotoServer\pending` に退避し、書いたあと読み直して確かめ、合わなければ元の内容に戻します。ほかのプログラムが開いていて直接書けないときは、一時ファイルで入れ替えます）。
- **電源断・強制終了で書き込みの途中で止まったときは、次回の起動で戻します。** 退避から写真を検査し、壊れていれば元の内容に戻します（写真のフォルダと backup には、退避を残しません）。
- 設定の「元の写真を、写真と同じ場所の backup フォルダに残す」をオンにすると、書き換える前の元の写真を、写真と同じ場所の `backup` フォルダ（例: `2026-10\backup\`）に残します（既定はオフ。容量が増えます）。

## 配布物を作る（開発者向け）

```powershell
powershell -File build/publish.ps1
```

- `dist\publish\FaraVrcPhotoServer.exe` … .NET のランタイムを含む、単体の実行ファイル（約 50 MB）
- `dist\FaraVRCPhotoServer-Setup-<版>.exe` … セットアップ（約 45 MB。[Inno Setup 6](https://jrsoftware.org/isinfo.php) が要ります。`winget install JRSoftware.InnoSetup`）
- 版は、`src/FaraVrcPhotoServer.App/FaraVrcPhotoServer.App.csproj` の `<Version>`。セットアップの定義は `installer/FaraVRCPhotoServer.iss`。

### リリース（GitHub Actions）

- `master` にマージ（develop → master の PR）すると、`.github/workflows/release.yml` が、テスト → インストーラーのビルド → **版と同じタグ（`v1.0.0` など）の Release** を作って、インストーラーを載せます。
- Release には、前のリリースから取り込んだ変更（マージした PR の題）と、インストールの手順が書かれます（`build/release-notes.ps1`）。
- **版の上げ方**: リリースしたあとにアプリを変えるときは、develop の PR で `<Version>` を上げます（機能の追加は 1.1.0、修正は 1.0.1 のように）。同じ版のタグがすでにあると、master への PR（`src`・`installer`・`build` の変更）は、Actions が止めます。版を上げていない push では、新しい Release は作りません（上書きしません）。
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
