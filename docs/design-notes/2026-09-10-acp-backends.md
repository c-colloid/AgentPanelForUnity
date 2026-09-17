# 2026-09-10 -- Claude 以外のサブスク型 AI への対応(ACP バックエンド)

発端: 「Claude 以外のサブスク型 AI への対応」。Claude Pro/Max と同じように、
ChatGPT(Codex)や Google AI Pro(Gemini CLI)などのサブスクリプションで動く
エージェント CLI をこのパネルから使えるようにする。

参照: `docs/ARCHITECTURE.md`(D1/D2/D4 -- 「将来の拡張シームとして `IAgentTransport`
抽象だけ残す」)、`Editor/Core/Process/ICliTransport.cs`、
`Editor/Core/Client/AgentClient.cs`、`Editor/Integration/AgentHub.cs`、
Agent Client Protocol 仕様 <https://agentclientprotocol.com/protocol/v1/>。

---

## 0. 結論(先に)

- **プロトコルは ACP(Agent Client Protocol)一本に絞る。** 各社 CLI を個別に
  ラップするのではなく、Zed 発の標準プロトコル ACP(JSON-RPC 2.0 over stdio)を
  話す「ブリッジ」を 1 本書く。Gemini CLI(`gemini --experimental-acp`)、
  Codex(`codex-acp` アダプタ)、Qwen Code、Kimi CLI、OpenCode などが同じ
  プロトコルを実装しているので、対応範囲が一気に広がる。認証は各 CLI 自身の
  サブスクログイン(Google アカウント / `codex login` の ChatGPT ログイン)を
  そのまま使う。API キーは要らない。
- **パネル側の変換層は `ICliTransport` の別実装 1 つ。** `AgentClient` /
  `AgentHub` / UI は Claude Code の stream-json しか知らないまま。
  `AcpBridgeTransport` が ACP エージェントを起動し、両方向でプロトコルを
  翻訳する(§3)。ARCHITECTURE.md D4 が「将来の差し替えシーム」として残した
  場所そのもの。
- **設定は「エージェント」ピッカー 1 つ + ACP 用のコマンド/引数/認証メソッド。**
  既定は Claude Code のまま。Claude 専用の機能(パネル内ログイン、履歴ブラウザ、
  サブエージェントモデル、スクリプトゲートのフック)は ACP では黙って無効に
  なり、設定にその旨のヒントが出る(§4)。

---

## 1. なぜ ACP か

| 案 | 内容 | 評価 |
|---|---|---|
| A | Codex CLI の `codex exec --json` / app-server、Gemini CLI の `--output-format stream-json` を個別にラップ | CLI ごとにプロトコルが違い、権限プロンプトの双方向経路が揃っていない(特に一発実行系は途中で許可を求められない)。N 社 × N 実装 |
| B | **ACP ブリッジ 1 本** | 権限要求(`session/request_permission`)、キャンセル、ストリーミング、ツール呼び出しの状態、MCP サーバ登録(http + ヘッダ)まで仕様化済み。各社が実装を持つ |
| C | 各社 API を直接叩く | サブスク認証ではなく従量課金になる。本パネルの前提(README の IMPORTANT)と真逆 |

**採用: B。** 唯一の懸念は「ACP の実装度合いが CLI ごとに違う」こと。
`session/load`(再開)、`models`(モデル一覧、仕様上 unstable)、
`mcpCapabilities.http` の 3 つは capability を見て分岐し、無いものは劣化させる
(§3.2)。

## 2. 設定モデル

`PanelSettings.agentBackend`(enum `AgentBackend`、整数値は固定):

| 値 | 意味 | 既定コマンド |
|---|---|---|
| `ClaudeCode` (0) | 従来どおり。stream-json ネイティブ | `claude`(既存の probe) |
| `GeminiCli` (1) | Gemini CLI の ACP モード | `gemini --experimental-acp` |
| `CodexAcp` (2) | OpenAI Codex の ACP アダプタ | `codex-acp` |
| `AcpCustom` (3) | 任意の ACP エージェント | ユーザー指定(必須) |

`acpCommand` / `acpArguments` / `acpAuthMethod` は ACP 用。空欄は既定
(`AgentBackends.EffectiveCommand / EffectiveArguments`)。コマンドを差し替えた
場合は既定引数を捨てる(差し替えたコマンドに `--experimental-acp` を勝手に付けない)。
4 つとも次回スポーンのみ反映で、`SettingsChangeDetector` と
`AgentHub.CloneNextSpawnOnlyFields` に加えたので「再接続で適用」バナーが出る。

コマンド解決は `AcpCommandProbe`(`ICliPathProbe`)。名前だけなら PATH を走査
(Windows は `.exe/.cmd/.bat/.com` を付けて試す)、パス風ならそのまま。Claude 用の
Windows probe が npm の `.cmd` シムを避けて `node_modules` の exe を辿るのに対し、
ACP は CLI ごとにレイアウトが違うので `.cmd` をそのまま起動する(CreateProcess が
cmd.exe を挟む。ツリーキルで殺せる)。

## 3. ブリッジの設計

### 3.1 構成

