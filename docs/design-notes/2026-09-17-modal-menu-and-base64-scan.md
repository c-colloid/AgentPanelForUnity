# `File/Save` で Editor が固まる件: ネイティブモーダルと base64 の O(n²) 走査

日付: 2026-09-17 / 対象: v0.54.6 / 関連: 2026-09-17-agent-screenshots.md(「見つかった不具合」2)、
2026-09-08-menu-timeout-and-tool-steering.md、2026-09-09-jobs-and-destructive-confirm.md、
2026-09-12-tool-result-image-preview.md、2026-09-06(§7 スロットル通知)

## 0. 結論(先に)

報告では 1 件の不具合に見えたが、**独立した 2 つの原因**だった。

| # | 止まった区間(JST) | 長さ | 原因 |
|---|---|---|---|
| A | 17:53:38 → 17:58:26 | 288 s | `uap_editor_execute_menu "File/Save"` が未保存シーンでネイティブの「Save Scene」モーダルを開いた。人が閉じるまで main thread は Win32 のモーダルループの中 |
| B | 17:58:26 → 18:02:43 | 256 s | 直後の `uap_editor_screenshot(return_image)` の結果(498 KB の base64)を Codex が **1 本の JSON 文字列**で渡し、`ToolResultImages` のパス検出正規表現が O(n²) で main thread を占有 |

「2 回目のフリーズが `EditorUpdatePump.OnEditorUpdate` に付く」のは B がその中
(`AgentClient.Pump` → `AgentHub` → `ToolResultImages.Resolve`)で走るから。モーダルとは無関係で、
`uap_job_status` + `uap_object_inspect`×3 は **固まった後に投げられて待たされただけ**(原因ではない)。

## 1. 現象 A: メニューがネイティブモーダルを開く

### 1.1 根拠

インシデント側の記録(Codex のロールアウト `~/.codex/sessions/2026/09/17/rollout-…dc6a….jsonl` と
パネルのセッション JSON):

- ジョブ `job-0bfb-90`: `startedUtc 08:53:38.35` / `finishedUtc 08:58:26.53` / `durationSeconds 288.186`、
  結果は `found:true`。シーンは Untitled のまま = 誰かがダイアログを**キャンセル**で閉じた
  (撮影セッションの操作。誰が、は記録に無い)。
- 同じスクリプト内で後続だった `uap_editor_screenshot` は 08:58:26.533 に実行(ファイル名
  `uap_scene_20260917_085826_593.png`)。ダイアログが閉じた瞬間に流れた。

再現(専用スクラッチプロジェクト、2022.3.22f1 GUI 起動、ワークツリーのパッケージを `file:` 参照。
AITemp は撮影セッションの Editor が開いたままだったので触っていない)。独自の
`UapMainThreadDispatcher` を `EditorApplication.update` に載せ、ワーカースレッドから
`File/Save` を実行、外から user32 でウィンドウを列挙:

```
18:15:32.701 scenario start: File/Save scenePath=''
  +6s  [#32770] "Save Scene" (enabled) / [UnityContainerWndClass] メインウィンドウ (disabled)
18:15:47.705 menu → TimeoutException "still running ... job-1f87-1"
18:16:02.718 後続 uap_ping ×3 → "was never started"(15 s)
  … 18:15:32 〜 18:17:24 の 110 s、update tick は 0 回 …
18:17:24     WM_CLOSE(=キャンセル)→ 18:17:24.860 から tick 再開。「Hold on」は出ない
```

つまり **入れ子の update ポンプは無い**。ブロックしているのはネイティブのモーダルループそのもので、
閉じれば即座に復帰する。エージェントからは閉じる手段が無く、その間 `uap_*` は全滅、パネルの
権限応答(main thread)も止まる。ダイアログが「ウィンドウ列挙に出なかった」のは、列挙した時点で
既に閉じられて B に入っていたため(B の間はダイアログは存在しない)。

そもそも Codex が `File/Save` に手を伸ばしたのは、**シーンを保存するツールが無かった**から。

### 1.2 どのメニューがモーダルか(実測)

同じプローブで 1 つずつ実行 → 5 秒後にウィンドウ列挙 → `#32770` があれば WM_CLOSE:

| メニュー | 結果 |
|---|---|
| `File/Save`(未保存シーン) | モーダル「Save Scene」 |
| `File/Save As...` | モーダル「Save Scene」 |
| `File/Open Scene` | モーダル「Load Scene」 |
| `File/Build And Run` | モーダル「Build Windows」 |
| `File/Exit`(dirty) | モーダル「Scene(s) Have Been Modified」(かつ Editor を閉じる) |
| `Assets/Import New Asset...` | モーダル「Import New Asset」 |
| `Assets/Import Package/Custom Package...` | モーダル「Import package ...」 |
| `File/Build Settings...` / `Edit/Project Settings...` / `Edit/Preferences...` | 通常の EditorWindow。**問題なし** |
| `File/New Scene` | 「New Scene」EditorWindow(非ブロック) |
| `Assets/Export Package...` / `File/Save Project` / `File/Open Project...` / `Window/Layouts/Save Layout...` | 5 秒以内にモーダル無し |

