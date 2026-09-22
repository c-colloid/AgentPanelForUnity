# uLoop 常駐のコストと「オン/オフ機能」の要否

Date: 2026-09-21
Status: 調査結果（実装判断は保留 — 本ノートは根拠の提示まで）
調査対象:

- uLoop (`hatayama/unity-cli-loop`) **v3.6.3**（`Packages/src/package.json`、
  パッケージ ID は歴史的経緯で `io.github.hatayama.uloopmcp` のまま）を
  GitHub から READ-ONLY でクローンしてソース確認。調査時点の main は
  `0a80c8c`（2026-09-21）。
- 本リポジトリ側の連携コード（`SettingsView.BuildUloopSection` /
  `AgentHub.ComposeUapOpsSteeringSection` / `UloopDetector` /
  `UloopCapabilityMatrix`）。
- 先行調査 `docs/research/10-uloop-and-ecosystem.md`（当時 3.0.0-beta.71）の
  更新版という位置づけ。Unity エディタは起動していないので、CPU 使用率や
  ドメインリロード時間の**実測値は取っていない**（§7）。

---

## 0. 結論（先に）

1. 「uLoop 導入後は常に読み込まれた状態になる」という理解は**正しい**。常駐物は
   4 つあり、いちばん重いのは **16ms 間隔の無条件 tick ポンプ**（エディタが
   アイドルでも非フォーカスでも止まらない）。
2. ただし **その 4 つはどれもパネル側のトグルでは消せない**。uLoop の
   エディタ側アセンブリ・サーバ・ポンプ・スキルは uLoop パッケージと Unity の
   所有物で、パネルから止める正規の口は無い。したがって
   **「uLoop オン/オフ」と名乗るトグルを付けると、ユーザーが期待するもの
   （常駐が消える）と実際に起きること（エージェントが uloop を使わなくなるだけ）が
   食い違う** — 今のパネルの「見せてから変える」方針に反する。
3. 現仕様（検出 + ワンクリック導入 + 許可プリセット + ステアリング）は
   壊れていない。足すとしたら「オン/オフ」ではなく、
   (a) **常駐コストの可視化**（導入ボタンの前後で事実を見せる）、
   (b) **エージェントに uloop を使わせるか**だけを切る限定トグル、
   (c) 導入ボタンの対になる**削除導線**（＝唯一の本物の OFF）。
   優先度は (a) > (b) > (c)。
   **v0.59.0-beta.1 で (a)(案 0)・(b)(案 A)・(c)(案 B) をすべて実装済み。**
4. 常駐にはこちらにとっての**得**もある。uLoop の tick ポンプは、UapOps 側の
   既知の失速（非フォーカス時にエディタがティックせずディスパッチャが止まる）を
   むしろ緩和する方向に働く（§3.2）。「常駐 = 一方的な害」ではない。

---

## 1. 「常駐」の中身（ソース確認）

### 1.1 16ms の無条件 tick ポンプ — 最も重く、サーバを止めても消えない

`Packages/src/Editor/Infrastructure/Threading/AutoTickPumpService.cs`

```csharp
/// Editor glue that keeps a SignalTick pump running for the whole editor session,
/// mirroring com.unity.pipeline's AutoTickCommand (unconditional 16ms pump).
/// Why always-on: the previous scoped pump (in-flight request + trailing window) let an
/// unfocused editor go fully idle after the window expired; macOS then stopped scheduling
/// the process, so the next IPC request could not even be accepted (pre_accept_timeout)
```

- `EditorApplication.update` と `EditorApplication.tick` の両方に `Pump` を登録し、
  16ms（`AutoTickPumpConstants.PUMP_INTERVAL_MS = 16`）ごとに
  `EditorApplication.SignalTick()`（内部 API）を叩き続ける。
- 登録元は `InfrastructureEditorStartup.Initialize`（17 行目）で、これは
  `[InitializeOnLoadMethod]`（`CompositionRoot/UnityCliLoopEditorBootstrap.cs`）から
  **無条件に**呼ばれる。IPC サーバの起動可否とは独立。
- つまり `Window > Unity CLI Loop > Server` で **サーバを Stop しても、この
  ポンプは止まらない**。エディタは約 60Hz でティックし続ける＝
  「非フォーカスで放置しても CPU/ファン/バッテリーを使い続ける」状態になる。
  uLoop 側にとっては意図した設計（IPC を確実に受けるため）であり、バグではない。
- 補足（コード上の観察、実機未確認）: `AutoTickPumpService` には
  `AssetDatabase.IsAssetImportWorkerProcess()` のガードが無い。サーバ制御側
  （`UnityCliLoopServerController.IsBackgroundUnityProcess`）には有るので、
  インポートワーカープロセスでもポンプだけは登録される可能性がある。

