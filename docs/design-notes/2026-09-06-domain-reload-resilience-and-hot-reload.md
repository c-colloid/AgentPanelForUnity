# 2026-09-06 -- ドメインリロード耐性の再点検と、ホットリロードの必要性評価

発端: 「ドメインリロード耐性が十分に機能していないように感じる。ホットリロードの
必要性も検証してほしい」というユーザー報告。実機の再現手順は添えられていないため、
本ノートは (1) 現行機構のコード監査で見つかった不備と修正、(2) 「不十分に感じる」
原因として設計上避けられていない体験の整理、(3) ホットリロード(=リロードをまたいで
CLI セッションを生かす仕組み)を導入すべきかの判定、の 3 部構成とする。

参照: `docs/ARCHITECTURE.md` D4/D5、`docs/research/11-compile-batching.md`(R11)、
`Editor/Integration/ReloadLifecycle.cs`、`Editor/Integration/AgentHub.cs`、
`Editor/Integration/CompileGate.cs`、`Editor/Ops/UapTurnScope.cs`。

---

## 0. 結論(先に)

- **コード上の不備は 2 件確認し、両方このノートと同じ変更で修正した**(§2)。
  1 件は「リロード直後にどちらが先に走るか」という Unity 側の未保証な順序に依存して
  いて、負けると **ターン中断のナッジも自動継続も出ずに静かに再接続する**。もう 1 件は
  リロード時のプロセス記録を kill の成否を見ずに消していたため、生き残った旧 CLI を
  二度と回収できなかった。
- ただし「不十分に感じる」体験の大半は不具合ではなく **D4 の kill+resume 設計そのものの
  限界**(§3)。実行中のターンは必ず中断される、Play モードの出入りでも中断される、
  毎回コールドスタートする、の 3 点が主因。
- **ホットリロード(CLI 常駐化)は「必須」ではない**(§4)。同じ体感改善の大部分は、
  既にある自動継続機構を中断ターン全般へ opt-in で広げるだけで得られ、コストは
  1/10 以下。中継プロセス方式は「Play モード中もエージェントを止めたくない」という
  要求が明確になった時点での v2 候補にとどめる(§5)。

---

## 1. 現行機構の棚卸し(コード読了に基づく事実)

| 事象 | 現行の挙動 | 守れているか |
|---|---|---|
| リロード前 (`beforeAssemblyReload`) | session id / TurnRunning / ClientWasRunning を SessionState へ、interrupt → stdin close → 500 ms 待ち → ツリーキル、トランスクリプト保存、コンパイル結果ダイジェストの持ち越し(HUB-8)、UapOps HttpListener 停止 | ○ |
| リロード後 (最初の update tick) | 孤児回収 → 自動継続の帳簿整理 → 自動継続の判定/キュー → `--resume` 再接続 → CompileGate キューの吐き出し | ○(順序は §2.1 の穴あり) |
| 入力途中テキスト / スクロール位置 | SessionState 経由で復元 | ○ |
| コンパイル中のユーザー送信 | CompileGate が SessionState キューへ退避、リロード後に順序保存で送信 | ○ |
| ターン中の Assets 書き込み | `UapTurnScope` が `DisallowAutoRefresh` でターン末まで検知を遅らせ、ターン末に 1 回だけ `Refresh()` | ○(エージェント起因のリロードは「ターン後」に寄せられている) |
| **ターン実行中のリロード**(ユーザーの手動コンパイル、IDE 保存後のフォーカス復帰、Rider の明示 Refresh、Play モード出入り) | interrupt 送信 → kill → `--resume` → 「中断されました [続きを実行]」バナー。**自動継続はしない**(uap_scripts_commit 起因のリロードのみ、設定 ON 時に自動継続) | △ 設計どおりだが体感の主因 |
| 実行中ツール / サブエージェント | kill で消える。カードは pending / stopped に降格 | △ 設計上の限界 |
| 保留中の許可カード | 破棄。resume 後にモデルへ再依頼が必要 | △ 設計上の限界 |
| エディタ終了 / クラッシュ | quit 時は Shutdown、クラッシュ時は次回起動の ZombieReaper が PID+開始時刻+プロセス名で回収 | ○ |
| Play モード | Reload Domain が有効なら enter/exit の 2 回とも上と同じ kill+resume | △ 未対策 |

EditMode テストは実際のドメインリロードを起こせない(既存ノートの「Honest residuals」
にも同じ記述)。従って上表の「○」はコード読了と過去の実機検証記録に基づく判定で、
今回も実機再現はしていない。

