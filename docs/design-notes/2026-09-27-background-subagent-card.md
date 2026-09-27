# バックグラウンドサブエージェントがカードとして認識されない問題の修正

日付: 2026-09-27 / 対象: Core `jp.colloid.unity-agent-panel`

## 1. 症状と原因

`Agent` ツールを `run_in_background: true` で起動したサブエージェントが、
サブエージェントカードとして扱われない(報告: 「バックグラウンドエージェントが
サブエージェントカードとして認識されていないように感じる」)。

実際には **カードは生成されるが、起動直後に「完了」で固まり、その後の活動が
すべてトップレベルに漏れる**。原因は Phase 4 設計
([2026-07-31-subagent-display.md](2026-07-31-subagent-display.md) §3)が
「トップレベル `tool_result` = サブエージェントの最終確定」と定めていたこと。
フォアグラウンド起動では `task_updated` → `task_notification` → `tool_result`
の順で届くのでこれで正しいが、バックグラウンド起動では CLI が **起動直後に**
`tool_result`(「Async agent launched…」)を返し、その後で
サブエージェントが動く。`AgentHub.OnToolResultReceived` はその時点で
`_openSubagents` から record を外し `status = "completed"` にしていたため:

- 以後の `parent_tool_use_id` 付き assistant / user 行は「親不明フォールバック」
  (受け入れ基準 6)でトップレベルの通常カード・本文として描画される。
- `task_progress` / `task_updated` / `task_notification` は `ResolveSubagent` が
  null を返して黙殺され、進捗行もサマリも一切出ない。
- さらに親のターンが `result` で終わると `DemoteOpenRecordsAndClear` が
  残りを全部閉じるので、ターンをまたいで動くバックグラウンド作業は
  構造的に追跡できなかった。

## 2. 決定

| 案 | 内容 | 評価 |
|---|---|---|
| A: tool_result の本文(「launched」等)を文字列判定 | 起動応答を検出して閉じない | CLI の文言依存で脆い。却下 |
| **B: 入力の `run_in_background` で判定し、終端 task イベントで閉じる** ✅ | spawn 時に `SubagentRecord.background` を立て、background の record は tool_result では閉じず、終端ステータスの `task_notification` で閉じる(`task_updated` は status を書くだけで閉じない: 実測順は `task_updated{completed}` → `task_notification{summary}` なので、`task_updated` で閉じると直後のサマリが解決できず捨てられる — 初回 CI で実際にそう失敗した)。ターン終了(`OnTurnCompleted`)では background record と、その中で実行中の入れ子ツール呼び出しを生かしたまま残す | 実測済みのワイヤ構造(R02c: 入力形 `{subagent_type, description, run_in_background, prompt}`、task_* 4 種)だけに依存。フォアグラウンド経路は無変更 |

- 終端判定 `IsTerminalSubagentStatus`: `running` / `pending` / `in_progress`
  **以外**の非空ステータスを終端とみなす(将来の `killed` 等の綴りでも閉じ損ねない)。
- `tool_result` が `is_error` の場合は background でも従来どおり即 `failed` で閉じる
  (起動自体の失敗)。
- クライアント破棄(`TearDownClient` / 中断・プロセス死亡の `AbortOpenTurn`)は
  従来どおり background も `stopped` に落とす。プロセスが消えれば完了通知は
  二度と来ないため。
- `SessionCacheFile` は `background` を追加(欠落時 false、加算的変更)。
  `TranscriptLoader` も入力から同じフラグを復元する。

## 3. UI

- `SubagentCard` ヘッダに「background / バックグラウンド」タグ(`.uap-subcard-bgtag`)。
  親のターンが終わった後も作業中であることを示す唯一の場所になる。
- 経過時間ラベル: 外側 `ToolCallRecord` は起動時点で完了するため、background では
  `SubagentRecord.status == "running"` の間ライブ更新し、完了後は
  `task_notification` の `duration_ms` を表示する。

## 4. 未検証事項

本セッションの環境では `--dangerously-skip-permissions` を伴う CLI 起動が拒否され、
バックグラウンド起動の実ストリームは取得できなかった。テストは R02c の実測形式から
合成した行で組んでいる(`SubagentGroupingTests` の `BackgroundSpawn_*`)。
未確認の点:

- 起動応答 `tool_result` の正確な文言(本修正は文言に依存しない)。
- 完了時に CLI が親ターンを自発的に開始するか(`<task-notification>` の注入)。
  していれば通常の assistant ターンとしてそのまま描画される想定。

## 5. 回帰ガード

- `SubagentGroupingTests.BackgroundSpawn_StaysRunning_AfterLaunchResultAndTurnEnd`
- `SubagentGroupingTests.BackgroundSpawn_LaterWork_RoutesIntoTheCard_NotTopLevel`
- `SubagentGroupingTests.BackgroundSpawn_NestedCallStillRunningAtTurnEnd_ResolvesLater`
- `SubagentGroupingTests.BackgroundSpawn_TearDownClient_StillDemotesToStopped`
- `SubagentGroupingTests.ForegroundSpawn_ToolResultStillClosesTheRecord`
- `SessionCacheFileTests`: `background` のラウンドトリップ