### 1.2 IPC サーバはエディタ起動時に自動で立つ

- `UnityCliLoopApplicationRegistration`:110 が
  `controllerService.InitializeForEditorStartup()` を呼び、その中
  （`UnityCliLoopServerController.cs`:150）で `ScheduleStartupRecovery` →
  `RestoreServerStateIfNeeded` が走る。
- `SessionRecoveryService`:63 で「手動停止フラグ」が立っているときだけ起動を
  スキップする。そのフラグの実体は
  `UnityCliLoopSessionFlagsRepository` → `UnityCliLoopEditorSessionStateStorage.GetBool`
  → **Unity の `SessionState`**。つまり **Stop はエディタ再起動で失われ、
  次回起動時にはまた自動で立つ**。「今後は起動しない」という永続設定は無い。
- トランスポートはプロジェクト単位の Named Pipe / Unix Domain Socket。
  TCP ポートを持たないのでポート衝突は原理的に無く、Windows では所有者限定
  ACL + リモート拒否（R10 §1.2 で確認済み、3.6.3 でも同構成）。
  **常駐によるネットワーク的な危険は小さい**。

### 1.3 ドメインリロードのたびに再初期化される

- `[InitializeOnLoadMethod]` → ブートストラップ →
  `ApplicationEditorStartup` / `FirstPartyToolsEditorStartup` /
  `InfrastructureEditorStartup` / `PresentationEditorStartup` を毎回実行。
  `FirstPartyToolsEditorStartup.Initialize` は 15 前後のツール別 startup を
  順に叩く（play-mode、compile、hot-reload、watch、dynamic-code、get-logs、
  screenshot、record-video、input 系…）。
- サーバは `beforeAssemblyReload` で落とし、`afterAssemblyReload` で
  エンドポイントを再バインド（最大 5 秒のリカバリ）。**コンパイルのたびに
  この往復が乗る**。
- uLoop のエディタアセンブリ自体も当然ビルド対象に増える（Mono.Cecil、
  Newtonsoft.Json、Harmony を依存に持つ）。ドメインリロード時間への影響は
  構造上「増える」方向だが、**本調査では実測していない**。

### 1.4 スキル 21 件がエージェントの文脈に常時載る

`uloop skills install --claude` で `.claude/skills/` に配置される
（uLoop リポジトリ内の実物を計測）:

| 項目 | 実測値 |
|---|---|
| スキル数 | 21 |
| frontmatter（name + description）合計 | 5,662 文字 |
| うち最大 | `uloop-hot-reload` 867 文字、`uloop-simulate-mouse-input` 542 文字 |
| 本文（SKILL.md 全体）合計 | 約 99,663 文字 |

Claude Code はセッション開始時に **各スキルの name/description をシステム
プロンプトに載せ、本文は呼び出し時に読む**。したがって常時課金されるのは
frontmatter 側 = **5.7KB ≒ 1.5〜2k トークン程度**（文字数からの概算）。
uLoop 自身も README でこのコストを認めている:

> "even when tools are packaged as Skills, each tool's description still
> consumes the context window."

これはパネルのセッション全部に乗る固定費で、**uLoop を一度も呼ばない
セッションでも同じだけ乗る**。

### 1.5 「常駐しない」もの（過大評価しないための確認）

- **Harmony パッチ**: `PausePointEditorStartup` は再アーム用のスケジューラを
  張るだけで、`SourcePausePointPatcher` が実際にパッチを当てるのは
  pause-point を仕掛けたときのみ（永続先も `SessionState`）。
  「導入しただけで全メソッドにパッチが当たる」ことは無い。
- **Auto Refresh の保持**: hot-reload のパッチが生きている間と compile 中だけ
  （`HotReloadAutoRefreshHold`）。常時 Auto Refresh を止めるわけではない。
- **CLI**: `uloop` はコマンド実行のたびに起動するディスパッチャで、
  常駐デーモンではない。PATH に 1 本増えるだけ。
- **MCP サーバ**: V3 で MCP 経路は廃止済み。`tools/list` を圧迫することは無い
  （圧迫するのは §1.4 のスキル）。

---

## 2. uLoop 側に用意されている調整つまみ

