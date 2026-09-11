# 2026-09-10 -- 権限サーフェスとドメインリロード挙動の改善

発端: 「権限」と「ドメインリロード時の挙動」の 2 点を改善せよ、という指示。具体的な
再現手順は添えられていないため、まず両領域のコード監査を行い、監査で裏付けの取れた
不備だけを対象にした。監査の根拠は `path:line` で示す(2026-09-10 時点の main)。

参照: `docs/design-notes/2026-09-06-domain-reload-resilience-and-hot-reload.md`(§3
「残る痛み」表、§5-2 未着手項目)、`docs/review-backlog.md`(ULTRA-3, UXA-9)、
`Editor/Core/Client/AgentClient.cs`、`Editor/Integration/AgentHub.cs`、
`Editor/Integration/ReloadLifecycle.cs`、`Editor/UI/PermissionCard.cs`。

---

## 0. 結論(先に)

監査で確認した「利用者が体感する不備」は 4 件。いずれも設計変更ではなく既存機構の
穴埋めで、同じ変更で実装した。

| # | 領域 | 症状(根拠) | 対応 |
|---|---|---|---|
| 1 | 権限 | 並列 tool_use の許可要求は `AgentClient._permissionQueue` に **無制限に**溜まるが(`AgentClient.cs:218`)、UI は常に 1 件しか見せず、後ろに何件待っているか分からない(`AgentHub.cs:66` の単一スロット) | キュー深さを Hub まで露出し、カード両ホストに「あと N 件」を表示 |
| 2 | 権限 | スクリプトゲートの自動拒否 → UapOps 自動承認の順序は **コメントでしか守られていない**(`AgentHub.cs:3766-3783` 自身が "an accident of today's naming, not a guarantee" と明記) | 順序を固定する回帰テストを追加(コードは変えない) |
| 3 | リロード | 許可待ちカードはリロードで **黙って消える**(`AbortOpenTurn` が `_pendingPermission = null`、`AgentHub.cs:3149`)。Deny には「拒否した」ノートが残るのに、リロードによる破棄には何も残らない。自動継続 ON でも継続メッセージは「実行中だったツールは終わっていない」としか言わず、**承認されなかった**ことを伝えない | リロード前に待機中ツール名を SessionState へ退避し、リロード後にシステムノートを出す。継続メッセージにも「X は許可待ちのまま承認されなかった。実行済みと見なすな」を添える |
| 4 | リロード | Play モードでの中断は Reload Domain 設定次第で **起きない**(§2.3 実測)が、パネルはそれを案内しない(前ノート §5-2 未着手) | Settings の UapOps セクションに、現在のプロジェクト設定(Enter Play Mode Options / Reload Domain)と「ターンが中断されるか」を表示するヒントを追加。設定は勝手に変えない |

見送ったもの(理由付き):

- **許可待ちのタイムアウト**: UX 仕様(R05 §3.7)が「許可待ちにタイムアウト無し」と定めており、
  `AgentClient.cs:502-512` はその意図で沈黙バックストップを止めている。変えない。
- **`--allowedTools` の複数値ワイヤ構文の実測**(`AgentClient.cs:859-867` が未検証と明記):
  実プロセスに対する計測が要る別作業。本ノートの範囲外とし、backlog へ残す。
- **Always-allow ルールの専用管理 UI**: 設定判断が要る(スコープ表示、由来の区別)。
  backlog の Q 群と同じ扱い。
- **中継プロセスによるホットリロード**: 前ノート §4.3 の判定どおり、まだ着手しない。

---

## 1. 権限: キュー深さの可視化(ULTRA-3 の前半)

### 1.1 根本原因

CORE-6 で `AgentClient` が can_use_tool を FIFO 化した(`AgentClient.cs:1198-1255`)。
「上位層の単一アクティブ許可モデルは変えない」が設計方針だったため、Hub は
`PendingPermission` 1 件しか持たず(`AgentHub.cs:407-411`)、UI からはキューの存在
自体が見えない。サブエージェント並列時(`docs/review-backlog.md` ULTRA-3)は
「答えても答えても次が出る」体験になり、あと何件かが分からない。

### 1.2 選択肢

| 案 | 内容 | 評価 |
|---|---|---|
| A | `AgentClient.QueuedPermissionCount` を追加し、Hub の `PendingPermissionQueueDepth` 経由でカードに「あと N 件」を表示 | 単一アクティブモデルを保ったまま情報だけ足す。最小 |
| B | キュー全体をカードにリスト表示し、一括承認/一括拒否を付ける | ULTRA-3 後半。一括承認は「見ていないものを許可する」導線になるため、設計判断が要る。今回は見送り |
| C | Hub が複数件を同時に保持する | CORE-6 の方針転換。HUB-4 の単一スロット前提(`AgentHub.cs:3796-3808`)を崩す。不採用 |

