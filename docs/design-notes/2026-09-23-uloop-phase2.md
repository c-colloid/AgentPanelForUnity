# uLoop 自前化フェーズ 2 の実装(記録と観測)

Date: 2026-09-23
対象: `jp.colloid.unity-agent-panel`(Core)v0.60.0-beta.1(フェーズ 1 と同じベータに同梱)
関連: `docs/design-notes/2026-09-22-uloop-full-native-roadmap.md` §3(計画)、
`docs/design-notes/2026-09-22-uloop-phase1.md`(フェーズ 1。入力注入・`uap_compile` の
返答後スケジュール・`IUapAbandonableTool` はここで入った)

---

## 0. 結論(先に)

1. ロードマップ §3 のフェーズ 2 を実装した。新規ツールは `uap_game_watch`(core、
   読み取り専用)と `uap_record_video` / `uap_input_record` / `uap_input_replay`
   (editor)の 4 本。
2. 時間のかかる処理は 2 通りに分けた。
   - **1 回の呼び出しで返すもの**: `uap_game_watch`。ディスパッチャの待ち
     (通常 15 秒、Editor が背面にあるときは 8 秒)に余裕を持って収まるよう、
     1 回の `duration` を最大 6 秒にした。
   - **start / stop / status で背景に回すもの**: `uap_record_video` と入力の
     記録・再生。`EditorApplication.update` で進むので、その間も他のツールが動く。
3. **ロードマップからの変更点**
   - §7 項 3 は「入力の記録/再生は要望が無ければフェーズ 3 の後ろ」だった。
     しかし「残り機能を全て自前実装」という依頼に含まれるので、フェーズ 2 のまま
     実装した。
   - 入力の記録は `InputSystem.onEvent` の購読ではなく、デバイス状態を毎 tick
     読んで差分だけを残す方式にした(§3.4)。
4. **版**: ブランチがまだ main に入っていないので、ロードマップの「v0.61.0」では
   なく、同じ `v0.60.0-beta.1` を切り直した(`docs/RELEASING.md`: 未マージの
   ブランチは 1 本のベータとして出す)。
5. §3.3(Test Runner の PlayMode 対応)はこのノートの対象外で、別ノートにある。

---

## 3.1 `uap_record_video`(uLoop `record-video` 相当)

### 何を

新規ツール `uap_record_video`(module: `editor`、`uap_editor_screenshot` と同じ)。`action: start / stop / status` の 3 択で、`start` は既定。

- **`start`**: 引数を検証し、`UnityEditor.Media.MediaEncoder`(Editor 同梱、追加パッケージ不要)でエンコードセッションを開き、フレーム 0 を即座に 1 枚書き込んでから**即座に応答を返す**。実際のフレーム取得は `EditorApplication.update` の各ティックで壁時計ペーシングにより進む(実時間の経過に応じて必要ならフレームを複製 = スキップも複製もする、uLoop と同じ設計)ため、録画中も他のツール呼び出しはブロックされない。戻り値には `recordingId` / `path` / `fps` / `width` / `height` / `maxSeconds` を含む。
- **`stop`**: 進行中の録画を今すぐ終える。録画が無ければ拒否。
- **`status`**: 現在/直前の録画状態(`framesWritten` / `droppedFrames` / `secondsRecorded` / `fileSizeBytes` / `stoppedBy`)を報告する。ドメインリロードを跨いで生き残る(後述)。

引数: `view`("scene"|"game"、既定 "game")、`capture`("camera"|"window"、既定 "camera")は `uap_editor_screenshot` の同名引数と**完全に同じ語彙・既定値**にし、パース自体も `UapEditorScreenshotPaths.TryParseView`/`TryParseCapture` をそのまま再利用した(重複実装を避け、2 つのツール間で規約がずれないようにするため)。`fps`(既定 30、最大 60)、`max_seconds`(既定 60、最大 300、到達で `stoppedBy:"duration"` の自動停止)、`max_width`(任意、アスペクト比を保って縮小してから偶数丸め)、`stop_on_play_exit`(既定 true)。

出力先は `uap_editor_screenshot` と同じ「プロジェクトルート直下の `Temp/`(バージョン管理外・アセットインポート対象外)」規則の兄弟フォルダ `Temp/UapOpsVideos/`。ファイル名はタイムスタンプ埋め込み(`uap_video_YYYYMMDD_HHMMSS_fff.<ext>`)。**最新 10 本を残して古いものは録画終了直後に自動で prune** する(スクリーンショットの PNG と違い 1 本が数十〜数百 MB になり得るため)。

コンテナ/コーデックは拡張子で決める: Windows/macOS は `.mp4`(H.264)、Linux は `.webm`(VP8、Linux Editor には H.264 エンコーダが無いため)。

新規ファイルは 3 つ:
- `UapRecordVideoTool.cs`(Editor、`IUapTool` 本体、薄いディスパッチ層)
- `UapRecordVideoPolicy.cs`(Unity 非依存: 引数レンジ、偶数丸め、出力ファイル名/prune 選択、壁時計ペーシングの目標フレーム数、永続化 JSON 形状。ci/SmokeTests 対象)
- `UapVideoRecorder.cs`(Unity 側: `MediaEncoder` のライフサイクル、カメラ/ウィンドウのピクセル取得、`EditorApplication.update`/`playModeStateChanged`/`beforeAssemblyReload` の 3 フック)

テストは `Tests/Editor/UapRecordVideoToolTests.cs`。

### なぜ

`uap_editor_screenshot` は 1 枚の静止画しか残せず、ジャンプの軌跡や UI アニメーションのような**時間方向に意味のある操作**の証拠には向かない。uLoop の `record-video` に相当する機能をこのパネルの実行モデル(型付きツール・権限カード・素直な失敗理由)で持たせる。ロードマップ §3.1 のブリーフどおり、録画そのものは Pollable ツールにはせず(`IUapPollableTool` は 1 回の `tools/call` を数ティックにまたいで完了させる仕組みで、最大 300 秒の録画には根本的に向かない -- `UapMainThreadDispatcher` の HTTP 待ちは既定 15 秒しかない)、`start` は即座に完了する普通の `IUapTool` とし、実体は `uap_compile` の `CompilationPipeline` 購読と同じ「ツール呼び出しの外で動く静的セッション」にした。

### 設計判断