---

## 2. 見つかった不備と修正

### 2.1 起動順序依存: 窓側の起動が先に走ると中断フラグと自動継続チケットが消える

リロード後に CLI を起動し得る経路は 2 つある。

- `ReloadLifecycle` の静的コンストラクタが購読する **最初の `EditorApplication.update`**
  (旧 `OnFirstUpdate`)。ここが `RestoreAfterReload` を呼び、`SessionStateBridge.TurnRunning`
  を読んで `_resumedMidTurn` を立て、`TryAutoContinueAfterCompile` がチケットを消費する。
- `AgentPanelWindow.CreateGUI` が予約する **`EditorApplication.delayCall += StartHubDeferred`**
  → `AgentHub.EnsureStarted()`。

後者が先に走ると、`EnsureStarted` は

1. 自動継続チケット(`AutoContinuePendingAttribution` ほか)を無条件にクリアし、
2. `StartClient → TearDownClient → AbortOpenTurn` で `TurnRunning = _client != null && ...`
   = **false** を書き戻す(この時点で `_client` は null)。

その後に走る `RestoreAfterReload` は `TurnRunning=false` を読むので `ResumedMidTurn` が
立たず、「中断されました [続きを実行]」バナーが出ない。`TryAutoContinueAfterCompile` は
空のチケットを読むので自動継続もしない。**症状は「ターン中にコンパイルしたら、
何も言わずに再接続だけして止まっている」**で、報告された体感と一致する。

`delayCall` と `update` のどちらがリロード後に先に呼ばれるかは UnityCsReference にも
記載がなく(`Internal_CallDelayFunctions` / `Internal_CallUpdateFunctions` はネイティブ側から
別々に呼ばれる)、保証された契約ではない。過去の実機検証で問題が出なかったのは
「たまたま update が先だった」可能性が高く、環境(描画イベントのタイミング、
ウィンドウのドック状態)で入れ替わり得る。

**修正**: リロード後の一連の処理を `ReloadLifecycle.EnsureStartupReconciled()` という
**冪等な入口**に切り出し、`AgentHub.EnsureStarted()` が最初に必ずこれを呼ぶようにした。
どちらの経路が先でも同じ順序(孤児回収 → 帳簿整理 → 判定/キュー → restore → drain)で
1 回だけ走る。`RestoreAfterReload → EnsureStarted → EnsureStartupReconciled` の再入は
`_startupDone` を先に立てることで no-op になる。`ReloadLifecycleTests` が「1 ドメインで
高々 1 回」を固定する(実リロードは EditMode で起こせないため、そこまで)。

### 2.2 リロード時のゾンビ記録を kill の成否を見ずに消していた

`ShutdownForReload` は `TearDownClient(clearZombieRecord: true, 500ms)` で、ツリーキル直後に
`UserSettings/AgentPanel/cli-process.json` を削除していた。kill は best-effort
(taskkill 不在、アクセス拒否、終了処理との競合)で、失敗しても記録が消えているため、
リロード後の `ReapOrphansNow` は何も見つけない。生き残った旧 CLI は旧ターンを続行し、
`--resume` した新プロセスと **同じセッション JSONL に書き続ける**。

**修正**: リロード経路だけ `clearZombieRecord: false` にし、リロード後の
`ReapOrphansNow`(PID + 開始時刻 + プロセス名の三重照合)に死亡確認を委ねる。死んでいれば
記録が消えるだけ、生きていれば resume 前に kill される。エディタ終了経路(`Shutdown`)は
「終了後に検証できる tick がない」ため従来どおり消す(次回起動の回収がある)。

既知の残課題: ZombieReaper はプロセス名 `claude` を要求するため、npm 経由で `node` として
起動している環境では照合をスキップする(以前からの挙動、今回は変更しない)。

### 2.3 実機検証(同日追記)

「EditMode テストは実ドメインリロードを起こせない」という従来の前提は誤りだった。
Unity Test Framework 1.1 の `[UnityTest]` は `WaitForDomainReload` /
`EnterPlayMode` / `ExitPlayMode` を yield でき、リロード後にコルーチン位置だけを
復元して続行する(ローカル変数は失われるので SessionState 経由で受け渡す)。これを使い、
本物の `ClaudeCliProcess` で bash 製スタンドイン(`ci/FakeCli/claude`)を子プロセスとして
起動し、ターン途中で実リロードを起こす `ReloadLifecycleLiveTests` を追加した。

