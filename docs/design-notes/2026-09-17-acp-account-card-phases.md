# ACP エージェントの「エージェント」カード:接続済みでもサインインボタンが出続ける

日付: 2026-09-17 / 対象: v0.56.1 / 関連: 2026-09-13-acp-feature-parity.md(§2 パネル内サインイン)、
2026-09-17-agent-card-merge.md(カード統合)、2026-09-10-in-panel-install-and-sign-in.md(§2)

## 1. 現象

ユーザー報告(Grok Build で接続済みの状態のスクリーンショット付き):

> ログインできているのにサインインボタンがずっと出ていて混乱する。
> ログイン前・ログイン処理中・ログイン後で UI を整えて。

設定の「エージェント」カードは「Grok Build に接続済み。」「保存済みのログインで接続しています。」と
出しているのに、その下に「エージェント自身のブラウザ手順でサインインします。始まらない場合は
ボタンを押してください。」という案内と、強調(primary)スタイルの「サインイン」ボタンが常に並ぶ。
接続済みなのに「サインインが必要」に見える。

## 2. 根本原因

`SettingsView.RefreshAccountSectionAcp` は、状態ラベルの文言こそ接続状態で切り替えていたが、
ボタンと案内の表示は状態を見ていなかった:

- 「サインイン」ボタンは `AgentBackends.HasInPanelLogin` なら**常に表示**し、
  サインイン中だけ無効化していた。
- 案内(`_accountAcpHintLabel`)は `RefreshAccountSection` が「ACP バックエンドなら表示」で
  固定していた。
- Claude 側のアカウントカードは「ログイン中はボタンを隠す」「ログイン済みなら『ログイン』を
  『アカウントを切り替え』に読み替える」を既にやっていたが、ACP 側にはその対応が無かった。

## 3. 対応

ACP のカードを 4 つの排他的な形(`AcpAccountPhase`)に整理し、純関数
`ResolveAcpAccountPhase(connected, starting, loginRunning, signInPending)` で選ぶ:

| 形 | 条件 | 状態行 | ボタン | 案内 |
|---|---|---|---|---|
| SigningIn | ログインコマンド実行中、またはブリッジがブラウザのサインイン待ち | 実行中 / 待機中の文 | キャンセル(コマンド実行中のみ)、URL があれば「ブラウザで開く」「コピー」と URL 欄 | 隠す |
| SignedIn | 接続済み | 「{0} に接続済み。」+ サインイン方式の行 | 「アカウントを切り替え」(強調なし。同じログインコマンドを実行し直す) | 隠す |
| Connecting | プロセス起動中でサインイン要求はまだ無い | 「{0} に接続しています...」(新規文言) | 隠す | 隠す |
| SignedOut | 未接続で何も進行していない | エラーがあればその文、無ければ「未接続。」 | 「サインイン」(強調あり) | 表示(コマンド名入りの案内) |

優先順位は「進行中 > 接続済み > 起動中 > 未接続」。接続済みなら以前の失敗文(`AcpSignInError`)は
状態行に出さない(接続できているのが事実)。ログインコマンドを持たないエージェントでは
ボタン自体を出さないのは従来どおり。

「再接続」は SigningIn の間だけ無効(従来と同じ)。

## 4. テスト

`SettingsViewLogicTests.ResolveAcpAccountPhase_Table`(7 ケース)で真理表を固定した。
Unity の UI そのものはテストしていない。

## 5. 変更ファイル

- `Editor/UI/SettingsView.cs`: `AcpAccountPhase`、`ResolveAcpAccountPhase`、
  `RefreshAccountSectionAcp` の表示切替、`RefreshAccountSection` の案内表示を ACP 側へ移譲。
- `Editor/UI/L10n/UiStrings.cs` / `UiStringsJa.cs`: `SettingsAccountAcpConnectingFmt` を追加。
- `Tests/Editor/SettingsViewLogicTests.cs`: 真理表テスト追加。