1. **ペーシングは「スキップまたは複製」方式、`MediaTime` 明示指定は使わない。** `MediaEncoder.AddFrame(Texture2D)` は常に `MediaTime.Invalid`(=宣言した `frameRate` に従って次のフレームとして連続追加)を渡す実装であることを UnityCsReference のソースで確認した。可変フレームレート(フレームごとに実タイムスタンプを明示)という道もあり得たが、ブリーフが明示的に求めていた「uLoop と同じスキップ/複製方式」を採用: `EditorApplication.update` の毎ティックで `UapRecordVideoPolicy.TargetFrameCount(経過秒, fps)` を計算し、まだ書くべきフレーム数に達していなければ何もしない(スキップ)、複数フレーム分遅れていれば同じテクスチャを複数回 `AddFrame` する(複製、`droppedFrames` としてカウント)。これにより出力は常に一定フレームレート(CFR)のファイルになり、プレイヤー側の互換性の懸念が VFR より小さい。
2. **`VideoTrackAttributes.codec` は internal フィールドなので公開 API からは設定できない**(UnityCsReference で確認済み)。したがってこのクラスが作る `MediaEncoder` は常に `VideoCodec.Auto`(既定値 0)になる。コンテナ/コーデックの選択は**出力ファイルの拡張子**(`.mp4` vs `.webm`)で行う設計にした -- ネイティブ実装がどう解決するかまでは確認できなかった(§検証済みシグネチャ/未検証を参照)が、Unity のマニュアル/サポート記事(Linux Editor には H.264 エンコーダが無く webm/VP8 が代替)という一般的なガイダンスと矛盾しない。
3. **AddFrame の失敗を「初回フレーム」と「以降のティック」で扱いを分けた。** `start` 内で最初のフレームを書く際に `AddFrame` が例外を投げた場合、それはエンコーダ自体が壊れている(サイズ/フォーマット不一致など)可能性が高く、次のティックでも同じ失敗を繰り返すだけなので、`start` 全体を中止してエンコーダを破棄し `InvalidOperationException` で失敗を返す。一方 `OnUpdate` 内での `AddFrame` 失敗は `stoppedBy:"error"` として正直に録画を終了させる(それまでの内容は `Dispose()` で確定させる)。
4. **`view:"window"` の実装は Scene View と Game View で非対称。** Scene View は `UapScreenCapture` が既に `SceneView.duringSceneGui` から測定・追跡している「カメラ表示領域」のジオメトリ(`TryGetGeometry`、public)をそのまま再利用できる -- `uap_editor_screenshot` の `capture:"window"` と全く同じ精度。Game View には `duringSceneGui` に相当する「毎フレーム GUI コールバック」が Unity に存在しない(`GameView` は封印された内部クラスで、`EditorWindow.OnGUI` を差し替える公開手段が無い)ため、`SceneView` と同じ精度でツールバー領域を除いた「表示領域」だけを正確にクロップする方法を検証できなかった。安全側に倒し、`EditorWindow.position`(UnityCsReference のソースコメントで「the position of the window in screen space」と明記されている)をそのまま読み取り原点にする実装にした -- 画像の上端に Game View 自身のツールバー(Play/Pause/Stats など)が数行分写り込む可能性がある、という限界を説明文とコード コメントの両方に明記した。これは「不正確なオフセットを当て推量するより、正直に『ウィンドウ全体』と言う」というこのタスクの安全側判断。
5. **ReadOnly は false。** `uap_editor_screenshot` の `ReadOnly:true` は `UapReadOnlyToolMetadataTests` が個別に検証・固定した小さな許可リストの一員としての例外であり、`uap_record_video` はその対象ではない。加えて書き込む内容(数百秒・数十 MB になり得る動画ファイルを複数本、prune 込みで保持)と副作用(録画中はエディタイベント購読が生き続ける)がスクリーンショット 1 枚より明らかに大きいため、素直に `false` とした。
6. **`[InitializeOnLoadMethod]` は使わない。** `EditorApplication.update` / `playModeStateChanged` / `AssemblyReloadEvents.beforeAssemblyReload` の 3 つは `action:start` が呼ばれて初めて購読し、録画が(どの理由であれ)終わった瞬間に必ず解除する(`uap_compile` の `CompilationPipeline` 購読と同じ「作業中だけ購読する」規律)。
7. **`AssemblyReloadEvents.beforeAssemblyReload` を録画の生死を分ける最後の砦として使う。** `MediaEncoder` はネイティブハンドルを持ちドメインリロードを越えて生存できないため、リロード直前にこのフックで `Dispose()` を呼ばないと、それまで撮れていた分すら再生不能なファイルとして残ってしまう。ここで確定させた `stoppedBy:"assembly-reload"` レコードは `SessionState` に同期的に書き込み、リロード後の新しいドメインでの `status` 呼び出しが正しく報告できるようにした。`playModeStateChanged` の `ExitingPlayMode`(`stop_on_play_exit:true` の既定)がこのフックより先に「きれいな」停止(`stoppedBy:"play-mode-exit"`)を済ませることが多いはずだが、`beforeAssemblyReload` はどちらが先着してもバックストップとして機能する。
8. **「既に録画中か」の判定は `UapVideoRecorder.IsActive`(このドメインのインメモリ状態)だけを信用し、永続化された `SessionState` の "recording" 文字列では判定しない。** `uap_compile` の `DecideRequest` が孤立した `Compiling` レコードを自己修復するのと同じ発想: `beforeAssemblyReload` がうまく発火しなかった(エディタクラッシュ等)場合に "recording" のまま固まったレコードが、新しいドメインでの `start` を永久に拒否し続けることを防ぐ。

### 却下した案

- **フレームごとに明示 `MediaTime` を渡す可変フレームレート方式**: `AddFrame(Texture2D, MediaTime)` オーバーロードで理論上は可能だが、ブリーフが明示的に「uLoop と同じスキップ/複製」を求めていたことと、固定フレームレート出力の方が再生互換性の懸念が少ないことから見送った。
- **Game View の `window` キャプチャでツールバー領域を厳密にクロップする案**: Scene View と同じ精度を狙うと、`GameView` 内部への reflection(非公開の GUI コールバックへのフック)が必要になり、Unity バージョンごとに壊れるリスクが高い。実機で測って確認する手段もこのセッションには無いため、「ウィンドウ全体を読み、上端に多少ツールバーが写り得る」ことを正直に説明する方を選んだ。
- **`max_width` の他に `max_height`/`crop` のような追加引数**: ブリーフの範囲を超えるため見送り。アスペクト比維持の `max_width` のみで十分という判断。
- **保持本数 `DefaultKeepCount` を引数化する案**: ブリーフが「最新 N 本(例 10)」と書いており、スクリーンショットには無い新しい概念のため、まずは固定定数(10)としてシンプルに始め、必要になれば後で引数化できる設計(`SelectFilesToPrune` は既に `keep` を引数に取る純粋関数)にした。

### 検証済みシグネチャ

