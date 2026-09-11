# 2026-09-09 -- ステータスバーのコンテキスト計 / トークン量をターン途中でも更新する

発端: ユーザー報告「コンテキストウィンドウ表示と使用トークン量表示の更新がリアルタイムでなく、
全ての会話が終わった後なので、長い処理を行うとかなりの長時間更新されない」。

## 1. 原因

`AgentHub` が `_lastContextTokens` と `ChatSession.total*Tokens` を書くのは
`OnTurnCompleted`(`type=result` 行)だけだった。ツール呼び出しを何十回も繰り返す
ターンでは result は数分〜数十分後にしか来ないため、その間ステータスバーの
メーターと "N tok" は **前のターンの値のまま**止まって見えた。

一方、CLI はターン中の各 API 呼び出しごとに `type=assistant` 行を流しており、その
`message.usage`(input / output / cache_read / cache_creation)は **その呼び出しが
実際に読んだコンテキスト量そのもの**である(result の `iterations[last]` と同じ
意味の数字。2026-09-07 ノート §2.2 参照)。これを捨てていた。

## 2. 決定

| # | 決定 | 根拠 |
|---|---|---|
| L1 | トップレベル(main chain)の assistant 行が来るたびに、その usage の 4 項目合計を `AgentHub.LiveContextTokens` として保持する。メーターは `CurrentContextTokens`(live ≥ 0 なら live、なければ result 由来の `LastContextTokens`)を読む | assistant 行の usage は「今のコンテキスト」を測る唯一の途中経過値 |
| L2 | 同ターンの assistant 行の input+output を message id 別に保持し、合計を `InFlightTurnTokens` として `FormatUsage` に上乗せする。result が来たら破棄(result.usage が Session に畳み込まれるので二重計上しない) | CLI は content block ごとに同じ message id・同じ usage を繰り返し送るため、単純加算では倍になる |
| L3 | サブエージェント(`parent_tool_use_id` が既知)の assistant 行は対象外 | サブエージェントは自分の窓を持つ(cache_read 0 から始まる)し、result.usage にも含まれない |
| L4 | 手動 `/compact` が pending の間は live 値を記録しない。auto compaction は boundary で live 値を捨て、次の assistant 行を最初の信頼できる値とする | 2026-09-07 §2.2 と同じ判断: 手動 compaction の要約呼び出しは捨てた旧コンテキストを読んでいる |
| L5 | `AbortOpenTurn` / `ResetForTests` / StartFresh / モデル切替で live 値を破棄 | 死んだターンの数字を残さない |

コストの suffix($)は result まで動かない(ターン途中に価格を出す値がワイヤに無い)。
初回ターンでメーターの分母(`modelUsage.contextWindow`)が無いのは従来通りで、
最初の result 以後は各ターン途中で動く。

## 3. 検証

`Tests/Editor/LiveTurnUsageHubTests.cs`(FakeCliProcess → AgentClient → AgentHub):
途中更新 / 反復で置換・累積 / 同一 id の重複排除 / result での引き継ぎと在庫クリア /
サブエージェント除外 / auto・manual compaction / AbortOpenTurn。
`StatusBarViewLogicTests` に `FormatUsage(session, showCost, inFlight)` の 2 件。
