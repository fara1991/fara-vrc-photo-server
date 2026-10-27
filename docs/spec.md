# 仕様（第 1 版・案）

VRChat で写真を撮ったとき、**そのときに操作していたアバターの情報**を、写真ファイルに追記するための、Windows 用の常駐ソフトの仕様です。
追記した情報は、写真ブログ用のデスクトップアプリ（[fara-vrc-photo-album](https://github.com/fara1991/fara-vrc-photo-album)）が、写真の取り込み時に読みます。

この文書は、**2 つのリポジトリで合わせるための取り決め**（第 5・6 節）を含みます。変えるときは、両方を直します。

## 1. 目的と、やらないこと

**目的**
- 写真を撮ったときのアバターの名前（と ID）を、写真ごとに自動で記録する。
- VRChat のログは約 1 日で消えるので、消える前に履歴として手元に残す。

**やらないこと**
- VRChat 本体への改造・DLL の注入・メモリの読み取りなど、VRChat の規約や不正対策（EAC）に触れること。**ログファイルの読み取り**、**OSC の受信**、**写真フォルダの見張り**、**写真ファイルへの追記**だけを行う。
- VRChat のアカウント情報・ログインを扱わない（VRChat の API を呼ばない）。
- 他のプレイヤーの情報を記録しない。**自分のアバターの切り替えだけ**を記録する。
- 自分の表示名を、写真・履歴のどちらにも書かない（ログから「自分の行」を選ぶためにだけ、設定で持つ）。

## 2. 全体の構成

.NET 10 の、タスクトレイに常駐する Windows アプリ。

| 部品 | 役割 |
| --- | --- |
| LogWatcher | VRChat のログ（`output_log_*.txt`）の追記を読み、イベントを取り出す |
| HistoryStore | イベントを、履歴ファイル（`history.jsonl`）に追記する |
| PhotoWatcher | 写真フォルダの新しい PNG を見つける |
| XmpWriter | 写真の XMP に、アバターの情報を追記する |
| （任意・後で）OscListener | OSC で来る `/avatar/change` や、アバターのパラメーター（服の番号など）を履歴に追記する |

流れ: VRChat がログに書く → LogWatcher が読んで履歴に追記 → 写真ができる → PhotoWatcher が見つける → 撮影日時の直前のアバターを履歴から探す → XmpWriter が追記。

## 3. VRChat のログの読み取り

- 場所: `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt`（VRChat が起動中も書き込んでいるので、**読み取り専用・他の書き込みを許す共有**で開く）。ログは約 1 日で消える（確認済みの動作。設定では変えられない）。
- 時刻: 行頭の `yyyy.MM.dd HH:mm:ss`（PC のローカル時刻。写真のファイル名と同じ基準）。
- 取り出す行（この PC の実ログで確認した形。VRChat の更新で変わり得るので、**見つからなければ何も記録しない**）:

| イベント | 行の形 |
| --- | --- |
| ワールドに入った | `[Behaviour] Entering Room: <ワールド名>` と、続く `[Behaviour] Joining wrld_<uuid>:<インスタンス>` |
| アバターの切り替え | `[Behaviour] Switching <表示名> to avatar <アバター名>`（**表示名が、設定の自分の表示名と一致する行だけ**） |
| アバターの ID | `Loading Avatar Data:avtr_<uuid>`（直前の切り替えの行に続けて出る。切り替えの行の後、同じ読み込みの範囲（30 秒以内）の最初のものを、その切り替えの ID とする。見つからなければ ID なし） |

- ワールドの行は、インスタンスの部分（`:49024~private(usr_…)~region(jp)` など。ユーザー ID を含む）を**記録しない**。ワールド名と `wrld_` の ID だけを持つ。
- 読み始めの位置は、起動時に、既存のログすべてを先頭から読み、履歴にまだ無いイベントだけを追記する（イベントは、時刻と種類・内容で重複を判定する）。

## 4. 履歴ファイル

- 場所: `%LOCALAPPDATA%\FaraVrcPhotoExtension\history.jsonl`（1 行 1 イベントの JSON。追記のみ。壊れた行は読み飛ばす）。
- 時刻は、ISO 8601 のオフセットつき（例: `2026-10-28T01:49:51+09:00`）。

```json
{"t":"2026-10-28T01:49:39+09:00","type":"world","worldId":"wrld_528e5412-7bb4-42c1-aab2-2b38a629f5de","worldName":"Crystal Garden"}
{"t":"2026-10-28T01:49:51+09:00","type":"avatar","avatarName":"FaraFrillBlood","avatarId":"avtr_71bd1202-157a-4264-b888-a133209de9ca"}
```

- `avatarId` は、見つからなければ省く。
- 表示名・インスタンス・ユーザー ID・位置は、**履歴にも書かない**。
- OSC を足したとき（任意・後で）: `{"type":"param","name":"OutfitId","value":3}` のような行を、同じファイルに追記する。

## 5. 写真への追記（XMP）

- 見張る場所: 写真フォルダ（設定。既定は `<ピクチャ>\VRChat`。サブフォルダ `yyyy-MM` を含めて見る）。
- 対象: `VRChat_yyyy-MM-dd_HH-mm-ss.fff_WxH.png`（ファイル名の日時が**撮影日時**。ローカル時刻）。
- 作成された直後は、VRChat が書き込み中のことがある。**ファイルの大きさが 1 秒間変わらなくなってから**処理する。
- 撮影日時の直前の `avatar` イベント（最大 24 時間前まで。ワールドに入った後の `avatar` を優先）を、写真のアバターとする。見つからなければ何も書かない。
- 書き込み先: PNG の `iTXt` チャンク（キーワード `XML:com.adobe.xmp`）の、XMP の `rdf:Description` に、**独自の名前空間**の項目を足す。VRChat が書いた既存の項目（`vrc:` と `xmp:`）は、変えない。

```xml
xmlns:faraxml="http://fara-labs.com/ns/faraxml/1.0/"
faraxml:Version="1"
faraxml:AvatarName="FaraFrillBlood"
faraxml:AvatarId="avtr_71bd1202-157a-4264-b888-a133209de9ca"
faraxml:Source="log"
```

- `AvatarId` は、分かったときだけ。`Source` は、取り出した方法（`log` または、後で足す `osc`）。
- **すでに `faraxml:` の項目がある写真は、書き換えない**（二重に書かない）。
- 書き込みの手順: 1) 元のファイルを読んで検査する（PNG として読めること）→ 2) 新しい内容を一時ファイルに書く（チャンクの CRC を計算する）→ 3) 一時ファイルを検査する（PNG として読めること・画像のデータが元と同じこと）→ 4) 元のファイルを入れ替える。**途中で失敗したら、元のファイルは変えない**。設定で、書く前の元のファイルを別のフォルダにコピーしておくこともできる（既定はオフ）。
- 設定で、写真には書かず、履歴だけにする（`writeXmp: false`）こともできる。