GameCI イメージ(`unityci/editor:ubuntu-2022.3.22f1-base-3`)上で全スイートを実行した結果:
2558 件中 2540 パス / 0 失敗 / 18 スキップ(`docs/verify/reload-live-tests-1.xml`)。
リロードが実際に起きたことはエディタログの
`Reloading assemblies due to reload request` / `Reloading assemblies for play mode`
で確認(`docs/verify/reload-live-tests-1-log-excerpt.txt`)。確認できた契約:

| シナリオ | 結果 |
|---|---|
| ターン中(tool_use 実行中)に `RequestScriptReload` | 旧プロセスに interrupt が届き、kill され、`--resume <同じ id>` で新プロセスが起動、`ResumedMidTurn` = true |
| ターン中に Play モードへ入る(Reload Domain 有効) | 同上 |
| Play モード中にターンを開いて Play を抜ける(既定設定) | ドメインリロードは起きず、同じプロセス・同じターンが継続 |

限界: バッチモードにはパネルウィンドウが無いため、§2.1 の「窓の deferred 起動が先に
走る」競合そのものは再現できない(冪等化の単体テストと、実リロード経路の E2E で挟んでいる)。
Windows 固有経路(`WindowsProcessKiller` / taskkill)は Linux コンテナでは通らない。

手動で確認する場合の手順:

1. ターン中(ツール実行中が分かりやすい)に、エディタ側で任意の `.cs` を保存して
   フォーカスを戻す → リロード後に **黄色い「中断されました」バナーと [続きを実行]** が
   出ること(2.1)。
2. リロード直前に `cli-process.json` が存在し、リロード後の最初の tick で消えること。
   意図的に `claude` を SIGSTOP/Suspend して kill を空振りさせれば、Console に
   `ZombieReaper: killing orphaned claude process` が出ること(2.2)。

`ci/HostProject/Assets/Editor/UapShot.cs` に `EditorUtility.RequestScriptReload()` を使う
リロード・シナリオを足せば CI で回せるが、それ自体が既存ノートで「opt-in」とされている
別作業なので今回は含めない。

---

## 3. 「不十分に感じる」体験の分析(不具合ではなく設計の限界)

kill+resume は「セッション(会話)は失わない」ことを保証するが、
「**進行中の作業**は失わない」ことは保証しない。ユーザーが体感するのは後者。

| 体感 | 原因 | 既存の緩和 | 残る痛み |
|---|---|---|---|
| コンパイルのたびにエージェントが止まり、[続きを実行] を押す | ターン中断は必ず interrupt+kill | ターン中の DisallowAutoRefresh、staging+commit、commit 起因のみ自動継続 | ユーザー起因のリロードは全部手動 continue |
| 毎回数秒待たされる | `--resume` はプロセス再起動 = node/bun のコールドスタート + トランスクリプト再読込 + 初回 API 呼び出しのキャッシュ再構築 | なし | 1 リロードあたり 2〜10 秒 |
| 走らせていたテスト / ビルド / サブエージェントが消える | ツリーキル | カードの降格表示 | 作業のやり直し |
| Play ボタンを押すとエージェントが止まる | Reload Domain 有効時は enter/exit で 2 回リロード | なし | ゲームを動かしながらの作業ができない |
| 許可待ちのカードが消える | 保留リクエストは resume で復元できない | なし | 再依頼が必要 |

つまり §2 の修正で「**中断したのに何も出ない**」は直るが、「**中断すること自体**」は
残る。ここを削るのがホットリロード議論の本体。

---

## 4. ホットリロードの必要性評価

### 4.1 用語の切り分け

「ホットリロード」は文脈で 3 つの別物を指す。

| 案 | 何をするか | 本パネルへの意味 |
|---|---|---|
| (A) Hot Reload for Unity 等の IL パッチ | ドメインリロードを起こさずメソッド本体だけ差し替える | ユーザー環境側の有償ツール。パネルが依存すべきものではなく、R11 でも「思想が近い」参考としてのみ言及 |
| (B) `LockReloadAssemblies` でターン中はリロードを遅延 | コンパイルは走るがアセンブリの切替を止める | ペア崩れ事故が重大(R11 §2)、ユーザー自身のコンパイル結果もターン末まで反映されず、エージェントの「書いて確かめる」ループとも相性が悪い。Phase 5 設計で **不採用** 済み |
| (C) CLI 常駐化(中継プロセス or パイプ引き継ぎ) | Unity のドメインが死んでも CLI プロセスとその stdio を生かし、リロード後に再接続する | 本当に「ターンを中断させない」唯一の案。D4 が v1 で退けた WS ブリッジ案の再検討 |

