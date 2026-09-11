# サブエージェント表示(Phase 4)設計

日付: 2026-07-31 / 根拠: [R02c 実測キャプチャ](../research/02c-subagent-captures.md) + 全レイヤ現状調査(Explore)

## 1. 現状(実証済み)

- `parent_tool_use_id` は Protocol 層 3 型(AssistantMessage:146 / UserEchoMessage:19 / StreamEventMessage:25)で
  パース済みだが、**そこで捨てられている**。AgentClient のイベント
  (`TextDelta(string)` / `ToolUseStarted(ContentBlock)` / `ToolResultReceived(ContentBlock)`)は親情報を運ばない。
- 結果: サブエージェントの発話・ツール呼び出しは**トップレベルの transcript に区別なく混入**する
  (AgentHub の単一 `_streamingAssistant` / `_openToolCalls` に合流)。
- `system/task_*` 4 サブタイプは未知型として黙殺。`TranscriptLoader` は `isSidechain` を読まない。
- 唯一の特別扱いは `ToolCardDescriber.cs:72-77`(Task の表示ラベルのみ)。
- Phase 1 検証(§8.2)で「実測フィクスチャ取得後に対応」と明示保留していた項目。フィクスチャは今回取得済み。

## 2. 選択肢と決定

### 2a. グルーピングの場所

| 案 | 内容 | 評価 |
|---|---|---|
| **A: AgentHub(モデル側)で振り分け** ✅ | parent 付きメッセージを top-level transcript に入れず、Agent tool_use に紐づく `SubagentRecord` へ集約 | キャッシュ/復元/描画が同一モデルを共有。UI は record を描くだけ |
| B: UI(MessageListController)で描画時グルーピング | ChatMessage に parent を持たせ flat のまま、描画時に束ねる | 300件間引き・差分シグネチャと干渉。キャッシュにも flat で残り復元時に再グルーピングが必要。却下 |

### 2b. アンカーのデータ構造

| 案 | 内容 | 評価 |
|---|---|---|
| **A: `ToolCallRecord` に nullable な `SubagentRecord` を追加** ✅ | Agent tool_use の既存レコードを拡張 | tool_result 相関(`_openToolCalls`)・許可フロー・result 由来のステータス確定が**無改修で流用**できる。キャッシュは加算的拡張 |
| B: 新 `ChatBlockKind.Subagent` ブロック | 専用ブロック型 | tool_result 相関とステータス遷移を二重実装することになる。却下 |

### 2c. イベント面(AgentClient)

- `ToolUseStarted` / `ToolResultReceived` を `Action<ContentBlock, string /*parentToolUseId*/>` に変更
  (内部 API。外部公開契約はない)。
- 新イベント `TaskEventReceived(SystemTaskEventMessage)` を追加(task_started / task_progress /
  task_updated / task_notification の写像)。未知の task_* サブタイプは従来どおり黙殺。
- `AssistantMessageCompleted` は既に全文型を運んでいる → AgentHub 側で `ParentToolUseId` により分岐。
- **前方互換ガード**: `HandleStreamEvent` は `ParentToolUseId != null` のデルタを top-level バッファに
  流さない(現行 CLI はサブエージェントをストリーミングしないが、将来変化しても本文が壊れない)。

## 3. データモデル

```csharp
// Editor/Model/ToolCallRecord.cs に追加
public SubagentRecord subagent;   // null = 通常ツール

// Editor/Model/SubagentRecord.cs(新規・[Serializable])
string taskId;            // task_started 由来(無ければ "")
string toolUseId;         // 結線キー(Agent tool_use の id)
string subagentType;      // "general-purpose" 等
string description;
string status;            // "running" | "completed" | "failed" | "stopped"
string progressLine;      // task_progress.description(現在の活動 1 行)
string lastToolName;
long totalTokens; int toolUses; long durationMs;
string summaryMarkdown;   // task_notification.summary
List<ChatMessageBlock> blocks;   // 入れ子 transcript(テキスト/ツールカード)
int droppedBlockCount;    // 上限超過で捨てた古ブロック数
```

- 入れ子 blocks 上限 `MaxNestedBlocks = 120`(超過は先頭から drop + カウント表示)。ブロック内
  ToolCallRecord の `subagent` は深さ 1 で打ち切り(孫は通常カード表示。spawnDepth 実測 1)。
- **グループ生成キー**: `OnToolUseStarted` で tool 名が `Agent`/`Task` **または input に
  `subagent_type` キーがある**場合に `SubagentRecord` を生成し `_openSubagents[toolUseId]` に登録。
  `task_started` は `taskId→toolUseId` 対応表の構築と description/type の補完に使う
  (どちらが先に来ても成立)。
- **振り分け**: parent 付き assistant → 対応 record の blocks へ(text/thinking を確定形で append。
  ストリーミングなしなので `_streamingAssistant` には触れない)。parent 付き tool_use/tool_result →
  record 内の入れ子 ToolCallRecord(相関は既存 `_openToolCalls` を共用。id はグローバル一意)。
  parent 付き user text(prompt 転写)は**入れ子に入れない**(Agent tool_use の input と重複するため)。
- **親不明フォールバック**: parent id が `_openSubagents` に無い → 従来どおりトップレベル描画
  (受け入れ基準 6。回帰テストで固定)。
- **ステータス遷移**: running → (task_updated.patch.status / task_notification.status) →
  トップレベル tool_result での確定(既存パス)が最終上書き。TurnCompleted 時点で running のままの
  record は "stopped" に落とす(interrupt 対応)。
- **SessionCacheFile**: toolCall オブジェクトに省略可能な `subagent` を追加(WriteBlock ↔
  WriteSubagent の相互再帰)。旧キャッシュはフィールド欠落として無害に読める(加算的変更)。

