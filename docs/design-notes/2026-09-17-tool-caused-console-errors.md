# エージェント自身のツール呼び出しが出した Console エラーで「修正を依頼」チップが出る

日付: 2026-09-17 / 状態: **実装済み(v0.55.1)。A と B を採用、C は見送り(末尾「実装メモ」)**

## 症状

実エージェントのスクリーンショット撮影(`2026-09-17-agent-screenshots.md`)で、
Codex の共通タスク以降のすべての画面に、入力欄の上の赤いチップ
「! コ... [Codex に修正を依頼]」が出続けた。バックエンドを Grok に切り替えても
「Grok に修正を依頼」として残り、新規セッションの空状態にも
「コンソールのエラーを修正して」の提案が出た。プロジェクトにはエラーは無い。

## 原因(実測)

撮影セッション全体の Unity ログで、Error 級は次の 1 行だけだった(ドライバ自身の
コンパイルエラーを除く):

```
ExecuteMenuItem failed because there is no menu named 'GameObject/Duplicate'
UnityEditor.EditorApplication:ExecuteMenuItem (string)
Colloid.AgentPanel.Ops.UapEditorExecuteMenuTool:Execute (...) (UapEditorExecuteMenuTool.cs:138)
```

1. **発生源はパネル自身のツール。** Codex が `uap_editor_execute_menu` に存在しない
   メニューパスを渡した。`EditorApplication.ExecuteMenuItem` は false を返すだけで
   なく **Unity 自身が Debug.LogError を出す**。ツールは `found:false` を返して
   エージェントは自力で回避した(Cube を新規作成)ので、ユーザーが直すべきものは
   何も無い。
2. **`ConsoleErrorProvider` は発生源を区別しない。**
   `Application.logMessageReceivedThreaded` の Error / Exception / Assert を全部
   `_entries` に積む。除外は「検証ビルド中(`BeginValidationBuild`)」と
   ユーザーの無視リストだけで、「UapOps ツールの実行中に出たログ」という区別は無い。
3. **消えない。** `_entries` が消えるのは `Clear()`(Console ウィンドウの Clear に
   `ConsoleWindowSync` が追従)と再コンパイル時のコンパイラエラー分だけ。1 回きりの
   エラーでも、セッション・バックエンドをまたいで残り、別のエージェントに
   「修正を依頼」させるチップになる。

## 方針の選択肢

| 案 | 内容 | 評価 |
|---|---|---|
| A | `uap_editor_execute_menu` だけ直す: 実行前にメニューの存在を確認し(内部 API `UnityEditor.Menu.MenuItemExists` をリフレクションで。無ければ従来どおり)、無ければ `ExecuteMenuItem` を呼ばずに `found:false` を返す | 今回の 1 件は消える。Unity がエラーを出す他のツール(`AssetDatabase` 系、`PrefabUtility`、`AddComponent` の失敗など)は残る |
| B | **ツール実行スコープで帰属させる**: `UapMainThreadDispatcher.ProcessOnce` が `Tool.Execute` / `Poll` を呼ぶ間だけ `ConsoleErrorProvider.BeginToolScope(toolName)` を開く。スコープ内にメインスレッドで出た Error は `_entries` に積まず、ツール結果の末尾に `Console errors during this call:` として最大 N 行添える | 原因の一般解。エージェントは結果でエラーを知るので情報は失われない。ユーザーのスクリプト(`OnValidate` など)がツール操作で例外を出した場合も、チップではなくその場でエージェントに届く |
| C | 鮮度で落とす: 新規セッション開始・バックエンド切替・ターン終了から一定時間でコンパイラ以外のエントリを破棄 | 「古いエラーが別エージェントに付く」は防げるが、発生直後のチップは出る。毎フレーム出る本物のエラーは再発して戻るので害は小さい |

**提案: B を主、A を併用、C は「新規セッション / バックエンド切替で非コンパイラ
エントリを破棄」だけ採用。**

- B のスコープ判定はメインスレッド ID の一致 + 深さカウンタ(`_validationBuildDepth`
  と同じ Interlocked 形)。別スレッドのログや、スコープ外で遅れて出るログは従来どおり
  チップ対象。
- スコープ内で落としたエラーをツール結果に添える件数は 5 行・各 300 文字で打ち切る
  (`FirstLine` を流用)。結果が `isError` でなくても添える。