- `UnityEditor.Media.MediaEncoder`、`MediaRational`、`VideoTrackAttributes`(`frameRate`/`width`/`height`/`includeAlpha`/`bitRateMode` が public、`codec`/`alphaLayout` は internal)、`MediaEncoder(string, VideoTrackAttributes)` コンストラクタ、`AddFrame(Texture2D)` が常に `MediaTime.Invalid` を渡すこと、`IDisposable.Dispose()` -- `Unity-Technologies/UnityCsReference` の `2022.3` ブランチと `6000.0`(`FETCH_HEAD` で取得)ブランチの両方で `Editor/Mono/Media/Bindings/MediaEncoder.bindings.cs` を直接取得・比較し、1 箇所の内部属性引数を除いてバイト同一であることを確認した。
- `UnityEditor.VideoCodec`(`Auto=0, H264=1, VP8=2, H265=3`)、`UnityEditor.VideoBitrateMode`(`Low/Medium/High`)-- `Modules/AssetPipelineEditor/Public/VideoImporter.bindings.cs`(両ブランチで同一)。
- `UnityEngine.RuntimePlatform.WindowsEditor=7 / OSXEditor=0 / LinuxEditor=16` -- `Runtime/Export/BaseClass.cs`(両ブランチで同一)。
- `UnityEditor.EditorWindow.position` の doc コメント「The position of the window in screen space.」-- `Editor/Mono/EditorWindow.cs`。
- 本パッケージ既存の `UapScreenCapture.TryResolveView` / `TryGetGeometry` / `FindOpenGameView`(いずれも public)、`UapEditorScreenshotPaths.TryParseView` / `TryParseCapture`(いずれも public)をそのまま呼び出して再利用 -- シグネチャは同一ファイルから直接確認。
- `UnityEditor.AssemblyReloadEvents.beforeAssemblyReload` / `UnityEditor.PlayModeStateChange.ExitingPlayMode` -- 本パッケージの既存コード(`ReloadLifecycle.cs`、`UapPlayModeTool.cs`)で既に同じシグネチャで使われていることを確認済み。
- Unity Manual "MediaEncoder" のスクリプトリファレンス(`docs.unity3d.com`)のサンプルで `AddFrame(Texture2D)` に渡すテクスチャが `TextureFormat.RGBA32`、`videoAttr.width`/`height` と同じサイズであることを確認(WebFetch)。

### 未検証(実機 Editor での確認が必要)

- `VideoCodec.Auto` が本当に出力ファイルの拡張子(`.mp4` → H264、`.webm` → VP8)**だけ**から解決されるか。ネイティブ実装は非公開で、この結論は「`codec` が internal で公開 API からは設定できない」という事実と、Unity の一般的なガイダンス(Linux Editor に H.264 エンコーダが無い)からの推論であり、Windows/macOS(`.mp4`)・Linux(`.webm`)の両方で実際にエンコードが成功し、意図したコーデックになるかは確認できていない。
- `capture:"window"` + `view:"game"` で `EditorWindow.position` から読んだ画像に、実際にどの程度ツールバーが写り込むか(設計判断 4 の限界)。
- 壁時計ペーシング(スキップ/複製)が実際に「実時間どおりの長さの動画」になるか、`droppedFrames` が実際の体感的な「カクつき」と一致するか。
- Edit Mode で開始した録画が、実際に Play Mode 開始(ドメインリロードあり)で `stoppedBy:"assembly-reload"` として正しく終了し、`beforeAssemblyReload` の中での `Dispose()` が本当にファイルを再生可能な状態まで確定させるか(ドメインリロードという非常にタイトなタイミングでのネイティブ呼び出しの成否)。
- `Texture2D(width, height, TextureFormat.RGBA32, false)` を毎ティック `SetPixels`/`ReadPixels` で使い回す実装が、実際の Editor で 30〜60 fps・最大 300 秒のパフォーマンス上許容できるか(GC/ネイティブアロケーションのプロファイリングは実機が無く未実施)。
- `com.unity.ugui`/`com.unity.inputsystem` の有無に関わらず動く設計のはずだが(このツールはどちらにも依存しない)、CI の `ci/HostProject` / `ci/HostProjectCoreOnly` の両方で EditMode テストが指定どおりの分岐(引数拒否・アイドル状態の status)を通ることは確認できていない(Unity 未使用のため)。

---

## 3.2 `uap_game_watch`(uLoop `watch` の Harmony 無し版)

### 何を作ったか

新規ファイル 3 本(+ `.meta`)。

- `Editor/Ops/UapGameWatchPolicy.cs`(Unity 非依存、smoke gate 対象):引数検証
  (`interval`/`duration`/`max_samples`/`members` の正規化)、Unity 型に一切触れない
  純粋リフレクションのドット区切りメンバーパス歩行(`ResolveMemberPath`)、`until`
  条件のパーサ(`changed` / `==` / `!=` / `<` / `<=` / `>` / `>=`)と比較器
  (`EvaluateCondition`)、値の分類(`ClassifyValue` -- 数値/真偽値/文字列・enum に
  加えて「数値フィールド(または数値プロパティ)だけで出来た構造体」を
  magnitude(ユークリッドノルム)として数値扱いする汎用ルール)、サンプル系列の
  圧縮規則(`ShouldEmitSample`)、複数の終了条件が重なったときの優先順位
  (`DecideStop`)。
- `Editor/Ops/UapGameWatchTool.cs`:`IUapPollableTool` 実装。`target`/`scene`/
  `component`/`componentIndex` は `uap_object_inspect` と全く同じ
  `UapAddressing.ResolveHierarchyPath` + `UapComponentTypeResolver.
  ResolveComponentType` + `GetComponents(type)[index]` で解決し(コード自体は
  再利用、値の読み取り経路は後述の理由で別物)、`members` の各項目を
  `UapGameWatchPolicy.ResolveMemberPath` で読む。表示用フォーマットは
  `UapObjectInspectTool` の `FormatFloat`/`FormatVector2/3/4`/`FormatColor`/
  `FormatRect` を `internal` に変更して再利用(下記「対象ファイル外への変更」)。
- `Tests/Editor/UapGameWatchToolTests.cs`:引数規則、`until` のパース/評価、
  `ResolveMemberPath`/`ClassifyValue` の POCO 型を使った単体テスト、そして
  Edit モードで生成した GameObject の `Transform.localPosition` を実際に
  `Poll()` を直接叩いて数回読ませる統合テスト(`max_samples` での終了、
  `changed_only` の圧縮、`until:"changed"` が初回サンプルでは発火しないこと、
  対象破棄時の `target-destroyed`)。

ツール名: `uap_game_watch`。モジュールは `uap_object_inspect` と同じ `core`。
`IUapAbandonableTool` は実装していない -- 保持する外部状態(キーやマウスボタンの
ような「離し忘れると実害が出るもの」)が無く、途中で見捨てられても
ディスパッチャが poll state を捨てるだけで実害が無いことを確認した。

