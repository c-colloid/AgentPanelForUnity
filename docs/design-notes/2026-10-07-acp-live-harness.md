# ACP ブリッジを実エージェントで回す(`ci/AcpLive`)

日付: 2026-10-07

発端: 「その他 ACP のテストを仮想環境で構築して行なって」。
`2026-09-10-acp-backends.md` §5 は「**実機未検証**: Gemini CLI / codex-acp との
実接続はこのリポジトリ内では行えていない(ネットワーク越しにインストール
できない)」と結んでいた。今回はネットワークのあるコンテナで実際の ACP
エージェントを npm から入れ、本番のトランスポートをそのまま通して往復させた。

参照: `docs/design-notes/2026-09-10-acp-backends.md`、
`2026-09-10-in-panel-install-and-sign-in.md`(codex-acp の認証メソッド順の報告)、
`2026-09-17-acp-tool-name-mapping.md`、`ci/README.md`。

---

## 0. 結論(先に)

- **本番コードは 4 エージェントすべてで無修正のまま通った。** パッケージ側の
  変更はなし(ベータは切らない)。追加したのは CI 用のハーネスとこのノートだけ。
- `ci/AcpLive` は `AcpBridgeTransport` → `ClaudeCliProcess` → `AcpProtocolBridge`
  を **verbatim でコンパイル**し(Unity 依存なし)、本物のエージェントプロセスを
  stdio で起動して、`AgentClient` が書くのと同じ Claude 形の行で駆動する。
  `ci/SmokeTests` の `AcpSmoke` は純粋状態機械にスクリプト JSON を流すだけ
  なので、プロセスホスト・リーダースレッド・両側ロック・そして**実 CLI が
  実際に返す形**はこのハーネスが初めて検証した。
- 結果(2026-10-07、このコンテナ): 49 チェック全通過。

| エージェント | 版 | シナリオ | 結果 |
|---|---|---|---|
| ACP SDK example agent(`@agentclientprotocol/sdk` 同梱) | 1.7.0 | 往復(§2.1) | 31/31 |
| Gemini CLI `gemini --experimental-acp` | 0.63.0 | ハンドシェイク + サインイン要求 | 4/4 |
| codex-acp | 0.16.0 | ハンドシェイク + サインイン要求 | 4/4 |
| claude-code-acp | 0.16.2 | ハンドシェイク + モデル/モード/コマンド | 10/10(参考扱い、§2.3) |

> 2026-10-07 追記: プリセット以外の ACP エージェント 9 本(OpenCode、Qwen
> Code、Copilot CLI、Cline、Auggie、fast-agent、Mistral Vibe、Goose、Kimi Code)
> は `2026-10-07-acp-other-agents.md` に。

## 1. 構成

```
ci/AcpLive/
  AcpLive.csproj      本番ソースを verbatim で参照(Json / Protocol / Process / Acp)
  Program.cs          ハーネス本体(--scenario roundtrip|handshake、--expect …)
  agents/package.json 実エージェントの固定版(npm install で入る)
  run-live.sh         インストール → ビルド → 4 シナリオ → out/report.jsonl
.github/workflows/acp-live.yml   ACP 関連パスの変更時と手動で実行
```

- 実行: `ci/AcpLive/run-live.sh`(dotnet 8 + node 22、アカウント不要)。
  個別は `run-live.sh gemini` など。全行の生ログは `out/<label>.dump`。
- `IProcessKiller` はハーネス内の `Process.Kill(entireProcessTree)`。Windows の
  ツリーキル実装は Unity 側のままで、ここでは検証対象外。
- `--expect` キー: `session-ready` / `models` / `set-mode=<claude mode>` /
  `slash-commands` / `interrupt-idle` / `auth-started=<methodId>` /
  `handshake-failed[=substring]`。

## 2. 何を検証したか

### 2.1 往復(SDK の example agent)

SDK 同梱のサンプルは「テキスト → read ツール(完了)→ テキスト → edit ツールで
`session/request_permission` → 応答に応じた結末」を 1 秒間隔で再生し、
`session/cancel` にも応える。認証不要なので、CI で**毎回**本番ブリッジの
全経路を実プロセスで通せる。

確認した変換(§3.2 の表の行): `initialize` 応答 → `control_response` +
`system/init`、`user` → `session/prompt`(`isReplay` エコー)、
`agent_message_chunk` → `stream_event` text_delta → ツール直前に `assistant`
テキストへ畳む、`tool_call(kind:read, locations)` → `assistant` `tool_use`
(id = toolCallId、name `Read`、input に path)、`tool_call_update(completed)` →
`user` `tool_result`、`request_permission` → `control_request/can_use_tool`
(`tool_name` `Edit`、先に `tool_use` call_2 を告知)、allow → `result`
(`end_turn`, `is_error:false`)、2 ターン目の途中 `interrupt` →
`control_response` success + `result` `stop_reason:"cancelled"`(プロセスは生存)、
3 ターン目の deny → エージェントが「スキップ」して `end_turn`。最後に stdin
を閉じるとサンプルは自分で終了する(`Exited`)。

### 2.2 Gemini CLI 0.63.0 / codex-acp 0.16.0(サインイン手前まで)

アカウント無しで決定的に見られるのは「`session/new` が認証エラーを返し、
ブリッジが正しいメソッドで `authenticate` を送る」ところまで。両方とも
`AuthenticationStarted` が期待どおりのメソッドで上がった。

