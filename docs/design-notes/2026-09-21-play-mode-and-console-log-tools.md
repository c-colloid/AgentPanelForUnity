# uap_play_mode と uap_console_logs — uLoop を「任意」に降格させる 2 本

Date: 2026-09-21
Status: 実装済み(v0.59.0-beta.1)
関連: `docs/design-notes/2026-09-21-uloop-always-loaded-cost.md` §9(規模の実測と
ギャップ表)、`docs/design-notes/2026-08-01-phase5-unity-ops-design.md` §7.2(能力マトリクス)

## 1. なぜこの 2 本なのか

前段の調査(上記 §9.2)で、uLoop の 20 ツールのうち本パネルに無いものを
洗い出した結果、**一般的なユーザーが uLoop を入れる理由**はほぼ 2 つに
集約された:

| 無かった能力 | uLoop 側の実装規模 | 影響 |
|---|---|---|
| Play Mode の制御 | 1,541 行 | 「直してくれたのはいいが、動くか確認して」がユーザーの手作業になる |
| Console ログの読み取り | 270 行 | ツールが「成功」と言っても、Unity が何と言ったかをエージェントが読めない |

残る hot-reload / pause-point / execute-dynamic-code は自前化しない
(§9.4)。この 2 本だけで、**uLoop を常駐させる理由が大半のプロジェクトから
消える** — それが今回の狙いであり、uLoop を否定するものではない
(脱出ハッチとしての連携は従来どおり)。

## 2. uap_play_mode

action: `status`(既定) / `start` / `stop` / `pause` / `resume` / `step`。

### 2.1 一番の設計上の勘所 — 応答より後に遷移させる

Play Mode の出入りは(Enter Play Mode Settings で切っていない限り)
**ドメインリロードを伴う**。リロードは `UapOpsServer` を落とすので、
`Execute` の中で `EditorApplication.isPlaying = true` してしまうと、
**その呼び出し自身の HTTP 応答が失われる**。エージェントから見ると
「ツールが失敗した」であり、実際には Play Mode に入っている、という
最悪の食い違いになる。

そこで `Execute` は遷移を **`TransitionDelaySeconds`(0.25 秒)後** に
`EditorApplication.update` 経由で適用し、自分は「遷移前の状態 +
requested + 次にやること」を返して即座に終わる。応答を書き終えるのに
必要な時間はサブミリ秒なので、0.25 秒は十分な余裕がある。

返答には必ず次の手順を書く: **uap_ping が答えるまで待ち、
`action:status` で確認する**。これは既存のステアリング
(「uap_ping がエディタ生存確認の正」)と同じ言い回しに揃えてある。

### 2.2 拒否する条件

- **コンパイル中**: Unity は要求を飲み込むか、差し替え直前のアセンブリで
  Play Mode に入る。どちらもエージェントには「黙って何もしなかった」に
  見えるので、理由と待ち方(uap_ping)を添えて拒否する。
- **すでに遷移中**: 同上。
- **走っていないのに pause / resume / step**: 拒否し、`action:start` を案内。
- 一方で **すでに Play Mode で start**、**Edit Mode で stop** は
  「何もすることがない」旨を返す **成功** とする(エラーにすると、
  状態を確認してから叩いた素直なエージェントが損をする)。

### 2.3 Run In Background の事前警告

`start` 時に `Application.runInBackground` が false なら、
「非フォーカス中はティックが散発的になり uap_* がタイムアウトしうる」旨を
`warning` に載せる。同じ条件は `UapOpsServer.PlayModeUnfocusedNotice` が
**事後に**報告していたもので、事前に言えば回避できる情報だった。

### 2.4 ReadOnly ではない

`status` は読むだけだが、`IUapTool.ReadOnly` は **引数単位ではなくツール単位**。
自動承認の安全性はこの粗さで担保されているので、ツール全体としては
false(毎回パーミッションカード)。`UapReadOnlyToolMetadataTests` に明記。

## 3. uap_console_logs

### 3.1 Console ウィンドウではなく自前のリングバッファ

Unity 2022.3 に Console のエントリを読む公開 API は無い。内部
`UnityEditor.LogEntries` へのリフレクションは、本パネルでも既に
`ConsoleWindowSync` が**整数 1 個**(エラー件数)のために使っているが、
エントリ本体を取りに行くのはエディタ版差で壊れやすく、
**黙って空を返すツールはツールが無いより悪い**。

したがって公開 API の `Application.logMessageReceivedThreaded` を使い、
`UapConsoleLogStore` に上限付きで溜める。違いは隠さずツール説明に書く:
ドメインリロードで消える / 直近 200 行 / ユーザーの Clear には追随しない。

### 3.2 常駐させない(今回の調査の自戒)

同じ日に「他所のパッケージの常時稼働コスト」を測った以上、こちらが
`[InitializeOnLoadMethod]` で無条件にフックを張るのは筋が通らない。
`UapConsoleLogBuffer.Install/Uninstall` は **`UapOpsServer.EnsureStarted/Stop`
からのみ**呼ばれる = UapOps を有効にしたエージェントセッションが生きている
間だけ捕捉する。上限は 200 件 × (メッセージ 500 文字 + スタック 1000 文字)
で、ログの洪水(本プロジェクトの実測で 1 セッション 1106 件)でも
**固定量**しか食わない。

