# 2026-09-10 -- 会話圧縮中の進行表示

## 症状

`/compact`(または CLI の自動圧縮)の最中、パネルは何も出さずに止まって見えた。
ステータスバーは「応答中...」のまま、会話欄には何も増えず、圧縮が終わって
`system/compact_boundary` が届くまで(長いと数十秒〜)固まっているように見える。

原因は CLI 側の出力の形にある。圧縮は「要約を作る 1 回の長い API 呼び出し」で、
その間 `stream_event` は一切流れない。パネルが圧縮を知る手段は、これまで
圧縮が**終わった後**に届く `compact_boundary` だけだった
(設計メモ 2026-09-07-slash-commands-and-compaction.md 第 2 節)。

## シグナル: `system/status`

CLI は粗い活動状況を `{"type":"system","subtype":"status","status":...}` で流す
(Agent SDK の `SDKStatusMessage`)。実キャプチャ
(`Tests/Editor/Fixtures/success_bidi_inbound.jsonl` 3 行目)では API 呼び出しの直前に
`"requesting"` が出ており、SDK の型では `"compacting" | null` を取る。この
`"compacting"` が、圧縮の開始から `compact_boundary` までの間に届く**唯一**の行である。

これまで `status` サブタイプは `InboundType.System`(無視)に落ちていた。今回
`SystemStatusMessage`(`InboundType.SystemStatus`、`AgentClient.StatusReceived`)に
配線した。必須フィールドはなく、`status` が無い/null なら空文字で落とさない
(`ProtocolMappingTests`)。`"compacting"` 以外の値は「圧縮していない」と読むだけで、
他に何もしない。

## ハブの状態: `AgentHub.IsCompacting`

- **ON**: `system/status` の `"compacting"`。加えて、パネル自身が `/compact` を送った
  瞬間(`SendUserMessage` で `SlashCommandCatalog.TryParse` + `IsCompact`)。後者は CLI の
  対応に依存しないヒントで、status 行が来ない古い CLI でも、コマンドが解釈される前の
  空白時間でも、送った直後から表示が出る。文中の `/compact` や `/compactor` は
  コマンド文法上コマンドではないので反応しない。
- **OFF**: `compact_boundary`(圧縮の終わり)、明示的なクリア(`status` が null / 欠落)、
  ターンの終了経路すべて(result の `OnTurnCompleted`、サイレンス・バックストップ/
  プロセス死/ティアダウンが通る `AbortOpenTurn`)、`StartFresh` / `SwitchToSession` /
  `ResetForTests`。
- `"requesting"` などその他の値は**無視する**。`"requesting"` は API 呼び出しの直前に
  毎回届き、圧縮の要約呼び出しの直前にも届き得る。これで OFF にすると、まさに待っている
  最中に表示が消える(`/compact` 送信側ヒントも同様に消えてしまう)。本当の「終わり」は
  boundary とターン終了であり、status に頼らない。状態が変わらない限り `Changed` も
  発火しない(再描画コストを増やさない)。
- ドメインリロードをまたいで永続化しない。リロード後は表示が消え、次の boundary か
  result で通常どおりに戻る(コンテキスト計と同じ「正直なフォールバック」)。

サイレンス・バックストップ(10 分)との関係: `status` 行自体が活動として記録されるが、
その後の圧縮本体は無通信になる。10 分を超える圧縮は想定外なので閾値は変えていない。

## 表示

1. **ステータスバー** (`StatusBarView`): `ShowCompactingState(IsCompacting, client.State)` が
   真(フラグ ON かつ Streaming / ToolRunning)のとき「コンテキスト圧縮中...」+ busy ドット。
   Ready / WaitingPermission / Errored では各状態の文言が勝つ。純関数なのでテストで固定。
2. **会話欄** (`ChatView`): メッセージリストと許可カードの間に、スピナー
   (`MessageBlockFactory.CreateSpinner`、ツールカードと同じ)+ 一文の行
   `.uap-compacting-row` をピン留めする。スクロール位置に関係なく見える。
   `MessageListController` の外に置いたのは意図的で、リストはメッセージ id で差分描画
   しており、メッセージでない一時行を混ぜると増分パスすべてに特例が要るため。

文言は `UiStrings.StatusCompacting` / `ChatCompactingIndicator`(日英)。

## 検証

EditMode: `ProtocolMappingTests`(compacting / 実キャプチャの requesting / null と欠落)、
`CompactingIndicatorHubTests`(実 `AgentClient` 経由で ON/OFF の全経路、`/compact` 送信、
`ShowCompactingState` のゲート)。このセッションには Unity も dotnet も無く、
スイートは未実行。CI(editmode-tests.yml)が検証。実 CLI での `"compacting"` 行の
キャプチャは未取得(SDK 型からのピン留め)。届かない CLI でも `/compact` 送信側の
ヒントで手動圧縮は表示され、自動圧縮は従来どおり boundary で初めて分かる。