| つまみ | 場所 | 効き方 | 永続性 |
|---|---|---|---|
| サーバ Start/Stop | `Window > Unity CLI Loop > Server` | IPC サーバのみ停止。ポンプ・アセンブリ・スキルは残る | **セッション限り**（SessionState） |
| ツール個別 enable/disable | `Window > Unity CLI Loop > Settings` → `.uloop/settings.tools.json` | 無効ツールはレジストリから外れ、`SkillDisabledToolFilter` によって**そのツールのスキルも配置対象から外れる** → §1.4 の固定費を削れる | ファイル永続（チーム共有可） |
| スキルの導入/更新 | 同 Settings の Install/Update Skills、または `uloop skills ...` | 配置先（`.claude/skills` / `.agents/skills` / `--global`）を選べる | ファイル |
| CLI の Uninstall | 同 Settings | PATH 上の `uloop` を削除 | 永続 |
| パッケージ削除 | Unity Package Manager | **これだけが完全な OFF** | 永続 |

要点: **「常駐を止める永続スイッチ」は uLoop 側にも無い**。最小化の唯一の
現実解は「使わないツールを個別に無効化してスキルを減らす」＋
「本当に要らないならパッケージごと外す」。

---

## 3. 本パネルにとっての損得

### 3.1 損

1. **ツール選択の迷い（実測済み）**。`docs/design-notes/2026-08-02-noise-permissions-and-uloop-preference.md`
   §3 のセッションでは、anim モジュールが有効で型付きツールも登録済みなのに
   アニメ作業 24 件がすべて `uloop execute-dynamic-code` を通った。
   2026-09-08 にも Camera / TextMeshPro / rotation の編集で同じ再発。
   → 対策は入っている（`AgentHub.ComposeUapOpsSteeringSection`:4572 のステアリング）。
2. **権限面**。`uloop execute-dynamic-code` は「エディタ内で任意 C# を
   コンパイル実行」の 1 粒度。許可プリセットは意図的にこれを allow にも
   disallow にも入れず、毎回カードを出す設計（CHANGELOG 0.18.1）。
3. **過去に実際にフリーズさせた**サブコマンド（`update` / `sync` / `launch`）。
   → プリセットが `disallowedTools` で影を落とし済み。
4. **常時 CPU**（§1.1）と**常時コンテキスト**（§1.4）。これが今回の質問の本丸で、
   **パネル側には対処する手段が無い**。

### 3.2 得

1. **非フォーカス時のティック**。UapOps 側は
   `UapMainThreadDispatcher`（37〜52 行）/ `UapOpsServer`（200〜230 行）に
   「Play Mode 中に非フォーカスだとエディタが散発的にしかティックせず、
   呼び出しが失速する」という既知の注意書きと通知文を持っている。
   uLoop の無条件ポンプはこの状況を**改善する**側に働く。
   つまり §1.1 は純粋なデメリットではなく、トレードオフ。
2. **こちらが実装しない領域をカバー**: compile / run-tests / pause-point /
   hot-reload。UapOps は「型付き・Undo 統合・権限カード」路線で、
   これらは意図的にスコープ外（`UloopCapabilityMatrix` の重複集合は今も空 — 20 行目）。

---

## 4. パネル側で実際に切れるもの / 切れないもの

| 常駐コスト | パネルのトグルで消せるか |
|---|---|
| 16ms tick ポンプ | **不可**（uLoop の InitializeOnLoad。サーバ停止でも残る） |
| IPC サーバ常駐 | **不可**（uLoop の Server ウィンドウのみ、しかもセッション限り） |
| ドメインリロードごとの再初期化 | **不可** |
| スキル 21 件の文脈コスト | **不可**（uLoop の tool disable / skills 再配置が必要） |
| エージェントが uloop を選ぶこと | **可**（ステアリング文 + allowed/disallowedTools） |
| `uap_*` 側の重複登録抑止 | 可（`UloopCapabilityMatrix`、ただし現状は空で無効果） |

---

## 5. 選択肢

### 案 0（推奨・必須）: 現仕様のまま、コストを可視化する

> **実装済み（v0.59.0-beta.1）**: uLoop 連携カードの先頭に 1 行 + ホバーで詳細、
> `docs/USER-GUIDE.md` の uLoop 連携の項にも同じ内容。未導入なら「導入すると
> 何が常駐するか」、導入済みなら「弱めるには uLoop 側のどのウィンドウか」に
> 文面が切り替わる（`SettingsView.UloopCostHint` / `UloopCostTooltip`）。
> v0.58.2 の「常時表示のヒントを減らす」整理と矛盾しないよう、
> 2026-08-04 ノート §4 の作法どおり **常時表示は 1 行・詳細はツールチップ**に
> 収めている（ヒントは 110 文字上限のテスト対象でもある）。

- uLoop 連携セクションの導入ボタン付近に、導入すると何が常駐するかを
  1〜2 行で明示（常駐サーバ、エディタを常時ティックさせるポンプ、
  スキルの文脈コスト）。導入後は「止めたいときは `Window > Unity CLI Loop`
  の Server / Settings（ツール個別無効化でスキルも減る）」という導線を出す。
