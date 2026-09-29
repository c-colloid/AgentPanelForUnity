# ACP の「サインイン方式」をエージェントごとに / インストール完了行の残留 / サインイン通知のエージェント名

日付: 2026-09-29 / 対象: v0.60.1-beta.6 / 発端: `2026-09-29-user-guide-split-and-sign-in-pages.md` §5
(サインイン手順の撮影中に見つけた 3 件)

## 1. 概要

| # | 現象 | 対応 |
|---|---|---|
| 2 | Gemini CLI 用に入れた「サインイン方式」`gemini-api-key` が、カスタム ACP に切り替えても欄に残り、そのままブリッジの `authenticate` に送られる | 値をエージェント(`AgentBackend`)ごとに保存 |
| 3 | インストール完了後の「… をインストールしました。接続しています...」が、未サインイン / サインイン中 / 接続済みになっても、別のエージェントに切り替えても、ドメインリロードまで消えない | 接続が決着したら、またはエージェントを切り替えたら隠す |
| 4 | Codex → Grok Build と切り替えると、Codex のサインイン要求の通知が「Grok Build へのサインインが必要です(ChatGPT)」と出る | 通知を出した(起動した)エージェントの名前で書く |

## 2. サインイン方式をエージェントごとに

### 原因

`PanelSettings.acpAuthMethod` は ACP バックエンド共通の 1 本の文字列だった。設定画面の欄はそれを
そのまま表示・保存し、`AgentHub` は起動するバックエンドに関係なくそれを
`AcpLaunchSpec.AuthMethodId` に入れていた。Gemini CLI のメソッド ID は他のエージェントには
存在しないので、残った値は「そのエージェントが提示しない方式を要求する」ことになる。

### 設計

- `PanelSettings.acpAuthMethods`: `List<AcpAuthMethodEntry { AgentBackend backend; string method; }>`。
  Unity のシリアライザは Dictionary を保存できないため、既存の設定と同じくリストで持つ。
  `GetAcpAuthMethod(backend)` / `SetAcpAuthMethod(backend, value)` 経由でだけ触る。
  空文字の Set はエントリを消す(「空欄 = エージェントが最初に提示する方式」を保つ)。
- 設定画面: 欄は選択中のバックエンドの値を表示し、入力はそのバックエンドにだけ保存する。
  バックエンドを切り替えると `RefreshBackendGroups` が欄の値を差し替える(入力中の欄は触らない)。
- 起動: `AgentHub` は `settings.GetAcpAuthMethod(backend)`(起動するバックエンド)を渡す。
- 再接続が必要かの判定(`SettingsChangeDetector`)は「起動時のバックエンドの値」と「今のバックエンドの
  値」を比べる。別のエージェントの値を変えても、動いているエージェントの再接続は求めない。
- `CloneNextSpawnOnlyFields` はリストを複製する(同じリストを共有すると、起動時スナップショットが
  その後の編集で変わってしまう)。

### 移行

旧フィールドは `[FormerlySerializedAs("acpAuthMethod")] private string legacyAcpAuthMethod`
として読み込み、`PanelStateStore.OnEnable` の `MigrateLegacyAcpAuthMethod()` で 1 回だけ移す
(`MigrateLegacyConsoleIgnores` と同じ形。移したら空にして、アセットを書き直す)。

| 旧値があるときの選択中のエージェント | 移し先 |
|---|---|
| ACP のどれか | そのエージェント(最後にその値で起動していたのはこれ) |
| Claude Code で、値が Gemini CLI の ID(`oauth-personal` / `gemini-api-key` / `vertex-ai`) | Gemini CLI(設定のヒントが例示していたのはこの 3 つだけ) |
| Claude Code で、それ以外の値 | 捨てる(どのエージェント用か分からず、別のエージェントに送るのがまさにこの不具合) |

移し先にすでに値があれば上書きしない。

