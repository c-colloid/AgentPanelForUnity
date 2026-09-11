# 2026-09-10 -- Claude Code の API キー認証をブロックしない(パススルー化)

発端: Anthropic の Claude Code 利用条件(legal page)に、Claude Code を実行するプロダクトは
「Claude account でのサインインや、ユーザー自身の API キーでの認証を含め、組み込みの認証方式を
削除・無効化・制限してはならない("may not remove, disable, or restrict any authentication
method built into it (including methods that permit signing in with a Claude account or the
user's own API key)")」という条件がある、という指摘。本パネルは現状、子プロセスの環境から
`ANTHROPIC_API_KEY` を無条件に取り除いており、ドキュメントもユーザーに「絶対に設定しないで」
と案内している。これはこの条件に抵触する。

参照: `Editor/Core/Process/ClaudeCliProcess.cs`、`Editor/Core/Process/ClaudeAuthMode.cs`、
`Editor/Model/PanelSettings.cs`、`Editor/Model/SettingsChangeDetector.cs`、
`Editor/Integration/AgentHub.cs`、`Editor/UI/SettingsView.cs`、
`Editor/Core/Protocol/SystemInitMessage.cs`、`docs/research/02-claude-cli-protocol.md`。

---

## 1. 何を変えるか

- **デフォルトを反転させる。** これまでは `CLAUDECODE` / `CLAUDE_CODE_ENTRYPOINT` /
  `CLAUDE_CODE_SESSION_ID` / `ANTHROPIC_API_KEY` の 4 つを常に環境から Remove していた。
  v0.40.0 からは前者 3 つ(セッション汚染防止。認証とは無関係)だけを常に Remove し、
  `ANTHROPIC_API_KEY` はデフォルトで**一切触らない**。CLI をターミナルで直接実行したときと
  全く同じ認証選択(環境に API キーがあればそれを使い、無ければ保存済みのサブスクリプション
  ログインを使う)になる。
- **ブロックの代わりに可視化する。** 何もしないままだと、エディタのプロセスにたまたま
  `ANTHROPIC_API_KEY` が残っていた場合、ユーザーが気づかないうちに従量課金の API に
  切り替わってしまう恐れがある。これを防ぐのは「ブロック」ではなく「見えるようにする」ことだと
  判断した: `system/init` の `apiKeySource`(`SystemInitMessage.ApiKeySource`、既に実装済み。
  観測値は `"none"` と、公式ドキュメントが挙げる `"ANTHROPIC_API_KEY"` / `"apiKeyHelper"`)が
  `"none"` 以外を報告したら、設定 > アカウントカードに常時ノートを出し、会話ログにもセッション
  開始時に一度だけ system note を出す。
- **常にサブスクリプションだけを使いたい人向けの脱出ハッチを残す。** 新設定
  `PanelSettings.claudeAuth`(`ClaudeAuthMode` enum、`Auto` / `SubscriptionOnly`)。
  `SubscriptionOnly` を選ぶと、これまでどおり `ANTHROPIC_API_KEY` を Remove する
  (パネルの旧デフォルト動作そのもの)。デフォルトは `Auto`。

## 2. なぜ `Auto` をデフォルトにするか

- 上記の Claude Code 利用条件を満たすには、少なくとも「デフォルトでブロックしない」ことが
  必要。`SubscriptionOnly` を明示的なオプトインとして残すこと自体は、ユーザー自身の選択で
  ある限り問題にならない(条件が禁じているのは製品側が一方的に認証方式を削る・制限すること)。
- パネルの主要ユースケース(個人の Unity プロジェクトでの日常的な補助)ではサブスクリプション
  ログインが圧倒的に一般的で、`ANTHROPIC_API_KEY` が環境にあるのはほぼ「他のツール用に設定した
  ものが漏れてきている」事故のケースが多いと想定される。だからこそ「ブロック」ではなく
  「見える化」で十分に事故を防げると判断した(ブロックだと、意図的に API キーを使いたい人まで
  一律に締め出してしまい、利用条件の精神に反する)。

## 3. 実装

### 3.1 環境変数の除去(純関数セーム)

`ClaudeCliProcess.ComputeEnvVarsToRemove(ClaudeAuthMode claudeAuth): string[]`
(`ComputeSubagentModelEnvEntries` と同じ形の純粋な static メソッド、プロセスを起動せずに
テスト可能)。`Auto` → 3 つの名前のみ。`SubscriptionOnly` → その 3 つ + `ANTHROPIC_API_KEY`。
`ClaudeCliProcess.Start` の第 5 引数 `claudeAuth`(デフォルト `ClaudeAuthMode.Auto`)がこれを
呼ぶ。`ICliTransport.Start` にも同じ引数を追加した(Claude Code 専用: `AcpBridgeTransport` は
無視し、内部の `_process.Start` には常にデフォルト値を渡す -- 既存の `subagentModel` 引数と
全く同じ扱い)。

### 3.2 設定 → スポーンへの配線

`PanelSettings.claudeAuth`(既定 `ClaudeAuthMode.Auto`)を新設。`AgentHub.StartClient` が
`AgentClientOptions.ClaudeAuth = isAcp ? ClaudeAuthMode.Auto : settings.claudeAuth` として渡し、
`AgentClient.Start` が `ICliTransport.Start` の `claudeAuth` 引数にそのまま渡す
(`SubagentModel` と全く同じ配線)。`SettingsChangeDetector.RequiresReconnect` にも
`claudeAuth` の比較を追加した -- `subagentModel` 同様、`--resume` を含む**毎回**のプロセス起動
時に環境変数の除去リストが評価されるため、reconnect-relevant である。`AgentHub.
CloneNextSpawnOnlyFields` にも `claudeAuth` のコピーを追加(コピー漏れは 2026-08-03 の
`extensionProfilesEnabled` 事故と同じ形の不具合を再現するため)。

### 3.3 可視化

- **設定 > アカウントカード**: `PopupField<ClaudeAuthMode>`(「APIキー認証」ラベル、
  Auto/Subscription only の 2 択、ヒント + ツールチップ)。Claude Code 専用 -- ACP エージェントの
  ときは非表示(`RefreshAccountSection` の acp 分岐)。
- 同カードに、`AgentHub.LastKnownInitMessage.ApiKeySource` が `"none"`/空以外の間ずっと
  表示される注意ラベル(`SettingsView.IsApiKeyAuth` / `RefreshApiKeyAuthNote`)。文言:
  「APIキー(`{0}`)で接続中です -- 利用料はサブスクリプションではなくこのキーに課金されます。
  ここでのログイン/ログアウトはこれを変えません。常にサブスクリプションログインを使うには、
  上の「APIキー認証」を「サブスクリプションのみ」に設定してください。」
  (英語版: "Connected with an API key ({0}) -- usage is billed to that key, not to a
  subscription. Logging in or out here does not change this; set \"API key authentication\"
  above to Subscription only to always use the subscription login.")
- **会話ログ**: `AgentHub.AppendApiKeyAuthNoteIfNeeded`(`OnInitMessageReceived` から呼ぶ)。
  `AppendScriptGateInertWarning` と同じ「スポーンごとに一度だけ」の形(`_apiKeyAuthNoted` を
  `TearDownClient` でリセット)。文言は上のノートを一文に圧縮したもの
  (`HubApiKeyAuthNoteFmt`)。ステータスバーには専用のフックが無いため(調査済み)、この
  会話ログ通知とアカウントカードの常時ノートの 2 本立てで十分とした。

### 3.4 ドキュメント

`README.md` / `README.en.md` / `docs/USER-GUIDE.md` /
`jp.colloid.unity-agent-panel/Documentation~/index.md` / `UiStrings.FirstRunLoginBody2` の
「`ANTHROPIC_API_KEY` を絶対に設定しないで」という警告を、「サブスクリプションログインを推奨する
が、環境にあれば CLI がそれを尊重し従量課金に切り替わる。設定 > アカウントで実際にどちらが
使われているか確認できる」という中立的な文言に置き換えた。`docs/ARCHITECTURE.md` の該当 3 箇所
(前提セクション、D1 の環境変数掃除の記述、リスク表の #1)も同様に更新(それ以外は書き換えて
いない)。

## 4. テスト一覧

- `Tests/Editor/ClaudeCliProcessAuthEnvTests.cs`(新規): `ComputeEnvVarsToRemove` の
  Auto/SubscriptionOnly 両方で、除去される名前の集合が期待どおりであること。
- `Tests/Editor/AgentClientOptionsExtensionTests.cs`: `AgentClientOptions.ClaudeAuth` が
  `ICliTransport.Start` にそのまま渡ること、`BuildArguments` の文字列には一切現れないこと
  (`SubagentModel` のテストと同じ形)。
- `Tests/Editor/SettingsChangeDetectorTests.cs`: `claudeAuth` の差が reconnect 要求になる
  こと、同一なら要求しないこと。
- `Tests/Editor/AgentHubStartClientArgTests.cs`: `CloneNextSpawnOnlyFields` が
  `claudeAuth = SubscriptionOnly` を正しくコピーすること(デフォルト `Auto` 方向は自明なので
  非デフォルト方向だけを固定)。
- `Tests/Editor/PanelSettingsTests.cs`: 新規 `PanelSettings` の `claudeAuth` 既定値が
  `Auto` であること、UnityYAML(`JsonUtility`)を往復しても値が保たれること。
- `Tests/Editor/SettingsViewLogicTests.cs`: `SettingsView.FormatClaudeAuthOption` の
  2 値、`SettingsView.IsApiKeyAuth` が `apiKeySource` の値で正しく真偽を返すこと
  (`"none"` / 空 / null → false、それ以外 → true)。

## 5. 見送ったもの

- `AuthCli`(`claude auth status --json`)の `apiProvider` フィールドの利用。観測できている
  値が `"firstParty"` のみで、API キー認証を区別する値が実機確認できていない
  (`docs/research/02-claude-cli-protocol.md` / `Tests/Editor/AuthCliTests.cs` のフィクスチャ
  参照)。`system/init` の `apiKeySource` の方が「このセッションが実際に何で認証したか」を
  直接報告する、より確実な信号なので今回はそちらだけを使う。将来 `apiProvider` の値が
  API キー使用時にどう変わるか実機確認できたら、`AuthStatus` に追加してアカウントカードの
  判定に足す余地がある。
- ステータスバーへの専用インジケータ。既存の env-token ケース(`SettingsAccountEnvTokenNote`)
  同様、専用のステータスバーフックが無いため見送り、アカウントカード + 会話ログ一行で代替した。
