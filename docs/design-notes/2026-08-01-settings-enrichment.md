# 設定画面の充実(v0.4.0)

日付: 2026-08-01 / ユーザー要望「設定画面の充実を行って」

## 1. スコープ(UX仕様 §5.6/§6.2 の拡張点+実用度で選定)

既存: CLIパス診断/Reconnect、permissionMode、Ctrl+Enter、ツール許可/拒否、危険スキップ、
フォントサイズ、CJKフォント、stderrログ。

追加する5点:

| # | 機能 | 根拠 |
|---|---|---|
| 1 | **カスタム指示**(`--append-system-prompt`) | CLI実フラグ確認済み。Unity前提の定型指示(「常に日本語で」等)。次回接続から適用 → SettingsChangeDetector の reconnect ヒント連動 |
| 2 | **表示セクション**: 思考ブロック表示(既定ON)/サブエージェントカード既定展開(既定OFF)/コスト表示(既定ON、OFFでトークンのみ) | サブスク運用ではコスト値が実質無意味(R05)。思考は好みが分かれる |
| 3 | **クイックアクション編集**(ラベル+プロンプト本文のリスト) | UX仕様§5.6「定型プロンプトはユーザーが設定で編集可能」。空状態の提案リストと、コンポーザ横 ⚡ メニューの両方に反映 |
| 4 | **通知**: 権限待ちビープ/ターン完了ビープ(いずれもパネル非フォーカス時のみ、既定OFF) | UX仕様§6.2 通知抽象化の第一歩。EditorApplication.Beep |
| 5 | **About**: パッケージ版(package.json)、CLI版(init応答 claude_code_version)、CHANGELOG/GitHubリンク | バージョン管理導入(v0.3.0)の可視化 |

非スコープ: MCPサーバ管理ビュー、テーマ編集、maxTurns、既定モデル(ヘッダーピッカーが既に永続化)。

## 2. 永続化の設計判断(UnityYAML地雷の回避)

- **クイックアクションのプロンプト本文はコード断片(brace-heavy)になり得る** →
  ScriptableSingleton(UnityYAML)には入れない。State.asset破損事件と同じ轍を踏まない。
  **`UserSettings/AgentPanel/QuickActions.json`** に自前 JsonWriter で保存
  (SessionCacheFile と同じ: アトミック書き込み・寛容ロード・破損時は握り潰さず1行ログ+初期化)。
- **カスタム指示も同様にコード例を含み得る** → 同じ理由で
  **`UserSettings/AgentPanel/CustomInstructions.txt`**(プレーンテキスト、UTF-8)に保存。
  PanelSettings(YAML)には入れない。
- 表示/通知トグル類は既存の PanelSettings(bool のみ、YAML安全)へ。

## 3. 各機能の実装方針

1. **カスタム指示**: AgentClientOptions に `AppendSystemPrompt` を追加し BuildArguments で
   `--append-system-prompt <text>` を付与(空なら省略=従来と同一引数列を維持する既存の回帰ガード
   パターンに従う)。SettingsView に multiline TextField(高さ上限+内部スクロール)。
   SettingsChangeDetector.RequiresReconnect の対象に追加。
2. **思考ブロック表示**: MessageBlockFactory が `PanelSettings.showThinking == false` のとき
   Thinking ブロックをスキップ。MessageListController のシグネチャに設定世代カウンタを混ぜて
   トグル時に再描画(既存 ReapplyContentRootStyling と同様の全パネル反映経路)。
3. **サブエージェント既定展開**: SubagentCard 生成時、ExpandedByToolUseId に記憶が無い場合の
   初期値を `PanelSettings.subagentDefaultExpanded` から取る(記憶は従来どおり優先)。
4. **コスト表示**: StatusBarView のコスト連結を `PanelSettings.showCostUsd` でガード
   (OFF時は「1.7k tok」のみ)。usage ポップオーバー内のコスト行も同様。
5. **クイックアクション**: 新 `QuickActionStore`(Model層、JSON永続化、
   `List<QuickAction {label, prompt}>`)。SettingsView にリストエディタ
   (行=ラベルTextField+プロンプトTextField+削除、末尾に追加ボタン。並べ替えは非対応=v1簡素化)。
   反映先: (a) EmptyStateView の提案リストの先頭に挿入、(b) ComposerView の ⚡ ボタン →
   GenericMenu でラベル一覧 → 選択でコンポーザに本文を挿入(即送信はしない。誤爆防止)。
6. **通知ビープ**: AgentHub の PermissionRequested / TurnCompleted 相当のフックで、
   `EditorWindow.focusedWindow` が AgentPanelWindow でないときのみ EditorApplication.Beep()。
   トグル2つ(permissionBeep / turnCompleteBeep、既定OFF)。
7. **About**: パッケージ版は `PackageInfo.FindForAssembly(typeof(AgentPanelWindow).Assembly)`、
   CLI版は AgentClient.InitMessage.ClaudeCodeVersion(未接続時は「未接続」)。
   CHANGELOG はパッケージ内実ファイルを `EditorUtility.OpenWithDefaultApp`、GitHub は Application.OpenURL。

## 4. 回帰ガード

- AgentClientOptionsExtensionTests: AppendSystemPrompt 未設定で引数列がバイト同一/設定で
  `--append-system-prompt` が正しく付く(引用符・改行・CJK)。
- SettingsChangeDetectorTests: カスタム指示変更が RequiresReconnect になる。
- QuickActionStoreTests: ラウンドトリップ(brace-heavy/CJK/改行入りプロンプト)、破損ファイル回復、
  空ストア既定。
- MessageBlockFactory: showThinking=false で Thinking ブロックが生成されない。
- StatusBarView 系ロジックがテスト可能な純関数なら: コスト文字列の有無。
- SubagentCard: 既定展開設定が記憶なしケースにのみ効く。

## 5. リリース

v0.4.0(minor: 機能のまとまり)。CHANGELOG [Unreleased] → 0.4.0 切り出し、
README の設定セクション&スクリーンショット(settings.png)更新。
