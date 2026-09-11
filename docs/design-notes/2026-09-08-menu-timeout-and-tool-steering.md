# 2026-09-08 -- メニュー実行のタイムアウト意味論 / uap_* への誘導 / プロパティ書き込みの偽成功

発端: ユーザーからの 3 つの指摘。
1. 「TMP・カメラ・Transform 編集・プロパティ編集の MCP 実装を確かめて」
2. 「実機で作業させると Bash で行おうとする」
3. 「ドメインリロードのたびに Bash で "Build Complete" 待ちが走る。コンパイルは終わっているのになぜ」

方法: (1) は AITemp サンドボックス(2022.3.22f1、TMP 3.0.6)で EditMode プローブテスト
5 本を実行して実測。(2)(3) は本番セッション(2026-09-08「アパート内装」、
`~/.claude/projects/.../88ca6511-....jsonl`)のツール呼び出し列を時系列で読み、
どの注入メッセージ・ツール結果の直後に何が起きたかを特定した。

---

## 0. 結論(先に決めたこと)

| # | 決定 | 根拠 |
|---|---|---|
| T1 | ディスパッチャのタイムアウトは **「未着手で破棄」「実行中(戻ってこない)」「ポーラブルを放棄」の 3 語を区別して返す**。判定は WorkItem の `StartState` に対する 1 回の Interlocked 交換で確定する | §2.1: 同一文言が「9 分かかったが走ったビルド」と「リロード直後に拾われず捨てられたビルド」の両方に出ていた |
| T2 | `uap_editor_execute_menu` に `action:"status"` を追加。main thread 上で ExecuteMenuItem の前後に記録した実行ログ(最大 10 件)を返す | §2.2: 長いメニューの完了を知る手段が Editor.log の grep しか無かった |
| T3 | リロード注入文の「結果を受け取っていないものは再実行せよ」を「副作用のあるものは再実行前に状態を確認せよ(uap_ping → status/query)」に変更 | §1: 注入文を文字通り守った結果、リロードのたびに 9 分のビルドが再発行されていた |
| S1 | 誘導文(`ComposeUapOpsSteeringSection`)に **遅延ロードの仕組み(ToolSearch と `mcp__unity-ops__` ワイヤ名)**、Transform/TMP/Camera の担当ツール、タイムアウト 2 文言への対処を追加 | §3: uap_* はスキーマが文脈に無く 3 手かかる。Bash は 1 手 |
| S2 | `uap_transform_set` を新設(local/world の position/rotation/scale を 1 回で。引数無しで world 込みの読み取り) | §3: エージェントが書いたのは `cam.transform.position`(world)。uap_property_set は local のみ・2 回必要 |
| S3 | `uap_property_set` / `uap_object_inspect` の description にプロパティパス早見表と検索語(Transform, Camera, TextMeshPro)を載せる | §3: パスを知るには inspect(これも遅延ロード)をもう 1 回挟む必要があった |
| P1 | ObjectReference 書き込みは **代入後に null なら例外**(型不一致の偽成功を潰す)。`path#subAssetName` でサブアセットを指定可能。型互換なサブアセットが 1 つだけならプレーンパスからも自動選択 | §4.1: SO のパスを Material スロットに書くと参照が null になったうえで "OK" |
| P2 | LayerMask 対応(int / レイヤ名 / 配列)。Enum 名は英数字以外を除去し大小無視で照合 | §4.2/4.3: Camera の m_CullingMask が拒否、`SolidColor` が拒否(表示名 "Solid Color" のみ) |
| P3 | inspect はベクトル等をフル精度で出し、配列は `.Array.size` / `.Array.data[i]`、入れ子構造体は 1 段展開 | §4.4: 0.005 が 0.01 と出る。`m_Materials` が `<Generic>` で要素パスを発見できない |
| X1 | 本番設定の `Bash(uloop execute-dynamic-code *)` 許可はパネル側では触らない(ユーザー設定)。報告のみ | §3: パネルは意図的にこれを許可リストから外している。入ったのは「常に許可」操作 |

---

## 1. 「Build Complete」待ちの正体(指摘 3)

`"BUILD COMPLETE"` はコンパイルの文言ではなく、エージェント自身が書いたシーン生成
スクリプトのログ行(`[Apt] BUILD COMPLETE`)。待っていたのは **自分が実行した
メニュー「Apartment/2. Build Interior Scene」の完了**。

セッション中の反復(3 回、11:48 / 11:58 / 12:11):

1. スクリプト編集 → `uap_scripts_commit` → `Assets/Refresh` → ドメインリロード
2. パネルが「前ターンはリロードで中断。結果を受け取っていないものは再実行せよ」を注入
3. エージェントが `uap_editor_execute_menu` でシーン生成メニューを実行
4. 15 秒でタイムアウト(「timed out waiting for the Unity main thread (15 s). The Editor
   loop last ticked 15.0 s ago.」)
