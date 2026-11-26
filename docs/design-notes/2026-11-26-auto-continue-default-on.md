# 自動継続(中断後 / コンパイル後)を既定 ON にする

日付: 2026-11-26 / 対象: Core v0.62.0-beta.4

## 背景

パネルにはターンの続きを自動で送る設定が 2 つあり、どちらも既定 OFF だった
(設定 > Unity 連携 > Unity操作(UapOps))。

| 設定 | フィールド | いつ送るか |
|---|---|---|
| 中断後に自動継続する | `autoContinueInterruptedTurn` | 利用者起因のドメインリロード(スクリプト保存後のフォーカス復帰、手動の再コンパイル、Play モードの出入り)が実行中のターンを切ったとき |
| コンパイル後に自動継続する | `uapOpsAutoContinueAfterCompile` | エージェント自身のスクリプトコミットでコンパイルとリロードが起きたとき(コンパイル結果を添える) |

既定 OFF だと次のようになる。

- リロードでターンが切れると、同じセッションへ自動で再接続はするが、バナーの「続行」を
  押すまで続きが始まらない。席を外している間にリロードが 1 回入るだけで止まったままになる。
- エージェントが C# を書いてコミットすると、コンパイルが通ったかどうかを見ないまま
  そこで作業が終わる。「書く → コンパイル → エラーを直す」を回すには毎回人が促す必要がある。

利用者から見ると、どちらも「再接続はしたのに続きをやらない」状態で、自動再接続の体験が
完結していない。両方を既定の動作にする(2026-11-26、最初に中断後、続けてコンパイル後も
同じ作業ブランチで依頼を受けた)。

## 決めたこと

1. **両方の既定値を ON にする。** フィールド初期化子を `true` に。
2. **既存の設定も一度だけ ON にする。** フィールド初期化子は保存済みアセットには
   届かない(保存された `false` が上書きする)。`EnsureAutoContinueDefaults()` を
   `PanelStateStore.OnEnable` で呼び、世代スタンプ(`autoContinueDefaultsGeneration`)で
   一度きりにする。`EnsureAutoApproveLevelMigrated` と同じ形。2 つの設定で 1 つの世代を
   共有する(同じ版で同時に変わるため)。
   - opt-in だった期間の `false` は「OFF を選んだ」のではなく「初期値のまま」が
     ほとんど(最初から OFF なので、切る操作をする理由がない)。両者は区別できないので、
     既定の変更として全員を一度 ON にする。
   - 切り替え後に利用者が OFF にした場合は、スタンプがあるので二度と戻さない。
3. **注記を警告トーンから通常のヒントにする。** これまでは ON のとき琥珀色だった
   (「危険なのは既定でない側の値」という 2026-09-06 の設定レビューの規則)。
   既定が ON になった以上 ON は警告ではなく、OFF も危険ではないので、どちらのトグルも
   常に通常のヒント(行そのものは今までどおり常時表示)。文言は「既定 OFF。」→「既定 ON。」。
   使い道の無くなった `SettingsView.IsRiskyAutoContinueSetting` は削除。

## 既定 ON でも安全と判断した根拠

当初はどちらも「人が送っていないターン」として既定 OFF にした
(`2026-09-06-domain-reload-resilience-and-hot-reload.md` §6 ほか)。歯止めは当時から
設定の ON / OFF とは別に実装されており、既定を変えてもそのまま効く。

共通:

- 送信は必ず会話ログのノートで明示される(無通知の裏ターンは無い)。
- クラッシュループで接続が停止している間は送らない。停止ボタンで止まる。
- 送信可能になるまで待ち、60 秒で諦めて撤回ノートを出す(単一スロットのキュー)。
- 自動継続が許可を肩代わりすることはない。継続ターンのツール呼び出しは、利用者が選んだ
  自動承認レベルと権限モードにそのまま従う(`2026-09-10-permission-queue-depth-and-
  reload-dropped-permission.md`)。
- 依頼ごとのチェックポイント(`2026-11-23-request-checkpoints.md`)は、自動継続した
  ターンを元の依頼の続きとして扱う。結果が気に入らなければ「この依頼を元に戻す」で戻せる。

中断後:

- 発火は「リロードが開いているターンを実際に中断した」とき(`AgentHub.ResumedMidTurn`)
  だけ。アイドル中のリロードでは何も送らない。続けるのは人が送ったターンそのもの。

コンパイル後:

- 発火は、リロードがエージェント自身の `.cs` / `.asmdef` のコミットに起因するときだけ。
  利用者が自分で保存したスクリプトのコンパイルでは送らない。
- 継続ターンにはコンパイル結果(エラーの要約)が付く。コンパイル前の前提のまま進む事故を
  防ぐための機能であり、送らない場合は「壊れたまま終わる」ほうが起きる。
- 連鎖に回数の上限は無い(`2026-09-15-chained-auto-continue-after-compile.md`)。
  止まるのは、エージェントがコミットをやめたとき、接続が停止したとき、停止ボタンを
  押したとき。既定 ON になるとこのループが誰の環境でも無人で回るので、費用の面では
  従量課金の利用者に影響しうる。エージェントは直すものが無くなればコミットをやめるため
  通常は数回で収束するが、気になる利用者は OFF にできる(ガイドに明記)。

CLI 中継(`2026-11-25-cli-relay.md`、既定 OFF・実験的)が ON でつなぎ直しに成功した
場合はターンが切れないので、どちらの自動継続も発火しない。中継が使えずに従来経路へ
落ちた場合に、この既定が効く。

## 変更点

- `PanelSettings`: `autoContinueInterruptedTurn` と `uapOpsAutoContinueAfterCompile` の
  初期値を `true` に。`CurrentAutoContinueDefaultsGeneration` /
  `autoContinueDefaultsGeneration` / `EnsureAutoContinueDefaults()` を追加。
- `PanelStateStore.OnEnable` で上記を呼び、走ったらアセットを書き直す。
- `SettingsView`: 2 つのトグル直下の行を `AddWarning` から `AddHint` に。
  `RefreshWarningTones` から外し、`IsRiskyAutoContinueSetting` を削除。
- 文言(英 / 日): `SettingsAutoContinueHelp` / `SettingsAutoContinueInterruptedHelp` の
  「既定 OFF」を「既定 ON」に。
- ガイド `docs/guide/reload-and-play-mode(.en).md` を「既定で自動継続する。止めたい
  ときは OFF」に書き換え。

## テスト

- `PanelSettingsTests`: 両方の初期値が ON、opt-in 期のアセットが一度だけ両方 ON になる、
  二度目は何もしない(後から OFF にした値が残る)、JSON 往復後もスタンプが残る。
- `HelpAffordanceTests`: 自動継続の警告トーンの検証を削除(ゲートの分は残す)。
- `ReloadLifecycleLiveTests.BeginMidTurn`: バナー経路を確かめるテストのために中断後の
  自動継続を明示的に OFF にする(自動継続のテストはこれまでどおり自分で ON にする)。
  `AgentHubAutoContinueAfterCompileTests` は各テストが設定を明示しているので変更なし。
- Unity はこの作業環境に無いため、コンパイルと EditMode テストは CI で確認する。