- `docs/USER-GUIDE.md` の uLoop 連携の項（248 行目付近）にも同じ趣旨を追記。
- 理由: パネルの導入ボタンこそがユーザーをこの状態に入れる入口なので、
  **入口で事実を見せる**のが最小コストかつ最大効果。実装は文字列と
  ヒントのみで、機能的リスクがない。

### 案 A（任意）: 「uloop をエージェントに使わせる」トグル

> **実装済み（v0.59.0-beta.1）**: `PanelSettings.uloopAgentUseEnabled`
> （default ON）。uLoop 連携カードの、案 0 のコスト行の直下に置いた。
> ラベルは「エージェントに uloop コマンドを使わせる」で、
> ヒント・ツールチップの両方に「uLoop 自体は止まらない」と明記してある。

**3 層にした理由。** deny パターン 1 枚では足りない。2026-08-02 の
スクリプトゲート調査（設計 §8.7 ケース A1）で、`--disallowedTools` の
パターンが CLI に受理されたのに黙って無視された実測例があるため、
OFF のときは次の 3 つが同時に効く:

1. **ステアリング文**（`UloopAgentUsePolicy.SteeringLine(false)`）:
   「uloop は拒否される」と先に言う。拒否されてから学ぶのでは 1 ターン無駄。
2. **起動引数**（`ComposeDisallowedTools`）: `ScriptGate.ShellToolNameList`
   の各シェル（Bash / PowerShell / Shell）に `X(uloop)` と `X(uloop *)` を
   ユーザーの `disallowedTools` の**後ろに重ねる**。ユーザーのリスト自体は
   書き換えない（spawn 時のオーバーレイ）。ACP バックエンドはツールリストを
   渡さないので、そちらは 3 を最後の砦とする。
3. **`can_use_tool` での拒否**（`AgentHub.TryAutoDenyUloopCommand`）:
   本パッケージが端から端まで所有している唯一の層。判定は
   `ScriptGate.CommandSegments` を再利用しており、`cd P && uloop compile`、
   `... | uloop`、`./uloop`、`C:\tools\uloop.exe`、`ULOOP` をすべて拾う。
   `uloopctl` や `echo uloop` のような別物は拾わない。

**順序は load-bearing。** `AgentHub` の権限処理でこの拒否は
**スクリプトゲートの後ろ**に置く。ゲートが先に refusal を返す必要がある
（同ファイルのコメントが明示している不変条件）ため。

- 効く相手: §3.1 の 1〜3（ツール選択の迷い・権限面・危険サブコマンド）。
  効かない相手: §1.1 / §1.4 — **これは仕様であって欠陥ではない**。
  トグルの文面もそう書いている。
- 反映は次の接続から（`SettingsChangeDetector` が再接続待ちを立てる）。
  ただし 3 の拒否は設定を都度読むので、再接続前の隙間で緩くなることはない。
- 拒否メッセージは代替ツール（`uap_play_mode` / `uap_console_logs` /
  `uap_scripts_commit` ほか）と「本当に必要ならユーザーに言え」を名指しする。
  素の「拒否しました」だけだと、モデルは別のシェルを試しにいく。
- テスト: `UloopAgentUsePolicyTests`（EditMode）と smoke ゲートの
  `UloopAgentUseSmoke`（ライセンス不要側）で同じ規則を二重に留めている。

**実機で見つかった不具合（v0.59.0-beta.2 で修正）。** オフにすると
「次回再接続後に適用されます」の保留表示が、再接続しても消え続けた。

原因は 1 行の書き漏らし。`uloopAgentUseEnabled` を
`SettingsChangeDetector.RequiresReconnect` の比較には足したが、
**spawn 時のスナップショットを作る `AgentHub.CloneNextSpawnOnlyFields`
に足し忘れていた**。あれはフィールドを 1 つずつ手書きでコピーする関数なので、
漏れたフィールドはスナップショット側で既定値（オン）に固定される。
以後 `RequiresReconnect` は「オン vs オフ」を永久に見続け、
どれだけ再接続しても保留が消えない。表示だけの問題ではなく、
以後の設定変更のたびに不要な再接続が 1 回走る状態でもあった。

**再発防止。** 個別のフィールド名を書くテストでは、コードと同じ書き漏らしを
テスト側でも繰り返すだけで意味が無い（実際、既存のテストは全部そうだった）。
そこで `CloneNextSpawnOnlyFieldsTests` は名前を一切書かず、
`PanelSettings` の全フィールドをリフレクションで 1 つずつ変更 → クローン →
`RequiresReconnect` が差分なしと言うことを検査する。
**「比較対象なのにクローンが落とすフィールド」だけが、自分の名前を出して落ちる。**
比較対象でないフィールドをクローンが落とすのは仕様（`model` /
`agentModelOverrides` は意図的にその側）なので、そこは何も言わない。
実測で、修正前はこのスイープが `uloopAgentUseEnabled` ただ 1 つを名指しで落とす。