ユーザーの言う「ホットリロード」は、体感課題(§3)から見て (C) と解釈する。

### 4.2 (C) の実現方式と見積もり

**C-1: 中継プロセス方式**(claude を子に持つ小さな relay が stdio を握り、Unity とは
loopback TCP/名前付きパイプで会話。切断中は出力をバッファし、再接続で再送)

- 効果: ターン無中断、コールドスタート無し、Play モード出入りも透過、実行中ツールも生存。
- コスト: relay の実装・配布(依存ゼロ方針のため node 前提にできない。Windows は
  PowerShell か同梱 .NET exe、mac/Linux は sh + socat 相当が要る)、再接続プロトコル
  (シーケンス番号、バッファ上限、relay 自体の死活監視)、許可カードの再同期(切断中に
  届いた `can_use_tool` を保持して再提示)、ZombieReaper の対象拡大、UapOps MCP サーバの
  ポート変更への追従(CLI 側 MCP 再接続 `mcp_reconnect` が必要)。
- 見積もり: 実装 2〜3 週間相当 + 実機検証。テストは実リロードを伴うため CI では担保
  しにくい。
- リスク: relay が新たな単一障害点。D4 の「中継プロセスの複雑さに見合わない」判断は
  今も概ね妥当。

**C-2: パイプ・ハンドル引き継ぎ方式**(relay を置かず、リロード前に stdio の OS ハンドルを
finalizer から守って SessionState に退避し、リロード後に `FileStream` で再ラップ)

- 効果: C-1 と同じ。プロセス追加なし。
- コスト・リスク: 読取スレッドがネイティブ `ReadFile` でブロックしたままドメイン
  アンロードに入ると「Completing Domain」フリーズになる(`ClaudeCliProcess.Dispose` の
  注釈にある既知パターン)ため、`CancelSynchronousIo` 等での確実な解除が前提。
  Mono の `Process`/`FileStream` の finalizer 抑止、Windows/Unix で挙動が異なる。
  検証困難で、失敗モードが「エディタごと固まる」。**推奨しない**。

### 4.3 判定

- **必須ではない。** セッションの連続性(D4 の保証対象)は kill+resume で満たせており、
  §2 の修正で「中断が可視化されない」不具合も塞がる。
- 「ターンを中断させない」価値は大きいが、その **体感の 7 割は次の低コスト策で得られる**:
  ユーザー起因のリロードで中断したターンも、設定 ON 時は自動で continue を送る
  (§5-1)。残る 3 割(コールドスタート時間、実行中ツールの喪失、Play モード透過)だけが
  (C) でしか埋まらない。
- したがって順序は「§5-1 → 実機での再評価 → それでも Play モード中の作業継続が必要なら
  C-1」。

---

## 5. 推奨する次の一手(優先順)

同日追記: ユーザーから「Play の出入りやフォーカス復帰は頻繁に行う操作で、一部のテストには
必須」との指摘を受け、1 を **実装した**(§6)。2〜4 は未着手。

1. **中断ターンの自動継続を opt-in で一般化**(効果大・コスト小)— 実装済み(§6)
   既存の `uapOpsAutoContinueAfterCompile` は「commit 起因のリロード」に限定している
   (R11 §4 ガードレール 2)。これとは別に「ターン実行中に(原因を問わず)リロードで中断
   されたら、`ResumeBanner` の [続きを実行] と同じメッセージを自動送信する」設定を追加する。
   ガードレールは既存を流用: ターンあたり 1 回、システムノート必須、コンパイル結果
   ダイジェストの注入、クラッシュループ中は送らない、既定 OFF。
   ARCHITECTURE リスク表 #4 の「自動継続しない」根拠(ツール途中状態が不定)は、CLI が
   interrupt 時に中断済み tool_result を自分で書くため、ボタン手押しと同じ安全性で
   自動化できる。判断はユーザーに委ねる(既定 OFF)。
2. **Play モードの案内**: `EditorSettings.enterPlayModeOptionsEnabled` かつ Reload Domain
   OFF ならリロードは起きない。Settings の Agent セクションに「Play モード時の
   ドメインリロードでターンが中断される」旨のヒントと、現在のプロジェクト設定の表示を
   足す(設定を勝手に変えない)。
