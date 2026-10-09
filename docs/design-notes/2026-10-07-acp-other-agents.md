# その他の ACP エージェント 9 本を実機で回す(接続手順と動作状況)

日付: 2026-10-07

発端: 「Codex / Gemini / Grok / Claude Code **以外**のエージェントに対して ACP
接続の手順確認や動作状況のテストをしたい」。前ノート
`2026-10-07-acp-live-harness.md` のハーネス(`ci/AcpLive`、本番の
`AcpBridgeTransport` → `ClaudeCliProcess` → `AcpProtocolBridge` を verbatim で
コンパイルして実プロセスを駆動)に、プリセットに無い ACP エージェントを足した。
ユーザーガイドの `docs/guide/agents/custom-acp.md`(「その他の ACP エージェント
(カスタムコマンド)」)の表はこの結果で更新した。

参照: `2026-09-10-acp-backends.md` §3.2/§3.4、`2026-09-10-in-panel-install-and-
sign-in.md` §2.4(認証メソッドの順位付け)、`ci/AcpLive/run-live.sh`。

---

## 0. 結論(先に)

- **本番コードは 9 本すべてで無修正のまま、設計どおりの経路を通った。**
  パッケージ側の変更はなし(ベータは切らない)。変更は CI ハーネスと
  ドキュメントのみ。
- 入手できた「その他」は 9 本: OpenCode、Qwen Code、GitHub Copilot CLI、Cline、
  Auggie(Augment)、fast-agent、Mistral Vibe、Goose、Kimi Code CLI。
  Amp はヘルプに ACP モードが無く(`amp --help` に acp が出ない)、Cursor /
  Kiro / Crush / cagent / Stakpak は npm / PyPI に無いか GitHub API が引けず
  入手できなかった(§4)。
- 結果(2026-10-07、このコンテナ、アカウント無し): 13 本・106 チェック全通過
  (前ノートの 4 本を含む)。アカウント無しで `session/prompt` まで**実際に
  往復できた**のは OpenCode(無料モデル `opencode/big-pickle`、thinking +
  text + usage が変換された)と fast-agent(`FAST_AGENT_MODEL=passthrough`、
  入力をそのまま返す組み込みモデル)。残りは「サインインを要求 → ブリッジが
  仕様どおりに処理」まで。

## 1. 接続手順の表(ガイドに載せた内容の根拠)

| CLI | 版 | 入れ方 | コマンド | 引数 | 先にターミナルで |
|---|---|---|---|---|---|
| OpenCode | 1.18.35 | `npm i -g opencode-ai` | `opencode` | `acp` | 不要(無料モデル)。有料モデルは `opencode auth login` |
| Qwen Code | 0.25.0 | `npm i -g @qwen-code/qwen-code` | `qwen` | `--acp` | `qwen`(初回起動でサインイン) |
| GitHub Copilot CLI | 1.0.92 | `npm i -g @github/copilot` | `copilot` | `--acp` | `copilot login` |
| Cline | 3.0.69 | `npm i -g cline` | `cline` | `--acp` | `cline auth`(または接続時のデバイスコード、§2) |
| Auggie | 0.36.0 | `npm i -g @augmentcode/auggie` | `auggie` | `--acp` | `auggie login`(必須。ACP 経由の認証は非対応) |
| fast-agent | 0.10.42 | `uv tool install fast-agent-mcp` | `fast-agent-acp` | | モデルとキーを `fast-agent.yaml` / 環境変数で |
| Mistral Vibe | 2.26.0 | `uv tool install mistral-vibe` | `vibe-acp` | | `vibe`(初回セットアップ) |
| Goose | 1.53.0 | 公式インストールスクリプト | `goose` | `acp` | `goose configure`(プロバイダとキー) |
| Kimi Code CLI | 2.1.1 | 公式インストールスクリプト | `kimi` | `acp` | `kimi login` |

- 旧 `kimi-cli`(PyPI 1.52.0)は「no longer maintained」を出して終了する。
  Kimi Code CLI は別物(`code.kimi.com` のスクリプト)。
- Qwen Code の `--experimental-acp` は 0.25.0 では `--acp` になっている。
  ガイドの表も `--acp` に直した。
- npm の `cline` パッケージは `.bin` シムを作らず `bin/cline` だけが入る
  環境があった(postinstall 依存)。`npm i -g` なら PATH に出る。ハーネスは
  `node node_modules/cline/bin/cline --acp` で起動している。

## 2. 動作状況(ブリッジが何をしたか)

### 2.1 サインイン無しでセッションが開くもの