5. 走ったのか捨てられたのか分からないので Editor.log を grep で監視

実測の内訳:
- 12:11 の実行: 完了まで **9 分**。メニューは走っていた(タイムアウト後も main thread を
  占有し続けた)。
- 11:48 の実行: 8 分半待っても新しい `BUILD COMPLETE` は出なかった。リロード直後で
  main thread が詰まり、15 秒以内に Pump が回らず、**メニューは一度も実行されていない**
  (SEC-7 の `Cancelled` 破棄)。
- 11:58: 同上。ユーザーが手で止めた。

## 2. ディスパッチャの欠陥と修正

### 2.1 タイムアウトの意味が 1 種類しか無かった

`UapMainThreadDispatcher.Execute` は `Done.Wait(budget)` が失敗すると `Cancelled=true`
を立てて同じ文言で投げる。`ProcessOnce` は `Cancelled` を見て破棄する。したがって:

| 状況 | 実際に起きること | 旧文言 |
|---|---|---|
| Pump が 15 秒内に回らない(リロード直後) | 拾われた時点で破棄。**何も走らない** | 同じ |
| 同期ツールが 15 秒以上かかる(長いメニュー) | 走り続け、完了する。止められない | 同じ |
| ポーラブルが 15 秒以上かかる | 次の tick で放棄(SEC-7) | 同じ |

エージェントは「再実行すべきか」「待つべきか」を決められない。

### 2.2 修正

- `WorkItem.StartState`(Queued=0 / Started=1 / Dropped=2)。`ProcessOnce` の先頭で
  `CAS(Queued→Started)`、タイムアウトした待ち手は `CAS(Queued→Dropped)`。どちらが
  勝ったかで意味が確定する(競合窓は無い)。
- 文言は `DescribeTimeout(tool, budget, outcome)` に集約し、テストで固定:
  - NeverStarted: "was never started ... NOTHING ran. Wait for uap_ping to answer, then issue it again."
  - StillRunning: "is still running on the Unity main thread ... Do not re-issue it and do not poll Editor.log; call uap_ping until it answers ... then verify the effect."
  - Abandoned(ポーラブル): "abandoned before completing; its terminal side effects did not run."
- `uap_editor_execute_menu` は main thread 上で `MenuRun{MenuPath, StartedUtc, FinishedUtc, Found, Error}`
  を ExecuteMenuItem の **前後**に記録。`action:"status"` で新しい順に返す。静的なので
  ドメインリロードで消える → 空のときは note でその旨を伝える(「走っていない」と誤認させない)。
- 「uap_ping が答える = main thread が空いた = メニューは終わった」がプロトコル。
  status はその後の確認用。

### 2.3 検討して採らなかった案

- **タイムアウトを延ばす**: 9 分のビルドには足りず、CLI 側の MCP タイムアウトが外側に
  ある。根本は「意味が伝わらない」ことで、長さではない。
- **メニューを EditorApplication.delayCall で非同期化**: ExecuteMenuItem 自体が同期で
  main thread を占有するため、非同期化しても status 問い合わせが同じ main thread を待つ。
  意味が無い。
- **タイムアウト後も未着手アイテムを実行する**: 「失敗と伝えた後に副作用が起きる」
  SEC-7 の論点そのもの。破棄を維持し、破棄したと明言する方を選んだ。

## 3. Bash に流れる理由(指摘 2)と誘導の修正

本番セッションで Unity 編集に使われた Bash はすべて `uloop execute-dynamic-code`
(14 回)で、中身は Camera.main の position/rotation、TMP の text、Transform の
localRotation。原因は 4 つ。

1. uap_* は CLI 側で**遅延ロード**(セッション冒頭でエージェント自身が
   `ToolSearch select:mcp__unity-ops__uap_ping,...` を打っている)。使うには「名前を思い出す →
   ToolSearch → 呼ぶ」の 3 手。Bash は 1 手。誘導文は裸名 `uap_*` しか書いておらず、
   ToolSearch に必要なワイヤ名を教えていなかった。
2. 本番の `State.asset` の allowedTools に `Bash(uloop execute-dynamic-code *)` が入っており
   無確認で通る(パネルは意図的にこれを既定許可から外している。`SettingsView.cs` 3369 行付近)。
   誘導文の「uloop は確認が多く遅い経路」という前提が崩れている。→ X1、ユーザーに報告。
3. `uap_property_set` は local 座標しか書けず position+rotation で 2 回。エージェントが
   書いたのは world 座標(`cam.transform.position`)。カスタム指示「ツールで表現できないものだけ
   uloop」に照らすと、エージェントの判断は指示どおり。→ S2。
4. プロパティパスの発見コスト(m_LocalPosition / m_text / "field of view")。→ S3。