実際の旧形式の `State.asset`(`agentBackend: 1`、`acpAuthMethod: gemini-api-key`)を
GameCI 2022.3.62f3 で開き、`acpAuthMethods: [{backend: 1, method: gemini-api-key}]` に
書き直されること、カスタム ACP の値が空であることを確認した。`JsonUtility` は
`FormerlySerializedAs` を見ないため、この経路は EditMode テストではなく実アセットで確認している
(テストは既存の移行と同じくテスト用の口 `SetLegacyAcpAuthMethodForTests` で移行ロジックを検証)。

## 3. インストール完了行の残留

`SettingsView.RefreshCliInstallState` は `AgentHub.LastCliInstallResult` があれば常にその行を
出していた。結果はドメインが続く限り残り、どのバックエンドのものかも見ていなかった。

純関数を 2 つ足した(`FirstRunView`):

- `IsConnectionSettled(state, signInPending, knownSignedOut)`: インストール後の接続が決着したか。
  クライアントが NotStarted / Starting 以外、ACP のサインイン要求中、Claude Code の
  `auth status` が未ログインと分かっている、のどれかなら決着。「サインインが必要」も答えの一つ。
- `ShouldShowInstallResult(last, selected, settled)`: 別のバックエンドの結果は出さない。
  成功の行は決着したら出さない(その時点ではカードの状態行が結果を言っている)。失敗の行は残す
  (理由が見えるのはそこだけ)。

チャット欄の「見つかりません」カード(`FirstRunView.RefreshInstallState`)も、カードのバックエンドと
違う結果は出さないようにした。

## 4. サインイン通知のエージェント名

`OnAcpAuthenticationStarted` / `Finished` は `AgentBackends.DisplayName(CurrentBackend)` で
通知を組んでいた。`CurrentBackend` は設定の選択そのものなので、ピッカーを Grok Build に
変えた直後(再接続前)に、まだ動いている Codex のプロセスから届いたサインイン要求が Grok Build の
名前で書かれた。

- 購読をその起動のバックエンドに束縛した(`AcpBridgeTransport` のイベントをクロージャで受け、
  起動時の `backend` を渡す)。通知は `FormatAcpSignInStartedNote(raisedBy, method)` で組む。
- ハンドシェイク中の終了で出す「サインインしていないため再接続を繰り返しませんでした」も、
  `ResolveNoteBackend(_lastSpawnedSettingsSnapshot, CurrentBackend)`(起動時スナップショットの
  バックエンド。まだ何も起動していなければ選択)で名前・ログインコマンド・API キーの案内を選ぶ。
- 設定カードの状態行は「選択中のエージェントについてのカード」なので、今のままで正しい。

## 5. テスト

- `AgentBackendsTests`: `AcpAuthMethod_IsPerBackend`、`MigrateLegacyAcpAuthMethod_*`(選択中の
  ACP へ 1 回だけ / Claude 選択時は Gemini ID だけ残す / 既存値を上書きしない / 旧値なしは変更なし)、
  `BackendFields_RequireReconnect`(別バックエンドの値の変更は再接続不要)、
  `CloneNextSpawnOnlyFields_CopiesBackendFields`(リストの複製)、`NoteBackend_*`、
  `SignInStartedNote_NamesTheAgentThatAsked`。
- `CliInstallPlanTests`: `InstallResult_*`(成功行は決着で消える / 別バックエンドでは出ない /
  失敗行は残る)、`ConnectionSettled_Table`。

GameCI 2022.3.62f3 の EditMode(関連 6 クラス、295 件)で、新規・変更したテストはすべて成功。
`AgentBackendsTests.ApplyAgentAccent_PutsExactlyOneAgentClassOnTheRoot_AndSwapsIt` だけ失敗したが、
変更前のコミットでも同じ環境で失敗する(撮影でカスタム ACP を選んだままのホストプロジェクトの
`UserSettings` に依存しているとみられる)。この変更とは無関係。

## 6. ガイド

`docs/guide/agents/gemini-cli.md` と `custom-acp.md` の「切り替えたら空欄に戻す」注意を、
「エージェントごとに保存される」説明に置き換えた。

## 7. 版

同じブランチの未マージのベータ v0.60.1-beta.6 に含める(番号は据え置き、ベータのコミットに畳む)。
