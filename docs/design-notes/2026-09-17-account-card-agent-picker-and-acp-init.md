# アカウントカードにエージェント選択を移す / ACP 合成 init の apiKeySource 誤読

日付: 2026-09-17 / 対象: v0.54.6 / 関連: 2026-09-10-acp-backends.md(4)、
2026-09-10-claude-api-key-auth-passthrough.md(3)、
2026-09-10-acp-auth-guidance-and-method-display.md(4)、2026-08-01-init-message-retention.md

## 1. 現象

ユーザー報告(スクリーンショット付き):

> 「APIキー認証」を「サブスクリプションのみ」にしているのに、アカウントカードに
> 「APIキー(acp)で接続中です -- 利用料はサブスクリプションではなくこのキーに課金されます」
> と出る。
> また、エージェントの切り替えが CLI カード、サインインがアカウントカードに分かれていて
> わかりづらい。

## 2. バグ: 「APIキー(acp)で接続中です」

### 2.1 根本原因

アカウントカードの Claude Code 分岐は `AgentHub.LastKnownInitMessage.ApiKeySource` を
読み、`none`/空以外なら「APIキー({0})で接続中」を出す(`SettingsView.IsApiKeyAuth`)。
これは Claude Code CLI の system/init が `apiKeySource` に `ANTHROPIC_API_KEY` /
`apiKeyHelper` / `none` を報告する前提の表示。

一方 ACP ブリッジ(`AcpProtocolBridge`)は、パネルの既存経路に乗せるために system/init を
**合成**しており、そこで `apiKeySource` に `"acp"` という目印値を入れていた。
ACP の認証方式は `AcpAuthMethodId/Name` で別に扱うので、ACP 分岐ではこの値は読まれない。

問題は `_lastKnownInitMessage` が **バックエンドを切り替えても保持される**こと
(2026-08-01-init-message-retention.md: 再開接続は system/init を再送しないことがあるため
意図的に残す)。ACP エージェントから Claude Code に戻すと、設定上は Claude Code なので
アカウントカードは Claude 分岐(ドロップダウン表示)になるが、`LastKnownInitMessage` は
まだ ACP の合成 init。`ApiKeySource == "acp"` は `none` ではないので「APIキー(acp)」が出る。
Claude の init が届くまで(再接続直後や、再開接続で init が来ないケースでは長く)残る。

### 2.2 修正

- `SystemInitMessage` に `AcpBackend`(合成 init だけが持つ `acp_backend` を読む)と
  `IsAcpSynthesized` を追加。
- `SettingsView.IsApiKeyAuth` は `IsAcpSynthesized` な init を無条件に false にする。
  ガードは `acp_backend` に基づく(下の `"none"` 化に依存しない)。
- `AcpProtocolBridge` の合成 init は `apiKeySource` を `"none"` にする。`"acp"` を読む
  箇所は他に無かった。ACP の目印は `acp_backend` のまま。
- 会話ログ側の一回限りの注記 `AppendApiKeyAuthNoteIfNeeded` は元から `IsClaudeBackend`
  でガードされていたので影響なし。

## 3. UI: エージェント選択をアカウントカードへ

### 3.1 なぜ分かれていたか

2026-09-10-acp-backends.md(4)で、エージェント選択は「実行ファイル/コマンド」と同じ
CLI カードに置いた(選択によって表示するフィールドが変わるため)。サインインはその後
2026-09-10-in-panel-install-and-sign-in.md でアカウントカードに足した。結果として:

- CLI カードは**既定で折りたたみ**なので、エージェントの切り替え口が見つけにくい。
- アカウントカードは「ログイン済みです」と言うが、**どのエージェントの**状態か書いて
  いない。ACP を選ぶとカードの中身が丸ごと入れ替わるのに、その理由が画面上に無い。

### 3.2 変更

- **エージェント選択(`_backendField`)をアカウントカードの先頭行へ移す。** カード名を
  「エージェントとアカウント」(en: "Agent & account")に変える。ドロップダウンの下に
  「次回の再接続時に適用されます。下のサインイン状態はこのエージェントのものです。」の
  1 行(既存のツールチップはホバー側に残す)。
- **並び順**を アカウント → CLI → 診断 → Pro の更新 → 情報 にする。選ぶ → サインインを
  確認する → 起動の仕組み(パス/コマンド/インストール)の順で、新規ユーザーが出会う順に
  合わせる。`BuildAccountSection` が `BuildCliSection` より先に走る必要がある(下の行が
  ドロップダウンの値を読む)。
- **CLI カードの先頭に「{エージェント} の起動設定です。エージェントの切り替えは上の
  『エージェントとアカウント』カードで行います。」**を出し、`RefreshBackendGroups` で
  追随させる。カードを開いたときに「実行ファイルのパス」から始まって主語が無い状態を
  避ける。
- 挙動(次回再接続で適用、`OnBackendChanged` の処理、ACP 用フィールド群の切り替え)は
  変えない。折りたたみキー `cli` も不変。

### 3.3 見送ったもの

- CLI カードをアカウントカードに統合する案: 1 枚が長くなり、既定で畳んでおきたい
  技術的フィールド(引数、認証方式 ID、インストール)まで常時見える。ドロップダウンだけ
  を移し、CLI カードは主語の 1 行で結び直す方が小さい。
- アカウントカードに「現在のエージェント: X」の読み取り専用行を置き、切り替え口は
  CLI カードに残す案: 表示が二重になり、なぜそこで変えられないのかが増える。

## 4. 検証

- `SettingsViewLogicTests.IsApiKeyAuth_IgnoresAnInitAnAcpBridgeSynthesized`:
  `acp_backend` 付き init は `apiKeySource` が何でも false、Claude の init は従来どおり。
- `SettingsViewLogicTests.SystemInitMessage_AcpBackend_ParsesAndFlagsSynthesizedInit`。
- `L10nTests` の 110 文字上限に新ヒント 2 本(en/ja)が収まること。
- 手動: Codex に切り替え → Claude Code に戻す → アカウントカードに「APIキー(acp)」が
  出ないこと。設定を開いてアカウントカードの先頭にエージェント選択があり、CLI カードを
  開くと先頭行にエージェント名が出ること。

## 5. 文言の追随

ユーザー向け文言の「設定 > アカウント」「設定のアカウントカード」(en: "Settings >
Account" / "Settings Account card")を新しいカード名に置き換えた(両言語 6 箇所ずつ)。
`docs/USER-GUIDE.md` の ACP 切り替え手順も同様。
