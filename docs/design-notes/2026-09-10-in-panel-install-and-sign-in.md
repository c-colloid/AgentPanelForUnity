# 2026-09-10 -- 導入をパネル内で完結させる(インストールとサインイン)

発端: 「導入にターミナルを経由する必要があるのがユーザビリティが低く感じる」。
v0.37.0 の ACP バックエンド(`docs/design-notes/2026-09-10-acp-backends.md`)は
インストールもサインインも「ターミナルでやってから再検出」だった。Claude Code
自体も、初回カードが npm のコマンドを見せるだけで、パネルからは何もできなかった。

参照: `Editor/UI/FirstRunView.cs`、`Editor/UI/SettingsView.cs`(CLI / アカウント
カード)、`Editor/Ops/UnityPlugin/UnityPluginInstaller.cs`(main に入ったばかりの
ワンクリック導入の前例)、`Editor/Core/Acp/AcpProtocolBridge.cs`。

---

## 0. 結論(先に)

| 手順 | これまで | これから |
|---|---|---|
| CLI のインストール | 初回カードに npm コマンドを表示、ユーザーがターミナルで実行 | 初回カードと設定 > CLI の「{名前} をインストール」ボタン。確認ダイアログでコマンドを見せてから、パネルがバックグラウンドで実行し、進捗・結果を表示。成功したら自動で再検出・接続 |
| ACP エージェントのサインイン | 「先にターミナルでログイン」 | ブリッジが `authenticate` を送ると、エージェント自身がブラウザの OAuth を開く(Gemini CLI / codex-acp の実装)。パネルは「ブラウザでサインインを完了してください」を会話・ステータスバー・アカウントカードに出し、完了後は自動で続行 |
| Claude Code のサインイン | パネル内(アカウントカード)で既に可能 | 変更なし |

ターミナルは **代替手段** として残す(初回カードの「手動でインストールする場合」
折りたたみ、ACP のヒント文)。主経路は全部パネル内。

## 1. インストール

### 1.1 何を実行するか(`CliInstallPlan`、純粋)

| バックエンド | Windows | macOS / Linux |
|---|---|---|
| Claude Code | `powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://claude.ai/install.ps1 \| iex"` | `/bin/bash -lc "curl -fsSL https://claude.ai/install.sh \| bash"` |
| Gemini CLI | `cmd.exe /d /s /c "npm install -g @google/gemini-cli"` | `/bin/bash -lc "npm install -g @google/gemini-cli"` |
| Codex | 同上 `npm install -g @openai/codex @agentclientprotocol/codex-acp` | 同上 |
| その他 ACP | なし(コマンド欄を自分で入力) | なし |

- Claude Code は Anthropic 公式のネイティブインストーラ(CLI ドキュメントの
  コマンドそのもの。2026-09-10 に両エンドポイントがスクリプトを返すことを確認)。
  導入先の `~/.local/bin/claude(.exe)` は既存の probe が探す場所。
- npm パッケージ名とバイナリ名は npm レジストリで確認(`@google/gemini-cli` →
  `gemini`、`@openai/codex` → `codex`、`@agentclientprotocol/codex-acp` →
  `codex-acp`)。
- Unix は **ログインシェル**(`bash -lc`)で走らせ、nvm 等が PATH に足す npm を
  拾う。Windows は cmd.exe 経由で `npm.cmd` を解決させる。
- 実行前に `EditorUtility.DisplayDialog` で **コマンドを全文表示して確認**する。
  リモートのスクリプトを落として実行する行為なので、黙って走らせない。

### 1.2 実行(`CliInstaller`)

`UnityPluginInstaller` と同じ形(ワーカースレッド + `AuthCli` のメインスレッド
ポンプでコールバック)。違いは 2 点: stderr も取る(npm も公式インストーラも
失敗理由を stderr に書く)、タイムアウトは 10 分(回線次第)。超過時はツリーキル。

失敗は `CliInstallPlan.Classify` で 4 種に分類し、UI が次の一手を出す:

| 種別 | 判定 | UI |
|---|---|---|
| `NodeMissing` | npm の "command not found" / "is not recognized" / exit 127・9009 | 「npm が見つかりません。Node.js(LTS)を…」+「Node.js を入手」ボタン(nodejs.org) |
| `ShellMissing` | プロセス起動不可、curl/irm 不在 | 起動できない旨 |
| `TimedOut` | 10 分超過 | 停止した旨 |
| `CommandFailed` | それ以外の非 0 | 終了コード + 最終行(Diagnostics に全出力) |

成功したら `AgentHub` が `RecoverConnection()` を呼び、初回カードは追加操作なしで
チャットに切り替わる(probe が新しいバイナリを見つける)。

状態は `AgentHub`(`CliInstallRunning` / `CliInstallStartedUtcTicks` /
`LastCliInstallResult`)に持ち、初回カードと設定の両方が同じ
`FirstRunView.DescribeInstallState` で文言を作る。ドメインリロードをまたぐ
耐性は持たせない(インストール中にスクリプトを触る状況は稀。子プロセスは
生き続け、次回「再検出」で拾える)。

## 2. ACP のサインイン

### 2.1 仕組み

ACP の `authenticate` は「エージェント側でサインインを完了させる」要求で、
Gemini CLI(`oauth-personal`)と codex-acp(`chatgpt`)はここでブラウザの
OAuth フローを開き、localhost コールバックを待つ(Zed の Authenticate ボタンが
この経路)。ブリッジは v0.37.0 から `session/new` の認証エラーで `authenticate`
を 1 回送っていたので、**プロトコル上は既にパネル内サインインだった**。足りなかった
のは (a) ユーザーへの説明、(b) 60 秒の初期化タイムアウト、(c) ブラウザが開かない
ときの URL。

### 2.2 変更

- `AcpProtocolBridge.AuthenticationStarted / AuthenticationFinished` イベント
  (`AcpBridgeTransport` が中継)。`AgentHub` が購読し、`AcpSignInPending` /
  `AcpSignInMethodName` / `AcpSignInError` を持つ。開始時に会話へシステムノート
  「{agent} へのサインインが必要です({方式})。開いたブラウザで完了してください」、
  失敗時は警告ノート。`Starting` を抜けたらフラグをクリア。
- `AgentClientOptions.InitializeTimeoutSeconds`(0 = 従来 60 秒)。ACP は
  `AgentHub.AcpInitializeTimeoutSeconds` = 600 秒。
- stderr 監視: サインイン待ちの間だけ、stderr 行の最初の URL を
  `AcpSignInUrl` に取り、「ブラウザが開かない場合はこのリンク」ノート + アカウント
  カードの URL 欄 / 「ブラウザで開く」。エージェントはブラウザを開けないときに
  URL を stderr に出す(Gemini CLI の headless 動作)。
- ステータスバー: `Starting` かつサインイン待ちなら「ブラウザでのサインインを
  待っています...」(「接続に時間がかかっています」の再接続ボタンは出さない)。
- アカウントカード: ACP でも表示。状態(接続済み / 未接続 / サインイン待ち /
  失敗)と「サインイン / 再接続」ボタン(= `RecoverConnection`。新プロセスで
  ハンドシェイクからやり直し、必要なら再び `authenticate`)。Claude 用の
  ログイン UI は非表示。
- 文言から「先にターミナルで」を外し、ターミナルは代替として末尾に残す。

### 2.3 限界

- `authenticate` がブラウザを開くかどうかはエージェント実装次第。API キー方式
  (`gemini-api-key` 等)を選ぶと環境変数が必要で、パネルからは設定できない
  (設定の「認証メソッド ID」は既定の最初の方式のまま推奨)。
- 実機接続は本リポジトリでは未検証(前ノート §5 と同じ理由)。

### 2.4 実機報告への修正(v0.38.1): 認証メソッドの順位付けと再接続ループ

v0.38.0 を Codex(codex-acp)で試した報告: 「Codex へのサインインに失敗しました:
CODEX_API_KEY or OPENAI_API_KEY is not set … サインインが必要です(API Key)」が
4 回繰り返され、毎回「プロセスが終了しました。再接続しています」で終わる。

原因は 2 つ。