| | Gemini CLI | codex-acp |
|---|---|---|
| `agentCapabilities` | loadSession ✓、prompt image/audio/embeddedContext、mcp http/sse | loadSession ✓、prompt image/embeddedContext(audio ✗)、mcp http(sse ✗、acp ✗)、`sessionCapabilities` list/resume/close、`auth.logout` |
| `authMethods` | `oauth-personal`(Log in with Google)、`gemini-api-key`、`vertex-ai`、`gateway` | `chatgpt`(Login with ChatGPT)、`codex-api-key`(type env_var)、`openai-api-key`(type env_var) |
| 未認証の `session/new` | `-32000` "Gemini API key is missing or not configured." | `-32000` "Authentication required" |
| ブリッジが選んだメソッド | `oauth-personal` | `chatgpt` |
| stdin を閉じたとき | 自分で終了 | **終了しない** → `Stop`(interrupt 行 + 猶予)→ `Kill` で落とす |

- codex-acp の `authMethods` は 0.16.0 で **ChatGPT が先頭**になっている
  (2026-09-10 のノートが報告した「API Key が先頭」は解消済み)。ただし
  `RankAuthMethods` は順序に依存しないので、どちらでも `chatgpt` を選ぶ。
  `type:"env_var"` と `vars[]` というフィールドは ACP の新しい形で、ブリッジは
  無視して通る。
- codex-acp は応答 JSON の `id` を**末尾**に置く(`{"jsonrpc":…,"result":…,"id":1}`)。
  `AcpJsonRpc.TryParse` はキー順に依存しないので問題なし。
- **Gemini CLI はヘッドレス環境では `authenticate` 中に stdout へ非 JSON 行を
  吐き、stdin から認可コードを読もうとする**(「Please visit the following URL…」
  「Enter the authorization code:」)。`ClaudeCliProcess` は非 JSON 行を
  `[agent stdout]` としてログに落とし、ブリッジは壊れない。ただし、この状態で
  パネルが次の JSON-RPC 行を書くと Gemini 側はそれを「コード」として読む。
  デスクトップではブラウザが開くので通常は起きないが、ブラウザが開けない
  環境(SSH 越しの Unity、`NO_BROWSER`)では「ターミナルで `gemini` を実行して
  先にログイン」案内(既存の `LoginCommand`)に頼る以外にない。§4 に残す。

### 2.3 claude-code-acp 0.16.2(参考)

このコンテナには Claude Code のサインインがあるため `session/new` まで通り、
`loadSession` ✓、`sessionCapabilities` fork/list/resume、`authMethods` は
`claude-login` のみ、`models.availableModels` 4 件(default/opus/opus[1m]/haiku)
→ `initialize` 応答の `models[]` に `{value, displayName, description,
resolvedModel}` で載る、`modes.availableModes` 5 件(default/acceptEdits/plan/
dontAsk/bypassPermissions)→ `set_permission_mode("acceptEdits")` が
`session/set_mode` を通して success、`session/new` 直後の
`available_commands_update`(9 件)で `system/init` が**2 回**出て 2 回目に
`slash_commands` が入る(設計どおり: `InitMessageReceived` は無条件発火)。
サインインの無い CI ランナーでは `session/new` の結果が変わるため、
`run-live.sh` では **informational**(失敗しても exit 0)にしてある。
モデル呼び出しは一度もしていない(`session/new` とモード変更のみ)。

`ResolveModeId` は `acceptEdits` / `bypassPermissions` / `plan` 以外の文字列を
すべて「default 系」に写すので、未知のモード名を送ってもエラーにはならず
agent の default が選ばれる。パネルが送るのは Claude の 4 モードだけなので
仕様上の問題ではない(ハーネス作成中に一度「エラーになるべき」と誤って期待し、
`2026-09-10-acp-backends.md` §3.2 の「該当なしはエラー応答」は**該当モードが
agent に無いとき**の話だと確認した)。

## 3. ハーネスの流儀(次に足す人へ)

- 本番の `OutboundMessages` で行を作り、`StreamJsonMessage` ではなく生の
  JSON をそのまま見る(`AgentClient` が受ける形そのもの)。
- `WaitFor` は**新しい**行だけを進む。同じ read で 2 行届くもの
  (`session/new` と `available_commands_update`)は `_panel.Find` で遡る。
- 実 CLI は stdin を閉じても終わらないものがある(codex-acp、claude-code-acp)。
  `ShutDown` は 5 秒待ってから本番の `Stop` → `Kill` 経路を使う。これはパネルが
  再接続時に踏む経路でもある。
- API キー系の環境変数はワークフロー側で空にしている。サインイン要求を
  検証するテストなので、ランナーに鍵が紛れ込むと期待が反転し、しかも課金される。

## 4. 見送り・残件

- **Gemini CLI のヘッドレス認証経路**(§2.2)。ブラウザが開けない環境で stdin が
  奪われる問題。`authenticate` 送信後に非 JSON の stdout 行を検知したら
  「ターミナルでログインしてください」に切り替えて `authenticate` を打ち切る、
  という対処が考えられるが、ACP 仕様外の挙動依存になるので、実ユーザーの報告が
  あるまで保留。
- 認証済みの Gemini / Codex での `session/prompt` 往復(ツール名写像の実データ、
  `usage_update` の形、`models` の有無)。アカウントが要るため CI には載せない。
  `run-live.sh` は `GEMINI_API_KEY` などを**消さない**ので、鍵のある端末で
  `--scenario roundtrip` を手で回せば見られる(example agent 固有の
  `call_1`/`Read` 期待が付くので、その場合は `--scenario handshake` +
  `--expect session-ready` から始めるのが現実的)。
- Windows の `.cmd` シム経由起動とツリーキル。ハーネスは Linux のみ。
