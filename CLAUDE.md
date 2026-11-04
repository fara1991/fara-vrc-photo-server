# CLAUDE.md

fara-vrc-photo-server。VRChat のログを監視し、写真ができたときに、そのときのアバターの情報を写真の XMP に追記する、Windows 用の常駐ソフト（.NET 10 の予定）。読み取り側は fara-vrc-photo-album（デスクトップアプリ）。詳しい取り決めは docs/spec.md。

## ブランチ運用（必ず守る）

- 作業ブランチは `develop` から作成する（`master` から切らない）
- 作業ブランチの PR は `develop` 向けに作る
- `master` へは `develop` からのみマージする。master へのマージはユーザーが行う（Claude は develop → master の PR の作成まで）

## リリース（必ず守る）

- master へのマージ（develop → master。ユーザーが行う）で、GitHub Actions（.github/workflows/release.yml）が、インストーラーを作り、**版と同じタグ（v<版>）の Release** に載せる。Release には、取り込んだ PR の題が並ぶ（build/release-notes.ps1）。**PR の題は、何を対応したかが分かる文にする**（Release の説明になるため）。
- **ワークフロー（`.github/workflows/*.yml`）や `build/*.ps1` を変えたら、push の前に `node build/check-workflow-powershell.mjs` を実行する**（PowerShell の構文だけを確かめる。実行はしない）。release.yml の `"$tag: ..."`（`:` が変数名の一部と読まれる。`${tag}` と書く）で、Release が作れなかったことがあった。develop への PR には CI が無く、master へのマージで初めて動くので、構文エラーはここで先に見つける。Release のジョブ全体（choco・Inno Setup・gh release）は、実際の master への push でしか動かせない
- **リリースしたあと（タグ v<版> ができたあと）にアプリ（src・installer・build）を変えるときは、develop の PR で `src/FaraVrcPhotoServer.App/FaraVrcPhotoServer.App.csproj` の `<Version>` を上げる**（機能の追加は minor、修正は patch）。上げないと、master への PR が Actions で止まる。まだリリースしていない版のあいだは、上げない。
- アンインストールは、アプリが作ったデータ（%LOCALAPPDATA%\FaraVrcPhotoServer）もまとめて消す（installer の UninstallDelete）。利用者が選んだフォルダは消さない。
- 日本語を含む PowerShell スクリプト（.ps1）・Inno Setup のスクリプト（.iss）は、**UTF-8（BOM つき）** で保存する（Windows PowerShell 5.1・Inno が、BOM なしを文字化けさせるため）。

## 方針

- **VRChat 本体への改造・DLL の注入・メモリの読み取りはしない**（規約・EAC に触れるため）。やるのは、ログの読み取り・OSC の受信・写真フォルダの見張り・写真ファイルへの追記だけ。
- 自分のアバターの切り替えだけを記録する。**表示名・ユーザー ID（usr_）・インスタンスは、履歴にも写真にも書かない**（表示名は設定で持つだけ）。
- 写真の書き換えは、元のファイルを壊さない手順。既定（控えを残さない）は、写真に直接書く。書く前に元の内容を退避（%LOCALAPPDATA%\FaraVrcPhotoServer\pending）に残し、書いて確かめたら消す。失敗したら退避から戻す。控えを残すときは、一時ファイル → 検査 → 入れ替え（入れ替えの前に失敗すれば元は変えない）。**電源断・強制終了のときは、退避が残り、次回の起動で写真を検査して、壊れていれば退避から元に戻す**（写真のフォルダと backup には退避を残さない）。すでに `faraxml:` の項目がある写真は書き換えない。
- **写真に書く項目の形式（v1.0.1 から）**: `rdf:Description xmlns:faratag="http://fara-labs.com/ns/faratag/1.0/"` の中に、子要素で `faratag:AvatarName`・`faratag:AvatarID`（分かったときだけ）・`faratag:Source`・`faratag:Version`（2）の順。**v1.0.0 の旧形式（`faraxml` の属性）は、読むだけ**（新しくは書かない。`FaraXml.Has` は両方を「書き込み済み」と判定する）。形式を変えたら、アルバム側の読み取り（fara-vrc-photo-album の `XmpReader`）と、docs/spec.md の第 5・6 節を、同時に直す
- 配布物（セットアップ）にコード署名は付けていないので、**SmartScreen の「不明な発行元」の警告が出る**。README の「インストール」と、リリースノート（`build/release-notes.ps1`）に、押す手順（「詳細情報」→「実行」）を書いてある。署名を付けたら、この説明を直す
- ログの行の形は VRChat の更新で変わり得る。見つからなければ何も記録しない（失敗させない）。
- spec.md の第 5・6 節は fara-vrc-photo-album と合わせる取り決め。変えるときは両方を直す。
- 作業（編集・ビルド・テスト）は、このリポジトリのフォルダーでそのまま行う（clone や worktree を別の場所に作らない）。リポジトリに入れない一時ファイル（スクリーンショット・計測の出力など）だけを Git の外に置き、作業が終わったら消す。