**採用: A。** 深さは「表示中の 1 件を除いた待ち件数」と定義する(0 なら何も出さない)。

### 1.3 実装

- `AgentClient.QueuedPermissionCount`(`_permissionQueue.Count`、main thread のみ)。
- `AgentHub.PendingPermissionQueueDepth`: `_client` があり `_pendingPermission` が非 null の
  ときだけ `_client.QueuedPermissionCount`、それ以外は 0。
- `PermissionCard.Refresh` がヘッダ行に「あと N 件待機中」ラベルを出す(両ホスト共通、
  N=0 で非表示)。文言は L10n(`PermQueueDepthFmt`)。
- 回帰ガード: `AgentClientPermissionQueueDepthTests`(FakeCliProcess で 3 件流し、
  深さ 2 → 応答で 1 → 0)、`PermissionCard` 側はラベルの表示/非表示のみ。

## 2. 権限: 自動拒否 → 自動承認の順序を固定する

### 2.1 根本原因

`OnPermissionRequested`(`AgentHub.cs:3764-3815`)は `TryAutoDenyForScriptGate` を
`TryAutoApproveUapOpsTool` より先に呼ぶ。逆順だと、`AllUnityOps` レベルで
「Assets 配下の .cs を書く UapOps ツール」が現れた瞬間にゲートを素通りする。今日は
ツール名の名前空間が重ならないので観測できないだけで、コードは順序をテストで
固定していない。

### 2.2 対応

コードは変更しない。`AgentHubScriptGateTests` と同じ配線(`WireClientForTests`)で、
自動承認レベルを `AllUnityOps` にしたうえで、スクリプトゲートに該当する Write 要求を
流し、**拒否ノートが出て許可は送られない**ことを固定する
(`AgentHubPermissionOrderTests`)。将来の並べ替えはここで落ちる。

## 3. リロード: 許可待ちの破棄を可視化し、継続メッセージに反映する

### 3.1 根本原因

`ReloadLifecycle.OnBeforeAssemblyReload` → `AgentHub.ShutdownForReload` →
`TearDownClient` → `AbortOpenTurn` が `_pendingPermission` を無条件に消す
(`AgentHub.cs:3146-3158`)。これは正しい(答える相手のプロセスはもう居ない)。問題は
その事実が **どこにも記録されない**こと:

- トランスクリプト: Deny は `RespondToPendingPermission` がノートを残す
  (`AgentHub.cs:2384-2397`)が、リロード破棄は無音。ユーザーには「カードが消えて
  バナーだけ出た」ように見える。
- 継続メッセージ(`AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage`、
  `AutoContinueAfterCompilePolicy.cs:447-470`)は「実行中だったツール呼び出しは
  終わっていない」としか言わない。許可待ちだったツールは **実行すら始まっていない**
  うえ、ユーザーが承認したのか拒否したのかも決まっていない。モデルが「中断された
  =途中まで走った」と読むと、`uap_ping` → 状態確認 → 「済んでいる」と誤判定する
  余地がある(前ノート 2026-09-08 §1 の「見てから再実行せよ」指示と同じ穴)。

### 3.2 選択肢

| 案 | 内容 | 評価 |
|---|---|---|
| A | リロード前に待機中ツールの表示名を SessionState に退避。リロード後にシステムノート + 継続メッセージへ 1 文追加 | 破棄自体は変えず、記録だけ足す。最小 |
| B | リロード後に同じ can_use_tool を再提示し、ユーザーの答えを resume 後の再要求に自動適用 | 再要求の request_id も input も変わり得るため照合できない。tool_use_id も新しくなる。不採用 |
| C | 許可待ち中は `LockReloadAssemblies` でリロードを遅らせる | R11 §2 のペア崩れ事故。Phase 5 で不採用済み |

**採用: A。**

### 3.3 実装

- `SessionStateBridge.ReloadDroppedPermissionTool`(string、空=無し)。
- `ReloadLifecycle.OnBeforeAssemblyReload`: `ShutdownForReload` の **前**に
  `AgentHub.PendingPermission` の表示名(`CanUseTool.DisplayName`、無ければ `ToolName`)を
  退避。`TurnRunning` と同じ「teardown が失敗しても書く」扱い。
- `ReloadLifecycle.EnsureStartupReconciled`: `RestoreAfterReload` の直後に
  `AgentHub.NoteReloadDroppedPermission()`(退避値を消費してシステムノート
  `HubReloadDroppedPermissionFmt` を会話ログへ)。`wasRunning` が false のときは
  消費だけして出さない(ターンが無いのにノートは出せない)。
