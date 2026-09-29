# Agent Panel for Unity 操作ガイド

Unity エディタ内でコーディングエージェント(Claude Code、Codex、Grok Build などの ACP 対応 CLI)を使うパネルの操作説明です。インストールと必要要件は [README](../README.md) を参照してください。

ガイドはテーマごとのページに分かれています。下の表から目的のページを開いてください。本文中の「」内はパネルに表示される日本語 UI の文言です(設定 > パネル > 外観 の「言語」で English に切り替えられます)。

## 目的から探す

| やりたいこと | ページ |
|---|---|
| はじめて使う | [はじめに](guide/getting-started.md) |
| エージェントにサインインしたい | [Claude Code](guide/agents/claude-code.md) · [Codex](guide/agents/codex.md) · [Grok Build](guide/agents/grok-build.md) · [Gemini CLI](guide/agents/gemini-cli.md) · [その他の ACP](guide/agents/custom-acp.md) |
| 使うエージェントを切り替えたい | [エージェントの導入とサインイン](guide/agents/README.md) |
| 選択中のオブジェクトやエラーを渡したい | [Unity のコンテキストを渡す](guide/context.md) |
| 許可カードを減らしたい | [許可カード・自動承認](guide/permissions.md#自動承認レベル) |
| 前の会話に戻りたい / モデルを変えたい | [セッション・履歴・モデル](guide/sessions-and-models.md) |
| エージェントにシーンを操作させたい | [Unity 操作ツール](guide/unity-ops.md) |
| Scene ビューで「ここ」を指したい | [マーカー・ピン・スケッチ](guide/scene-tools.md) |
| 再コンパイルで会話が途切れた | [再コンパイル・Play モードとの共存](guide/reload-and-play-mode.md) |
| 設定項目の場所を知りたい | [設定画面リファレンス](guide/settings.md) |
| 動かない・エラーが出る | [困ったときは](guide/troubleshooting.md) |

## ページ一覧

1. [はじめに](guide/getting-started.md) — パネルを開いてから最初のメッセージを送るまで
2. [エージェントの導入とサインイン](guide/agents/README.md) — 切り替えの流れ、エージェントによる違い
   - [Claude Code](guide/agents/claude-code.md) / [Codex](guide/agents/codex.md) / [Grok Build](guide/agents/grok-build.md) / [Gemini CLI](guide/agents/gemini-cli.md) / [その他の ACP エージェント](guide/agents/custom-acp.md) — 画面つきのサインイン手順
3. [画面とチャット](guide/chat.md) — 画面の構成、送信と停止、ツールカード・サブエージェントカード、スラッシュコマンドとコンテキスト圧縮
4. [Unity のコンテキストを渡す](guide/context.md) — 選択・コンソールエラー・ドラッグしたアセット・画像のチップ
5. [許可カード・自動承認・質問カード](guide/permissions.md) — ツール実行の許可、自動承認レベル、AskUserQuestion
6. [セッション・履歴・モデル](guide/sessions-and-models.md) — 新規セッション、履歴ブラウザ、モデルの選択
7. [Unity 操作ツール(UapOps)](guide/unity-ops.md) — モジュール一覧、スクリプト検証ゲート、拡張プロファイル、uLoop 連携
8. [Scene ビューのマーカー・ピン・スケッチ](guide/scene-tools.md) — Agent Tools ツールバーと使用例
9. [スクリプトの再コンパイル・Play モードとの共存](guide/reload-and-play-mode.md) — 自動再接続と自動継続
10. [設定画面リファレンス](guide/settings.md) — 5 つのタブとカードの一覧
11. [キーボードショートカット](guide/shortcuts.md)
12. [困ったときは](guide/troubleshooting.md)

## 以前の章番号から探す

このガイドは 2026-09-29 まで 1 ページで、章番号で参照されていました。以前の番号の移動先は次のとおりです。

| 以前の章 | 移動先 |
|---|---|
| 1. パネルを開く・初回セットアップ | [はじめに](guide/getting-started.md) |
| 1.1 Claude 以外のエージェントを使う | [エージェントの導入とサインイン](guide/agents/README.md) |
| 2. 画面の構成 / 3. チャットの基本操作 | [画面とチャット](guide/chat.md) |
| 4. Unity のコンテキストを渡す | [Unity のコンテキストを渡す](guide/context.md) |
| 5. 権限カード / 6. 自動承認レベル / 7. 質問カード | [許可カード・自動承認・質問カード](guide/permissions.md) |
| 8. ツールカードとサブエージェントカード | [画面とチャット](guide/chat.md#ツールカードとサブエージェントカード) |
| 9. セッションと履歴 / 10. モデルの選択 | [セッション・履歴・モデル](guide/sessions-and-models.md) |
| 11. スラッシュコマンドとコンテキスト圧縮 | [画面とチャット](guide/chat.md#スラッシュコマンドとコンテキスト圧縮) |
| 12. Unity 操作ツール(UapOps) | [Unity 操作ツール](guide/unity-ops.md) |
| 13. Scene ビューのマーカー・ピン・スケッチ | [マーカー・ピン・スケッチ](guide/scene-tools.md) |
| 14. スクリプトの再コンパイル・Play モードとの共存 | [再コンパイル・Play モードとの共存](guide/reload-and-play-mode.md) |
| 15. 設定画面リファレンス | [設定画面リファレンス](guide/settings.md) |
| 16. キーボードショートカット | [キーボードショートカット](guide/shortcuts.md) |
| 17. 困ったときは | [困ったときは](guide/troubleshooting.md) |