### なぜこの設計か

1. **リフレクションであって `SerializedProperty` ではない。** `uap_object_inspect`
   は `SerializedObject`/`SerializedProperty` でシリアライズされた値しか読めない。
   ブリーフの例である `PlayerHealth.current` や `velocity.y`(`Rigidbody.velocity`
   は非シリアライズのランタイムプロパティ)はそもそも `SerializedProperty` を
   持たない。そのため本ツールは `System.Reflection` で
   `FieldInfo.GetValue`/`PropertyInfo.GetValue` を直接呼ぶ独自の読み取り経路を
   `UapGameWatchPolicy.ResolveMemberPath` に持つ。ブリーフが「reuse those
   helpers rather than writing a second resolver」と言っている「resolver」は
   パス+コンポーネントの**アドレス解決**(`UapAddressing`/
   `UapComponentTypeResolver`)を指すと解釈し、そちらはそのまま再利用した。
   値の読み取り自体は `uap_object_inspect` に相当する既存ヘルパーが無い
   (`SerializedProperty` 経由でしか値を読んでいない)ため、新規に書いた。
2. **`ResolveMemberPath` は Unity 型を一切名指ししない。** `object root` と
   `string dottedPath` だけを取り、`System.Reflection` のみで歩く。これにより
   ライセンス不要の smoke gate でも(Unity の `Component` の代わりに
   `System.Text.StringBuilder` のような BCL 型で)そのまま検証できる。
   フィールドを優先し、無ければプロパティを試す。`Public | NonPublic |
   Instance` で検索するのは、`uap_object_inspect` が `[SerializeField]` の
   private フィールドも普通に見せているのと同じ理由。
3. **「ベクトルは magnitude、成分はドットパスで」という設計。** `until` は
   ブリーフの「numbers/bools/strings/enums/vectors-as-magnitude-or-component」
   要求を満たす必要がある。`ClassifyValue` は Unity の `Vector2/3/4` を型名で
   特別扱いする代わりに、「public インスタンスフィールドが 1〜8 個ですべて
   数値プリミティブ(それが無ければ public インスタンスプロパティで同じ判定)」
   という**構造ベースの汎用ルール**にした。これにより `Vector2/3/4`・
   `Quaternion`・`Color`(いずれも Unity のフィールドベースの構造体)だけでなく
   プロジェクト独自の同じ形の構造体も同じ扱いになる。symmetry の代償として
   符号が失われる(magnitude は常に非負)ため、「特定成分と比較したい」場合は
   `members` 側でドットパス(`"velocity.y"`)を使って先に scalar まで
   降りてもらう設計にした(この場合 `ClassifyValue` は float の scalar 分岐に
   入り、符号はそのまま保持される)。
4. **duration の上限は 6 秒、根拠は `UapMainThreadDispatcher` の実測値。**
   `UapMainThreadDispatcher.DefaultTimeoutMillis` は 15000ms だが、Play Mode
   中にエディタがバックグラウンドだと `ThrottledTimeoutMillis`(8000ms)に
   落ちる(SEC-7: タイムアウトした pollable はその後二度と poll されず、
   結果は永遠に失われる)。8 秒ぎりぎりまで `duration` を許すと、応答の
   JSON 構築やディスパッチャの往復ぶんの余白が無くなるため、ブリーフが
   例示した 6 秒をそのまま採用し、`UapGameWatchPolicy.MaxDurationSeconds`
   という定数 1 箇所にまとめた(smoke gate でも
   `MaxDurationSeconds < 8` を assert してこの前提を固定している)。
5. **`interval` の下限は明示的なバリデーションを設けなかった。** ブリーフの
   「min one editor tick」は「1 エディタティックより速くはサンプリングできない」
   という**実際の挙動の説明**であって、引数エラーにする性質のものではないと
   判断した。`interval:0` は「毎ティックサンプリングする」という意味として
   そのまま受け入れ、`ValidateInterval` は有限かつ非負であることだけを見る。
6. **1 回目のサンプルは `Begin()`(=最初の `Poll` 呼び出し)の中で同期的に取る。**
   これにより (a) `duration` の秒数を 1 ティックぶん無駄にしない、(b) `until`
   が t=0 で既に真(例: 開始時点で `"== 0"`)なケースを即座に捉えられる。
7. **`until:"changed"` は「直前に返したサンプル」ではなく「最初の読み取り
   (baseline)」との比較にした。** `changed_only` の「変化があったサンプルだけ
   残す」圧縮と、`until:"changed"` の「値が動いたら止める」は別の概念で、
   もし同じ「直前比較」を使うと `changed_only:true` のときに
   `AnyValueDifferent(null, ...)` が「前回値が無い(=最初のサンプル)」を
   「変化あり」として返してしまい、`until:"changed"` が baseline サンプル
   そのもので即座に発火してしまう(実装中にテストで検出、
   `Poll_UntilChanged_DoesNotFireOnTheFirstSample_...` として固定した)。
   このため状態機械では `BaselineValues`(最初のサンプルで確定、以後不変)と
   `LastEmittedValues`(`changed_only` の圧縮判定専用、出力するたびに更新)を
   別々に持つ。
8. **`max_samples` はキープ(出力)したサンプル数のカウンタで、読み取り回数
   ではない。** `changed_only:true` で値が動いていない間は誰も
   `max_samples` を消費しない -- 動かないものをいくら読んでも「記録すべき
   情報」は増えていないので、上限に達することもない。これは意図した挙動で、
   `stoppedBy:"max-samples"` は「本当に大量の変化を記録しきれなかった」場合
   にのみ現れる。
9. **`stoppedBy` の優先順位は「対象破棄・Play モード終了(続行不能)」→
   `until`(意図的な条件)→ `duration`(自然な終了)→ `max_samples`(打ち切り)。**
   `duration` 到達と `max_samples` 到達が同一ティックで重なった場合、
   「たまたま上限と同時に自然終了しただけ」であって「打ち切られた」わけでは
   ないため `truncated:false` の `duration` を優先する
   (`UapGameWatchPolicy.DecideStop` に一箇所に集約、テストで全組み合わせを固定)。
10. **Play モード終了と対象破棄は別の `stoppedBy` にした。** どちらも
    「これ以上読めない」という点は同じだが、ブリーフが両方を別の語として
    要求しているため、`WasPlayingAtStart && !EditorApplication.isPlaying`
    (Play モード終了)を対象破棄チェックより先に見る(Play モード終了時は
    対象も道連れで破棄されていることが多いが、両方が真でも「Play モード終了」の
    方を理由として報告する)。
