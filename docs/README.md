# docs/ — ドキュメント索引

このフォルダには Agent Panel for Unity の設計・調査・検証の記録を置いています。
ほとんどが日本語で、開発の経緯をそのまま残した一次資料です。利用者向けの説明は
リポジトリルートの [README.md](../README.md)([English](../README.en.md))を参照してください。

| パス | 内容 | 読者 |
|---|---|---|
| [USER-GUIDE.md](USER-GUIDE.md) | 操作ガイド。画面構成・チャット・チップと添付・権限カードと自動承認レベル・履歴・Unity 操作ツール・設定画面リファレンス・ショートカット | 利用者 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | アーキテクチャ決定文書(ADD)。CLI の起動方式・プロトコル・権限処理・ドメインリロード戦略・永続化・UI スタックなど、主要な設計判断とその根拠。2026-07 に作成した初版に、Phase 4/5 の決定を追記したもの | 実装を読む人・設計を変えたい人 |
| [RELEASING.md](RELEASING.md) | バージョン規約とリリース手順、既存タグの一覧 | メンテナ |
| [design-notes/](design-notes/) | 機能・修正ごとの設計メモ(`YYYY-MM-DD-<slug>.md`)。原因分析・採用した設計・却下した案・検証結果を 1 ノート 1 トピックで記録。CHANGELOG の各エントリからリンクされる | 変更の「なぜ」を知りたい人 |
| [research/](research/)\* | 開発前後の技術調査レポート(R01〜R11)。Claude Code CLI の stream-json プロトコル実測、Unity エディタ統合の制約、MCP トランスポート、コンパイル抑制技法など | プロトコルや Unity API の挙動を確かめたい人 |
| [verify/](verify/)\* | 実機検証の記録(EditMode テスト結果 XML、検証レポート、スクリーンショット)。設計ノートから参照される証跡 | 検証結果を確認したい人 |
| [review-backlog.md](review-backlog.md)\* | パッケージ全体レビュー(v0.25〜v0.27 で 87/118 件を是正)の残項目一覧 | 貢献のネタを探す人 |
| [images/](images/) | README のスクリーンショットと、設計ノート・検証記録から参照される画像 | — |

\* 印のフォルダ/ファイルは開発用モノレポにのみあり、公開リポジトリ
([c-colloid/AgentPanelForUnity](https://github.com/c-colloid/AgentPanelForUnity)、
Core パッケージの配布元)には含まれていません。公開版では `USER-GUIDE.md` /
`ARCHITECTURE.md` / `RELEASING.md` / `design-notes/`(Core に関するもの)/ `images/` が
読めます。設計ノートが `research/` や `verify/` を参照している箇所は、公開版では
リンク先が無い歴史的参照として読んでください。

## 読み方の目安

- **まず全体像**: `ARCHITECTURE.md` の「1. 決定事項サマリ」(D1〜D9)。
- **CLI とのやり取りを追う**: `research/02-claude-cli-protocol.md` → `02b` → `02c`(サブエージェント)(\* モノレポのみ)。
- **UapOps(Unity 操作ツール)**: `research/09-editor-api-surface.md`(\* モノレポのみ)と `design-notes/2026-08-01-phase5-unity-ops-design.md`。
- **ある機能がなぜその形か**: CHANGELOG の該当エントリからリンクされている設計ノート。

## 注意

- 調査レポートと初期の設計ノートには、作者の開発環境(Windows、検証用サンドボックスプロジェクト、
  作者自身の VRChat アバタープロジェクトなど)に固有のパスや前提がそのまま残っています。
  歴史的記録として保持しており、パッケージの動作要件ではありません。
- `verify/` の XML はテスト実行ごとの完全な結果で、サイズが大きめです。設計ノートが参照する
  証跡として残しています。
