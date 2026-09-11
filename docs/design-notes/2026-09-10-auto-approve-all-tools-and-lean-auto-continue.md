# 2026-09-10 -- 「自動」を自動にする: 全ツール自動承認、汎用 Always ルール、軽い自動継続

発端: 同日のユーザー報告(原文の要旨)。

1. リロード後の自動継続が **長文** を送っていてトークンの無駄。
2. その **送信自体を会話ログに出す必要** を感じない。
3. **3 回で自動継続が止まる** 理由が分からない。
4. 権限の「自動」が自動と呼べる機能を持っておらず、**1 アクションで 10 回以上** 確認される。
5. 権限の事前設定が煩雑。「常に許可」が **パス付きのルール** を保存するので汎用性が無く
   ほぼ意味をなしていない。

参照: `docs/design-notes/2026-09-06-domain-reload-resilience-and-hot-reload.md` §6、
`docs/design-notes/2026-09-10-permission-queue-depth-and-reload-dropped-permission.md`、
`Editor/Model/UapAutoApproveLevel.cs`(2026-08-03 の CLI モード実測表)、
`Editor/Model/AutoApprovePolicy.cs`、`Editor/UI/PermissionCard.cs`、
`Editor/Model/AutoContinueAfterCompilePolicy.cs`、`Editor/Integration/AgentHub.cs`。

---

## 0. 結論(先に)

| # | 根本原因 | 決定 |
|---|---|---|
| 1 | 継続文面が「見てから再実行せよ」の説明込みで約 600 文字(≈150 トークン)。2026-09-08 の事故(9 分のメニューを再実行)への対策として膨らんだ | 要点だけの 2〜3 文に圧縮(≈40 トークン)。「実行前に確認」の指示は 1 文で残す |
| 2 | 継続はユーザーメッセージとして吹き出しに出し、さらに「自動継続します」のシステムノートも出す(2 重) | 吹き出しを出さない。短いシステムノート 1 行だけ残す(会話ログの正直さは保つ) |
| 3 | `MaxInterruptedContinuationsInARow = 3` は「リロードの嵐」への保険として置いたが、正当な作業(コミット→リロード→継続→…)も止める。クラッシュループは別ガードが既にある | 上限を撤廃。クラッシュループ停止(`IsCrashLoopSuspended`)と手動 interrupt だけ残す |
| 4 | `AutoApprovePolicy.ShouldAutoApprove` は **UapOps 以外を常に拒否**(`isUapOpsTool == false` → false)。Bash/Edit/Write/Read/Glob/Grep/WebFetch は最上位レベルでも毎回カード | 新レベル **AllTools** を追加: AskUserQuestion(`requires_user_interaction`)以外の全 can_use_tool を承認。スクリプトゲートの自動拒否は従来どおり先に走る |
| 5 | Always メニューは CLI の `permission_suggestions` をそのまま出す。CLI の提案はパスやコマンド接頭辞付き(`Read(//…/**)`、`Bash(git status:*)`)で、ツール全体の許可が選べない。MCP ツールだけ例外的にツール全体ルールを合成していた | 全ツールで **ツール全体ルール**(`Edit`、`Bash`、`Read`…)を合成して先頭に置く。CLI の提案は「狭い選択肢」として残す |

`--allowedTools` のワイヤ構文(`AgentClient.cs` が未検証と注記)は本日 CLI 2.1.220 の
`--help` で確認: 「Comma or space-separated list of tool names to allow (e.g. "Bash(git *) Edit")」。
現行の「フラグの後に値を 1 つずつ」はこの space-separated 形そのもの。ヘッドレス実行での
効果測定は npm 版・デスクトップ同梱版とも OAuth 期限切れで不可(パネル経由の対話セッション
は動いている、既知の非対称)。

---

## 1. 権限: AllTools レベル

### 1.1 なぜ CLI 側でなくパネル側か

2026-08-03 の実測表(UapAutoApproveLevel.cs 冒頭)のとおり、CLI の `acceptEdits` は
Write/Edit 系だけ、`dontAsk` は拒否、`bypassPermissions` はスポーン時の
`--dangerously-skip-permissions` 相当でフックによるスクリプトゲートを含めて全部素通り。
パネル側で can_use_tool に即答する方式なら、ゲートの自動拒否(`TryAutoDenyForScriptGate`)が
先に走る順序(2026-09-10 前ノート §2 でテスト固定済み)を保ったまま「残り全部を承認」できる。

### 1.2 v0.14 の「Bash は遅い経路のまま」判断の撤回