11. **サンプリングの基準時刻は `Time.realtimeSinceStartupAsDouble`。**
    Edit モードでも Play モードでも一貫して進む実時間で、`uap_game_key` が
    使う `EditorApplication.timeSinceStartup` と同種の選択(こちらは
    ブリーフが名指しした `Time.realtimeSinceStartup` 系列そのもの)。
    ブリーフの「plus Time.frameCount and, in Play Mode, Time.time」は
    出力メタデータとしてそのまま実装した(`frame` は常に、`time` は
    `EditorApplication.isPlaying` のときだけ)。

### 却下した案

- **Vector2/3/4 等を型名で特別扱いする案**: 却下(§設計判断 3)。構造ベースの
  汎用ルールの方が、Core が直接参照していない `Physics2D` 系の値や
  プロジェクト独自の数値構造体もそのまま拾えて汎用性が高い。表示フォーマットは
  逆に型名ベース(`UapObjectInspectTool` の各 `Format*` を再利用)にした --
  「比較(数値化)」と「表示(文字列化)」は別の関心事だと割り切った。
- **`until` を「最後に出力したサンプル」との比較にする案**: 却下
  (§設計判断 7)。baseline 比較に統一。
- **`max_samples` を「読み取り回数」のカウンタにする案**: 却下(§設計判断 8)。
  出力サンプル数のカウンタの方が「上限に達した」の意味が呼び出し側にとって
  自然(JSON の `samples` 配列の長さと一致する)。
- **対象アドレスに instance id を受け付ける案**: ブリーフの文面
  (「hierarchy path or instance id, same addressing as uap_object_inspect」)
  はそう書いているが、実際の `uap_object_inspect` 自身も `UapAddressing` も
  instance id 解決を持っていない(`path` + 任意の `scene` のみ)。存在しない
  ヘルパーを新規に作るより「同じアドレス指定方法」を文字通り
  `uap_object_inspect` に合わせることを優先し、instance id 対応は見送った
  (`target` は hierarchy path のみ)。
- **`IUapAbandonableTool` の実装**: ブリーフどおり不要と確認して見送った。
  途中で見捨てられた watch はディスパッチャが poll state を破棄するだけで、
  ゲーム側の状態は一切触っていないため復旧すべきものが無い。

### 対象ファイル外への変更(報告)

`Editor/Ops/UapObjectInspectTool.cs` の `FormatFloat`/`FormatVector2`/
`FormatVector3`/`FormatVector4`/`FormatColor`/`FormatRect` を
`private static` から `internal static` に変更した(ブリーフの
「if reuse needs a tiny additive visibility change in a helper, make it and
report it」に基づく)。**可視性のみの変更で、既存の動作・シグネチャ・呼び出し
元は一切変えていない。** `uap_object_inspect` と表示フォーマットを完全一致
させるため(浮動小数の "R" ラウンドトリップ書式、Vector/Color/Rect の
括弧区切り書式)に、同じ書式ロジックを二重実装せず再利用する目的。
`UapObjectInspectTool.cs` はどのアイテムの Owns にも入っていない共有外
ファイルだが、HARD RULES の禁止編集リスト(ToolRegistry.cs 等)には含まれて
いないため、このピンポイントな追記のみ許容されると判断した。

### 検証済みシグネチャ

- `UnityEngine.Time.realtimeSinceStartupAsDouble`(`double`、`Time.realtimeSinceStartup`
  は `float`)、`Time.frameCount`(`int`)、`Time.time`(`float`)
  -- `Unity-Technologies/UnityCsReference` の `2022.3` ブランチ
  `Runtime/Export/Time/Time.bindings.cs` と `6000.0` ブランチの同ファイルを
  直接取得して両方で同一シグネチャであることを確認した。
- 本ツールが再利用する `UapAddressing.ResolveHierarchyPath` /
  `UapComponentTypeResolver.ResolveComponentType` / `GameObject.GetComponents
  (Type)` は `UapObjectInspectTool.Execute` が既に全く同じ形で使っている
  既存コードで、今回新たな検証は不要と判断した(既存ツールの検証実績に乗る)。
- smoke gate 用の代替型として使った `System.Numerics.Vector2`(`public float X`/
  `Y` が **フィールド**)と `System.Collections.Generic.KeyValuePair<TKey,TValue>`
  (`Key`/`Value` が get-only **プロパティ**、バッキングフィールドは private)
  を `dotnet/runtime` の該当ソース(`Vector2.cs`/`KeyValuePair.cs`)で確認した
  上で、`TryVectorMagnitude` のフィールド優先・プロパティへのフォールバックの
  両方の経路を smoke テストで実際に踏ませている。

### 未検証(実機 Editor / 実エージェントでの確認が必要)

- 実際の `UapMainThreadDispatcher` 経由(HTTP ワーカースレッド → `Pump()` →
  `EditorApplication.update`)での `interval` の実時間ペーシング精度。
  EditMode テストでは `Poll()` を直接叩いているため、`interval:0` 以外の
  値での実際のティック間隔のばらつきは未検証。
- 実際のコンポーネント(`Rigidbody.velocity` のような非シリアライズプロパティ、
  独自 MonoBehaviour の public プロパティ)を、実プロジェクトで
  `uap_game_watch` から読んだときの正しさ。テストは `Transform.localPosition`
  のみで検証している。
- `Quaternion`/`Color`/`Rect` を実際に Unity の値として `members` に渡した
  ときの表示フォーマット(`UapObjectInspectTool.Format*` の再利用先の
  実引数側)。smoke テストは Unity 型を参照できないため BCL の代替型で
  ロジックだけを検証しており、Unity 型そのものでの通しは EditMode の
  `Transform.localPosition`(`Vector3`)テストでのみ確認している。
- Play モードが**ドメインリロードを伴って**終了したときの `play-mode-exit`
  検出(`EditorApplication.isPlaying` の遷移で拾う設計だが、実際のドメイン
  リロードを跨いだ poll state の扱いは未確認 -- そもそも poll state は
  ドメインリロードで失われるはずなので、この場合は「呼び出しごと失われる」
  だけで実害は無いと考えているが、実機未確認)。
- `uap_game_watch` を実際の MCP ラウンドトリップ(`UapOpsRequestHandler` /
  HTTP)経由で呼んだときの JSON 応答の見え方。テストはツールオブジェクトを
  直接呼んでいる。

---

## 3.4 `uap_input_record` / `uap_input_replay`(uLoop `record-input` / `replay-input`)

### 何を

新規ツール 2 本(module `editor`)。

