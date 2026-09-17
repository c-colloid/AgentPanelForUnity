# ヘッダーのモデル切替が「+」直後に効かない / 確認の手段が無い

日付: 2026-09-17 / 対象: v0.54.5 / 関連: 2026-08-01-model-settings-rework.md(4.1)、
2026-07-31-live-model-tracking.md、2026-07-31-model-picker-refresh-gap.md、
2026-09-06-ui-redesign 系(折りたたみチップ)

## 1. 現象

ユーザー報告(2026-09-16 の「小さくしたときの残像」報告の続き):

> 直前の操作は新規会話を作成(+ ボタン)。折りたたみチップからモデルを切り替えた
> (Opus → Fable)が、確認の方法がなく、会話を開始してみるとモデルが変わっていなかった。
> 以前には会話の途中でモデルの切り替えを行おうとしたが切り替わらなかったこともあった。

## 2. 根本原因

ヘッダーのモデルメニュー(`HeaderView.ShowModelMenu`、折りたたみチップも同じ関数)は
`ResolveModelPickerState` で入口を選ぶ:

- **Live**: 接続後の initialize 応答にある `models[]` から選ぶ → `SwitchSessionModel`
  (このセッションを `set_model` で切り替える)。
- **Cache**: `models[]` がまだ無いときは永続化したカタログから選ぶ → `SetDefaultModel`
  (**次回スポーンからの既定**を書くだけで、動いているセッションには触らない)。

「+」は `StartFresh` → 新しい CLI をスポーンする。initialize 応答が返るまでの数秒間、
`InitializeResponse` は null なので、この間にチップを押すと **Cache 入口** になる。
ユーザーは「このセッションのモデルを変えた」つもりでも、実際は既定を書き換えただけ。
スポーン済みのセッションは `--model` で渡した旧モデルのまま。

見え方がさらに悪い: チップの文字は `ResolveModelName` が「ライブモデルが無ければ
設定の既定」を出すため、選んだ直後は一瞬 Fable と表示され、system/init が届いた時点で
`InitMessage.Model`(Opus)へ戻る。確認手段が何も無い。

「会話の途中で切り替わらなかった」も同じ窓で起きうる(リロード後の再接続直後など)。
また Live 入口でも、`set_model` の失敗・タイムアウトは Console ログに出るだけで
チップは旧モデルのまま、会話ログには何も出なかった。`AgentClient` はタイムアウトした
control_request を `ControlRequestResolved` に載せていなかった。

## 3. 検討した選択肢

1. **Cache 入口でも「+」直後は既定を書きつつ再スポーンする**: 空セッションなら
   成立するが、再接続直後(会話あり)には使えず、二重スポーンの管理が要る。却下。
2. **initialize 前でも `set_model` を即送る**: ハンドシェイク前の control_request は
   応答無しで落ちるのが観測されている(報告の「切り替わらなかった」に一致)。却下。
3. **切替要求を保留し、initialize が解決した瞬間に送る(採用)**: 意図(このセッション)を
   保ったまま、CLI が受け付けられる最初のタイミングで送る。`models[]` が揃ってから
   送るので別名解決(`ResolveModelValue`)も従来どおり効く。

## 4. 決定と実装

- `AgentHub.SwitchSessionModel`: クライアントはあるが `InitializeResponse == null` なら
  `_pendingSessionModel` に保留し、会話ログに「接続の完了を待っています」の
  システムノートを出す。initialize の `ControlRequestResolved`(success)で
  `SendSessionModel` に流す。保留はクライアントと運命を共にする(`TearDownClient` で
  クリア。次のスポーンは設定を読む)。
- `AgentHub.PendingSessionModel`(公開): ヘッダーとステータスバーの
  `ResolveModelName(client, pending)` がライブモデルより優先して表示する。
  選んだ直後に旧名へ戻る現象をなくす。
- `set_model` の解決時に会話ログへ確認を出す: 成功「このセッションのモデルを {0} に
  切り替えました」/ 失敗・タイムアウト「切り替えに失敗しました({1})。以前のモデルの
  まま」(警告ノート)。`AgentClient` はタイムアウトした control_request も
  `ControlRequestResolved(kind, false, "timed out")` で通知するようにした。
- `HeaderView.ShowCachedCatalogMenu`: 純関数 `ResolveCachedMenuTargetsSession(hasClient,
  state)` が true(スポーン済みで NotStarted / Errored 以外)なら選択を
  `SwitchSessionModel` へ、クライアントが無ければ従来どおり `SetDefaultModel` へ。
  「セッションのピッカー」を押したのにセッションが変わらない、を無くす。
- L10n 3 件(英/日): `HubModelSwitchQueuedNoteFmt` / `HubModelSwitchedNoteFmt` /
  `HubModelSwitchFailedNoteFmt`。

`PanelSettings.model`(新規セッションの既定)は引き続き `SetDefaultModel` だけが書く。
2026-08-01 の「既定と使用中の分離」は維持。

## 5. 回帰ガード

- `AgentHubSwitchModelTests`: ハンドシェイク前の保留 → initialize 解決で 1 回だけ送信
  → 成功ノート / 失敗応答で警告ノートと旧モデル維持 / 保留はクライアント破棄で消える。
  既存の Live 分岐テストはハンドシェイク完了後に切り替えるよう更新。
- `HeaderViewLogicTests`: `ResolveCachedMenuTargetsSession` の両側。
- `StatusBarViewLogicTests`: 保留中のモデル名がチップに出る。

## 6. 教訓

「接続後」を前提にする UI 入口は、接続の**途中**に押されたときの意図を落としてはいけない。
既定を黙って書き換えるより、意図を保留して最初に可能なタイミングで実行し、結果を
会話ログに残す。ユーザーが「確認の方法がない」と言うとき、足りないのは成功の表示より
**失敗の表示**であることが多い。