### 3.3 並び順の契約 — filter → collapse → count

`Select` は必ずこの順で処理する。逆にすると、1 本の例外が連打された
場面(まさにこのツールを使う場面)で `count` 分がすべて同じ行で埋まり、
周囲の手がかりが消える。折り畳んだ行は **その連なりの最新 id** を持つので、
`since_id` でのポーリングが同じ行を二度返すことはない。

### 3.4 ReadOnly である

バッファを読むだけで何も変えない → 自動承認の対象(`autoApproveReadOnlyOps`)。
`uap_object_inspect` と同じ位置づけ。

## 4. 能力マトリクスを適用しない

Phase 5 設計 §7.2 は「uLoop が賄える能力は UapOps 側を登録しない」だったが、
**この 2 本には適用しない**(`UloopCapabilityMatrix` の集合は引き続き空)。
理由は前段ノート §9.4 のとおり: 型付きツール側には権限カード・ジョブ台帳・
パネル自身のコンソール経路があり、Bash 越しの `uloop control-play-mode` /
`uloop get-logs` では置き換えられない。uLoop 導入済みプロジェクトでも
こちらを登録する。

## 5. テスト

- 純粋部分(`UapPlayModePolicy` / `UapConsoleLogStore` / `UapConsoleLogsTool`)は
  **Unity 非依存**に切り出し、ライセンス不要の `ci/SmokeTests` でも
  コンパイル+検証する(`dotnet-smoke.yml`)。判断ミスが高くつく箇所
  (コンパイル中の Play 突入、洪水時にどの行を見せるか)を、Unity ライセンス
  待ちに関係なく毎プッシュで守るため。
- Editor 依存部分は EditMode 側:
  `UapPlayModeToolTests` は **スケジューラを差し替えて**「遷移が後回しに
  なっているか」を、実際に Play Mode に入らずに検証する。
  `UapConsoleLogBufferTests` は実際の `Debug.Log` が捕捉されること、
  Uninstall 後に捕捉されないこと、そしてフィクスチャが開発者のライブ
  セッションの捕捉状態を壊さないこと(SetUp/TearDown で復元)を見る。

## 5.1 実機(CI の実 Unity)での結果 — 2026-09-21

`editmode-tests.yml`(GameCI コンテナ + Personal seat、Unity **2022.3.22f1**)で実行:

| 走らせ方 | total | passed | failed | skipped |
|---|---|---|---|---|
| Pro あり(`ci/HostProject`) | 4401 | 4374 | **0** | 27 |
| Core のみ(`ci/HostProjectCoreOnly`、公開ミラーと同じ形) | 3587 | 3560 | **0** | 27 |

新規フィクスチャは両方の走らせ方で全件 Passed(`UapPlayModeToolTests` 9 件 /
`UapConsoleLogBufferTests` 8 件、results.xml で確認)。

### 実機が捕まえたもの(ローカル検証をすり抜けた 1 件)

最初の実機走行は 4401 件中 **1 件**だけ落ちた:
`GlyphAuditTests.SourceScan_AllConstructedCodepoints_AreWhitelisted`。
`UapConsoleLogStore` が (type, message) を 1 本の辞書キーにまとめるのに使った
`"\u0000"` 区切りが、**Editor ソースにホワイトリスト外のコードポイント
エスケープを置けない**という本リポジトリの監査に触れた(この監査は「エディタ
フォントに無いグリフをソースが持ち込めない」ためのもので、拒否は正しい)。

区切り文字という発想自体が筋が悪かった: 印字可能な文字はどれもログ本文に
現れうるので、現れない NUL を選んだ結果が監査と衝突した。現在は序数比較の
小さな構造体をキーにしており、衝突も監査対象も無い。

**教訓**: Unity 非依存に切り出した部分は `ci/SmokeTests` で早く検証できるが、
**リポジトリ固有のソース監査は実 Unity の EditMode 走行でしか回らない**。
ライセンス待ちを理由に最終確認を省かないこと。

### まだ実機で確認していないこと

Play Mode に**実際に入って戻る**往復(0.25 秒スケジューラが実エディタループで
発火し、ドメインリロードを跨いで `uap_ping` → `action:status` が繋がるか)は
バッチモードの EditMode テストでは再現できない。サンドボックスプロジェクトで
パネルから `uap_play_mode action:start` → `uap_console_logs` → `action:stop` を
1 往復させるのが残りの確認。

## 6. 残ギャップ(今回やらないこと)

`clear-console` と `set-game-view-size` は同じ「安い 4 本」の残り 2 本だが、
今回は見送った(それぞれ 167 / 157 行相当)。PlayMode 中の実入力シミュレーション
(simulate-keyboard / mouse)は別規模の話で、今回のスコープ外。