**同じ症状のもう 1 つの原因（v0.59.0-beta.3 で修正）。** 上の修正後も、
**短い間隔で 2 回切り替える**と保留表示が固まった。今度はこのトグル固有ではなく、
設定の自動適用そのものの欠陥。

1 回目の変更で自動再接続が走り、`Reconnect()` が spawn を始める。
その **spawn 中（`Starting`）に 2 回目の変更**が来ると、
`RequestAutoApplyReconnect` の
`if (!IsAutoApplyEligibleClientState()) { CancelAutoApply(); return; }`
が「未接続だから何もしない」と判断して**要求を捨てる**。
`TryAdvanceAutoApply` も同じ判断でティックを外す。
そして **`Ready` になったときに再評価する経路が存在しない**
（ティックは外れており、呼び戻すのはターン完了だけ）。
結果、spawn 時のスナップショットは 1 回目の値のまま固定され、
現在の設定は 2 回目の値。**誰も実行しない再接続を永久に予告し続ける。**
手動再接続だけが（再スナップショットするので）解ける。

**修正。** 「使えない」を 2 種類に分けた（`AutoApplySettingsPolicy.ShouldArm`）:

- **一時的**（spawn 中）: 要求を armed のまま保持し、client が落ち着いてから適用する。
  `Starting` の client は「二度と起動しない client」ではなく、たいていは
  **この機能自身の 1 回目の再接続**である、というのが要点。
- **恒久的**（未起動 / エラー）: 従来どおり破棄する。次の本物の spawn が
  現在の設定を読むので、それで正しい。

あわせて、ティックは毎エディタ更新で走るのに
`ComputeAutoApplyReconnectNeeded()` がカスタム指示ファイルをディスクから読むため、
spawn 待ちの間は**安価な判定だけで抜ける**ように順序を入れ替えた
（待機のコストが毎フレームの read ではなく enum 比較になる）。

**実機（Unity 2022.3.22f1、GameCI コンテナ）の結果**（`c07323f`）:

| 構成 | total | passed | failed | skipped |
|---|---:|---:|---:|---:|
| Pro あり（`ci/HostProject`） | 4458 | 4431 | **0** | 27 |
| Core のみ（`ci/HostProjectCoreOnly`） | 3644 | 3617 | **0** | 27 |

案 0 時点（4425 / 3611）から +33 で、内訳は新規 `UloopAgentUsePolicyTests`
26 ケース + ステアリング 3 + 変更検出 3 + 既定値 1。ライセンス不要の smoke
ゲートは 184 → 202 チェック。

**まだ確認していないこと**（実機の手動確認が要る）: 実際に CLI が
`--disallowedTools` の `X(uloop)` / `X(uloop *)` を**効かせる**かどうか。
本ノートが 3 層にした理由そのものが「効かない実測例がある」なので、
ここは自動テストでは押さえられない。層 3（`can_use_tool` 拒否）は
このパッケージ側で完結しているため、層 2 が無視されても最終的な挙動は
変わらない、という設計にしてある。

### 案 B（要望があれば）: 導入ボタンの対になる「削除」導線

> **実装済み（v0.59.0-beta.1）**: 「uLoop を削除」ボタン。導入と同じ確認カード・
> 同じ差分表示・同じバックアップ作法。設計と、なぜ 2 段階になるかは
> `docs/design-notes/2026-09-22-uloop-remove-from-panel.md`。

- 本当の OFF は「パッケージを外す」だけなので、導入ボタンと対称に
  `Client.Remove` / manifest 差分提示付きの削除を出す案。
- 導入時と同じ「差分を見せてから適用」の作法を守る必要があり、
  scopedRegistry の後始末（他パッケージが同レジストリを使っている場合は
  残す）など分岐が増える。**今回の質問への答えとしては過剰**で、
  Package Manager から外せる導線を案内するだけで足りる可能性が高い。
- **↑ この「過剰」という判断は取り下げた。** 実装してみて分かったのは、
  分岐が増えるのは scopedRegistry の後始末**規則**（完全一致のスコープ
  だけ消す・他の依存が使っていれば残す・他所のレジストリには触らない）
  であって、そこは純粋関数として書けてテストで留められる、ということ。
  むしろ厄介だったのは**順序**のほうで、`Client.Remove` が manifest を
  自分で書き換えるため、こちらの書き込みは削除の着地を観測してからでないと
  依存行を復活させる。詳細は新ノートの §2。

