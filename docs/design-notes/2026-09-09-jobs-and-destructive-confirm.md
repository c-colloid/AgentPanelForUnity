# 2026-09-09 -- ジョブ化(uap_job_status)と Destructive ゲート(confirm / dry_run)

発端: ユーザーから「isuzu-shiranui/UnityMCP を参考に MCP へ採用できる部分があるか」
という問い。両者は「Editor 内 HTTP サーバ + 127.0.0.1 限定 + Bearer トークン +
メインスレッドへのディスパッチ + 型付きツール」という同じ骨格なので、丸ごとの
置き換えではなく仕組み単位の移植を提案し、採用が決まった 2 点を実装した。
uloop が担当する領域(console / compile / test / play mode / execute_code)と
入力合成は、設計 §7.2 の重複排除方針と Phase 5 の「C# を書かせない」方針に
反するため採用しない(理由は会話ログに残した比較表のとおり)。

---

## 0. 結論(先に決めたこと)

| # | 決定 | 根拠 |
|---|---|---|
| J1 | ディスパッチャの WorkItem は enqueue 時点で `UapJob` を持つ。待ち手が **StillRunning** でタイムアウトしたときだけ `UapJobLedger` に登録し、メッセージにジョブ ID を載せる | §1.1: 9 分のメニュー実行の結果が捨てられ、Editor.log の grep しか手段がなかった |
| J2 | `uap_job_status` は **メインスレッドを経由しない**(`IUapOffThreadTool`)。ディスパッチャが HTTP ワーカー上でそのまま実行する | §1.3: ジョブがメインスレッドを塞いでいる最中こそ問い合わせたい。UnityMCP の `/jobs` が `MainThread=false` なのと同じ理由 |
| J3 | `wait_ms`(上限 10 s)で完了を待てる。ディスパッチャ既定 15 s と CLI 側 HTTP 予算より短い | §1.3: 状態確認そのものが「still running」にならないため |
| J4 | 台帳は Editor セッション寿命、上限 32 件、完了済みの古いものから追い出す。実行中は追い出さない | §1.2 |
| D1 | `uap_asset_delete` と `uap_prefab_apply_overrides` は `UapDestructiveToolBase` を継承し、`confirm:true` なしでは **拒否+プレビュー**、`dry_run:true` で **プレビューのみ**(dry_run が confirm に優先) | §2.1: Ctrl+Z で戻せない/共有アセットに書き抜ける操作が、権限カード以外に何も持っていなかった |
| D2 | `confirm` / `dry_run` はベースがスキーマに追記する(`additionalProperties:false` のため未宣言だと弾かれる)。`required` には入れない | §2.2: 「confirm なしの呼び出し」こそがプレビュー経路 |
| D3 | 権限カード・AutoApprovePolicy は変更しない | §2.3: このゲートはエージェント⇔ツール間、カードはユーザー⇔エージェント間。層が違う |

---

## 1. ジョブ化

### 1.1 何が起きていたか

2026-09-08 のノートで、メインスレッドのタイムアウトを「未着手で破棄」「実行中」
「ポーラブル放棄」の 3 語に分けた。しかし「実行中」の場合、ツールはいずれ結果を
返すのに、待っていた HTTP ワーカーは既に去っていて、その結果は `WorkItem` と
ともに捨てられていた。エージェントに残された手段は `uap_ping` での生存確認と、
`uap_editor_execute_menu` の `status`(メニュー専用)だけで、汎用ツールの
戻り値そのものは得られなかった。

### 1.2 仕組み

- `UapJob`: Id / ToolName / Queued・Started・Finished の UTC / Result(content 配列)
  / Error / State(queued・running・succeeded・failed)。メインスレッドが
  `MarkStarted` / `Complete` を、HTTP ワーカーが `Register` / `WaitForCompletion`
  を呼ぶ。完了イベントと volatile により、「登録してから完了」でも「完了して
  から登録」でも読み手からは同じに見える(競合窓なし)。
- `UapJobLedger`: lock 付きの有界リスト + 辞書。ID は `job-<4hex>-<連番>`。4hex は
  台帳インスタンスごと(=ドメインリロードごと)に変わるので、リロード前の ID が
  新しいジョブに一致することはない。
- `UapMainThreadDispatcher`: `Execute` で `UapJob` を生成、`ProcessOnce` の初回
  tick で `MarkStarted`、同期ツールの finally / ポーラブルの完了・例外で
  `Complete`。タイムアウト時に `StartState` の CAS に負けた(=実行中)場合だけ
  `Jobs.Register`。`DescribeTimeout` に `jobId` 付きオーバーロードを追加し、
  従来の 3 引数版は委譲するので既存のテスト文言は維持される。
- ポーラブル(`uap_scripts_commit`)の放棄はジョブにしない: SEC-7 の通り終端の
  副作用が走らないので、取りに行く結果が存在しない。