- 「ツール起因だが後から毎フレーム出続ける」エラー(壊れた参照を作った等)は、
  スコープ外で再発した時点で通常どおりチップになるので見逃さない。
- A はログそのものを Console に出さないための併用。Unity の Console に赤が残ると
  ユーザーは結局気になるので、出さずに済むものは出さない。

## 回帰ガード(EditMode)

1. スコープ内で `Debug.LogError` → `VisibleCount == 0`、スコープを閉じた後の
   `Debug.LogError` → `VisibleCount == 1`。
2. `uap_editor_execute_menu` に存在しないパス → `LogAssert` に Error が出ない、
   結果は `found:false`。
3. ツールが例外ログを出す偽ツールをディスパッチャに通し、結果テキストに
   `Console errors during this call` と当該メッセージが含まれる。
4. `StartFresh` / バックエンド切替で非コンパイラエントリが消え、コンパイラエントリは
   残る。

## 影響範囲

`Editor/Integration/ConsoleErrorProvider.cs`、`Editor/Ops/UapMainThreadDispatcher.cs`
(Ops → Integration の依存方向に注意: スコープの口は Ops 側にインターフェースを置き、
Integration が実装を差す)、`Editor/Ops/UapEditorExecuteMenuTool.cs`、
`Editor/Integration/AgentHub.cs`(StartFresh)。ユーザーに見える変更なので Core の
patch リリース(`### Fixed`)。

## 実装メモ(v0.55.1)

- **B**: `ConsoleErrorProvider.BeginToolScope()` / `EndToolScope()`。スコープを開いた
  スレッドの Error / Exception / Assert だけを横取りし(`_queueLock` の下で深さと
  スレッド ID を見る)、最大 5 件・重複なしで保持して、最外の End が返す。
  `UapMainThreadDispatcher` は Unity 非依存で dotnet のスモークビルドにも入って
  いるので、直接は呼ばず `StallHintProvider` と同じ流儀のフック
  (`BeginToolLogScope` / `EndToolLogScope`)を `UapOpsServer` が差す。
  ディスパッチャは `Execute` / `Poll` の 1 ティックごとにスコープを開閉し、
  ポーリング型ツールの分は WorkItem に溜めて、完了時に
  `Unity Console errors logged during this call:` のテキストブロックを結果の
  content 配列へ 1 つ足す。ツールが例外で終わった場合は足さない(例外メッセージが
  そのままエージェントに返る)。ドメインリロード前と `ResetForTests` で深さを 0 に戻す。
- **A**: `UapEditorExecuteMenuTool.TryMenuItemExists` が内部 API
  `UnityEditor.Menu.MenuItemExists(string)` をリフレクションで呼ぶ(2022.3.22f1 と
  6000.5.2f1 の `UnityEditor.CoreModule.dll` に存在を確認)。「無い」と分かったら
  `ExecuteMenuItem` を呼ばずに `found:false`。メソッドが解決できない版では
  従来どおり実行し、そのエラーは B が受ける。
- **C は見送り**: 「新規セッションで実行時エラーを捨てる」は、ユーザーが本物の
  エラーを見て「+」で新しい会話を開き、チップから修正を頼む、という正当な導線を
  壊す(エラーは Console に残っているのにチップだけ消える)。B でツール起因の
  エラーがそもそも積まれなくなるので、古いチップが別エージェントに付く原因の
  大半は消える。Console ウィンドウの Clear に追従して消える既存の仕組み
  (`ConsoleWindowSync`)はそのまま。
- テスト: `ConsoleErrorToolScopeTests`(7 本: スコープ内は End が返しチップに
  出ない / 閉じた後は従来どおり / 別スレッドは従来どおり / 上限・重複・入れ子 /
  不均衡な End / ディスパッチャ経由で結果に追記されチップに出ない /
  追記なしのとき結果を触らない)。`UapEditorExecuteMenuToolTests` の未知パスの
  テストは `LogAssert.Expect` を外し、Error が出たら落ちる形に変えた
  (`UapEditorExecuteMenuStatusTests` の同じ Expect も撤去)。

## 撮影側の当座の対処

実装までの間、スクリーンショットは Console をクリアしてから保存済みセッションを
復元して撮り直した(撮影キットの `clearerrors` / `restore` verb)。