- `TryAutoContinueInterruptedTurn`: 退避値を `ComposeInterruptedContinuationMessage`
  の新引数 `pendingPermissionTool` に渡す。文面: 「ツール X は許可待ちのまま中断され、
  承認も拒否もされていない。実行されたとは見なさず、必要なら改めて要求せよ」。
  順序上、ノートの消費より前に読む必要があるため、退避値の読み出しは
  `NoteReloadDroppedPermission` が返し、Hub が同 tick 内で使う。
- 回帰ガード: `AutoContinueInterruptedTurnMessageTests` に文面の有無、
  `ReloadLifecycleTests` に「退避 → 消費 → ノート」の単体経路(実リロード無し)。
  Windows では `ci/FakeCli` が bash 製のため Live テストは走らない(既存の限界)。

## 4. リロード: Play モード設定の案内(前ノート §5-2)

### 4.1 根本原因

Play の出入りでターンが中断されるかは `EditorSettings.enterPlayModeOptionsEnabled` と
`EnterPlayModeOptions.DisableDomainReload` で決まる(Live テスト
`ReloadLifecycleLiveTests.cs:155` も同じ判定で分岐)。パネルはこの状態を表示せず、
ユーザーは「Play を押すと止まる」を仕様と思うか、自動継続を ON にするしかない。

### 4.2 対応

- 純粋関数 `PlayModeReloadAdvisor.Describe(bool optionsEnabled, EnterPlayModeOptions options)`
  → `{ ReloadsOnPlay: bool }`。UI は `SettingsView.BuildUapOpsSection` の
  「中断後に自動継続する」トグルの直下にヒントを 1 行足し、
  再コンパイル系のリロードは設定に関係なく起きることも併記する。
  設定を変えるボタンは付けない(前ノートの方針「勝手に変えない」)。
  Project Settings を開く導線は `SettingsService.OpenProjectSettings("Project/Editor")`
  のリンクボタン 1 つに留める。
- 回帰ガード: `PlayModeReloadAdvisorTests`(4 通りの組み合わせ)。

---

## 5. 同日観測: 実機の「Reloading Domain」ハングと、teardown の孤児掃除の競合

### 5.1 観測(2026-09-10 12:03、KAWAII GAME ROOM / Unity 2022.3.22f1 / Windows 11)

本作業の開始時点で、パネルを導入した別プロジェクトのエディタ(PID 78708)が
「Reloading Domain (busy for 20:29)...」のまま固まっていた。触らずにログと
ファイルの時刻だけを集めた事実:

| 時刻 | 事象 | 根拠 |
|---|---|---|
| 12:01:56 | パネルが CLI をスポーン(PID 48804) | `UserSettings/AgentPanel/cli-process.json` の書込時刻 |
| 〜12:02 | Package Manager が git パッケージ `jp.colloid.unity-agent-panel` を再解決(`@f1505f0dd4` → `@79195776f1`)。UI イベント経由 = ユーザー操作 | Editor.log 24889 行目 "Done resolving packages"、直前の UIElements スタック |
| 12:02〜03 | 旧キャッシュのソースが消えて `CS2001` 多数 → Tundra 再実行で成功 → 「Reloading assemblies after forced synchronous recompile」 | Editor.log 26399〜26798 行目 |
| 12:03:22 | "Begin MonoManager ReloadAssembly"(ログの最終行) | Editor.log の最終書込時刻 12:03:22 |
| 12:03:24 | `SessionCache.json` 保存 = `ShutdownForReload` の最終ステップが完走 | 書込時刻。teardown(interrupt → stdin close → 150 ms → taskkill /T)は終わっている |
| 12:23 現在 | ドメインアンロードが完了しない。CLI 48804 は死亡、12:01〜12:04 に生成された孤児プロセスは無し | `Get-CimInstance Win32_Process` |

セッションキャッシュの末尾は AskUserQuestion(約 22 時間待ち)に答えた直後の
Thinking ブロックで、**ターン実行中**のリロードだった。直前に
`[AgentPanel] Unexpected state transition Ready -> WaitingPermission` が 6 回出ている
(§5.3)。

### 5.2 判定

デバッガ(cdb/procdump)が環境に無く、スレッドスタックは取れていない。したがって
**このハングの原因をパネルと断定はできない**(候補: 本パッケージ、uLoopMCP 3.6.1、
VRChat SDK、その他)。言えるのは:

- パネルの beforeAssemblyReload 処理自体は完走している(SessionCache 保存まで到達)。
- ハングは「アンロードを待つ側」、すなわち **ネイティブ呼び出しでブロックしたまま
  abort できない managed スレッド**の古典パターンと整合する。本パッケージで該当し得る
  のは CLI の stdout/stderr 非同期リーダ(パイプの書込側を握る生き残りがいる場合)だが、
  今回は生き残りプロセスが観測されなかったため、この経路が今回の原因である証拠は無い。

再発時に取る証拠: タスクマネージャで Unity.exe の「ダンプ ファイルの作成」→ WinDbg で
`!threads` / `~*k`。これが無いと次も断定できない。

