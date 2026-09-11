# 設計ノート: 設定変更の反映タイミング(即時 / 次回接続時)とその伝達方法

- 日付: 2026-07-31 / ステータス: 採用済み
- 関連: ARCHITECTURE.md D2/D3、`SettingsView.cs`、`AgentClient.BuildArguments`、`AgentHub`

## 課題

`PanelSettings` の各フィールドは、変更した瞬間に効くもの(UI 側が毎回読み直す/CLI に制御リクエストを送れるもの)と、**次に CLI プロセスを起動し直すまで一切効かないもの**(起動引数として渡すだけで、動作中プロセスへ後から伝える手段が protocol 上に存在しないもの)に分かれる。この区別をユーザーに黙って UI から隠すと「設定を変えたのに効かない」という混乱を生む(D3 のリスク表が警告する類の UX 事故)。

## 反映方式の一覧(マトリクス)

| 設定 | 反映方式 | 根拠 |
|---|---|---|
| `permissionMode` | **即時**(接続中なら `set_permission_mode` control_request を即送信)+ 次回起動時の `--permission-mode` にも反映 | `AgentClient.SetPermissionMode` が既存(D2 3.2 で送信フォーマット確定済み) |
| `ctrlEnterToSend` | **即時**。`ComposerView` は毎回 `PanelStateStore.instance.Settings` を直接読むため、明示的な再適用コードは不要 | 既存実装がそもそもポーリング型 |
| `preferCjkUiFont` | **即時**(`AgentPanelWindow.ReapplyContentRootStyling()` で開いている全ウィンドウに再適用)+ `cjkUiFontDecided=true` を同時に立てて `OnEnable` の既定値ロジックに上書きされないようにする | フォントは静的引数ではなく UI 側の描画設定 |
| `fontSizePx` | **即時**(同上のヘルパーで再適用) | 同上。詳細は `2026-07-31-font-size-application.md` |
| `cliManualPath` | **次回接続時のみ**(`AgentClient.Start` 時に解決される実行ファイルパスは起動の瞬間にしか読まれない) | D1: パス解決は `AgentClient.Start` 呼び出し時の 1 回だけ |
| `model` | ~~次回接続時のみ~~ **更新(2026-07-31): 即時 + 永続化の両方**。ヘッダーのモデルピッカーが `AgentClient.SetModel`(`set_model` control_request)で接続中セッションへ即座に反映しつつ、`PanelSettings.model` にも保存して次回起動時の `--model` にも反映する。詳細は `2026-07-31-history-restore-flow-and-model-picker.md` §3 | 当初は Settings 画面に UI が無く「モデルピッカーは別エンジニアの担当範囲」として次回接続時のみに分類していたが、実装時に `set_model` がライブ反映可能な control_request として既に存在することが分かり、次回接続時のみに限定する理由が無かった |
| `allowedTools` / `disallowedTools` | **次回接続時のみ**(`--allowedTools`/`--disallowedTools` は起動引数。動作中セッションへ後から追加/除去する control_request は D2/D3 に存在しない) | `AgentClient.BuildArguments` 拡張のコメント参照。ワイヤーフォーマット自体は公開ドキュメント準拠だが実機未検証(下記「未検証事項」) |
| `dangerouslySkipPermissions` | **次回接続時のみ**(`--dangerously-skip-permissions` も起動引数) | 同上 |

## UI での伝達方法(検討した選択肢)

| 案 | 内容 | 判定 | 根拠 |
|---|---|---|---|
| A | 何も表示せず、ヒントテキストで「次回接続時に適用されます」と書くだけ | 棄却(部分採用) | 静的な文言だけでは「今の状態が既に古い」ことに気づきにくい |
| B | 各次回接続オンリー項目の隣に**静的**な注記を出す | 部分採用 | 分かりやすいが、実際に変更が保留中かどうかは分からない |
| C | 「最後に起動した時点のスナップショット」と「現在の設定」を比較し、差分があれば動的なバナーを出す | **採用(B と併用)** | ユーザーが実際に変更した後にだけ警告が出る。変更していなければ何も出ない(ノイズがない) |

## 決定

- `SettingsChangeDetector.RequiresReconnect(spawnedWith, current)` という純粋関数を追加(`Model` 層、Unity API 非依存、EditMode テスト完備)。次回接続時のみ反映される 5 フィールド(`cliManualPath`/`model`/`allowedTools`/`disallowedTools`/`dangerouslySkipPermissions`)だけを比較する。
- `AgentHub` は **接続成功のたびに**その 5 フィールドだけを複製したスナップショットを `LastSpawnedSettingsSnapshot` として保持する(全フィールド複製ではない -- 即時反映系フィールドは意図的に既定値のまま残し、誤読を防ぐためドキュメントコメントで明示)。
- `SettingsView` は各次回接続オンリー項目の変更ハンドラの最後で `RefreshReconnectHint()` を呼び、`RequiresReconnect` が真になった瞬間だけ「Some changes will apply the next time you reconnect.」という警告色のバナーを表示する。`AgentHub.Changed`(再接続完了を含む)でも再評価するので、`[Reconnect now]` を押して再接続が成功すればバナーは自然に消える。
- `AgentHub.Reconnect()` を新設(既存の `StartFresh` と違い、**同じ session_id を維持したまま** kill+respawn する)。Settings 画面の「Reconnect now」ボタンが呼ぶ経路であり、会話を失わずに次回接続オンリーの変更を即座に適用する手段を提供する。

## 未検証事項(次のラウンドへの申し送り)

`--allowedTools`/`--disallowedTools` の複数値の正確なワイヤーフォーマット(値をスペース区切りで флаг後に並べる形)は、本リポジトリの他の全フラグと異なり **実機の v2.1.218 プロセスに対して未検証**(R02 の実測ログに該当キャプチャが無い)。`AgentClient.BuildArguments` のコメントと `AgentClientOptions` のドキュメントコメントに明記済み。空リスト(フラグ自体を出さない)の場合は既存動作と完全に同一であることを回帰テストで保証しているため、**この不確実性は「両方のリストを実際に入力したときの動作」にのみ及ぶ**。次に実機接続で確認できるタイミングで `docs/research/02-claude-cli-protocol.md` に実測ログを追記し、このコメントを更新すること。
