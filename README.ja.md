# TarkovHelper

[![en](https://img.shields.io/badge/lang-English-blue.svg)](README.md)
[![ko](https://img.shields.io/badge/lang-한국어-red.svg)](README.ko.md)
[![ja](https://img.shields.io/badge/lang-日本語-green.svg)](README.ja.md)
[![Latest release](https://img.shields.io/github/v/release/josephjang/TarkovHelper)](https://github.com/josephjang/TarkovHelper/releases/latest)

Escape from Tarkovのクエスト・ハイドアウト・アイテムの進行状況を追跡し、ゲーム自身が書き出すログファイルを監視して自動的に同期するWindowsデスクトップコンパニオンです。

> **注意**: このリポジトリは [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper) から派生し、独立してメンテナンスされているフォークです。CalVer（`YYYY.M.N`）方式で独自のリリースを配布しており（**v2026.7.0** から開始）、機能追加を継続しています。

![Tarkov Helperのクエスト追跡](screenshots/quests.png)

## ダウンロード

[最新リリース](https://github.com/josephjang/TarkovHelper/releases/latest)から **TarkovHelper.zip** をダウンロードし、任意の場所に展開して `TarkovHelper.exe` を実行してください。

- **Windows** と [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要
- 起動時に**管理者権限への昇格**を求めます。理由は[仕組みと安全性](#仕組みと安全性)を参照してください。

インストール後は、アプリ本体とゲームデータの両方が自動的に最新に保たれます。

## 主な機能

- **クエスト**: 全クエストの一覧・検索、ステータス/トレーダー/マップ/Kappa/陣営でのフィルタ、目標・前提・後続クエストの確認
- **ハイドアウト**: 施設レベルを追跡し、各アップグレードに必要なアイテム・トレーダー・スキル・関連施設を確認
- **アイテム**: クエストとハイドアウトのアップグレードにまだ必要な全アイテムを1つのリストに集計、FIR（Found in Raid）/通常アイテムを所持数とともに別々に追跡
- **コレクター**: Collectorクエストのアイテム専用チェックリスト
- **マップ**: クエストマーカーと脱出口を表示するインタラクティブマップ、レイド中の位置追跡に対応
- **オーバーレイミニマップ**: プレイ中に使える最前面ミニマップ、グローバルホットキーで操作
- **ゲームログ同期**: クエストの開始/完了/失敗とゲームモードをゲームのログファイルから自動認識
- **PvP/PvEプロファイル**: モードごとに分離された進行状況、プレイ中のモードに合わせて自動切り替え
- **自動更新**: アプリとゲームデータベースをバックグラウンドで自動更新
- **3言語対応**: English、한국어、日本語、アプリ内で切り替え可能

## 仕組みと安全性

Tarkov Helperはすべてのゲーム状態を、**ゲーム自身が書き出すファイルを読む受動的な方法**でのみ取得します:

- **ログファイル**: クエスト・レイドのイベントとゲームモードはゲーム自身のログから読み取ります
- **スクリーンショットのファイル名**: レイド中の位置はゲームのスクリーンショット機能から取得します。ゲームがファイル名に座標を記録するためです

ゲームメモリの読み取り、コードの注入、ゲームファイルの変更は**行いません**。オーバーレイミニマップはごく普通の最前面ウィンドウであり、グローバルホットキーはTarkov Helper自身のプロセス内で動作するシステム全体のキーボードフックを使用します。このフックとログファイルへのアクセスが、起動時に管理者権限を求める理由です。

サードパーティ製ツールがBattlestate Gamesに代わって何かを保証することはできません。使用は自己判断でお願いします。

## はじめに

### ゲームログ同期

同期は追加設定なしで動作します。アプリがTarkovのインストール場所（BSGランチャーおよびSteam）を自動検出し、ログの監視を開始します。見つからない場合は、**設定** → **Tarkovログフォルダ**で**自動検出**または**参照...**を使い、ゲームの `Logs` フォルダを指定してください。

### マップの位置追跡

レイド中に**マップ**タブで自分の位置を更新するには、ゲームのスクリーンショットキーを押してください。アプリは新しいスクリーンショットのファイル名から座標を読み取ります。スクリーンショットを撮らない限り、位置が自動的に更新されることはありません。

### 進行状況の保存場所

進行状況は `TarkovHelper.exe` の隣の `Config` フォルダに保存されます。インストール場所ごとにデータが分かれているため、アプリを新しい場所に移して進行状況が空に見える場合は、**設定** → **Data Migration** で以前の場所からデータを取り込んでください。ゲームデータ（クエスト、アイテム、ハイドアウト）はアプリに同梱されており自動更新されるため、手動で取得するものはありません。

## その他のスクリーンショット

![必要アイテムの集計](screenshots/items.png)
![ハイドアウトのアップグレード追跡](screenshots/hideout.png)

## ソースからビルド

[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。

```powershell
git clone https://github.com/josephjang/TarkovHelper.git
cd TarkovHelper
dotnet build TarkovHelper/TarkovHelper.csproj -c Release
```

その後、`TarkovHelper\bin\Release\net8.0-windows\TarkovHelper.exe` を起動し、昇格プロンプトを承認してください。（アプリのマニフェストが管理者権限を要求するため、昇格していないターミナルからの `dotnet run` は動作しません。）

## ライセンス

[MIT License](LICENSE)

`TarkovHelper/Fonts/` 配下に同梱されているフォントはサードパーティの著作物であり、
MIT ライセンスの対象では**ありません**。Play と Noto Sans CJK KR は SIL Open Font
License 1.1、Bender は `TarkovHelper/Fonts/LICENSE-Bender.txt` の出所表記に
従います。各 `Fonts/LICENSE-*.txt` を参照してください。

## クレジット

- オリジナルプロジェクト: [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper)
- ゲームデータ: [tarkov.dev](https://tarkov.dev)
- Escape from TarkovはBattlestate Gamesの商標です。
- フォント: Bender (Jovanny Lemonad / TypeType), Play (OFL), Noto Sans CJK KR (OFL)