- **`uap_input_record`**(`action: start`(既定)/ `stop` / `status`): Play モード中、`EditorApplication.update` の tick ごとにキーボード(押されている `Key` の集合)とマウス(位置・左右中央ボタン・スクロール量)の状態を読み、**前回サンプルとの差分だけ**を `t`(記録開始からの秒数)付きイベントとして貯める。`start` は引数を検証してすぐ返り、実際のサンプリングは応答後の tick で進む(`uap_record_video`/`uap_compile` と同じ「応答をブロックしない」形)。`stop` はイベント列を 1 本の JSON ファイルへ書き出し、パス・イベント数・記録時間を返す。既定の保存先は `Library/UnityAgentPanel/InputRecordings/<timestamp>.json`、明示パスはプロジェクト内に解決できるものだけを許可(`..` セグメントは絶対/相対を問わず拒否)。上限は `max_seconds`(既定 120 秒、最大 300 秒)と、イベント数のハード上限 20000(引数ではなく固定の安全弁)。最新 20 本を残して古いファイルを自動で間引く。
- **`uap_input_replay`**(`action: start` / `stop` / `status`): 記録ファイルを読み、`start` 以外のツールと同じく `EditorApplication.update` 駆動のバックグラウンドで、記録時と同じ `t` の位置へ(`speed` 倍速で)`uap_game_key`/`uap_game_mouse` が使っているのと同じ `InputSystem.QueueStateEvent` 経路で状態を注入し続ける。押されてすぐ離されるような短い操作でも `UapGameInputPolicy.MinHoldPolls`(2 tick)以上は必ず保持してから離すため、記録時にどれだけ短くても再生先のゲームは press エッジを取りこぼさない。停止・完了・Play モード終了・ドメインリロード・エラーのどの経路でも、保持していたキー/ボタンを必ず解放してから終わる。

いずれも `IUapPollableTool` ではない(`uap_record_video` と同じ理由: 記録・再生とも `UapMainThreadDispatcher` の ~15 秒待ちよりずっと長く動きうるため、Execute 呼び出しの外で完結させる)。`IUapAbandonableTool` も実装していない -- ブリーフの想定どおり、入力を保持するのは Execute 呼び出しの中ではなく tick 駆動のバックグラウンドセッション自身なので、呼び出しが「見捨てられる」概念が無い。

新規ファイル: `UapInputRecordingPolicy.cs`(Unity 非依存 -- 記録ファイル形式とバージョニング、パース/検証、イベント差分化、リプレイのタイムライン・スケジューリング、速度/パス/上限の引数規則)、`UapInputRecorder.cs` / `UapInputReplayer.cs`(Editor 側、`EditorApplication.update` 駆動)、`UapInputRecordTool.cs` / `UapInputReplayTool.cs`(薄い action ディスパッチ)、`Tests/Editor/UapInputRecordingTests.cs`。加えて `UapInputSystemMouseAccess.cs` に `ReadScroll(object mouse)` を 1 メソッド追記(`ReadPosition` と全く同じ形のリフレクション読み取り。既存メンバの挙動は変えていない)。

### なぜこの設計か

- **サンプリング(全 tick スキャン + 差分)を選び、`InputSystem.onEvent` の購読は選ばなかった。** ブリーフの指示どおり uLoop 3.6.3 の実装(`/tmp/.../scratchpad/uloop/Packages/src/Editor/FirstPartyTools/Common/InputRecording/`)を読んで確認したところ、**uLoop 自身も `onEvent` ではなく `InputSystem.onAfterUpdate` フックでの全 tick サンプリング + 前後の `HashSet<Key>`/`HashSet<MouseButton>` 差分**という、本ツールとほぼ同じ発想だった(`InputRecorderFrameCapture.cs`)。違いは 2 点: (1) uLoop はキー走査を `_defaultScanKeys` という約 50 キーの固定リストに絞っているのに対し、本ツールは `UapInputSystemKeyboardAccess.PressedKeyNames`(`uap_game_key` の `release_all` 読み戻しで既に使っている、`Key` enum 全メンバーを回す実装)をそのまま再利用して**全キー**を対象にした(OEM キーやテンキーも取りこぼさない。コストは tick あたりのリフレクション呼び出しが増えることだが、既存コードの再利用を優先した)。(2) uLoop は `InputSystem.onAfterUpdate` という入力専用のフックを別途購読しているが、本ツールは**このパッケージの他のツール(`uap_game_key` の Poll、`uap_compile`、`uap_record_video` の `OnUpdate`)と同じ `EditorApplication.update`** に統一した。記録専用の別イベントを増やすより、パネル全体で 1 種類の tick 駆動に揃える方が保守しやすいと判断した。
  サブフレーム(1 tick 未満)の押下を取りこぼす、という設計上の弱点は残るが、これは `uap_game_key`/`uap_game_mouse` 自身が `UapGameInputPolicy.MinHoldPolls`(2 tick)以上保持することを既に強制しているため、**この 2 本のツールで注入された操作は record 側で確実に拾える**。取りこぼしうるのは物理キーボードでの生身の操作のみで、それは「実機で未確認」に明記した。
