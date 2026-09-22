# uap_console_clear と uap_game_view_size — 「安い 4 本」の残り 2 本

Date: 2026-09-21
Status: 実装済み(v0.59.0-beta.1)
関連: `docs/design-notes/2026-09-21-uloop-always-loaded-cost.md` §9.2(ギャップ表)、
`docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md`(先行の 2 本)

## 1. 位置づけ

uLoop 調査で洗い出した「こちらに無くて、uLoop を入れる理由になっていた安い 4 本」
のうち、残っていた 2 本:

| 能力 | uLoop 側の実装規模 | 本ノートで追加 |
|---|---|---|
| Console クリア | 167 行 | `uap_console_clear` |
| Game View のサイズ変更 | 157 行 | `uap_game_view_size` |

これで 4 本すべてが揃い、**一般的なプロジェクトが uLoop を入れる理由は
hot-reload / pause-point / execute-dynamic-code の 3 つだけ**になる
(どれも自前化しない、という §9.4 の判断は変わらない)。

## 2. uap_console_clear

### 2.1 3 つまとめて消す

Console は本パネルから見ると 3 つの顔を持つ:

1. Unity の Console ウィンドウ(ユーザーが見るもの)
2. `UapConsoleLogStore`(`uap_console_logs` がエージェントに返すもの)
3. `ConsoleErrorProvider`(パネルのエラーチップ)

**どれか 1 つだけ消すのは、消さないより悪い**。ウィンドウだけ消えても
エージェントは古い行を読み続けて「直っていない」と判断するし、バッファだけ
消してもユーザーのチップには無い行のエラーが残る。だから 1 回の呼び出しで
3 つとも消す。

### 2.2 LogEntries リフレクションは 1 か所に集約

Console ウィンドウを消すには内部 API `UnityEditor.LogEntries.Clear()` が要る。
本パネルは既に `ConsoleWindowSync` が **同じ型の別メソッド**
(`GetCountsByType`、エラー件数の読み取り)をリフレクションで使っているので、
`TryClearConsole()` もそこに置いた。内部型への依存はリポジトリ内で 1 ファイルに
留める。

消せなかった場合(将来のエディタで内部 API が動いた場合)は
`consoleWindowCleared: false` と、**「ウィンドウの行は画面に残っている」**旨を
返す。ここで「全部消した」と答えると、エージェントはユーザーの画面状態を
誤解したまま進む。

副次効果として、`ConsoleWindowSync` のベースライン(`_lastErrorCount`)も 0 に
落とす。自分で消した減少を次のポーリングで「ユーザーが Clear を押した」と
再解釈して同じ後始末を二度やる必要はない。

### 2.3 破壊的だが、ゲートは付けない

消えるのはユーザーがまだ読むかもしれないログで、Undo もできない。ただし
`uap_asset_delete` のような confirm/dry_run ゲートは付けなかった:
プロジェクトのデータは失われず、ユーザー自身がボタン 1 つでやることと同じだから。
代わりに **ReadOnly=false**(毎回パーミッションカード)とし、ツール説明で
**`uap_console_logs` の `since_id` を先に勧める**。「新しい行だけ見たい」は
消さずに達成できる、というのを使う場所で言うのが一番安い抑止になる。

## 3. uap_game_view_size

### 3.1 なぜ要るか

`uap_editor_screenshot capture:"game"` は **その時の Game View のサイズ**で撮る。
「1080p でどう見えるか」「スマホ比率だと崩れないか」を見せるには、これまで
ユーザーにドロップダウンを操作してもらうしかなかった。

### 3.2 内部 API のかたまりなので、読み返して確かめる

Unity 2022.3 に Game View サイズの公開 API は無い(`GameViewSizes` /
`GameViewSizeGroup` / `GameViewSize` / `GameView.selectedSizeIndex` はすべて
internal)。したがって実装は全面リフレクションで、**壊れ方が静かになりやすい**。

そこで `UapGameViewSizeAccess` は次を守る:

- 解決できなかったステップごとに、**どのステップか**を名指しした失敗を返す。
- `TrySetSize` は書いたあと **必ず読み返す**。要求したサイズと違えば、
  「設定した」ではなく「エディタは X を保持した」と報告する。
  これは `uap_editor_ui_set_value` が「コントロールが受け付けていない値を
  成功として返していた」不具合(0.18.1)の後に採った規律と同じ。
- 一覧に無いサイズは `AddCustomSize` で追加する(= ドロップダウンの「+」と
  同じこと)。ラベルは `Agent Panel 1920x1080` のようにパネル名入りにして、
  **ユーザーがどこから来た項目か分かる**ようにし、次回は同じ項目を再利用して
  重複を作らない。

### 3.3 `get` は開いていなくても答える

バッチモードにも EditMode テストランナーにも Game View は無い。`get` で
「開いていない」は**正常な答え**なので `open:false` + 理由を返す(例外にしない)。
`set` は要求が果たせないので失敗にする。

## 4. モジュールとステアリング

両方とも `editor` モジュール。ステアリング文には**足していない**:
2026-08-02 の教訓は「ファミリを名指しする」ことで、この 2 本は既に名指し済みの
`editor` ファミリの中にある。説明文が十分に具体的なので、これ以上プロンプトを
太らせる理由が無い。

## 5. テスト

- 純粋部分(`UapGameViewSizePolicy` の寸法検証とラベル)は Unity 非依存にして
  `ci/SmokeTests` でも実行。
- EditMode: `UapConsoleClearToolTests`(3 つとも消えること、片方しか消せなかった
  ときの文言、引数を取らないスキーマ)と `UapGameViewSizeToolTests`。
- **意図的にテストしないこと**: `set` の成功経路。対話的エディタで走らせた人の
  Game View を勝手にリサイズしてしまうし、バッチモードには Game View が無い。
  代わりに「開いていないときの 2 つの答え」を固定し、実リサイズは
  サンドボックスプロジェクトでの手動確認に回す(下記)。

### 実機(CI の実 Unity)での結果 — 2026-09-21

`editmode-tests.yml`(Unity **2022.3.22f1**):

| 走らせ方 | total | passed | failed | skipped |
|---|---|---|---|---|
| Pro あり | 4422 | 4395 | **0** | 27 |
| Core のみ | 3608 | 3581 | **0** | 27 |

本ノートの 2 本のフィクスチャ(`UapConsoleClearToolTests` 4 件 /
`UapGameViewSizeToolTests` 9 件)は両方の走らせ方で全件 Passed。
バッチモードには Game View が無いので、「開いていないときの 2 つの答え」
(`get` が `open:false`、`set` が失敗して開き方を案内)は**実機で実際に通った**
経路である。

### 手動確認の手順(サンドボックス)

1. Game View を開いた状態で `uap_game_view_size action:get` → 現在のサイズが返る。
2. `action:set width:1920 height:1080` → Game View が 1080p になり、ドロップダウンに
   `Agent Panel 1920x1080` が増える。もう一度同じ呼び出しをしても項目は増えない。
3. `uap_editor_screenshot capture:"game"` が 1920x1080 相当で撮れる。
4. `uap_console_clear` → Console ウィンドウ・エラーチップ・`uap_console_logs` の
   3 つが同時に空になる。