当時の理由は (a) スクリプトゲートがカード経路に依存する、(b) Bash は何でもできる。
(a) は順序固定で解消。(b) は事実だが、確認疲れで 10 回以上クリックする状態は
「読まずに Allow を押す」を誘発し、安全性にも寄与していない。**ユーザーが明示的に
選ぶ最上位レベル**として提供し、既定にはしない。ヘッダーの昇格確認ダイアログは
AllUnityOps と同様に AllTools への昇格でも出す。

### 1.3 例外

- `requires_user_interaction`(AskUserQuestion)は自動承認しない。質問に答えるのは人。
- スクリプトゲート該当の Write/Edit/Bash は従来どおり自動拒否(順序は前ノート §2 のテスト)。
- 何も表示しない代わりに、ツールカード自体は従来どおり会話ログに出る。

## 2. 権限: ツール全体の Always ルール

`PermissionCard.CollectSuggestions` は CLI 提案が空かつ MCP ツールのときだけ
`SynthesizeMcpAlwaysAllowRule` でツール全体ルールを作っていた。これを
「質問カード以外の全ツールで、CLI 提案にツール全体の unscoped ルールが無ければ合成して
先頭に置く」に変更する。保存先は従来どおり `PanelSettings.allowedTools`(次回スポーンの
`--allowedTools`)と、その場の `updatedPermissions`(CLI セッション側)。

「exactly one or the other, never both」だった旧方針は、1 回の Always で適用される
ルールが 1 つである限り崩れない(メニューから 1 項目を選ぶ)。

## 3. 自動継続: 文面・表示・上限

- **文面**: `ComposeInterruptedContinuationMessage` は
  「Unity のドメインリロードでターンが中断。実行中のツールは終わっていない。副作用の
  ある操作は状態を確認してから再実行し、続けよ」+(あれば)許可待ちだったツール名 1 文
  +(あれば)コンパイルエラー要約。`ComposeContinuationMessage` も 1〜2 文へ。
- **表示**: `TrySendPendingAutoContinueMessage` はユーザー吹き出しを追加しない
  (`SendUserMessage` に `recordTranscriptBubble:false` 相当の内部経路を追加)。
  既存の 1 行システムノート(`HubAutoContinueResuming` / `HubAutoContinueInterruptedResuming`)
  だけを短くして残す。CLI 側 JSONL には従来どおり user メッセージとして残るため、
  履歴を CLI トランスクリプトから読み直した場合は本文が見える(残課題、許容)。
- **上限撤廃**: `MaxInterruptedContinuationsInARow`、`SessionStateBridge.AutoContinueInterruptedStreak`、
  枯渇ノート `HubAutoContinueInterruptedStreakExhaustedFmt` を削除。判定は
  「設定 ON・実際に中断・クラッシュループ停止中でない・同じリロードでコンパイル継続が
  未キュー」の 4 条件のみ。

---

## 変更ファイル

- `Editor/Model/UapAutoApproveLevel.cs`(AllTools)、`Editor/Model/AutoApprovePolicy.cs`
  (requiresUserInteraction 引数付きオーバーロード)、`Editor/UI/AutoApproveLevelLabels.cs`、
  `Editor/UI/HeaderView.cs`、`Editor/Integration/AgentHub.cs`(自動承認・自動継続)。
- `Editor/UI/PermissionCard.cs`(ツール全体ルール合成)。
- `Editor/Model/AutoContinueAfterCompilePolicy.cs`、`Editor/Model/SessionStateBridge.cs`。
- `Editor/UI/L10n/UiStrings*.cs`(レベル名、短縮ノート、ヘルプ文)。
- Tests: `AutoApprovePolicyTests`、`AutoApproveLevelLabelsTests`、
  `PermissionCardSuggestionSynthesisTests`、`AutoContinueInterruptedTurnPolicyTests`、
  `AutoContinueInterruptedTurnMessageTests`、`AgentHubAutoContinueAfterCompileTests`、
  `ReloadLifecycleLiveTests`(streak 参照の除去)。

## 検証(2026-09-10、AITemp / 2022.3.22f1 / Windows)

- フル EditMode スイート: 2930 件中 2920 パス / 1 失敗 / 9 スキップ
  (`docs/verify/2026-09-10-full-editmode-b.xml`)。失敗 1 件は短縮したノート文面への
  古い文字列ピン(`AgentHubAutoContinueAfterCompileTests`)で、L10n 定数との等価比較に
  直して同フィクスチャ 25 件を再実行、全パス。
- `--allowedTools` の効果測定(ヘッドレス実行)は CLI の OAuth 期限切れで未実施。構文は
  `--help` の記述で確認済み。
- 実機での「1 アクション 10 回以上」の再計測は未実施(AllTools を選んだ状態で 1 アクション
  あたりのカード数を数えれば判定できる)。