## 4. Protocol 層

`SystemTaskEventMessage`(新規): `Subtype, TaskId, ToolUseId, Description, SubagentType, Status,
LastToolName, SummaryMarkdown, OutputFile, TotalTokens, ToolUses, DurationMs, PatchStatus`。
`StreamJsonMessage.FromNode` の `system` 分岐で subtype が `task_*` 4 種のとき生成。
それ以外の system は従来どおり。

## 5. UI

- **SubagentCard**(新規、ToolActivityCard の意匠を踏襲した専用カード):
  - ヘッダ: 状態アイコン(スピナー/✓/✗) + `»` バッジ + subagentType + description + 経過時間
  - 実行中: `progressLine`(task_progress.description)+ `last_tool_name` + トークン計をライブ更新
  - 完了: summaryMarkdown を既存 Markdown パイプラインで描画(折りたたみヘッダ直下)
  - 展開: 入れ子 blocks を描画(text は通常本文、tool は ToolActivityCard 流用)。既定折りたたみ。
    `droppedBlockCount > 0` なら「N 件の古いステップを省略」ノート
- `MessageBlockFactory`: `toolCall.subagent != null` なら ToolActivityCard の代わりに SubagentCard。
  3 連続グループ圧縮(`ToolGroupMinRun`)の対象から subagent 付きは除外。
- `MessageListController.ComputeSignature`: subagent の
  (status, progressLine, blocks.Count, totalTokens) をシグネチャへ算入(ライブ更新の再描画トリガ)。
- USS: `.uap-subcard` 系(アクセント左ボーダー、バッジ、入れ子インデント)。`!important` 禁止・
  shorthand var() 禁止(UssHygieneTests が既にガード)。

## 6. 履歴復元(TranscriptLoader)

- メイン JSONL 側: user 行の `tool_use_result` エンベロープ(`status, agentType, resolvedModel,
  totalDurationMs, totalTokens, totalToolUseCount`)を読み、Agent tool_use に対応する record の
  確定状態を復元(サマリ = content の先頭 text)。
- サイドチェーン: `<projects>/<slug>/<sessionId>/subagents/agent-*.meta.json` を列挙し
  `toolUseId` で結線。**入れ子 transcript は展開時の遅延ロード**
  (`TranscriptLoader.LoadSubagentBlocks(jsonlPath)` — 既存の行処理を流用して blocks 化)。
  ディレクトリ/ファイル欠損時はサマリのみ表示に劣化しエラーを出さない(受け入れ基準 5)。
- メイン JSONL 内の `isSidechain` 行(旧形式)は従来どおり(読まない)。挙動変更なし。

## 7. 回帰ガード(新規テスト)

1. `ProtocolMappingTests`: task_subagent フィクスチャから Agent tool_use / parent id /
   task_* 4 種の写像を検証(現状は null ケースのみだった穴を塞ぐ)。
2. `SubagentGroupingTests`(新規): FakeCliProcess + AgentClient + Hub 経路でフィクスチャ全再生 →
   (a) トップレベル transcript にサブエージェント由来ブロックが混入しない
   (b) Agent の ToolCallRecord.subagent に入れ子 Bash カードと progress/summary が入る
   (c) 親不明 parent id はトップレベルにフォールバック
   (d) TurnCompleted で running → stopped。
3. `SessionCacheFileTests`: subagent 付きラウンドトリップ + 旧形式(subagent 無し)読み込み互換。
4. `TranscriptLoaderTests`: subagent_sidechain フィクスチャ(meta+jsonl)で復元・遅延ロード・
   欠損時劣化の 3 系。

## 7b. レビュー後追補: ライブ更新と展開状態(2026-07-31)

レビューで確定した「実行中カードの展開状態が毎tick破壊される」問題
(ComputeSignature が progressLine.Length / totalTokens を含む → task_progress 毎に行再構築 →
新品 SubagentCard は折りたたみ初期値)への対処。

| 案 | 内容 | 評価 |
|---|---|---|
| A: カードの in-place 更新 API | シグネチャ変化時に再構築せず既存カードを更新 | 行単位 diff の設計(1行=複数ブロック)を壊す。大改修 |
| **B: ホット項目をシグネチャから外し、カード自身が更新** ✅ | ToolActivityCard の経過時間表示と同じ既存イディオム(schedule().Every(500ms))で progressLine / lastToolName / トークン / 経過時間をカード内ライブ更新。シグネチャは構造変化のみ(status, blocks.Count, droppedBlockCount) | 再構築は「状態遷移 or 入れ子ブロック追加」時のみに減る |
| 併用: 展開状態の記憶 | SubagentCard に static Dictionary<toolUseId, bool>。生成時に復元、トグルで記録 | 入れ子ブロック追加による正当な再構築でも展開が保たれる。エディタセッション寿命の小さな辞書で許容 |

決定: **B + 展開状態記憶の併用**。lastToolName もシグネチャではなくカード内更新に含める
(レビュー所見「lastToolName がシグネチャ外で固まる」はシグネチャ追加ではなくカード内ライブ更新で解決)。
回帰ガード: (a) progressLine/totalTokens/lastToolName の変更でシグネチャが変わらないこと、
(b) status / blocks.Count の変更で変わること、(c) 展開状態が再生成を跨いで保持されること。

## 8. 明示的な非スコープ

- サブエージェントへの入力/操作(SendMessage 相当)、run_in_background タスクの一覧ビュー
- can_use_tool が parent 付きで来た場合の専用 UI(現行は通常カードで表示 — 実測では発火せず)
- メイン JSONL 内 isSidechain 行(旧 CLI 形式)の再グルーピング