### 5.3 コードで確認できた競合(修正)

`ClaudeCliProcess.OnProcessExited` は Exited イベント(ThreadPool)から
`KillOrphanedChildren`(親 PID が死んだ root の子を Toolhelp で拾って kill)を呼ぶ。
一方 `Dispose()` は `process.Exited -= OnProcessExited` で購読を外す。リロード経路は
`Stop(150 ms) → Dispose()` の連続呼出しなので、CLI が interrupt + stdin EOF に応じて
**自力で終了した直後**(150 ms の猶予を僅かに超えたケース、または taskkill 発行と同時)
には、Exited コールバックがまだキューにあるうちに購読が外れ、掃除が走らない。
その場合、CLI の子(MCP サーバ、シェル)は親 PID が死んだ孤児として残り、継承した
stdout/stderr の書込ハンドルを握り続ける = リーダスレッドが `ReadFile` で止まったまま
ドメインアンロードに入る、という OnProcessExited のコメントが警告している状況そのもの。

対応: `Dispose()` が `_running` のとき `Kill()` の直後に `KillOrphanedChildren()` を
同期的に呼ぶ(冪等、Toolhelp スナップショット + 子ごとの taskkill で有界)。
回帰ガード: `ClaudeCliProcessDisposeSweepLiveTests`(UAP_LIVE_CLI=1 かつ Windows 限定。
cmd.exe を root、`start /b ping` を孫に見立て、root が interrupt 行で自然終了した後の
Stop→Dispose で ping が消えることを固定)。ユニットテストは実プロセスを起動しない
規約のため、ゲート付きの live テストとした。

### 5.4 `Ready -> WaitingPermission` の警告について

`AgentClientState.IsExpectedTransition` の遷移表は Ready → WaitingPermission を
「予期しない」としているが、`AgentClient.PromoteNextPermission` は Ready からの昇格を
明示的に許している(`AgentClient.cs:1252-1257`)。コード内で矛盾しており、ログのとおり
実際に起きる(適用はされる)。仮説: `--resume` 直後(Ready、ユーザー送信なし)に、
CLI が **tool_result の無い tool_use を再実行**して can_use_tool を送ってくる
(今回のセッションは AskUserQuestion が約 22 時間待ちで、リロード=resume の回数と
警告 6 回が近い)。実測せずに遷移表へ足すと、本当に異常な順序を見失うため今回は
変更しない。次の権限まわりの作業で、resume 直後の最初の受信行を記録して確かめる。

---

## 変更ファイル

- `Editor/Core/Process/ClaudeCliProcess.cs`: `Dispose()` での同期孤児掃除(§5.3)。
- `Tests/Editor/ClaudeCliProcessDisposeSweepLiveTests.cs`(新規、ゲート付き)。

## 検証(2026-09-10、AITemp / 2022.3.22f1 / Windows)

- フル EditMode スイート: 2914 件中 2905 パス / 0 失敗 / 9 スキップ
  (`docs/verify/2026-09-10-full-editmode.xml`)。新規 5 フィクスチャ + 拡張 2 件は
  すべてパス。
- `UAP_LIVE_CLI=1` で `ClaudeCliProcessDisposeSweepLiveTests` を単独実行: 1 件パス
  (`docs/verify/2026-09-10-dispose-sweep-live.xml`)。途中で 2 回落ちたのはテスト側の
  問題(cmd /c の外側クォート、Unity の Mono が `ProcessName` を "PING.EXE" と返す)で、
  いずれもテスト内に注記して修正した。この live テストは「Stop→Dispose 後に孤児が
  消える」という不変条件を固定するもので、OnProcessExited 側と Dispose 側のどちらが
  掃除したかまでは区別しない。
- 実機(KAWAII GAME ROOM)での再現確認は未実施。当該エディタは §5.1 のハング状態のまま
  で、本作業では触っていない。

- `Editor/Core/Client/AgentClient.cs`: `QueuedPermissionCount`。
- `Editor/Integration/AgentHub.cs`: `PendingPermissionQueueDepth`、
  `NoteReloadDroppedPermission`、`TryAutoContinueInterruptedTurn` への引き渡し。
- `Editor/Integration/ReloadLifecycle.cs`: 退避と消費の 2 ステップ。
- `Editor/Model/SessionStateBridge.cs`: `ReloadDroppedPermissionTool`。
- `Editor/Model/AutoContinueAfterCompilePolicy.cs`: 継続文面の引数追加。
- `Editor/Model/PlayModeReloadAdvisor.cs`(新規)。
- `Editor/UI/PermissionCard.cs`、`Editor/UI/SettingsView.cs`、`Editor/UI/L10n/UiStrings*.cs`。
- Tests: 上記 5 フィクスチャ。