### 案 C: 何もしない

- 機能的には破綻していないので成立する。ただし「常時 CPU」「常時トークン」は
  ユーザーから見えないままで、今回のような疑問が繰り返し出る。案 0 を推す理由。

---

## 6. 推奨

- **「uLoop オン/オフ」という形の機能は付けない**（パネルが所有していない
  ものを切れると誤認させるため）。
- 代わりに **案 0 を実施**。案 A は、uLoop 導入済みで `uap_*` を主に使う
  ユーザー向けの実利があるので、やるなら案 0 と同じブランチで
  「エージェント使用のみを切る」と正直にラベリングして入れる。
  → **実施済み**: 案 0・案 A とも v0.59.0-beta.1 で、同じ uLoop 連携カードに
  入った（コスト行の直下にトグル）。ラベルは「エージェントに uloop コマンドを
  使わせる」で、常駐が残ることをヒントとツールチップの両方に書いてある。
- 案 B は要望が出てから。
  → **要望が出たので実施済み**（v0.59.0-beta.1）。「uLoop を削除」ボタンが
  導入ボタンと同じ位置に入り、常駐を本当に止める唯一の手段がパネルの中から
  実行できるようになった。設計は
  `docs/design-notes/2026-09-22-uloop-remove-from-panel.md`。

---

## 7. 未確認事項（推測に留めた点）

- **実測していない**: uLoop 導入前後の CPU 使用率、ドメインリロード時間の差、
  メモリ増分。この環境から Unity エディタを起動できないため。案 0 を出す
  前提としては「ポンプが無条件に回る」というコード上の事実で十分だが、
  数値が欲しい場合は実機で（アイドル 5 分の CPU 時間を uLoop 有/無で比較）。
- **スキルのトークン数は概算**（5,662 文字からの換算）。正確な値は
  実セッションのシステムプロンプト長で測る必要がある。
- インポートワーカープロセスでのポンプ登録（§1.1 補足）は実機未確認。
- uLoop は更新が速い（調査時 3.6.3）。ここでの引用は `0a80c8c` 時点のもので、
  ポンプやサーバ自動起動の仕様は将来変わりうる。

---

## 8. 追調査: 16ms ポンプの負荷は何の負荷か / メモリ増加の犯人候補

「16ms tick は相当な負荷では? アイドル放置中にメモリリークで PC が落ちたのは
これが原因では?」という問いへの切り分け。結論: **ポンプは CPU/電力のコストで
あってメモリのコストではない**。メモリ増加には別の、より直接的な候補がある。

### 8.1 ポンプが食うのは CPU であってメモリではない

- `AutoTickPumpService.Pump` は 1 tick あたり `Stopwatch.Restart()` と
  `EditorApplication.SignalTick()` を呼ぶだけで、**割り当てを行わない**
  （使い回しの `Stopwatch` 1 個のみ）。ポンプ自体がヒープを増やす経路は無い。
- 効くのは「エディタがアイドルに入れなくなる」こと。Unity には
  `Preferences > General > Interaction Mode`（"specifies how long the Editor
  can idle before it updates" / "throttle Editor performance, and reduce
  consumption of CPU resources and power"）があり、
  Default = 1 tick あたり最大 4ms アイドル、No Throttling、
  Monitor Refresh Rate（≒16ms）、Custom 0〜33ms。
  ポンプは 16ms ごとに `SignalTick` でループを叩き起こすので、
  **非フォーカスで放置していても実質 Monitor Refresh Rate 相当で回り続ける**
  ＝ 「裏に回したときの省電力」が消える。ノート PC ではファン/バッテリーに
  効くが、RAM が増える話ではない。
- 注意すべきは**増幅効果**。エディタループが 60Hz で回るということは、
  `EditorApplication.update` に登録された**全パッケージの毎フレーム処理**と、
  毎フレーム発生する例外ログが、アイドル時でもフル回転するということ。
  本リポジトリには実測の前例がある: 2026-08-02 のノート §1 で、
  1 セッションのログに同一の `MissingReferenceException` が **1106 個**
  溜まった自己増幅ループ（v0.14.0 で修正済み）。この形の不具合は、
  60Hz 常時ティックと組み合わさると桁違いに速くメモリを食う。

### 8.2 uLoop 側で「実際に回収されない」と本家が認めている箇所

uLoop の issue **#949**「execute-dynamic-code reset does not provide true
memory reclamation for loaded dynamic assemblies」:

- `execute-dynamic-code` は `Assembly.Load(byte[])`
  （`DynamicCompilation/CompiledAssemblyLoader.cs`:18）でスニペットを
  ドメインに読み込む。**コンパイルキャッシュは 32 件で上限があるが、
  読み込まれたアセンブリはキャッシュから追い出されても残る**。