誘導文に足した 3 行(core 有効時のみ):
- 遅延ロードの説明と `select:mcp__unity-ops__uap_property_set,...` の実例
- 「位置・回転・スケールは uap_transform_set、TMP/Camera/その他は uap_property_set。
  これらのために dynamic code やスクリプトを書かない」
- タイムアウト 2 文言("still running" / "was never started")への対処

既存テスト `SteeringSection_StaysWithinSixLines` は 3 行増で 7 行になるため上限を
10 に改めた(長さの健全性チェックという意図は維持)。

## 4. プロパティ書き込み・inspect の実測欠陥(指摘 1)

AITemp で `UapProbeTests`(Transform / RectTransform / Camera / TMP / ObjectReference)を
実行。動作 OK だったもの: Transform の m_LocalPosition/m_LocalRotation(オイラー)/
m_LocalScale と Undo、RectTransform の m_AnchoredPosition/m_SizeDelta、Camera の
"field of view"/"near clip plane"/orthographic/m_BackGroundColor/m_Depth、TextMeshProUGUI の
追加(Transform→RectTransform 自動変換)と m_text/m_fontSize/m_fontColor/
m_HorizontalAlignment/m_fontStyle/m_enableAutoSizing。

### 4.1 型違い・サブアセットの偽成功

ScriptableObject のパスを `MeshRenderer.m_Materials.Array.data[0]` に書くと、Unity は
`objectReferenceValue` の型検査で黙って null にし、ツールは "Set ... OK" を返した。
`LoadMainAssetAtPath` しか使わないため、TMP フォント内のマテリアル(`m_fontSharedMaterial`)、
FBX 内メッシュ、テクスチャ内スプライトはそもそも指定不能。

修正: 代入後 `objectReferenceValue == null` なら、与えた型と `prop.type`("PPtr<Material>")
から抜いた期待型を添えて例外。`path#name` でサブアセット指定。プレーンパスで main asset が
不適合かつ互換サブアセットが唯一ならそれを採用(複数なら列挙して `path#name` を促す)。
inspect はサブアセット参照を `path#name` で出力し、往復可能にする。

### 4.2 LayerMask 未対応

Camera/Light の `m_CullingMask` が "does not support propertyType LayerMask" で拒否、
inspect も `<LayerMask>`。→ int / レイヤ名 / 配列を受け、inspect は `5 [Default, TransparentFX]`。

### 4.3 Enum 名の照合が表示名基準

`prop.enumNames` は "Skybox, Solid Color, Depth only, Don't Clear"。C# 名 `SolidColor` は
拒否。→ 両辺から英数字以外を除去し大小無視で照合(完全一致を先に試す)。

### 4.4 inspect の精度と配列

`Vector3.ToString()` は小数 2 桁(0.005 → 0.01)。`m_Materials` などは `<Generic>` のみで
要素パスが発見できない。→ フル精度("R")、配列は `.Array.size` と `.Array.data[i]`
(32 要素まで)、入れ子構造体は 1 段展開、総数 500 で打ち切り。

## 5. 実装計画と回帰ガード

| ストリーム | ファイル | テスト |
|---|---|---|
| A(PM) | `UapMainThreadDispatcher.cs`, `UapEditorExecuteMenuTool.cs`, `AutoContinueAfterCompilePolicy.cs` | `UapMainThreadDispatcherStartStateTests`(未着手→破棄・走らない / 実行中→"still running"・完走 / 3 文言), `UapEditorExecuteMenuStatusTests`, `AutoContinueInterruptedTurnMessageTests` |
| B(sonnet) | `UapPropertyValueWriter.cs`, `UapObjectInspectTool.cs` | `UapPropertyValueWriterTests`, `UapComponentListAndInspectToolTests`, `UapPropertyObjectReferenceTests` |
| C(sonnet) | `UapTransformSetTool.cs`(新), `ToolRegistry.cs`, `UapPropertySetTool.cs`(description), `AgentHub.cs`(steering) | `UapTransformSetToolTests`, `AgentHubStartClientArgTests`(steering), `UapCoreToolsMetadataTests` |

検証: AITemp で `-runTests -testPlatform EditMode`(パッケージ全テスト)を実行し、
加えて §4 のプローブ `UapProbeTests` を再実行して 4.1〜4.4 の実測値が変わることを確認する。

## 6. 未決・残件

- `uap_editor_execute_menu` の記録はドメインリロードで消える。SessionState に退避する
  価値はあるが、メニューがリロードを起こした場合「走った/終わった」の判定自体が曖昧なので、
  今回は note で伝えるに留めた。
- 本番設定の `Bash(uloop execute-dynamic-code *)` 許可(X1)。外すかはユーザー判断。
- 3D 版 `TextMeshPro` の単独追加、TMP フォントアセット割り当ては AITemp にフォントが無く未実測。