### 1.3 検討した選択肢

| 案 | 評価 |
|---|---|
| (a) 既知メニューの拒否リスト+代替の案内 | 実測で確定した項目には確実に効き、エラー文で次の一手を渡せる。サードパーティのメニューは列挙できない |
| (b) `File/Save` だけ事前チェック(scene.path が空なら拒否) | (a) の 1 行として取り込む。保存済みシーンの `File/Save` は今まで通り通す |
| (c) `delayCall` で実行して即 return | **ブロックは何も解消しない**(モーダルは結局 main thread を止める)。`found` も結果も失い、現行の「still running + job」より情報が減る。不採用 |
| (d) ダイアログを検出して自動で閉じる(WM_CLOSE) | ファイルピッカーなら安全だが、`DisplayDialog` 系は「閉じる」がどのボタンに対応するかが呼び出し側次第(Don't Save 相当になり得る)。人が席にいて答えようとしている場合も壊す。不採用 |
| (e) ダイアログを検出して**タイムアウト文に書く** | 列挙できないサードパーティ分を事後に救う。現行文の「will finish on its own」はモーダル相手だと嘘になるので、その訂正にもなる。ワーカースレッドから user32 だけで判定でき、Unity API に触らない |
| (f) シーン保存ツールを足す | 根っこの動機(保存手段が無い)を消す。`EditorSceneManager.SaveScene(scene, path)` は path があればダイアログを出さない |

**採用: (a)+(b)+(e)+(f)。**

### 1.4 実装

- `UapMenuDialogPolicy`(純関数、Unity 非依存): §1.2 でモーダルと実測した 7 項目の表。
  `Refusal(menuPath, anyOpenSceneUntitled)` が拒否文か null を返す。大文字小文字・`\`・前後空白・
  末尾の `...` を正規化して照合。表に無いもの(`Tools/Vendor/File/Save As...` など)は通す。
- `UapEditorExecuteMenuTool`: 実行記録(`RunLog`)に載せる**前**に判定し、`InvalidOperationException`
  (`Refused: … Nothing was executed. Call uap_scene_save …`)。拒否は `status` に出ない。説明文にも明記。
- `uap_scene_save`(新規、core、Undo 不可): `scene`(省略時はアクティブシーン)/ `path` /
  `saveAsCopy` / `overwrite`。未保存シーンで `path` 無しは**Unity に渡さず拒否**
  (path 無しの `SaveScene` はまさに同じダイアログを開く)。`Assets/` 配下・`.unity` 必須、
  フォルダは作成、別の既存シーンファイルへの上書きは `overwrite:true` が要る。
- `UapNativeModalProbe`: 可視トップレベルに `#32770` があり、かつ `UnityContainerWndClass` が
  全て disabled なら「モーダル中」。タイトルは `SendMessageTimeout(WM_GETTEXT, ABORTIFHUNG, 200ms)`
  (`GetWindowText` は同一プロセスだとタイムアウト無しの SendMessage になり二次ハングし得る)。
  Windows のみ、他は null。判定部 `FindBlockingDialog` は純関数。
- `UapOpsServer.ReadStallHint` = スロットル通知 ?? モーダル通知 を `StallHintProvider` に接続。
  副作用として、モーダル中に投げられた呼び出しは 15 s ではなく 8 s で原因付きで落ちる。
- 誘導文(`ComposeUapOpsSteeringSection`)に「保存は uap_scene_save、File/Save メニューは使わない」を 1 文。

実機確認(修正後、同じスクラッチ Editor):

```
18:40:42.143 File/Save → 18:40:42.246 Refused: … Call uap_scene_save with 'path' …   (0.1 s、ダイアログ無し)
18:41:01.077 Tools/MenuProbe/Third Party Dialog(EditorUtility.OpenFilePanel を呼ぶだけの [MenuItem])
18:41:16.083 … still running … NOTE: a native modal dialog ("Vendor Pick File") is open … ends only when a PERSON closes it …
18:41:24.097 後続 uap_ping → 8 s で "was never started" + 同じ NOTE
```

### 1.5 残すもの

- `File/Open Scene` の代替ツール(`uap_scene_open`)は今回入れていない。拒否文は「ユーザーに開いて
  もらう」と案内する。dirty シーンの破棄確認をどう扱うか(confirm ゲート)を決めてから別タスクで。
- macOS / Linux のモーダル検出は無し(拒否リストと `uap_scene_save` は全 OS で効く)。

## 2. 現象 B: base64 入りテキストに対するパス検出が O(n²)

### 2.1 根拠

パネルのセッション JSON(`UserSettings/AgentPanel/Sessions/01a0ae8f-….json`)のツール呼び出し開始時刻:

```
08:58:26.533 uap_editor_screenshot  Succeeded 158ms  imgs=[…/Temp/UapOpsScreenshots/uap_scene_20260917_085826_593.png]
09:02:43.292 uap_job_status         Succeeded 35ms      ← Codex 側の発行は 08:58:37.186
```

Codex 側は 08:58:37 に `uap_job_status` を出しているのに、パネルがそれを見たのは 09:02:43。
この 256 s、main thread は `EditorUpdatePump.OnEditorUpdate` の中にいた(「Hold on (busy for …)」の表示と一致)。

- Codex(codex-acp)は MCP の結果を ACP の `rawOutput` として
  `{"result":{"content":[{"type":"text",…},{"type":"image","data":"<base64>"}]}}` の形で渡す。
  `AcpProtocolBridge.AppendToolOutput` は `content` が空なら `rawOutput` を **JSON 文字列のまま**
  テキストにする(`resultSummary` が `{"result":{"content":[…` で始まっているのが証拠)。
  画像ブロックとしては届かないので、`ToolResultImages.Resolve` は「テキスト中のパスを探す」経路に入る
  (`imgs=` が Attachments ではなく Temp のパスなのもその証拠)。
- `StrictPathPattern` / `LenientPathPattern` はどちらも `/` をアンカーに、停止文字まで lazy に進んで
  `.png` を探す。base64 は **約 64 文字に 1 個 `/` があり、停止文字が 1 つも無い**。
  各 `/` から blob の末尾まで歩いて失敗する → O(n²)。

再現(スクラッチ Editor、インシデントの実 PNG 373,528 バイト → base64 498 KB を同じ JSON 形に包んで
`ToolResultImages.Resolve`):

| 入力 | `/` の数 | 所要 |
|---|---|---|
| 64 KB | 1,010 | 3,426 ms |
| 498 KB | 7,583 | **431,510 ms**(strict+lenient の 2 パス。この間 Editor は「応答なし」) |

インシデントはテキスト先頭の実在パスが strict で解決して lenient を回らなかった分の約半分 = 256 s と整合。

### 2.2 検討した選択肢

| 案 | 評価 |
|---|---|
| 走査するテキスト長に上限 | 上限内でも二乗は残る(64 KB で 3.4 s)。パスが後ろにある結果を落とす |
| 量指定子に上限(`{1,1024}?`) | 線形にはなるが、498 KB で `/` 7,583 個 × 最大 1,024 歩 ≒ 1 秒弱が main thread に残る |
| ワーカースレッドへ逃がす | 二乗のまま CPU を焼く。カード表示の経路が非同期になり波及が大きい |
| ブリッジで `rawOutput` の MCP 形を画像ブロックに戻す | Codex には効くが、base64 をテキストで渡す別のエージェント/ツールで再発する。検出側を直すのが先。(別途やる価値はある: 画像がインラインで出るようになる) |
| **拡張子起点の窓走査** | base64 に `.` は無いので、`.png`/`.jpg`/`.jpeg` の出現位置だけを起点に、停止文字まで(最大 1,024 文字)戻った窓の中でだけ既存の正規表現を回す。パターンも結果も変えずに線形になる |

**採用: 窓走査。** `FindImagePaths` のみ変更。直前のマッチ終端より前へは戻らない(左から 1 回なめるのと同じ結果)。
1,024 文字を超えるパスは途中から始まるトークンになるが、実在しないので `resolvePath` で落ちる。

修正後、同じ 498 KB 入力で **1 ms**(431,510 ms → 1 ms)。

## 3. テスト

- `ToolResultImagesTests`: 500 KB の base64 風テキスト+実パスで strict/lenient 合計 2 s 未満
  (旧実装は数百秒)かつパスを拾う / blob の後ろのパス / lenient で 1 行 2 パス。既存の形テストは不変。
- `UapMenuDialogPolicyTests`: 拒否 9 表記、`File/Save` の条件分岐、通すべき 11 項目、
  ツールが実行前に拒否し `status` に記録を残さないこと、説明文。
- `UapNativeModalProbeTests`: 実測したウィンドウ並びでの判定 5 本、文言、ダイアログ無しで null、
  ディスパッチャのタイムアウト文に載ること。
- `UapSceneSaveToolTests`: パス解決の拒否/正規化、未保存+path 無しは例外、`saveAsCopy` で実ファイルを
  書きフォルダを作り開いているシーンを変えない、上書き拒否、未ロードシーン、登録とメタデータ。
  (テストランナーが開いているシーンを汚さないため、実保存は全て `saveAsCopy:true`。`NewScene` は使わない)
- `AgentHubStartClientArgTests`: 誘導文に `uap_scene_save`。

## 4. 版

`### Added`(`uap_scene_save`)を含むので **v0.55.0**。Pro は変更なし(Pro の版・販売ページ文書は触らない)。
