# 設定の CLI カードとアカウントカードを「エージェント」カードに統合する

日付: 2026-09-17 / 対象: v0.54.7 / 関連: 2026-09-17-account-card-agent-picker-and-acp-init.md、
2026-09-10-acp-backends.md(4)、2026-09-10-in-panel-install-and-sign-in.md、
2026-09-10-claude-api-key-auth-passthrough.md、2026-08-23-phase2-batch-c-settings-ia-and-io.md

## 1. 経緯

v0.54.7 でエージェント選択を CLI カードからアカウントカードへ移し「エージェントとアカウント」
と改名した。その直後のユーザー指摘:

> CLI カードの必要性を再評価。名前から何をしたいカードなのかわかりづらい。

再評価の結論は「CLI カードを独立させる理由が無い」。中身(実行ファイルのパス / コマンド・
引数 / 認証メソッド ID / 再検出・再接続 / 解決結果 / インストール)はすべて「選んだ
エージェントを起動できる状態にする」ための項目で、選択とサインインと同じ一連の流れ。
「CLI」は実装の語彙で、ユーザーの探し方(「エージェントを変えたい」「動かない」「ログイン
したい」)と一致しない。3 枚(CLI / 診断 / アカウント)に散っていること自体が分かりづらさの
原因なので、名前の付け替えでは解決しない。

## 2. 新しい 1 枚: 「エージェント」カード

「接続とアカウント」グループの先頭。常時展開(旧 CLI カードの折りたたみキー `cli` は廃止)。
上から:

1. **エージェント**(ドロップダウン)+「次回の再接続時に適用されます。以下はすべてこの
   エージェントについての表示と設定です。」
2. **解決結果 / 見つかりません**(旧 CLI カードの行)と、未検出のときだけ出る
   **インストール**ボタン(既存の挙動)。
3. **サインイン状態**(ログイン中 / 未ログイン / 環境変数トークン / API キー課金の注記 /
   ACP の方式表示)。
4. **サインイン方式**(§3)。
5. サインイン操作: Claude Code は ログイン / ログアウト とログインサブカード、ACP は
   サインイン / キャンセル / ブラウザで開く / コピー と URL・出力行。
6. **詳細設定(実行ファイル / コマンド)** フォールドアウト(既定で閉): Claude Code は
   実行ファイルのパス、ACP はコマンド / 引数 / ターミナルでのサインイン代替 / 制限事項。
   `RefreshCliStatus` が「見つかりません」を出すときは自動で開く(閉じる側には触らない)。
7. **再接続**ボタン 1 個(§4)。

「診断」カードは残すが、中身が分かる **「CLI の出力(stderr)」** に改名。

## 3. 「サインイン方式」: Claude 専用項目をやめる

旧「APIキー認証」(Claude Code のみ表示)の実体は `ClaudeCliProcess.ComputeEnvVarsToRemove`
で `ANTHROPIC_API_KEY` を環境から取り除くかどうか。ACP エージェントはこの変数を見ない代わりに、
ACP の `authenticate` メソッド ID(旧 CLI カードの「認証メソッド ID」)で方式を選ぶ。
つまり両者は同じ問い(「どの方式でサインインするか」)への、エージェントごとの答え。

そこで 1 つの行「サインイン方式」にし、エージェントで形だけ変える(`BuildSignInMethodFields`):

- Claude Code: ドロップダウン「自動(CLI に任せる)」/「サブスクリプションのみ」。
  ヒント「『自動』は環境の ANTHROPIC_API_KEY を優先(従量課金)。『サブスクリプションのみ』は
  それを取り除きます。」
- ACP: テキスト欄(認証メソッド ID)。ヒント「ACP の認証メソッド ID。空欄ならエージェントが
  最初に提示するサインイン方法を使います。」

設定値(`claudeAuth` / `acpAuthMethod`)、次回スポーンで適用、`SettingsChangeDetector` は不変。
旧実装では ACP 選択時に `_claudeAuthField` だけ隠してヒント行が残っていたが、グループ単位で
切り替えるようにしたのでそれも直る。

## 4. ボタンの整理

- **再検出** を削除。パス欄の変更で自動的に再解決される(`RefreshCliStatusIfPathChanged`)し、
  再接続も `RefreshCliStatus` を呼ぶ。
- **今すぐ再接続**(旧 CLI カード)と **再接続**(旧アカウントカードの ACP 用)を 1 個の
  **再接続** に統合(`OnAgentReconnectClicked`)。Claude Code では従来の `AgentHub.Reconnect`
  (セッションを再開)、ACP では従来の `RecoverConnection`(ハンドシェイクをやり直し、必要なら
  再サインイン、認証状態の再照会)。設定変更バナーの「今すぐ再接続」は不変。

## 5. 影響範囲

- `SettingsView`: `BuildCliSection` / `BuildAccountSection` → `BuildAgentSection` +
  `BuildAgentAdvancedFoldout` + `BuildSignInMethodFields`。`RefreshCliStatus` / `RefreshBackendGroups` /
  `RefreshAccountSection` はフィールドをそのまま使う。折りたたみキー `cli` 廃止。
- 文言: `SettingsSectionAgent`(旧 `SettingsSectionAccount`)、`SettingsSignInMethodLabel`、
  `SettingsAgentAdvancedFoldout`、`SettingsReconnectButton` を追加。`SettingsSectionCli`、
  `SettingsRedetectButton`、`SettingsClaudeAuthLabel`、`SettingsAcpAuthMethodLabel`、
  `SettingsAccountAcpSignInButton`、`SettingsCliForAgentFmt` を削除。「設定 > CLI」
  「設定 > エージェントとアカウント」の参照は「設定 > エージェント」に更新。
- テスト: `SettingsViewSectionDisclosureTests` の `cli` 参照を `uapops` に置換。
- ドキュメント: `docs/USER-GUIDE.md`、`Documentation~/index.md`。

## 6. 検証

- 手動: Claude Code で「サインイン方式」がドロップダウン、Codex に切り替えるとテキスト欄に
  なり、ヒント行も入れ替わること。パス欄を壊すと「見つかりません」と同時に詳細設定が開き、
  インストールボタンが出ること。再接続ボタンが両バックエンドで動くこと。
- EditMode: `SettingsViewSectionDisclosureTests`(cli 廃止後も残り 6 枚の折りたたみが
  既定で閉じる)、`L10nTests` のヒント 110 文字上限。
