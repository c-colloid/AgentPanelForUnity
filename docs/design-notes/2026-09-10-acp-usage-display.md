# ACP バックエンドでのトークン量・コンテキスト量表示

日付: 2026-09-10 / 対象: v0.39.1 / 関連: 2026-09-10-acp-backends.md(3.「限界」の使用量項目を解消)

## 1. 現象

Codex(codex-acp)や Gemini CLI をバックエンドにすると、ステータスバーの
トークン数が常に「0 tok」、コンテキストメーターは非表示のままだった。
ACP ブリッジが `result` の `usage` を 0 で埋め、`modelUsage` を空で
返していたため(acp-backends.md 3 節で「ACP に usage が無い」として
既知の限界にしていた)。

## 2. 事実確認(2026-09-10)

- ACP スキーマ(`@agentclientprotocol/sdk` 1.4.0 の schema.json):
  - `session/update` の `usage_update`: `used`(コンテキスト中のトークン)、
    `size`(コンテキストウィンドウ)、任意の `cost {amount, currency}`
    (セッション累計)。RFD「Session Context Size and Cost」で標準化。
  - `PromptResponse.usage`(UNSTABLE): `inputTokens` / `outputTokens` /
    `cachedReadTokens` / `cachedWriteTokens` / `thoughtTokens` / `totalTokens`。
- codex-acp 1.11.0(`dist/index.js` を読解):
  - Codex の `thread/tokenUsage/updated` ごとに `usage_update {used, size}`
    を送る。`used` = 直近のモデル呼び出しの `totalTokens`、`size` =
    `modelContextWindow`。cost は送らない。
  - `PromptResponse.usage` = 直近呼び出しの TokenCount(`inputTokens` は
    キャッシュ分を引いた値、`cachedReadTokens` = cachedInputTokens)。
    ターン合計ではない。同じ値が `_meta.quota.token_count`(camelCase)
    にも入る。
- Gemini CLI 0.59.0(`bundle/gemini-*.js` を読解):
  - `usage_update` は送らない。`PromptResponse.usage` も無い。
  - `_meta.quota.token_count {input_tokens, output_tokens}`(ターン合計)と
    `model_usage[{model, token_count}]`(モデル別)を返す。
- Grok Build(xai-org/grok-build main を読解。`crates/codegen/xai-grok-shell`):
  - 標準の `usage_update` も `PromptResponse.usage` も送らない。すべて
    `_meta` 拡張。
  - コンテキスト幅: `session/new` / `session/load` 応答の
    `models.availableModels[].._meta.totalContextTokens`(`agent/config.rs`
    `to_acp_model_info`。Grok の TUI 自身も同じキーで分母を得ている)。
  - 使用量(ライブ): 全 `session/update` 通知の `params._meta.totalTokens`
    (`acp_session_impl/updates.rs`。コンテキスト中のトークン推定値)。
  - ターン終了: `PromptResponse._meta` に `totalTokens`(ターン後の
    コンテキスト)、`inputTokens` / `outputTokens` / `cachedReadTokens` /
    `reasoningTokens`(最終呼び出し分)、`usage`(PromptUsage: ターン全体の
    請求。`inputTokens` はキャッシュ読み込みを含む合計、`modelUsage{model}`
    行、`costUsdTicks` = 1e10 ティック/USD、`usageIsIncomplete` /
    `costIsPartial` 付き)、`modelId`。

## 3. 変換(AcpProtocolBridge)

パネル側(AgentHub / StatusBarView)は Claude の stream-json しか読まない
ので、ブリッジで次の形に写す。

| ACP 側 | パネル側 |
|---|---|
| `usage_update.used` | `result.usage.iterations[0]` の 4 フィールド和(`LastIterationContextTokens` → メーターの分子)。ターン中は次に出す `assistant` 行の `usage.cache_read_input_tokens`(ハブの live 読み値。input/output を 0 にして in-flight カウンタを膨らませない) |
| `usage_update.size` | `result.modelUsage[<現在のモデル>].contextWindow`(メーターの分母。`SelectPrimaryModelUsage` がモデル id 完全一致で拾う) |
| `usage_update.cost`(USD のみ) | `result.total_cost_usd`(累計。他通貨は単位が違うので捨てる) |
| `PromptResponse.usage` → 無ければ `_meta.quota.token_count`(snake/camel 両対応) | `result.usage.input/output/cache_*`(パネル合計に加算) |
| `_meta.quota.model_usage` | `result.modelUsage` のモデル別行(使用量ポップオーバー) |
| Grok: モデル一覧の `_meta.totalContextTokens` | `usage_update.size` が無いときの分母(現在のモデルの値。`set_model` で追従) |
| Grok: 通知 `params._meta.totalTokens` / 応答 `_meta.totalTokens` | `used` と同じ扱い(0 は「キューから外された」応答なので無視) |
| Grok: `_meta.usage`(無ければ `_meta.inputTokens` 系) | `result.usage`。`inputTokens` はキャッシュ込みなので `cachedReadTokens` と `cacheCreationTokens` を引いた値を input に。`costUsdTicks / 1e10` を累計コストに(partial / incomplete のときは捨てる) |

- ステアリング送信で複数 prompt が 1 ターンに畳まれる場合は各応答の
  usage を合算する。パネルターン開始でリセット。
- 何も報告されないときは従来どおり 0 と空 `modelUsage`(メーターは
  非表示のまま。0% と誤表示しない)。
- `session/load` 中の `usage_update` も保持する(再開直後の初期読み値)。

## 4. 限界

- Codex のターン内トークンは「最終呼び出し分」。ターン合計は codex-acp が
  公開しない(`/status` の文面のみ)。
- Gemini CLI はコンテキストメーターが出ない(サイズ・使用量の通知が無い)。
  トークン数のみ。
- Grok Build のメーター分子は Grok 側の推定値(`totalTokens`)で、実測の
  usage ではない(Grok の TUI と同じ数字)。
- 思考トークン(`thoughtTokens`)は出力に含まれるか不明なので加算しない。

## 5. 検証

- `AcpProtocolBridgeTests`: Grok の `_meta` 形(カタログ幅・通知 totalTokens・
  PromptUsage・最終呼び出しのみ・set_model 追従)、usage_update → iterations / contextWindow、
  Gemini の `_meta.quota` フォールバック、codex の camelCase、ターン中の
  assistant 行への live 読み値、USD 以外の cost 破棄、未報告時の 0、
  ステアリング合算、load 中の保持。
- `ci/SmokeTests`(dotnet): usage_update + usage が result に出ることを確認。
- 実機(codex-acp / Gemini)は未検証。