```
AgentClient ──(Claude stream-json)──> AcpBridgeTransport : ICliTransport
                                         │  AcpProtocolBridge(純粋な状態機械)
                                         └─ ClaudeCliProcess(stdout インターセプタ付き)
                                                  │  JSON-RPC 2.0 / stdio
                                                  └─ gemini / codex-acp / ...
```

- `ClaudeCliProcess` に第 3 引数 `stdoutInterceptor` を足した。非 null なら
  stdout 行を `Output` に積まず、リーダースレッド上でそのままインターセプタに渡す。
  Windows のツリーキル・孤児掃除・リーダー排出のライフサイクル(CORE-4/5/10)を
  一行も変えずに流用するため。名前が Claude 固有なのは承知の上で、汎用 stdio
  子プロセスホストとして使う(CLAUDE_* 環境変数の除去は他の CLI に無害)。
- `AcpProtocolBridge` は I/O を持たない純粋クラス。`toAgent` / `toPanel` の 2 つの
  シンクに行を書くだけ。`AcpBridgeTransport` が 1 つのロックで直列化して呼ぶ
  (リーダースレッドからの agent 行、メインスレッドからの panel 行)。ロックを
  持ったまま子プロセスを待つ箇所はない。
- `AgentClient.Start` が組み立てる Claude 用フラグ文字列は**無視**する。必要な
  情報(再開 ID、モデル、権限モード、システムプロンプト、MCP エンドポイント)は
  `AcpLaunchSpec` で渡す。`AgentHub.StartClient` が spec を作り、UapOps サーバの
  起動後にエンドポイントを埋めてから `client.Start` する。

### 3.2 変換表

パネル → エージェント:

| Claude 行 | ACP |
|---|---|
| `control_request/initialize` | `initialize`(protocolVersion 1、fs/terminal は全部 false) → `session/load`(`ResumeSessionId` があり `loadSession` 対応時)または `session/new`(cwd + `mcpServers[{type:http, url, headers:[Authorization]}]`)。`session/new` が認証エラー(-32000 か文面)なら `authenticate` を 1 回試して再試行。成功したら Claude の `initialize` control_response(`models[]` を `{value, displayName, resolvedModel}` に詰め替え)と `system/init`(session_id, model, slash_commands, mcp_servers)を出す |
| `user` | `session/prompt`(text / image ブロック)。**新規セッションの最初の 1 回だけ**、カスタム指示を先頭ブロックに前置する(ACP にシステムプロンプトが無いため。`session/load` した会話は既に見ている)。`isReplay:true` のエコーを返して `TurnTracker.MarkAck` を満たす |
| 実行中の `user`(ステアリング) | キューに入れ、進行中の prompt が返ってから次を送る。Claude CLI が複数送信を 1 ターンに畳むのと同じく、**result はキューが空になってから 1 回だけ** |
| `control_response`(許可回答) | `session/request_permission` への応答。allow → `allow_once`(`updatedPermissions` 付きなら `allow_always`)、deny → `reject_once`、deny+interrupt はさらに `session/cancel` |
| `control_request/interrupt` | `session/cancel` 通知。待機中の許可要求には `cancelled` で応答 |
| `set_model` / `set_permission_mode` | `session/set_model` / `session/set_mode`。権限モードは同義語表(`acceptEdits`→auto-edit/auto、`bypassPermissions`→yolo/full-access、`default`→default/ask/read-only)で agent のモード ID に写す。該当なしはエラー応答 |
| `mcp_reconnect` | エラー応答(ACP に相当機能なし) |

エージェント → パネル:

| ACP | Claude 行 |
|---|---|
| `agent_message_chunk` | `stream_event` text_delta。次のツール呼び出しかターン終了で `assistant`(text)に畳む |
| `agent_thought_chunk` | `content_block_start`(thinking) + thinking_delta。同上で thinking ブロックに畳む |
| `tool_call` | 先に溜まったテキストを `assistant` に畳んでから、`assistant` に `tool_use` 1 個(id=toolCallId、name=§3.3、input=rawInput+title/kind+locations の path) |
| `tool_call_update`(completed/failed) | `user` に `tool_result` 1 個(content/diff/terminal を文字列化、無ければ rawOutput)。ターン終了時に未完了のものは合成 result で閉じる |
| `session/request_permission` | `control_request/can_use_tool`(request_id `acp-perm-N`)。`allow_always` があれば addRules 形の `permission_suggestions` を 1 個付け、既存の「常に許可」メニューがそのまま働く |
| `session/prompt` 応答 | 畳み込み → `result`(`stopReason` を `stop_reason` に、refusal は is_error) |
| `available_commands_update` | `system/init` を再送(`slash_commands` 更新。`InitMessageReceived` は無条件発火なので catalog が追従する) |
| `fs/*` `terminal/*` 要求 | JSON-RPC -32601(capability を宣言していない) |

### 3.3 ツール名の写像