1. **最初のメソッドを取っていた。** codex-acp の `authMethods` は
   `api-key` / `chat-gpt` / `chat-gpt-device-code` / `gateway` の順(ソース確認)。
   ブリッジは「設定の指定が無ければ先頭」だったので、環境変数が無いと絶対に失敗する
   API Key を選び、2 番目のブラウザログインに届かなかった。
2. **失敗しても同じ手順を再試行していた。** ハンドシェイク失敗 → stdin を閉じて
   プロセス終了 → `OnProcessDied` の自動再接続(最大 3 回)→ 同じ失敗、の繰り返し。

対応:

- `AcpProtocolBridge.RankAuthMethods`: 明示指定 → ブラウザ/OAuth 系(chatgpt /
  oauth / google / login / browser / personal / account …)→ device code 系 →
  未分類 → 鍵/ゲートウェイ系(api+key / apikey / gateway / vertex / token)の順。
  id と表示名のキーワードで分類するので、未知のエージェントでも「環境変数が要る
  ものは最後」になる。
- `Authenticate` は候補を順に試し、失敗したら次へ。全滅したときだけ
  `FailHandshake`(全失敗の要約 + そのバックエンドのターミナルログインコマンド)。
  `AuthenticationStarted` は試行ごと、`AuthenticationFinished` は最終結果で 1 回。
- `AgentHub.ShouldAutoReconnectAfterDeath(acpSignInError)`: サインイン失敗の
  直後の死亡は自動再接続しない(クラッシュ回数にも数えない)。代わりに
  「サインインしていないため再接続を繰り返しませんでした。設定 > アカウントの
  『サインイン / 再接続』か、ターミナルで `codex login`」を 1 回だけ出す。
- 「Claude CLI プロセスが終了しました」の文言を「エージェントのプロセス」に一般化。

codex-acp の ChatGPT メソッドは、`NO_BROWSER=1` でない環境ではブラウザログインを
開く(README)。実機での確認はまだ無いが、これで少なくとも「絶対に失敗する方式を
4 回繰り返す」ことは無くなる。

### 2.5 Grok Build(v0.39.0)

xAI 公式の Grok Build(`grok`、Apache 2.0、SuperGrok / X Premium+)は ACP を
ネイティブに話す(`grok agent stdio`)。プリセットとして追加:

| 項目 | 値 | 根拠 |
|---|---|---|
| コマンド / 引数 | `grok` / `agent stdio` | agent-shell の実装 PR、xai-org/grok-build ドキュメント 15 |
| インストール | `curl -fsSL https://x.ai/cli/install.sh \| bash` / `irm https://x.ai/cli/install.ps1 \| iex`(`~/.grok/bin` に配置) | 公式 README とスクリプト本文で確認 |
| ログイン | `grok login`(ブラウザで grok.com、トークンは `~/.grok/auth.json`) | 公式 authentication ガイド |

注意: Grok Build は未サインインだと `session/new` にエラーを返す代わりに
`AuthorizationRequired` で**プロセスごと終了する**という報告がある(agent-shell PR)。
そのため `AgentHub.ShouldAutoReconnectAfterDeath` を拡張し、ACP バックエンドが
Ready に到達する前に死んだ場合も自動再接続しない(「接続確立前に終了したため
再試行しませんでした。インストールとサインイン(`grok login`)を確認して…」を
1 回出す)。Claude Code の早期死亡は従来どおり 3 回まで再試行する。

`AcpCommandProbe` は PATH の後に各社の導入先(`~/.grok/bin`、`~/.local/bin`、
npm の global bin、Windows は `%APPDATA%\npm`)も見る。エディタ起動時の PATH に
無くても、パネルから入れた直後のバイナリを見つけられる。

## 3. 検証

- `Tests/Editor/CliInstallPlanTests.cs`: プラン(バックエンド × OS)、失敗分類、
  状態文言、URL 抽出、ブリッジのサインインイベント、タイムアウト定数。
- `ci/SmokeTests` に `CliInstallPlan.cs` を追加(Unity 非依存)。
- 実インストール・実サインインは CI では走らせない(ネットワークとアカウントが
  要る)。
