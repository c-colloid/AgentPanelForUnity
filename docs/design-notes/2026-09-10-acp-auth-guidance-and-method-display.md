# 2026-09-10 -- ACP バックエンドの認証案内の是正と、接続した認証方式の可視化

発端: Google が 2026-06-18 に、Gemini Code Assist for individuals / Google AI Pro /
Google AI Ultra 向けの「Login with Google」を Gemini CLI で終了した。個人ユーザーが
Gemini CLI を使う道は **Gemini API キー**(`GEMINI_API_KEY` 環境変数、または
`~/.gemini/.env`)か Vertex AI だけになり、Google ログインは Gemini Code Assist
Standard / Enterprise(Google Cloud プロジェクトが必要)のみ継続している。
出典: <https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals>

パネルは Gemini CLI プリセットを「Google アカウント(AI Pro/Ultra)または無料枠」と
説明したままで、これは誤りになっている。しかも ACP ブリッジは認証方式を
「明示設定 → ブラウザ/OAuth → デバイスコード → その他 → API キー系」の順に試すため
(`AcpProtocolBridge.RankAuthMethods`)、Gemini CLI では `oauth-personal` が失敗してから
`gemini-api-key` に落ち、`GEMINI_API_KEY` が無ければそこでも失敗する。会話ログには
「サインインしていない」としか出ず、**何を設定すればよいのかがユーザーに伝わらない**。

参照: `Editor/Core/Acp/AgentBackend.cs`、`Editor/Core/Acp/AcpProtocolBridge.cs`、
`Editor/Integration/AgentHub.cs`、`Editor/UI/SettingsView.cs`、
`Editor/UI/FirstRunView.cs`、`Editor/UI/L10n/L10n.cs`、
`Editor/UI/L10n/UiStrings.cs`、`Editor/UI/L10n/UiStringsJa.cs`。
先行ノート: `2026-09-10-acp-backends.md`(バックエンド選択)、
`2026-09-10-in-panel-install-and-sign-in.md` 2.4(認証メソッドの順位付け)、
`2026-09-10-claude-api-key-auth-passthrough.md`(Claude 側の API キー可視化。本ノートは
その ACP 版)。

---

## 2. 方針: パネルは API キーを保存しない

本ノートの全体を貫く前提。パネルに API キーの入力欄を作らない。キーの保存場所は
**各 CLI の公式な経路**(OS の環境変数、あるいは CLI 自身の設定ファイル)に任せる。
パネルが担うのは 3 つだけ:

1. 認証方式を剥がさない(v0.40.0 で Claude Code について既に達成済み。ACP バックエンドは
   そもそも環境に手を入れていない)。
2. **どの方式で接続したかを可視化する**(第 4 節)。
3. **未認証時にバックエンド別の正しい案内を出す**(第 3 節)。

理由は v0.40.0 と同じで、「ブロック」ではなく「見えるようにする」こと。加えて、キーを
パネルが預かると Unity プロジェクトの `State.asset` やリポジトリにキーが混入する経路を
作ってしまう。それは作らない。

バックエンドごとのキーの置き場所は `AgentBackends` に純粋関数として持たせた
(ログインコマンドを返す `LoginCommand` の隣):

| バックエンド | `ApiKeyEnvVars` | `ApiKeyConfigPath` | `ApiKeyHint`(文中に差し込む形) |
|---|---|---|---|
| Gemini CLI | `GEMINI_API_KEY` | `~/.gemini/.env` | `GEMINI_API_KEY (~/.gemini/.env)` |
| Codex (codex-acp) | `CODEX_API_KEY / OPENAI_API_KEY` | (なし) | `CODEX_API_KEY / OPENAI_API_KEY` |
| Grok Build | `XAI_API_KEY` | (なし) | `XAI_API_KEY` |
| Claude Code / カスタム ACP | 空 | 空 | 空(=案内文ごと出さない) |

空を返すことが「API キーの文を一切出さない」の合図になっている。カスタム ACP エージェントの
キーの置き場所はパネルには知りようがないので、推測で書かない。

---

## 3. 案内文の是正(Gemini は Fixed、Codex / Grok は Changed)

### 3.1 バックエンド別のサインイン説明

`UiStrings` にバックエンドごとの説明を足し、`L10n.ResolveAcpAuthSummary` /
`ResolveAcpAuthDetail`(純粋関数、カタログを引数に取るので日英両方をテストできる)で引く。
サブスクリプション側を推奨として先に書き、API キー経路を後ろに併記する。

**インラインは 1 行、長文はツールチップ**(`docs/design-notes/2026-08-04-settings-annotation-load.md`
4 節の規約。同じ規約は v0.40.0 の `SettingsClaudeAuthHint` にも後から適用された —
main の `79a3311`)。そこで 1 バックエンドにつき 2 本の文字列を持つ:

- `AcpAuthSummary*` = インラインの 1 行。**110 文字上限**(`AgentBackendsTests.
  AcpAuthSummary_StaysWithinTheInlineHintCap_InBothCatalogs` が日英両方で検査)。
  - Gemini CLI: 「サインイン: Gemini API キー(GEMINI_API_KEY、または ~/.gemini/.env)、
    または Code Assist Standard/Enterprise。」
  - Codex: 「サインイン: ChatGPT アカウント、または CODEX_API_KEY / OPENAI_API_KEY の API キー。」
  - Grok Build: 「サインイン: SuperGrok / X Premium+ のアカウント、または XAI_API_KEY の API キー。」
- `AcpAuthDetail*` = ホバー時のツールチップ。**なぜそうなのか**を書く場所で、Gemini なら
  「個人向けの『Login with Google』は 2026-06-18 に終了した」「Google ログインが続くのは
  Standard / Enterprise だけで Google Cloud プロジェクトが要る」を含む。

ツールチップは `L10n.AcpAuthTooltip(backend, loginHintFormat)` が
`AcpAuthDetail*` +「パネルは API キーを保存しません…OS の環境変数はエディタを再起動しないと
反映されません。」(`AcpAuthKeysNotStoredNote`)+ 従来からある「ターミナルでの代替: `{0}`」を
繋いで組む。設定 > CLI と初回カードは書式が違う(`SettingsAcpLoginHintFmt` /
`FirstRunAcpLoginHintFmt`)ので、その書式だけを引数で受け取る。ツールチップはラベル自身に
設定する(子の tooltip は親の tooltip に勝つので、説明対象の 1 行にきちんとスコープされる)。
要約が空のバックエンド(カスタム ACP エージェント)は従来どおりログインヒント行のままで、
それも空なら行ごと非表示・ツールチップ無しになる。

### 3.2 サインイン失敗時の「次の一手」

全方式が失敗して `OnProcessDied` が再接続を止めるとき、これまでの文言は
「ブラウザのサインインをやり直すか、ターミナルで `gemini` を実行してから再接続してください」
だけだった。Gemini CLI ではこれを実行しても個人アカウントでは通らない。そこで
`HubAcpSignInRequiredNoteFmt` の後ろに `AgentHub.ComposeAcpApiKeyGuidance(backend)` を
1 文足す:

> 代わりに API キーで Gemini CLI を使うには、`GEMINI_API_KEY (~/.gemini/.env)` を設定してから
> 再接続してください(OS の環境変数はエディタを再起動しないと反映されません)。パネルは
> API キーを保存しません。

`ApiKeyHint` が空のバックエンド(カスタム ACP)では 1 文まるごと出ない。
**ハンドシェイク死(`HubAcpHandshakeDeathNoteFmt`)には足さない**: あれは「起動前に落ちた」
= 未インストールなどが主因で、キーの話をする場面ではない。

なお `acpAuthMethod` の既定値とランキング(`RankAuthMethods`)は変えていない。Gemini CLI が
Standard / Enterprise の Google ログインを提示できる限り、それを先に試す現状の順序が正しい。

---

## 4. どの認証方式で接続したかを表示する(Added)

v0.40.0 が Claude Code についてやったこと(`apiKeySource` をアカウントカードに常時表示 +
会話ログに 1 回だけ注記)の ACP 版。ACP には `apiKeySource` に相当するものが無いが、
**`authenticate` が成功した方式の ID と表示名**は既にブリッジが持っている
(`_authMethodIds` / `_authMethodNames`、`AuthenticationStarted` / `AuthenticationFinished`)。

### 4.1 どこで捕まえるか

`AuthenticationFinished` は成功/失敗しか運ばない。イベントのシグネチャを変えると
`AcpBridgeTransport` と既存テスト 2 本を巻き込むので、代わりに `AgentHub` 側で
**`AuthenticationStarted` が最後に告げた方式を保持し、`AuthenticationFinished(true)` で
確定させる**。ブリッジは 1 方式ずつ順に試し、成功した時点で打ち切るので、これで
「実際に通った方式」が一意に決まる。

- `AgentHub.AcpAuthMethodId` / `AcpAuthMethodName`(public、main スレッドのみ)。
- リセットは `TearDownClient` と `StartClient` の 2 箇所(v0.40.0 の `_apiKeyAuthNoted` と
  同じ「1 スポーンにつき 1 回」の寿命)。`ResetAcpSignInState` では**消さない** —
  あれは Ready 遷移でも呼ばれるため、消すと接続直後に表示が消える。

### 4.2 表示