| | OpenCode | fast-agent(passthrough) | Goose(`GOOSE_PROVIDER` 指定時) |
|---|---|---|---|
| `agentCapabilities` | loadSession ✓、mcp http/sse、session list/resume/fork/close | loadSession ✓、mcp http/sse、session list/resume | loadSession ✓、mcp http(sse ✗)、session list/delete/close |
| モデル | `configOptions[id=model]` 236 件 → `initialize` 応答の `models[]` に載る | 無し | `configOptions[id=model]`(プロバイダ依存)→ 同上 |
| モード | `configOptions[id=mode]` build / plan → `plan` は `set_permission_mode("plan")` で `session/set_mode` が **success**。`acceptEdits` / `bypassPermissions` / `default` は同義語表に無く「no session mode matching」エラー(OpenCode の許可は `opencode.json` 側の設定で、モードではない) | `modes` 1 件 `agent` のみ | `modes` auto / approve / smart_approve / chat → `acceptEdits` → `auto`、`default` → `approve` が写る |
| スラッシュコマンド | `available_commands_update` 3 件 → 2 回目の `system/init` | 多数(status/tools/model/skills/…) | 11 件 |
| `session/prompt` | **実往復**: `agent_thought_chunk` → thinking ブロック、`agent_message_chunk` → text、`usage` → `result.usage` / `modelUsage`、`end_turn` | **実往復**: 入力(スタンディング指示の前置込み)がそのまま返る → `result.result` に含まれる | 未実施(キー無し) |
| 備考 | `session_info_update` という新しい update 種別はブリッジが「Ignored」でログに落とす(無害) | 同左 | `GOOSE_PROVIDER` 無しだと `session/new` が `-32603` "Configuration value not found: GOOSE_PROVIDER" → 認証エラーではないので `HandshakeFailed`(文面がそのままチャットに出る) |

### 2.2 サインインを要求するもの

| | `authMethods` | 未認証の `session/new` | ブリッジ | パネルに出るもの |
|---|---|---|---|---|
| Qwen Code | `openai`(Use OpenAI API key、`_meta.type:"terminal"`) | `-32000` "Authentication required: Use Qwen Code CLI to authenticate first." | `authenticate("openai")` → `-32603` Missing API key → 全メソッド失敗 → `HandshakeFailed` | 「sign-in failed for every method … Sign in to the agent's own CLI once, then press Sign in or Reconnect」 |
| Copilot CLI | `copilot-login`(`_meta.terminal-auth` に `copilot login` の実行形) | `-32000` "Authentication required" | `authenticate` → `-32000` → `HandshakeFailed` | 同上 |
| Kimi Code | `login`(`type:"terminal"`、`_meta.terminal-auth`) | `-32000` | `authenticate` → `-32000` → `HandshakeFailed` | 同上 |
| Mistral Vibe | `browser-auth`(Sign in through Mistral AI Studio) | `-32000` "Missing API key for mistral provider." | `authenticate` → ヘッドレスでは `-32603` "Failed to open browser for sign-in." → `HandshakeFailed`。デスクトップではブラウザが開き、完了後に `session/new` を再試行する設計どおりの経路 | 同上(デスクトップでは「ブラウザでサインインを完了してください」) |
| Cline | `cline` / `cline-pass` / `openai-codex`(ChatGPT Subscription) | `-32000` "Call authenticate before starting a session" | `authenticate("cline")` → **デバイスコード**: `https://authkit.cline.bot/device?user_code=XXXX` を **stderr** に出して待つ | `AuthenticationStarted`(「ブラウザでサインイン」注記)。stderr の URL は `AgentHub.OnStderrLine` が拾い「ブラウザが開かない場合は、このリンクを開いてください」の注記になる(既存機能。初稿で「設定にしか出ない」と書いたのは誤り) |
| Auggie | **空** | `-32000` "Auggie does not currently support authenticating over ACP. Please run `auggie login`" | メソッドが無いので `authenticate` せず即 `HandshakeFailed` | エージェントの文面そのまま(`auggie login` の案内が含まれる) |

- `RankAuthMethods` の順位付けは Cline で効いている: 3 メソッド中、アカウント
  ログイン `cline` を先に選んだ(`openai-codex` も候補に残る)。