## 6. 写真ブログのアプリ側の読み取り（fara-vrc-photo-album）

取り込み時に、写真のアバターを、次の順で決める。**すでに手で選んだアバターは上書きしない**（空の写真にだけ入れる）。

1. 写真の XMP の `faraxml:AvatarName`（と `AvatarId`）。
2. 無ければ、`history.jsonl`（設定で場所を指定。既定は上の場所）から、撮影日時の直前の `avatar` イベント。

アバターの名前を、アバターのマスタ（`content/avatars.json`）の名前・別名と比べる（**空白を取り、大文字小文字を区別しない**。VRChat の `Fara AlmarianDress` と、prefab の `FaraAlmarianDress` が合う）。合うものが無ければ入れない。
`AvatarId` が `avtr_…` のときは、マスタに `blueprintId` があれば、それも使って合わせる（任意の項目。Unity の prefab の PipelineManager から読む案）。

## 7. 設定（このソフト）

`%LOCALAPPDATA%\FaraVrcPhotoExtension\settings.json`。

| 項目 | 既定 | 内容 |
| --- | --- | --- |
| `displayName` | 空 | 自分の VRChat の表示名。空のあいだは何も記録しない |
| `photoFolder` | `<ピクチャ>\VRChat` | 写真を見張るフォルダ |
| `logFolder` | `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat` | VRChat のログのフォルダ |
| `historyPath` | `%LOCALAPPDATA%\FaraVrcPhotoExtension\history.jsonl` | 履歴ファイル |
| `writeXmp` | `true` | 写真に追記するか |
| `backupFolder` | 空 | 書く前の元のファイルのコピー先（空ならコピーしない） |
| `autoStart` | `false` | Windows の起動時に自動で起動するか |

## 8. プライバシー・安全

- 履歴・設定は、この PC の中だけ。ネットワークには何も送らない。
- 写真に書くのは、アバターの名前と ID だけ。表示名・ユーザー ID・インスタンスは書かない。
- **写真を SNS などに公開するときは、追記したアバターの情報も、ファイルに含まれる**（VRChat 自身が、ワールドの情報を書いているのと同じ）。公開したくないときは、`writeXmp` をオフにする。
- 書き込みは、元のファイルを壊さない手順（第 5 節）に従う。

## 9. 進め方

| 段階 | 内容 |
| --- | --- |
| E0 | この仕様（今） |
| E1 | ログの読み取り・履歴（テストつき。実ログの形の行で検証） |
| E2 | 写真の見張りと XMP の書き込み（検査・入れ替え。壊れた写真・書き込み中の写真のテスト） |
| E3 | タスクトレイ・設定の画面・自動起動 |
| E4 | fara-vrc-photo-album 側の読み取り（第 6 節） |
| E5（任意） | OSC の受信（服の番号のパラメーターなど） |

## 10. 未確定（あとで確かめる）

- 切り替えの行と `Loading Avatar Data` の対応（30 秒の範囲で足りるか。ほかのプレイヤーの読み込みの行が混ざらないか）。実ログで確かめる。
- ワールドに入った直後のアバターの切り替えが、すべてログに出るか（出ない場合は、直前のワールドの行の後の最初の `avatar` を使う）。
- 写真のファイル名の日時と、ログの時刻のずれ（同じ PC のローカル時刻のはずだが、秒の丸めで 1 秒ほどずれるか）。
- XMP の書き込みで、VRChat 以外のビューア（Windows の「写真」・Twitter など）が問題なく開けるか。