3. **実機リロード検証の自動化**: UapShot に `RequestScriptReload` シナリオを足し、
   §2.3 の 2 点を CI(EditMode ジョブ)で固定する。
4. **C-1 中継プロセス**: 上記 1〜3 の後、「Play モード中にエージェントを止めたくない」
   「長時間ツールを殺したくない」が実運用で残るなら着手。`IAgentTransport` の差し替え
   シームは D4 で残してある。

---

## 6. 実装: 中断後の自動継続(`autoContinueInterruptedTurn`、既定 OFF)

- **設定**: `PanelSettings.autoContinueInterruptedTurn`。Settings の UapOps セクション、
  「コンパイル後に自動継続する」の直下に「中断後に自動継続する」トグルとして追加
  (警告トーンの注記付き、既定 OFF)。
- **発火点**: `ReloadLifecycle.EnsureStartupReconciled` が `RestoreAfterReload` で
  `ResumedMidTurn` を立てた直後に `AgentHub.TryAutoContinueInterruptedTurn()` を呼ぶ。
  判定は純粋関数 `AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted`:
  設定 ON、実際にターンが中断された、クラッシュループ停止中でない、同じリロードで
  コンパイル継続がすでにキューされていない、連続回数が上限(3)未満、のすべてを要求する。
- **送信経路**: 既存の単一スロット自動継続キュー(`QueueAutoContinueMessage` →
  `TrySendPendingAutoContinueMessage`)を流用。よって「送信可能になるまで待つ」「60 秒で
  諦めて撤回ノートを出す」「クラッシュループ中は送らない」を無償で継承する。
- **メッセージ**: `ComposeInterruptedContinuationMessage` — 「Unity のドメインリロード
  (再コンパイルまたは Play モード切替)で前のターンが中断された。結果を受け取っていない
  ツール呼び出しは再実行して続けよ」。HUB-8 の持ち越しダイジェスト(または生きている
  Console の可視エラー)があれば添付する(古い「コンパイルは通っている」前提での継続を防ぐ)。
- **可視性**: 送信直前に専用のシステムノート `HubAutoContinueInterruptedResuming` を
  会話ログへ。送信が確定したら `ResumedMidTurn` を下ろし、バナーの [続きを実行] を消す。
- **暴走防止**: `SessionStateBridge.AutoContinueInterruptedStreak` がターン完了なしの連続
  回数を数え、上限で停止してその旨のノートを出す(手動ナッジは残る)。`OnTurnCompleted`
  で 0 に戻る。継続ターンは `AutoContinueTurnIsContinuation` も立てるため、その継続が
  スクリプトをコミットしてもコンパイル継続は連鎖しない(既存ガードレール 1 と同じ扱い)。
- **検証**: `AutoContinueInterruptedTurnPolicyTests`(判定表と文面)と、
  `ReloadLifecycleLiveTests.MidTurn_ScriptReload_WithAutoContinueOn_SendsContinueByItself`
  (実リロード後、resume した新プロセスに continue が届き、ノートが残り、バナーが消える)。

Play モードの「出」については実機で「既定設定ではドメインリロードが起きずターンは
継続する」ことを確認済み(§2.3)。従って本機能で Play の入りと、IDE 保存後のフォーカス
復帰の両方が「クリック不要」になる。

## 7. ユーザー実機レポート(Windows 11 / VRChat プロジェクト)への対応

ユーザーが実機(Unity 2022.3.22f1, Windows 11, uloop 3.0.0-beta.37)で行った検証では、
MCP / uloop ツール接続そのものはリロード(Play 突入・終了)を跨いで再接続なしに即応答し、
SessionState / EditorPrefs も保持された。指摘された 3 件の扱い:

| Issue | 管轄 | 対応 |
|---|---|---|
| #1 Play モード中・エディタ非フォーカス時に `uap_query_hierarchy` が "The operation timed out." | 本パッケージ(UapOps) | 対応済み(下記) |
| #2 `uloop focus-window` が稼働中の Unity を "No running Unity process found" と誤報告 | uLoopMCP(`io.github.hatayama.uloopmcp`) | 上流の問題。本パッケージ側では、CLI へ注入する UapOps 誘導文に「Editor の死活判定は `uap_ping` が正。focus-window の未検出は検出失敗であり再起動の根拠にしない」を追記して誤読を防ぐ |
| #3 uloopmcp パッケージの空ディレクトリ `.meta` 警告 | uLoopMCP | 上流へ報告する事項。本パッケージでは対応なし |

### Issue #1 の分析と対応

