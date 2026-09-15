# 2026-09-15 -- 「コンパイル後に自動継続する」の連鎖を許可する

発端: ユーザーからの質問「このターン自体が自動継続だったため、続けて 2 回目の自動継続は
行いません」という制約がある理由は何か、続けて「緩和したい」。

## 1. 従来の挙動と、その理由

`uapOpsAutoContinueAfterCompile`(コンパイル後に自動継続する)は、エージェント自身の
`uap_scripts_commit` が起こしたコンパイル → ドメインリロードの後に、コンパイル結果を
添えた継続ターンをパネルが 1 回送る機能。2026-08-01 の Phase 5 設計(R11 §4)は
ガードレール 1 として **「1 ターンにつき最大 1 回」** を置いていた。実装上は
`SessionStateBridge.AutoContinueTurnIsContinuation`(いま開いているターンはパネルが
送った継続ターンか)を `OnTurnCompleted` でチケットに写し
(`AutoContinuePendingWasContinuation`)、次のリロードで
`AutoContinueAfterCompilePolicy.ShouldAutoContinue(enabled, attributable,
alreadyContinuedThisTurn)` の第 3 引数として拒否していた。

理由は「パネルの中で唯一、人間が何も送らずにエージェントを動かす機能」だから。継続ターンが
またコミットし、またリロードし、また継続する……という **人間が一度も介在しないコンパイル→
再プロンプトの無限ループ** を、設計は「the worst possible failure here」と位置づけていた。

## 2. なぜ緩和するか

2026-09-10 の設計ノート(`2026-09-10-auto-approve-all-tools-and-lean-auto-continue.md`
§3)で、兄弟機能「中断後に自動継続する」の `MaxInterruptedContinuationsInARow = 3` を
撤廃した判断とまったく同じ構図になっている。

- 「書く → コミット → コンパイル → エラーを読む → 直す → コミット」は、この機能が
  自動化したい **正当な作業ループそのもの**。1 ホップで止めると、2 ホップ目ごとに人間が
  「続けて」を打ち直すことになり、機能の目的が半分しか達成されない。
- 上限を撤廃しても残る停止条件は 3 つあり、中断側と揃う:
  1. **起因判定**: 各ホップは、そのターンで `uap_scripts_commit` が実際にファイルを
     Assets/ に移した(`ScriptCommitMovedFiles`)場合だけチケットを武装する。エージェントが
     コミットをやめた瞬間に連鎖は終わる。チケットの 5 分期限(`TicketIsFresh`)も不変。
  2. **クラッシュループ停止**: `AgentHub.IsCrashLoopSuspended` なら
     `TrySendPendingAutoContinueMessage` が送信を放棄する(defect 4 の既存ガード)。
  3. **停止ボタン**: 継続ターンも通常ターンと同じく interrupt できる。
- 各ホップの `uap_scripts_commit` は自動承認レベルを上げていなければ許可カードを通るので、
  「無人」になるのはユーザーが明示的にそう設定したときだけ。

「無限ループ」の実態は「エージェントがコンパイルエラーを直し続ける」ことであり、それは
停止ボタンで止めるべき事象であって、機械的な 1 回制限で毎回人間に返すべき事象ではない、
と判断した。

## 3. 変更

- `AutoContinueAfterCompilePolicy.ShouldAutoContinue` / `DescribeOutcome` から
  `alreadyContinuedThisTurn` を削除。`AutoContinueSkipReason.AlreadyContinuedThisCycle`
  を削除(enum は `WillContinue` / `DisabledInSettings` の 2 値で残す)。
- `SessionStateBridge.AutoContinuePendingWasContinuation` を削除。
  `AutoContinueTurnIsContinuation` は「このターンはパネルが送った」という記録として
  従来どおり書き込み・クリアされるが、何も判定には使わない(defect 5 の取り残し防止
  テストはそのまま)。
- `AgentHub.HandleAutoContinueArming` はフラグを読まずにリセットだけする。
  `TryAutoContinueAfterCompile` は 2 引数で判定。
- L10n: `HubAutoContinuePendingAlreadyContinued`(英/日)を削除。
  `HubAutoContinuePendingWillContinue` の「1 回だけ」を外し、設定のヘルプとツールチップに
  「連鎖する・止まる条件は 3 つ」を明記。
- テスト: `ContinuationTurnItselfCommitsScripts_DoesNotArmASecondContinuation` を
  `..._ArmsAndSendsTheNextContinuation`(2 ホップ目が実際に送られる)に置き換え、
  `ContinuationTurnThatDoesNotCommit_DoesNotArmAnotherContinuation`(コミットしなければ
  連鎖しない = 起因判定が上限の代わり)を追加。ポリシーテストは 2 入力 × 2 の 4 通りに縮小。
- `docs/USER-GUIDE.md` の「(1 ターンにつき 1 回だけ)」を書き換え。

## 4. 変更ファイル

- `Editor/Model/AutoContinueAfterCompilePolicy.cs`、`Editor/Model/SessionStateBridge.cs`、
  `Editor/Model/PanelSettings.cs`(doc コメント)
- `Editor/Integration/AgentHub.cs`
- `Editor/UI/L10n/UiStrings.cs`、`UiStringsJa.cs`
- `Tests/Editor/AutoContinueAfterCompilePolicyTests.cs`、
  `Tests/Editor/AgentHubAutoContinueAfterCompileTests.cs`、
  `Tests/Editor/AgentHubAbortOpenTurnTests.cs`(アサートメッセージのみ)
- `docs/USER-GUIDE.md`