- 本家の計測値: reset でマネージドメモリ約 -4.0MB、さらにドメインリロードで
  約 -2.8MB 追加回収。つまり **完全な解放はドメインリロード（＝再コンパイル
  やエディタ再起動）まで起きない**。
- 性質は「使った分だけ増える」であり、アイドル放置で増えるものではない。
  ただし **エージェントに dynamic-code を何十回も使わせたあと、
  ドメインリロードなしでエディタを開きっぱなしにすると、その分は居座る**。
- 本パネルのステアリング（`AgentHub.ComposeUapOpsSteeringSection`）が
  `uap_*` を優先させているのは、結果的にこの蓄積を減らす方向に働く。

### 8.3 ディスクに書かれるもの（RAM ではない）

`VibeLogger` の出力先は `{project_root}/.uloop/outputs/VibeLogs/`、
最新 **20 ファイル**まで自動 prune（`MAX_LOG_FILES = 20`）。
放置でディスクが埋まる形にはなっていない。

### 8.4 本パネル側の該当箇所（自分の側も確認した）

- `ConsoleErrorProvider`: 保持は最大 100 件、メッセージ 500 文字で切り、
  同一メッセージは出現回数に畳む（無制限に溜まらない）。
- `ImageThumbnailCache`: 容量 32 の LRU で、追い出し時に
  `DestroyImmediate` する（スクリーンショットのテクスチャは無制限に残らない）。
- いずれも今回のような「放置で GB 級に増える」形ではない。

### 8.5 切り分け手順（実機でやるべきこと）

1. **増えているのが何かを見る**: Windows なら Task Manager で `Unity.exe` の
   コミットサイズを 30 分おきに記録。Native が増えているのか Managed かは
   Memory Profiler のスナップショット 2 枚（放置前/放置後）の diff で判る。
2. **ログループの有無**: `%LOCALAPPDATA%\Unity\Editor\Editor.log` の
   サイズ増加率。数十 MB/時のオーダーなら例外ループが犯人で、中身を見れば
   どの例外かまで分かる（8.1 の前例と同じ形）。
3. **Play Mode で放置していなかったか**: Play 中の放置はゲーム側のリークを
   そのまま拾う。エディタの常駐物より先に疑うべき。
4. **A/B テスト**: uLoop パッケージを一時的に外して同じ条件で放置し、
   増加率が変わるか見る。ポンプもサーバもこれでしか止まらない（§2）。
5. **dynamic-code の使用履歴**: その日のセッションで
   `uloop execute-dynamic-code` を多用していたなら 8.2 の蓄積が乗っている。
   対処は「重いセッションのあとは一度コンパイル（ドメインリロード）か
   エディタ再起動を挟む」。

### 8.6 現時点の判定

- **16ms ポンプが単独でメモリリークを起こすことはない**（割り当てゼロ）。
  常時 CPU を使う設計であることは事実なので、ノート PC での常用には向かない。
- **メモリ増加の第一容疑者は (a) 例外ログのループ、(b) Play Mode 放置、
  (c) dynamic-code のアセンブリ蓄積**の順。ポンプは (a) を増幅する立場で、
  「犯人」ではなく「共犯になりうる」位置。
- 断定するには 8.5 の 1〜2 の数字が要る。現時点では**証拠不足**。

---

## 9. ADR 追補: 「uLoop をそのまま使う」のは悪手か / 自前実装すべきか

問い（2026-09-21 ユーザー質疑）: uLoop に相乗りせず、実装から学んで
AgentPanel に最適化した自前実装にすべきだったのでは。

既存の判断は `2026-08-01-phase5-unity-ops-design.md` §3.1/§3.2/§8.6。
今回 v3.6.3 の全ソースを読んだので、**規模と残ギャップを実数で**更新する。

### 9.1 規模の実測（テスト除く、`Packages/src/Editor` 配下の .cs 行数）

| 対象 | 行数 |
|---|---|
| uLoop Editor 全体 | **156,708** |
| └ hot-reload | 51,118（手書き。最大ファイル 666 行、Cecil/Harmony 併用） |
| └ execute-dynamic-code | 14,283 |
| └ pause-point + watch | 9,387 + 1,526 |
| └ Infrastructure（IPC/スレッド/設定） | 21,821 |
| └ Domain | 14,774 |
| └ screenshot / compile / run-tests / play-mode | 4,485 / 5,451 / 4,173 / 1,541 |
| 本パネル `Editor/Ops` 全体（Core） | **25,114**（ツール 32 本） |
| Agent Panel Pro のツール | 34 本 |

