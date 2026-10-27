# CLAUDE.md

fara-vrc-photo-server。VRChat のログを監視し、写真ができたときに、そのときのアバターの情報を写真の XMP に追記する、Windows 用の常駐ソフト（.NET 10 の予定）。読み取り側は fara-vrc-photo-album（デスクトップアプリ）。詳しい取り決めは docs/spec.md。

## ブランチ運用（必ず守る）

- 作業ブランチは `develop` から作成する（`master` から切らない）
- 作業ブランチの PR は `develop` 向けに作る
- `master` へは `develop` からのみマージする。master へのマージはユーザーが行う（Claude は develop → master の PR の作成まで）

## リリース（必ず守る）

- master へのマージ（develop → master。ユーザーが行う）で、GitHub Actions（.github/workflows/release.yml）が、インストーラーを作り、**版と同じタグ（v<版>）の Release** に載せる。Release には、取り込んだ PR の題が並ぶ（build/release-notes.ps1）。**PR の題は、何を対応したかが分かる文にする**（Release の説明になるため）。
- **リリースしたあと（タグ v<版> ができたあと）にアプリ（src・installer・build）を変えるときは、develop の PR で `src/FaraVrcPhotoServer.App/FaraVrcPhotoServer.App.csproj` の `<Version>` を上げる**（機能の追加は minor、修正は patch）。上げないと、master への PR が Actions で止まる。まだリリースしていない版のあいだは、上げない。
- アンインストールは、アプリが作ったデータ（%LOCALAPPDATA%\FaraVrcPhotoServer）もまとめて消す（installer の UninstallDelete）。利用者が選んだフォルダは消さない。
- 日本語を含む PowerShell スクリプト（.ps1）・Inno Setup のスクリプト（.iss）は、**UTF-8（BOM つき）** で保存する（Windows PowerShell 5.1・Inno が、BOM なしを文字化けさせるため）。

## 方針

- **VRChat 本体への改造・DLL の注入・メモリの読み取りはしない**（規約・EAC に触れるため）。やるのは、ログの読み取り・OSC の受信・写真フォルダの見張り・写真ファイルへの追記だけ。
- 自分のアバターの切り替えだけを記録する。**表示名・ユーザー ID（usr_）・インスタンスは、履歴にも写真にも書かない**（表示名は設定で持つだけ）。
- 写真の書き換えは、元のファイルを壊さない手順（一時ファイル → 検査 → 入れ替え）。途中で失敗したら元は変えない。すでに `faraxml:` の項目がある写真は書き換えない。
- ログの行の形は VRChat の更新で変わり得る。見つからなければ何も記録しない（失敗させない）。
- spec.md の第 5・6 節は fara-vrc-photo-album と合わせる取り決め。変えるときは両方を直す。
- 一時作業は `E:\Projects\fara\Temp` に作る。