- **記録ファイルは `t`(記録開始からの経過秒、浮動小数)でイベントを並べ、uLoop のような「フレーム番号(`InputFrameEvents.Frame`)でグルーピング」は採らなかった。** uLoop の `InputRecordingData.cs` を見ると `Frames: List<InputFrameEvents>`(各要素が整数フレーム番号を持つ)という形をしている。フレーム番号は「そのフレームの実時間の長さ」を記録していないと再生時に絶対時間へ戻せず、**任意倍速の再生(`speed`)と相性が悪い**(元の記録時のフレームレートと同じペースで tick が進む前提になってしまう)。本ツールは `t` を秒単位で持つことで `elapsedScaledSeconds = elapsedRealSeconds * speed` という単純な掛け算だけで倍速/低速再生ができるようにした。ブリーフが要求する「optional speed 0.25-4」は uLoop の replay-input 側(`InputReplayer.cs`)には無い概念で、これは本ツール独自の追加である(確認したが `InputReplayer.cs`/`InputReplayEventProcessor.cs` に "speed" や "hold" という語は一つも出てこない)。
- **注入経路は `InputSystem.QueueStateEvent<TState>` に統一し、uLoop の `InputState.Change(device, eventPtr, updateType)` は使わなかった。** ブリーフの指示(「既存の QueueStateEvent パスを再利用」)どおりであり、`UapInputSystemKeyboardAccess`/`UapInputSystemMouseAccess` が既に持つ `QueueState` をそのまま呼ぶだけで済む。uLoop の `InputStateChangeApplier.cs` はイベントポインタを直接組み立てる、より低レベルな API を使っているが、それを追いかける理由がない(本パッケージには QueueStateEvent 経由の実績が既にある)。
- **press エッジの最短保持(`MinHoldPolls`)は本ツール独自の追加。** 記録側が 1 tick 未満の操作を取りこぼす代わり、記録できた操作(=最低でも 1 サンプル区間は続いた操作)は、再生時に **必ず** `UapGameInputPolicy.MinHoldPolls` tick 以上保持してから離す。記録の `t` 間隔がどれだけ短くても、`UapInputRecordingPolicy.AdvanceReplay` が対応する KeyUp/MouseButtonUp を「まだ MinHoldPolls tick 経っていない」うちは適用せず足踏みさせる(カーソルをそのイベントの手前で止める)ことで実現した。uLoop にはこの概念自体が無い(`InputReplayer.cs` は記録された時刻が来たらそのまま状態を反映するだけ)。
- **座標は `uap_game_mouse` の「左上原点・Y 下向き」に変換せず、デバイスの生の値(`Mouse.current.position`、左下原点)のままファイルに書く。** record は読み取った値をそのまま書き、replay は同じ値をそのまま `QueueState` に渡すだけなので、往復で座標変換のバグが入り込む余地がない。エージェントが座標を手で読み書きする形式ではなく、機械が書いて機械が読む内部フォーマットなので、人間に読みやすい座標系を選ぶ理由も無いと判断した。
- **`delta`(マウスの移動量)は記録しない。** `uap_game_mouse` の `move_delta` と同じ理由(`position` と `delta` は独立した値で、片方から他方を合成できない)に加え、record 側は `position` の絶対値の差分だけで「カーソルがどこにあるべきか」を完全に再現できるため、`delta` を別途記録・再生する必要が無い。`Mouse.current.delta` を読むゲームコード(FPS 視点操作など)には、replay の絶対位置再現だけでは効かないという限界が残るが、これは意図的な割り切りとして説明文とこのノートの両方に明記した。
- **スクロールは「その tick のデルタ」として記録・再生し、保持状態には含めない。** Input System の delta 系コントロールは新しい state を注入してもしなくても次の update で自動的に 0 へ戻る(Phase 1 ノートの `uap_game_mouse` 設計判断で確認済みの仕様)ため、replay 側も「その tick に該当するスクロールイベントがあれば、その値を乗せた 1 回だけ `QueueState` する」だけでよく、明示的なリセット処理は不要にした。
- **`uap_input_record` / `uap_input_replay` は互いに排他。** 両方とも同じ `Mouse.current`/`Keyboard.current` を叩くため、再生中に記録すると再生自身の注入イベントを記録してしまう(ブリーフの明示要求)。`UapInputRecordingPolicy.DecideRecordStart`/`DecideReplayStart` の 2 引数版の相互チェックで実装した。
- **`UapInputRecorder`/`UapInputReplayer` は `UapGameKeyTool`/`UapGameMouseTool` の `private static` な保持状態(`HeldKeys`/`HeldButtonsMask`)を再利用しなかった(「reuse ... if accessible」の "accessible" ではない)。** 代わりに、両クラスとも `UapInputSystemKeyboardAccess`/`UapInputSystemMouseAccess` という共有の `internal` アクセス層を直接叩き、**自分自身の保持状態**(record 側は前回サンプルとの比較用、replay 側は `UapInputReplayState`)を独立に持つ。これにより、record/replay が `uap_game_key`/`uap_game_mouse` の保持状態と混線する心配がない(例えば replay の stop がエージェントの `uap_game_key action:down` で保持中のキーまで解放してしまう、という事故を避けられる)。停止時の解放(`ReleaseEverything`)は `UapInputSystemKeyboardAccess.QueueState`/`UapInputSystemMouseAccess.QueueState` を空/ゼロの状態で直接呼ぶだけで、`uap_game_key`/`uap_game_mouse` 自身は一切呼び出さない。
- **Play Mode 終了時も(uap_game_key と違い)解放を「試みる」。** `uap_game_key.OnPlayModeStateChanged` は「デバイスごと無くなるので送っても無駄」として `HeldKeys.Clear()` のみで送信しない設計だが、`UapInputReplayer` は同じタイミングでも `ReleaseEverything` を(失敗を握りつぶす形で)呼ぶ。複数キー/ボタンを同時に保持しうる replay では、`playModeStateChanged` とデバイス破棄の実際の順序に確証が持てないため、無害な追加の解放試行を常に行う方を安全側とした(実機で未確認)。
- **引数の形の検証(ArgumentException)を Play Mode などの状態検証(InvalidOperationException)より先に行う。** `UapGameKeyTool.Begin` が key/duration を先に検証してから `CheckPlayState` を呼ぶのと同じ順序にした。これにより `max_seconds`/`speed`/`path` の引数エラーはバッチモード(Play Mode に入らない EditMode テスト)でもテストできる。
- **`UapAssetPath.CollapseDotSegments` は使わず、パス内の `.`/`..` セグメントは検出したら即座に拒否する自前の軽い実装にした。** `NormalizeUnderAssets` は「`Assets/` 配下に着地するかどうか」だけを見る設計で `..` を解決して判定するが、本ツールは絶対パスも許可する必要があり、絶対パスに対して `CollapseDotSegments`(先頭の `/` を失う仕様)をそのまま使うと判定が壊れる。`Path.GetFullPath` も使わなかった(ドライブレター・大小文字・シンボリックリンクなど OS 依存のセマンティクスを Unity 非依存の Policy クラスで扱いたくなかったため)。結果として「`..`/`.` セグメントを含む経路はどんな形でも拒否」という、やや保守的だが説明しやすいルールにした。

### 却下した案

- **フレーム番号でスケジューリングする案(uLoop 方式)**: 上記の理由(任意倍速との相性)で却下。
- **`InputSystem.onEvent` を購読して `InputEventPtr` をデコードする案**: ブリーフが最初から示唆していた案で、uLoop 自身も実際には使っていないことを確認した(`onAfterUpdate` でのサンプリングだった)。バージョン間(1.7〜1.19)でイベントの内部レイアウトが変わるリスクを負ってまで導入する理由がないと判断し、既存の `QueueStateEvent`/`PressedKeyNames` と対称的な「読み取りも `Keyboard`/`Mouse` の公開コントロールから行う」方式に統一した。
- **記録専用に `InputSystem.onAfterUpdate` を購読する案**: uLoop はこちらを使っているが、本パッケージの他の全ツールが `EditorApplication.update` に統一されているため、記録だけ別のイベントソースを増やす理由がないと判断し見送った。
- **`uap_game_key`/`uap_game_mouse` の `Execute` を replay の注入経路として呼ぶ案**: 両ツールの `action:down`/`up` は「エージェントが呼んだ回数だけ状態を持つ」独立した保持状態を前提にしており、そこに replay が割り込むと二重管理になる。`UapInputSystemKeyboardAccess`/`UapInputSystemMouseAccess` という共有の低レベル層を両者から独立に叩く設計にした(上記の設計判断参照)。
- **キー走査を uLoop 同様の固定リストに絞る案**: 既に `UapInputSystemKeyboardAccess.PressedKeyNames` が全 `Key` enum を安全に(未対応キーは例外を握りつぶして)スキャンする実装を持っていたため、そのまま再利用する方が「取りこぼしが少ない」「新しいコードが要らない」の両方を満たすと判断した。