**uLoop 全体の再実装は、こちらの Ops 層を 6 倍に膨らませる規模**であり、
しかもその半分近く（hot-reload + dynamic-code + pause-point ≒ 75k 行）は
Cecil / Harmony / Roslyn を使う最も壊れやすい種類のコードである。

### 9.2 実は「ほぼ自前実装済み」だった — カバレッジ対照

| uLoop のツール | 本パネル側 | 判定 |
|---|---|---|
| find-game-objects / get-hierarchy | `uap_query_hierarchy` / `uap_search` / `uap_object_inspect` | 済 |
| screenshot | `uap_editor_screenshot`（マーカー座標付き） | 済 |
| run-tests | `uap_test_run`（Pro） | 済 |
| compile | `uap_scripts_commit`（ステージ→コンパイル成功時のみ Assets へ）+ CompileGate + コンパイル結果の会話への自動差し戻し | **別方式で済**（エージェントが polling しない） |
| simulate-mouse-ui 系（エディタ UI） | `uap_editor_ui_click` / `_set_value` / `_dump` / `_list_windows`（Pro） | 済 |
| control-play-mode | **無し** | ギャップ（uLoop 実装 1,541 行） |
| get-logs | **無し**（`ConsoleErrorProvider` はパネル UI 用で、ツールとしては未公開） | ギャップ（**270 行**） |
| clear-console | 無し | ギャップ（167 行） |
| set-game-view-size | 無し | ギャップ（157 行） |
| simulate-keyboard / mouse-input（PlayMode 中の実入力） | 無し | ギャップ（約 3.7k 行） |
| execute-dynamic-code | 無し（意図的） | **自前化しない** |
| hot-reload | 無し | **自前化しない**（51k 行） |
| pause-point / watch | 無し | **自前化しない**（Harmony transpiler） |

書き込み経路（シーン/コンポーネント/プロパティ/アセット）については、
**そもそも uLoop に相乗りしていない**。型付きツール + 権限カード + Undo +
`uap_job_status` という、こちらの安全モデルに合わせた自前実装を最初から
持っている。相乗りしているのは「脱出ハッチ」だけ。

### 9.3 結合度の実測 — 「そのまま使う」は依存になっていない

- 連携コードは `UloopDetector`（`Packages/manifest.json` の文字列スキャン）、
  allowedTools の文字列パターン、ステアリング文の 1 行だけ。
  **uLoop の C# API は一度も呼んでいない。**
- その間に uLoop 側では **V3 で MCP 経路が全廃**され、バージョンは
  3.0.0-beta.71（R10 調査時）→ 3.6.3 へ進んだ。**こちらの連携コードは無傷**。
- つまり §3.1 で懸念した「外部 OSS への機能依存でロードマップが引きずられる」は、
  薄い連携にしたことで**実際には発生しなかった**。この点で当初の設計判断は
  結果的に正しかったと言える。

### 9.4 結論と方針

1. **uLoop 全体の自前実装は割に合わない。** 得られるのは hot-reload /
   pause-point / dynamic-code という、こちらのユーザーの大半が使わない
   深い機能であり、75k 行の最も壊れやすいコードを保守することになる。
   しかも dynamic-code は本家自身が未解決のメモリ問題（#949, §8.2）を抱える。
2. **本当の問題は「uLoop を入れないと足りないもの」が残っていること。**
   §9.2 のギャップのうち **play-mode 制御 / console ログ読み取り /
   clear-console / game view サイズ** は、uLoop 側で合計 2,135 行、
   こちらの既存ツールモデルに乗せれば同等かそれ以下で書ける。
   この 4 本を自前で持てば、**一般的なユーザーにとって uLoop は「任意」に降格**し、
   §1 の常駐コストを払う必要がそもそも無くなる。これが今回の質問に対する
   最も効果の大きい打ち手。
3. **自前版のほうが統合上は優れている**（uLoop の CLI 経由では得られない）:
   権限カード、Undo グループ、`uap_job_status`、そしてコンパイル/コンソール
   エラーを**エージェントに polling させず会話へ差し戻す**既存経路。
4. **再考すべき既存方針**: `UloopCapabilityMatrix` の「uLoop が賄える能力は
   UapOps 側を登録しない」という 7.2 の方針は、上記 3 の理由で**逆**が正しい
   可能性が高い（実測でもエージェントは放っておくと uloop に流れる:
   2026-08-02 §3）。集合は現在も空なので実害は出ていないが、play-mode などを
   追加する際に「uLoop 検出時は登録しない」を適用してはならない。
5. 優先順位: §5 の案 0（コスト可視化）→ 本節 2（ギャップ 4 本の自前実装）→
   案 A（エージェント使用トグル）。案 B（削除導線）は 2 が入れば必要性が下がる。