### 1.3 uap_job_status

`core` モジュール、`ReadOnly=true`(自動承認対象)、`IUapOffThreadTool`。
`job_id` ありで単体(succeeded なら `result` にツールの content 配列、failed なら
`error`、running なら再問い合わせを促す `note`)、なしで一覧(newest first、
result は含めない)。未知の ID は「セッション限り・最大 32 件・リロードで消える」
と説明して例外。

`IUapOffThreadTool` の契約は「Unity API・Editor 静的状態に一切触れない」。
ディスパッチャは `Execute` の先頭で型を見て、キューに入れずに呼び出し元スレッド
で実行する。`DirectUapToolExecutor` は元々呼び出し元で実行するので影響なし。

### 1.4 誘導文の更新

`ComposeUapOpsSteeringSection` の「still running」対処を「メッセージのジョブ ID で
`uap_job_status` を呼べ(Editor がブロック中でも答える)。`uap_ping` は空いたか
どうかの確認だけ」に差し替えた。「was never started」は従来通り。

---

## 2. Destructive ゲート

### 2.1 対象の選定

「Ctrl+Z で戻せない」または「共有アセットに書き抜けて他の利用箇所が黙って変わる」
を基準に、登録済み 41 ツールを見直した。

| ツール | 判定 | 理由 |
|---|---|---|
| `uap_asset_delete` | 対象 | OS ゴミ箱行き。Editor 内で戻せない。フォルダを渡すと配下すべて |
| `uap_prefab_apply_overrides` | 対象 | Undo は同一セッション内のみ。他シーンの全インスタンス・ネストした利用先が変わる |
| `uap_scene_destroy_object` | 非対象 | Undo 対象 |
| `uap_prefab_revert_*` | 非対象 | インスタンス側のみ、Undo 対象 |
| `uap_asset_create` | 非対象 | 既存があれば連番で回避(上書きしない) |
| `uap_scripts_commit` | 非対象 | 検証ゲートが既に承認経路 |
| `uap_editor_execute_menu` / bake 系 | 非対象 | 効果が任意で予告できない。別枠で扱う |

### 2.2 ゲートの形

`UapDestructiveToolBase.Execute` が 3 分岐を一手に持つ:

1. `dry_run:true` → `Preview` の文言に "DRY RUN -- nothing was changed." を前置して返す。
2. `confirm` なし/false → `InvalidOperationException`(CLI には isError:true)。文言は
   "Refused: <tool> is destructive and requires confirm:true. <preview> Nothing was
   changed. Re-issue with confirm:true to apply, or dry_run:true to only preview."
3. `confirm:true` → `Apply`。

`Preview` は `Apply` と同じ解決経路を通す(解決できない対象は同じ例外で落ちる)ので、
プレビューが通れば適用も通る。プレビューの中身:

- asset_delete: ファイルなら主アセットの型名、フォルダなら `AssetDatabase.FindAssets`
  で数えた配下アセット数。
- prefab_apply: 掃くオーバーライド数(既存 `AffectedOverrideCount`)、アセットパス、
  ロード中シーンにある同じプレハブの **他の最外殻インスタンス**の階層パス一覧
  (`FindOtherLoadedInstances`)。未ロードシーンとネストした利用先も変わる旨を添える。

### 2.3 変えなかったもの

- 権限カード、`AutoApprovePolicy`、`ReadOnly` / `Undoable` の意味。ゲートは
  エージェントが「影響範囲を見た上で confirm を書く」ことを強制する層で、
  ユーザー承認の層ではない。両方残る。
- `uap_prefab_apply_overrides` の `Undoable=true`。Undo で戻せる事実は変わらない。

---

## 3. テスト

- 純粋部分: `UapJobLedgerTests`(ID・登録・追い出し・状態遷移・Describe)、
  `UapMainThreadDispatcherJobTests`(StillRunning で登録→ブロック中に status が
  答える→完了後 result、失敗時 error、`wait_ms`、時間内完了/未着手はジョブなし、
  off-thread はキューに入らない)、`UapDestructiveToolBaseTests`(3 分岐・文言・
  スキーマ追記・レジストリ内の destructive 集合を固定)。
- EditMode: `UapAssetToolsTests` / `UapPrefabToolsTests` に confirm なし拒否と
  dry_run を追加し、既存の実行系テストには `confirm:true` を付けた。
- メタデータ: `UapReadOnlyToolMetadataTests` に `uap_job_status` を追加。
- 本環境には Unity Editor がないため、Unity 非依存部分(JSON / ディスパッチャ /
  台帳 / status ツール / ゲート基底)を .NET 8 でそのままコンパイルし、上記
  ディスパッチャ系の流れをハーネスで通した。EditMode 全体は CI に委ねる。