- アカウントカード(`SettingsView.RefreshAccountSectionAcp`): 接続中は常時
  「サインイン方式: {表示名}」。`authenticate` を経ずに接続できた場合(既にログイン済みで
  `session/new` がそのまま通った場合)は方式が分からないので
  「保存済みのログインで接続しています。」という中立な表示にする — 推測で方式名を書かない。
  純粋関数 `SettingsView.FormatAcpAuthMethodLine(id, name)` に切り出してテストする。
- 方式が API キー系のときだけ、その下に「利用料はサブスクリプションではなくそのキーに
  課金されます」の注記を出す。判定は `AcpProtocolBridge.IsApiKeyAuthMethod(id, name)` =
  `ClassifyAuthMethod` が 4(= `ApiKeyRank`)を返すもの。ランキングと同じ 1 つの分類器を
  使い回すので、「順位付けでは API キー扱いなのに表示では違う」というズレが起き得ない。
  ラベルは Claude 用の `_accountApiKeyAuthNoteLabel` を ACP 分岐で流用する(同じ場所・同じ
  意味の注記が 2 つ並ぶ状況は無い)。
- 会話ログ: `AgentHub.AppendAcpApiKeyAuthNoteIfNeeded` が、API キー系で通った場合にだけ
  1 スポーン 1 回の system note を出す。フラグは Claude 版と同じ `_apiKeyAuthNoted`
  (Claude 側の経路は `IsClaudeBackend` で守られているので衝突しない)。

---

## 5. 追加した文字列キー(日英とも)

`AcpAuthSummaryGemini` / `AcpAuthSummaryCodex` / `AcpAuthSummaryGrok` /
`AcpAuthDetailGemini` / `AcpAuthDetailCodex` / `AcpAuthDetailGrok` /
`AcpAuthKeysNotStoredNote` / `HubAcpApiKeyGuidanceFmt` /
`SettingsAccountAcpAuthMethodFmt` / `SettingsAccountAcpAuthMethodStored` /
`SettingsAccountAcpApiKeyAuthNoteFmt` / `HubAcpApiKeyAuthNoteFmt`。

`UiStrings` の末尾に `= null` 付きの省略可能引数として足し、`UiStringsJa.Create()` に
同名の名前付き引数を足す(v0.40.0 の `settingsClaudeAuthLabel` 以降と同じやり方)。

---

## 6. テスト

`Tests/Editor/AgentBackendsTests.cs`:

- `ApiKeyEnvVars_AreTheVariablesEachCliActuallyReads`
- `ApiKeyHint_AddsTheConfigFileOnlyWhereTheCliHasOne`
- `ComposeAcpApiKeyGuidance_NamesTheVariableAndItsFile_EmptyWithoutOne`
- `AppendSentence_JoinsWithOneSpace_AndToleratesEitherHalfMissing`
- `AcpAuthSummary_NamesTheKeyVariable_InBothCatalogs`
- `AcpAuthSummary_StaysWithinTheInlineHintCap_InBothCatalogs`(110 文字上限)
- `AcpAuthDetail_CarriesTheDeprecationDate_InBothCatalogs`(`2026-06-18` ほか)
- `ComposeAcpAuthHint_JoinsBothHalves_AndToleratesEitherMissing`
- `AcpAuthHint_IsTheShortSummary_AndTheTooltipCarriesTheRest`
- `FormatAcpAuthMethodLine_NamesTheMethod_ElseSaysSavedSignIn`

`Tests/Editor/AcpProtocolBridgeTests.cs`:

- `IsApiKeyAuthMethod_TrueForKeyAndGatewayMethods_FalseForAccountLogins`
  (方式なし = 既ログインを API キー扱いしないことも含む)
- `DescribeAuthMethod_PrefersTheDisplayName_ThenTheId_ThenEmpty`

---

## 7. 未検証・残件

- **実機未検証**: この作業環境に Unity も .NET コンパイラも無いため、コンパイルと EditMode
  テストの実行は行えていない。Gemini CLI / codex-acp / Grok Build との実接続も未確認で、
  特に「`authenticate` を経ずに接続できたときにアカウントカードが中立表示になる」経路は
  実機で見ていない。
- Gemini CLI が Standard / Enterprise 向けに提示する認証メソッドの **ID の実測値**は
  未確認(`oauth-personal` は個人向けのもの)。ID が変わっていても `ClassifyAuthMethod` は
  表示名のキーワードでも判定するため順位付けは壊れないが、文言側で ID を名指ししている
  `SettingsAcpAuthMethodTooltip` はいずれ実測に合わせて見直す余地がある。
- Vertex AI 経路の案内は書いていない(Google Cloud の設定が前提で、パネルが 1 文で
  案内できる範囲を超える)。