"The operation timed out." は本パッケージの文面ではない(dispatcher の文面は
"timed out waiting for the Unity main thread")。Bun の `AbortSignal.timeout` の既定
メッセージがこの文字列であり、Claude Code CLI 側の HTTP 待ち時間が先に尽きたと読むのが
自然。Play モード中に Editor が非フォーカス(かつ Run In Background が OFF)だと Editor
ループの tick が疎らになるため、`EditorApplication.update` で回している
`UapMainThreadDispatcher.Pump` が呼ばれず、tools/call が main thread を待ったまま
CLI 側の予算を超える。`uap_ping` が同時刻に通ったのは、たまたま tick が予算内に来たため
と考えると「一過性」の観測と整合する。

対応(すべて本パッケージ内):

1. **停滞の検知**: `UapOpsServer.OnUpdate`(main thread)が毎 tick
   `isPlaying && !InternalEditorUtility.isApplicationActive && !runInBackground` を
   スナップショット(`ComputeThrottleNotice`、純粋関数)し、worker スレッドからは
   volatile 文字列だけを読む。`UapMainThreadDispatcher.Pump` は最終 tick 時刻を刻む。
2. **タイムアウト文面**: main-thread 待ちのタイムアウトに「Editor ループの最終 tick から
   N 秒」と原因・対処(Editor ウィンドウをフォーカスする、または Run In Background を ON)を
   含める(`DescribeStall`)。
3. **スロットル中は先に短く失敗する**: 停滞スナップショットが立っている間は待ち予算を
   15 s → 8 s(`ThrottledTimeoutMillis`)に切り替える。CLI 側の予算がいくらであれ、意味の
   ある本パッケージの文面が先にモデルへ届くようにするため(ユーザー提案の「延長」は、
   CLI 側予算が先に尽きる以上、文面なしの失敗を増やすだけと判断)。
4. **成功応答にも警告を付与**: uloop と同様、スロットル条件下の成功した tools/call には
   `Warning: ...`(focus-window 推奨)のテキストブロックを末尾に追加する
   (`UapOpsRequestHandler` の `noticeProvider`)。

未確定事項: CLI 側の tools/call 予算(`MCP_TIMEOUT` 30 s / `MCP_TOOL_TIMEOUT`)は
docs/research/08 のバイナリ解析値であり、今回の失敗が何秒で起きたかは報告に無い。
次回は失敗までの所要秒数を記録してもらえると、CLI 起動時に環境変数で予算を延ばす
(`ClaudeCliProcess` はスポーン環境を握っている)価値を判断できる。

検証: `UapOpsThrottleNoticeTests`(判定表・文面・短縮予算・成功応答への付与)。
Play モード非フォーカス状態そのものはバッチモードでは再現できない。

## 8. 属性の堅牢化(`[InitializeOnLoadMethod]` への移行)

`ReloadLifecycle` と `UapTurnScope` は `[InitializeOnLoad]` の静的コンストラクタで購読して
いた。静的コンストラクタが例外を投げると型ごと `TypeInitializationException` で使用不能に
なり、リロード前の teardown・quit ハンドラ・リロード後の復帰がそのセッションの残り全部で
黙って消える。`[InitializeOnLoadMethod]` なら例外はログに出るだけで型は生き残る。
コストは同じ(ドメインロード時に 1 回、購読のみ。エディタログの `Domain Reload Profiling`
では `ProcessInitializeOnLoad*Attributes` はエディタ全体で 200 ms 前後、本パッケージの寄与は
計測不能な水準)。

併せて `EnsureStartupReconciled` の各ステップ(孤児回収 → 帳簿整理 → コンパイル継続判定 →
restore → 中断継続判定 → drain × 2)と `beforeAssemblyReload` の teardown を個別に
try/catch で囲み(`Guarded`)、1 ステップの失敗が後続のセッション復元を道連れにしない
ようにした。特に teardown が失敗しても `TurnRunning` / `ClientWasRunning` は必ず書かれる。

## 変更ファイル

- `Editor/Integration/ReloadLifecycle.cs`: `EnsureStartupReconciled()`(冪等)へ切り出し、
  `StartupReconciled` seam、孤児回収の注釈更新。
- `Editor/Integration/AgentHub.cs`: `EnsureStarted()` 冒頭で `EnsureStartupReconciled()`、
  `ShutdownForReload()` を `clearZombieRecord: false` に。
- `Tests/Editor/ReloadLifecycleTests.cs`: 1 ドメイン高々 1 回の固定。