パネルの描画(`ToolCardDescriber`)・自動承認(`AutoApprovePolicy`)・Undo バッジは
Claude のツール名で分岐する。ACP の `kind` を最も近い名前に写す:
read→`Read`、edit→`Edit`、execute→`Bash`、fetch→`WebFetch`、search→`Search`、
delete/move/think/other→`Delete`/`Move`/`Think`/`Tool`。describer は input に
`command` / `file_path` / `path` があればそれを、無ければ名前を出す設計なので、
入力形が違っても壊れない。

UapOps は特別扱い: title か rawInput に `uap_<snake>` があれば
`mcp__unity-ops__uap_<snake>` に写す。`UapOpsServer.FindByWireName` が解決でき、
自動承認レベル・読み取り専用判定・Undo バッジ・スクリプトゲートの事前フィルタが
Claude と同じに働く。

> 2026-09-17 追記: 「title か rawInput に含まれていれば」という文中検索はやめ、
> ツール id が入る場所だけを構造で読むようにした(Grok の `unity-ops__uap_x` /
> `use_tool` 形、Codex の `{server, tool, arguments}` 形、シェル実行の除外)。
> kind が other / 無しのときはエージェントの title を名前に出す。
> `2026-09-17-acp-tool-name-mapping.md` を参照。

### 3.4 劣化の仕方(capability 別)

| 無いもの | 挙動 |
|---|---|
| `loadSession` | 再開せず `session/new`。パネル側トランスクリプトは残るが agent の文脈は失われる(ドメインリロードのたび)。ノートに明記 |
| `models` | モデル一覧空。ヘッダーのピッカーはキャッシュ/空表示、`--model` 相当は送らない |
| `mcpCapabilities.http` | UapOps を登録しない。`system/init` の `mcp_servers` を `failed` で報告 |
| `promptCapabilities.image` | 画像ブロックを落としてログに残す |
| 認証 | `authenticate` 失敗時はエラー応答 + `is_error` の result(チャットにエラーとして出る)+ stdin を閉じてプロセスを終わらせる(AgentClient の死亡経路へ) |

### 3.5 ゾンビ掃除

`ZombieReaper.Record` にプロセス名を持たせた(既定 "claude")。ACP は実行ファイルの
ベース名(`gemini` / `codex-acp`)。`.cmd` シム経由だと実プロセスは `cmd` なので
名前不一致で**殺さない**(安全側)。

## 4. UI

- 設定 > CLI: 「エージェント」ポップアップ(Claude Code / Gemini CLI / Codex /
  その他 ACP)。Claude なら従来の実行ファイルパス欄、ACP ならコマンド・引数・
  認証メソッド ID の 3 欄 + 既定コマンドのヒント + 「先にターミナルでログイン」
  + 制限事項のヒント。「再検出」「今すぐ再接続」「解決結果」は共通。
- アカウントカードは Claude 以外では非表示(`claude auth` の面なので)。
- 初回セットアップカード(`FirstRunView`)はバックエンドに応じて表題・本文・
  インストールコマンドを差し替え、パス欄は `acpCommand` を編集する。
  `ChatView.ResolveFirstRunMode` に `claudeBackend` 引数を足し、ACP では
  「Claude にサインイン」カードを絶対に出さない(エラー本文に "login" が含まれても)。
- `BannerErroredText` を「エージェントのプロセスが終了しました」に一般化。

## 5. 検証

- `Tests/Editor/AcpProtocolBridgeTests.cs`: スクリプト化した ACP エージェントで
  §3.2 の全行を往復し、パネル側の行は `StreamJsonMessage.ParseLine` で再解釈して
  形を確認(ハンドシェイク・再開・認証・プロンプト・ストリーミング・ツール・
  許可・割り込み・ステアリング・fs 拒否・純粋ヘルパー)。
- `Tests/Editor/AgentBackendsTests.cs`: 既定コマンド、probe、設定変更検出、
  リーパー名、初回カードの判定。
- `ci/SmokeTests`: ブリッジの Unity 非依存部分を verbatim でコンパイルし、
  同じ往復を dotnet だけで回す(このコンテナに dotnet/mono が無く、EditMode も
  Unity CI 任せなので、コンパイルは両 CI が最初の検証になる)。
- **実機未検証**: Gemini CLI / codex-acp との実接続はこのリポジトリ内では
  行えていない(ネットワーク越しにインストールできない)。ACP 仕様 v1 の
  文面に従って書いたが、各 CLI 固有の癖(モード ID、`models` の有無、
  `authenticate` の挙動)は最初の実接続で見直す前提。ログは Diagnostics に出る。

## 6. 見送ったもの

- ACP エージェントの履歴ブラウザ(ACP に `session/list` が無い。Gemini CLI は
  独自形式で保存)。→ v0.47.0 で解消: 2026-09-13-acp-feature-parity.md(パネル側の
  セッション保存で一覧化。パネル内ログイン・サブエージェントのモデル設定も同ノート)。
- ACP 側のサブエージェント表示(`tool_call` の `kind:think` 程度しか手掛かりが無い)。
- 使用量/コスト表示(ACP に usage が無い。result は 0 で埋める)。→ v0.39.1 で解消: 2026-09-10-acp-usage-display.md(usage_update / PromptResponse.usage を変換)。
- 中継プロセスによるホットリロード(前ノートの判断どおり)。