### 検証済みシグネチャ

- `UnityEngine.InputSystem.Mouse.scroll`(`public DeltaControl scroll { get; protected set; }`)-- com.unity.inputsystem 1.7.0 **と** 1.19.0 の両方の `InputSystem/Devices/Mouse.cs` で確認(バイト単位でこの宣言は同一)。`DeltaControl : Vector2Control`(`InputSystem/Controls/DeltaControl.cs`)で `ReadValue()` が `Vector2` を返すことも確認。`position` プロパティ(`Pointer.cs` の `public Vector2Control position { get; protected set; }`)と全く同じ形の宣言であることを確認した上で、既存の `UapInputSystemMouseAccess.ReadPosition` と全く同じリフレクション手順(`GetProperty` → `GetValue` → `GetMethod("ReadValue")` → `Invoke`)で `ReadScroll` を実装した。
- `UnityEngine.InputSystem.Mouse.leftButton`/`rightButton`/`middleButton` と `UapGamePointerButton`/`MouseButton` の対応、`InputSystem.QueueStateEvent<TState>`、`UapInputSystemKeyboardAccess.PressedKeyNames`/`ParseKey`/`QueueState`、`UapInputSystemMouseAccess.ReadPosition`/`IsPressed`/`BuildState`/`ButtonBit`/`QueueState` は、いずれも Phase 1(`docs/design-notes/2026-09-22-uloop-phase1.md` 2.2 節)で検証済みの既存コードをそのまま呼ぶだけで、本タスクで新たに検証すべき箇所は増えていない。
- `Colloid.AgentPanel.Core.FileIo.AtomicFile.WriteAllTextOrThrow`/`ReadAllText`、`Colloid.AgentPanel.Core.Json.JsonParser.TryParse`/`JsonWriter.Write`、`UapGameKeyPolicy.KnownKeys`、`UapAssetPath.CollapseDotSegments`(既存の使用箇所からの類推、直接は再利用していない)は既存の本パッケージコードで既に本番稼働している。
- uLoop 3.6.3 の実際の実装(`InputRecorderFrameCapture.cs`/`InputRecorder.cs`/`InputReplayer.cs`/`InputRecordingData.cs`/`RecordInputConstants.cs`)を直接読み、上記の「なぜこの設計か」「却下した案」の記述の裏付けにした(コードは 1 行も転用していない)。`MAX_RECORDING_DURATION_SECONDS = 300f` が本ツールの `MaxMaxSeconds` と一致したのは、ブリーフの「e.g. 300 s」を独立に採用した結果の偶然の一致。

### 未検証(実機 Unity Editor / 実際の Input System デバイスが必要)

- 実際の Unity Editor で Play Mode 中に `uap_input_record` → `uap_input_replay` を通しで動かし、記録したキー/マウス操作(押す・離す・移動・スクロール)が実際に元のゲームと同じ結果を再生で得られるか。
- `PressedKeyNames` の全 `Key` enum スキャンを毎 tick 行うことによる実際のパフォーマンス影響(リフレクション呼び出しの積み重ね)。
- `Mouse.current.scroll` の値が、実際の物理スクロールおよび `uap_game_mouse action:scroll` による注入の両方に対して、記録・再生を通して期待どおりに振る舞うか。
- `EditorApplication.update` と `InputSystem` の入力更新(input update)の実際のタイミング関係(1 Editor tick の間に input update が 0 回・1 回・複数回走りうるかどうか)が、本ツールのサンプリング粒度にどう影響するか。
- ドメインリロード(`OnBeforeAssemblyReload`)が実際に発火するタイミングと、その時点でまだ書き込み中のファイル IO(`AtomicFile.WriteAllTextOrThrow`)が確実に完了するか。
- `UapInputReplayer.ReleaseEverything` が Play Mode 終了時に実際にゲーム側へ届くか(uap_game_key と異なりここだけ「送信を試みる」設計にしたが、実機での効果は未確認)。
- 2022.3.22f1 / Unity 6 系の両方で `com.unity.inputsystem` 1.19.0 を使った EditMode テスト実行(`ci/HostProjectCoreOnly`)は、このタスクでは Unity Editor が無いため走らせていない。


---

## 3. 統合時に直したこと・判断したこと

- **CS0177(smoke ビルドで検出)**: `UapGameWatchPolicy.NormalizeMembers` が、成功した
  ときに out 引数 `error` へ代入せずに返っていた。実装担当とレビュー担当はどちらも
  コンパイラ無しで書いたため見落とした。smoke ゲート(net8)でビルドして見つけ、修正した。
- **レビューで直したもの**:
  - `MediaEncoder.AddFrame` が false を返したとき(例外は出ない)に、書けたものとして
    数えていた。
  - `capture:"window"` と `max_width` を一緒に指定すると、縮小されずに窓の隅だけが
    切り出されていた。
  - 入力の記録ファイルに Input System 1.19 の追加キー(F13〜F24、メディアキー)が
    入っていると、再生時に「不明なキー」としてファイル全体を拒否していた。
- **直さずに残した判断**: 再生時、押してから最低保持 tick 数が経っていない
  `KeyUp` / `MouseButtonUp` に当たると、その後ろのイベント(時刻が来ているもの)も
  一緒に待たせる。レビューでは「無関係なマウス移動まで遅れる」と指摘された。
  ただし、記録順を守ること(W を離してから S を押す、などの前後関係)のほうが、
  最大数 tick のずれより再現性にとって重要だと判断した。ずれは
  `UapGameInputPolicy.MinHoldPolls` tick に収まる。
- **全体レビュー(コンパイルエラーに絞った 5 系統)**: 追加の修正は無し。

## 4. 検証の状況

- smoke ゲート: 480 件がすべて通る(フェーズ 1 の時点では 377 件)。今回足したのは
  録画、監視、入力の記録/再生の純粋関数。
- Unity を使う側(EditMode テスト、両ホスト)は CI で確かめる。
- **実機で未確認**(各節の「未検証」の要約):
  - 録画: mp4 / webm が実際に再生できるファイルとして確定するか。ドメインリロード
    直前の Dispose で確定するか。実時間とペースが合うか。
  - `uap_game_watch`: ディスパッチャ経由でのサンプリング間隔の精度。
  - 入力: 実デバイスで記録してから再生するまでの往復。Play 終了時の解放が
    ゲートに届くか。
