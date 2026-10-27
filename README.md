# fara-vrc-photo-extension

VRChatのログを監視し、写真を撮ってファイルが作成されたとき、選択中のアバター情報をファイルに追記します。

追記した情報は、写真ブログ用のデスクトップアプリ（[fara-vrc-photo-album](https://github.com/fara1991/fara-vrc-photo-album)）が、写真の取り込み時に読みます。

- 仕様: [docs/spec.md](docs/spec.md)（第 1 版・案。ログの読み取り・履歴・写真への追記・アプリ側の読み取りの取り決め）
- 動作環境: Windows（.NET 10 のタスクトレイの常駐ソフトの予定）
- VRChat 本体への改造・注入は行いません。ログの読み取り・写真フォルダの見張り・写真ファイルへの追記だけです（任意で OSC の受信）。

状況: 仕様の段階です（実装はこれから）。