- Qwen / Copilot / Kimi が出す `type:"terminal"` / `_meta.terminal-auth` は
  「このメソッドは端末で実行するもの」という ACP の新しい印。ブリッジは未知
  フィールドとして無視し、`authenticate` が失敗した時点の文面(「Sign in to the
  agent's own CLI once」)が結果的に正しい案内になっている。§3 に改善案。

## 3. 改善候補と実装(1・2 は同日に実装、v0.61.0-beta.5)

1. **`terminal-auth` を読んで `authenticate` を省く(実装済み)。**
   `AcpProtocolBridge.TerminalAuthCommand(method, agentCommand)` が各メソッド
   を分類する: `_meta["terminal-auth"]` の `{command, args}`(Copilot CLI、
   Kimi Code)はそのコマンド行、`type:"terminal"`(メソッド直下か `_meta`)
   で `args` だけのもの(Qwen Code の `--auth-type=openai`)はエージェント
   自身の実行ファイル名 + args。パスはファイル名に落とす(`copilot login`)。
   `Authenticate()` は順位付け後に端末メソッドを候補から外し、残りが無ければ
   `authenticate` を送らずに `TerminalSignInReason` の文面で `FailHandshake`
   する: 「This agent signs in from a terminal, not through the panel: run
   `qwen --auth-type=openai` once, then press Reconnect (Settings > Account).」
   ブラウザ系と混在するときはブラウザ系を先に試し、全部失敗したら失敗要約の
   後に「The remaining method must be completed in a terminal: run `kimi
   login` once …」を続ける。端末メソッドしか無いときは `AuthenticationStarted`
   / `Finished` を発火しない(AgentHub の「ブラウザで完了してください」注記を
   出さないため)。実行ファイル名は `AcpBridgeTransport.Start` が
   `bridge.AgentCommand` に渡す。
   実機: Qwen Code → "run `qwen --auth-type=openai` once"、Copilot CLI →
   "run `copilot login` once"、Kimi Code → "run `kimi login` once"(`run-live.sh`
   の `--expect no-auth-started` + `handshake-failed=…` で固定)。
2. **サインイン URL の表示(実装済み)。** stderr の URL は既に
   `AgentHub.OnStderrLine` が注記にしていた(Cline のデバイスコードはこれで
   出る)。足りなかったのは **stdout** に書くエージェント(Gemini CLI の
   ブラウザ無し経路)。`AcpBridgeTransport.OnAgentStdoutLine` が `{` で
   始まらない行を `ErrorOutput` に流すようにし(`LooksLikeJsonRpc`)、
   stderr と同じ経路でリンク注記と CLI 出力ログに載るようにした。ブリッジに
   届くのは JSON-RPC 行だけになる。実機: Gemini の
   `https://accounts.google.com/o/oauth2/...` が stderr チャネルに現れる
   (`--expect stderr-url=`)。
   残る問題: Gemini はその後 stdin から認可コードを読もうとする。パネルが
   次に書く JSON-RPC 行が「コード」として読まれる件は未対処(前ノート §4)。
3. **OpenCode のモード同義語。** `build` を `acceptEdits` / `bypassPermissions`
   の同義語にするかは判断が要る(OpenCode の build は「許可設定に従う通常
   モード」で、自動承認ではない)。現状のエラー応答のままで、ガイドに
   「OpenCode では plan のみ」と書くに留めた。
4. `session_info_update` を無視せずタイトル表示に使う(OpenCode / fast-agent
   が送る)。

## 4. 入手できなかったもの

| | 理由 |
|---|---|
| Amp(`@sourcegraph/amp`) | `amp --help` に ACP の項が無い(2026-10-07 時点のビルド) |
| Cursor Agent CLI | npm に無い(独自インストーラ)。未着手 |
| Kiro CLI | npm の `kiro-cli` 0.0.1 は別物 |
| Goose 以外のバイナリ配布(Stakpak、cagent、Crush) | GitHub API の `releases/latest` がこのコンテナから引けなかった。Goose は公式スクリプトが使えた |
| Grok Build | プリセット(`GrokBuild`)にあるため今回の「その他」から除外。実機未検証のまま |

## 5. CI への載せ方

`run-live.sh` の **required**(アカウント無しで決定的): example agent、
Gemini、codex-acp、OpenCode、Qwen Code、Copilot CLI、Cline、Auggie。
**informational**(落ちても exit 0): claude-code-acp(サインインが要る)と、
`ACP_LIVE_EXTRAS=1` のときの fast-agent / Mistral Vibe(uv)、Goose / Kimi
(ベンダーのインストールスクリプト。CDN 依存で、Kimi は 1 回接続リセットを
見た)。`acp-live.yml` は `astral-sh/setup-uv` を足して extras も回す。
`HOME` は `out/home` の使い捨てにして、ランナーやローカル機の実サインイン・
trust 設定が結果に混ざらないようにした。

## 6. ガイド用スクリーンショット(2026-10-08)と、撮影で見つかった不具合

### 撮り方

docker デーモンの無いコンテナだったので、GameCI のイメージは使わず Unity
2022.3.62f3 の Linux 版 tarball を直接展開し(22f1 は llvmpipe で UI Toolkit を
描かない、`2026-09-29-user-guide-split-and-sign-in-pages.md` §3)、
`Unity.Licensing.Client --activate-all --include-personal` で Personal シートを
取って Xvfb(1920x1080)で GUI 起動した。ホストは `ci/HostProjectCoreOnly`
(`make-core-only-host.sh` + `stamp-unity-version.sh`)。ドライバは既存の
`ci/HostProject/Assets/Editor/UapShotLogin.cs` に `send`(ユーザー発言)と
`scroll` を足したもの。`login-capture.sh` の `cmd` / `shot` を docker 無しで
動かす数行のシェルで駆動した(`import -window root` → パネル矩形で切り抜き)。
エージェントは `ci/AcpLive/agents/node_modules/.bin` へのシンボリックリンクを
`/usr/local/bin` に置いて解決結果の見た目を揃え、`xdg-open` は URL を記録して
成功を返すダミー(前回と同じ)。ソース変更後の再コンパイルは、ウィンドウ
マネージャが無いので `xdotool` でメインウィンドウをクリックしてから Ctrl+R。

撮ったもの(`docs/images/guide/agents/`): `custom-01-empty` / `custom-02-command`
(qwen `--acp`) / `custom-03-resolved`(opencode、接続済み)の差し替え、
`opencode-01-command` / `opencode-02-connected` / `opencode-03-reply`(無料モデル
`opencode/nemotron-3.5-lightning-free`。最初の `big-pickle` は無料枠のレート制限で
"Rate limit exceeded" が返ったので `~/.config/opencode/opencode.jsonc` の `model`
で切り替えた)、`qwen-01-terminal-signin`、`copilot-01-terminal-signin`、
`auggie-01-login-required`、`cline-01-device-link`(デバイスコードは撮影後に
失効)。設定画面は下の空白を詰め(背景と違う最後の行 + 14px)、チャットは
パネル全体。

### 見つかった不具合(同じベータで修正)

1. **ドメインリロード後に ACP のサインイン注記が出ない。** `AgentHub` の
   `OnAcpAuthenticationStarted/Finished` は `AuthCli.EnqueueCallback` でメイン
   スレッドに送るが、そのポンプ(`EditorApplication.update`)は Claude の
   サインイン補助が最初に呼んだときだけ購読される。ACP バックエンドでは誰も
   呼ばないので、リロード後はキューに溜まったまま: Cline の「ブラウザで
   サインイン」注記もリンク注記も出ず、`AcpSignInPending` も立たない
   (最初の Cline の撮影が空画面だった原因)。`AgentHub` が ACP トランスポートの
   イベントを配線するところで `AuthCli.EnsurePumpHooked()` を呼ぶようにした。
2. **端末専用認証 / 認証メソッド無しで再接続を 4 回繰り返す。** §3 の実装で
   端末専用のときに `AuthenticationFinished(false)` を出さなかったため、
   ハブの `_acpSignInFailedThisProcess` が立たず、プロセス死亡が「普通の死」
   として自動再接続された(Qwen の最初の撮影に 4 回分のエラーが並んだ)。
   Auggie(`authMethods` 空 + `-32000`)も元から同じ経路だった。どちらも
   `Finished(false)` を出して `FailHandshake` するようにし、Auggie 型は
   「sign-in required, but the agent offers no sign-in method over ACP
   (<agent の文面>)」という理由にした。
3. **「再接続を繰り返しませんでした」注記の `ACP agent` を実行せよ。**
   カスタムバックエンドは `LoginCommand` が空で表示名に落ちていた。
   ブリッジの失敗理由からバッククォートのコマンドを拾う
   `AgentHub.ExtractLoginCommandHint` を足し(`copilot login` /
   `qwen --auth-type=openai` / Auggie の文面の `auggie login`)、理由は
   `Finished` の時点で同期的に保存する(`_acpSignInFailReasonThisProcess`、
   死亡処理が遅延コールバックより先に走るため)。
4. **二重表示。** `authenticate` を送っていないのに「サインインに失敗しました:
   <理由>」の注記とハンドシェイクのエラーブロックが同じ文で並んだ。
   `AuthenticationStarted` を見たプロセスだけ失敗注記を出す
   (`_acpSignInStartedThisProcess`)。
5. 細かい点: 設定カードの例文 `qwen --experimental-acp` を `opencode acp`,
   `qwen --acp` に更新。設定カードの「サインインに失敗しました」の文は次の
   スポーンまで残るので、別エージェントのコマンドを入力した画面に前の
   エージェントのエラーが写る(撮影では Claude Code に一度戻して消した)。
   表示の寿命を見直す余地はあるが今回は触らない。

