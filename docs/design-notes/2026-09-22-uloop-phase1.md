# uLoop 自前化フェーズ 1 の実装

Date: 2026-09-22
対象: `jp.colloid.unity-agent-panel`(Core)v0.60.0-beta.1
関連: `docs/design-notes/2026-09-22-uloop-full-native-roadmap.md` §2(計画)、
`docs/design-notes/2026-09-22-game-input-tools.md`(同じベータに入る入力ツール)、
`docs/design-notes/2026-09-21-uloop-always-loaded-cost.md` §9

---

## 0. 結論(先に)

1. ロードマップ §2 のフェーズ 1 を実装した。新規ツールは `uap_compile` / `uap_help`
   (core)、`uap_game_mouse` / `uap_project_info` / `uap_editor_focus`(editor)の 5 本と、
   コンパクト一覧専用の `uap_tools` / `uap_call`。既存の `uap_editor_screenshot` に
   `annotate_ui` / `ui_only` を足した。HTTP MCP を名乗らない ACP エージェント向けに
   stdio 中継を足し、CI の Core-only ホストに uGUI と Input System を入れた。
2. **ロードマップからの変更点が 2 つある。**
   - **2.8 A(説明文の手作業での圧縮)はやめた。** Claude Code はツール説明を
     必要になってから読み込むので、説明を削っても得がなく、埋め込んであった
     使い分けの指示(座標系、ドメインリロードの待ち方など)を失う損だけが残る。
     コスト削減が必要なのは Claude 以外のエージェントで、それは B(コンパクト
     一覧)が接続単位で解く。A の代わりに、一行要約を説明文の先頭文から自動で
     作り(`UapToolSummary`)、全文は `uap_help` / `uap_tools names:[...]` で
     取りに行く形にした。
   - **2.8 C(`tools/list_changed`)は入れていない。** 実クライアント(Codex /
     Grok / Gemini)がこの通知で一覧を取り直すかが未測定で、サーバ側に GET の SSE
     ストリームを足す必要もある。B だけで必要な削減は得られるので、測定できる
     環境で試すまで保留する。
3. **版はロードマップの「v0.60.0 をフェーズ 1 単独で」ではなく、同じブランチの
   入力ツールと合わせて v0.60.0-beta.1 1 本になった。** ブランチを開いている間に
   main が v0.59.0 を安定版として出し、このブランチの「v0.59.0-beta.2」は別の
   コミットがその番号で出ていたため、main をマージして切り直した(CLAUDE.md の
   版規則 4)。
4. 実装は機能ごとに並列で書き、機能ごとに独立したレビュー役が読み直して修正した。
   その後、統合担当が統合時と全体レビューで見つかった点を直した(§3)。**Unity でのコンパイルは
   このセッションでは確認できていない**(ライセンス不要の smoke ゲートは 377 件全緑)。
   最終確認は CI の EditMode 実行と、各節末尾の「実機で未確認」。

---

## 2.1 `uap_editor_screenshot` の `annotate_ui` / `ui_only`

### 何を

`uap_editor_screenshot` に二つの引数を追加した。

- **`annotate_ui:true`**: `Selectable`(`UnityEngine.UI.Button` / `Toggle` / `Slider` /
  `Scrollbar` / `Dropdown` / `InputField`、および TMP 版 `TMP_InputField` /
  `TMP_Dropdown` -- いずれも `Selectable` を継承するので、`Selectable` 1 種類の
  型判定だけで全部拾える)と、`IPointerClickHandler` / `IDragHandler` /
  `IDropHandler` を実装する任意のコンポーネントのうち、**有効な Canvas の配下**
  にあるものを列挙し、`uap_game_pointer` にそのまま渡せる形(ラベル、階層パス、
  コンポーネント型、`interactable`、表示テキスト、Game View レンダーピクセルでの
  中心座標(左上原点、`Handles.GetMainGameViewSize()` -- `uap_game_pointer` の
  x/y と同じ規約)、0..1 正規化中心、矩形)で JSON 配列として返す。既定 60 件で
  打ち切り、`truncated` フラグを立てる。`view:"game"` のときは同じ情報を
  スクリーンショット PNG にも枠線 + 1〜2 文字のラベル(内蔵の 5x7 ビットマップ
  フォント、A-Z のみ)として焼き込む。
- **`ui_only:true`**: `annotate_ui:true` を含意し、画像の撮影・書き込みを
  丸ごと省略して一覧だけを返す。ビューを一切使わないので、Scene/Game
  ビューが無い(バッチモードを含む)状態でも答えられる。

新規ファイルは `UapUguiElementCatalog.cs`(Unity 側・リフレクションで探索)と
`UapUiAnnotationPolicy.cs`(Unity 非依存・並び順/座標変換/JSON 整形/埋め込み
フォント)に分割し、`UapScreenCapture.cs` にはカメラ描画結果を PNG 化する前に
触れる `CaptureCameraToTexture` と、そこへ枠線・ラベルを焼き込む
`DrawUiAnnotationMarks` を足した(いずれも既存の `CaptureCameraToPng` の
出力は変えない、単なる中間ステップの追加)。

### なぜ

uLoop の `--annotate-elements --elements-only` に相当する機能で、
`docs/design-notes/2026-09-22-uloop-full-native-roadmap.md` §2.1 が
「フェーズ 1 で最も効果が大きい 1 本」と位置づけた項目。これが無いと
「スクリーンショットを見る → ピクセル座標を目分量で読む → `uap_game_pointer`
に渡す」を毎回やる必要があり、座標の読み違いが `uap_game_pointer` の
失敗の主因になっていた。JSON の一覧が階層パスを返すので、
`uap_game_pointer` の `target` にそのまま渡せば座標の目分量が丸ごと要らなくなる。

### 設計判断

1. **座標系は `uap_game_pointer` と完全に同じ経路で作る。** 各要素の中心/四隅は
   `RectTransformUtility.WorldToScreenPoint(camera, worldPoint)` -- `camera` は
   Screen Space - Overlay なら `null`、それ以外は Canvas の `worldCamera` か
   `Camera.main` -- で「ボトムレフト原点スクリーン座標」を得てから、
   `UapUiAnnotationPolicy.FlipToTopLeft` で Y だけ反転してトップレフトに直す。
   これは `UapGamePointerTool.WidgetCentre` / `CanvasCamera` と全く同じ経路で、
   Camera.WorldToViewportPoint によるカメラ投影は使わない
   (Overlay Canvas の「ワールド座標」はそもそも実際のカメラ投影と無関係な
   スクリーン座標そのものであり、`RectTransformUtility.WorldToScreenPoint` の
   `cam==null` 分岐がその特殊ケースを正しく扱う -- UnityCsReference で確認、
   §検証済みシグネチャ参照)。`UapGamePointerTool` / `UapUguiEventAccess` は
   編集不可なので、`CanvasCamera` と `FindType` は小さく複製した
   (このタスクのスコープ規則どおり)。
2. **画像への描画は `view:"game"` のときだけ。** JSON の x/y は常に
   Game View のレンダー解像度(`Handles.GetMainGameViewSize()`)基準だが、
   Scene View の撮影(`capture:"camera"` でも `"window"` でも)はカメラも
   ピクセル空間も全く別物なので、そこに同じ座標で枠線を描いても位置がずれる。
   したがって Scene View のときは一覧だけ返し、`drawnOnImage:false` と
   理由を `note` に書く。Game View 側でも「Game View のウィンドウ確保サイズ」と
   「設定されたレンダー解像度」が一致するとは限らないため、
   `UapUiAnnotationPolicy.ScaleMarks` で実際に撮影した画像のピクセル数へ
   線形スケーリングしてから焼き込む。
3. **オーバーレイ Canvas は画像に写らないことを明記する。** ブリーフに
   ある既知事実(`capture:"camera"` は `Camera.Render()` の出力で、
   Unity の Overlay 合成パスを経由しないため Screen Space - Overlay の
   内容を含まない)をそのまま結果の `note` に書いた:「一覧には載るが
   この画像には写らない、座標自体は `uap_game_pointer` に対して正しい」。
   Scene View がオーバーレイを画面表示するかどうかは未確認(下記)なので、
   そもそもそちらには描画しない設計にしてこの論点を避けた。
4. **`interactable` はコンポーネントの `interactable` プロパティを読む。**
   `Selectable` 系だけが持つ概念なので、それ以外(ハンドラのみ実装する
   カスタムコンポーネント)は既定で `true` とした(クリック自体は
   常に可能なため)。
5. **有効な Canvas の判定は「GameObject が `activeInHierarchy`」+
   「Canvas コンポーネント自身の `enabled`」の 2 段。** 前者は Canvas の
   GameObject が非アクティブな場合も自動的に拾う(Canvas は必ず祖先なので)。
   後者だけ `Canvas : Behaviour` へのキャストで読む -- 型自体への直接参照は
   避けつつ(`UapCanvasProbe` と同じ理由)、`Behaviour` はコア API なので
   キャストにリフレクションは要らない。
6. **並び順は「Canvas の `sortingOrder` の降順」→「同じ Canvas 内は
   階層順(祖先からの兄弟インデックス列)の昇順」。** レイキャストは
   最前面(`sortingOrder` が高い方)を優先して当たるため、重なった要素の
   中で実際にクリックが届く可能性が高いものを先頭に出す設計にした。
7. **ラベルはスプレッドシート列と同じ A, B, … Z, AA, AB, …** で数字は
   使わない(内蔵フォントも A-Z の 26 文字だけで足りる)。
8. **走査は全ロード済みシーンの `GetRootGameObjects()` を再帰**(uGUI が
   無ければ即座に警告付きで空リストを返す)。`UapQueryHierarchyTool` と
   同じ「訪問ノード数の上限」(20,000)を安全弁として持たせた。

### 却下した案

- **`Camera.WorldToViewportPoint` で投影する案**: Screen Space - Overlay の
  「ワールド座標」に対しては意味を持たない(上記 1 参照)ため却下。
  `uap_game_pointer` と同じ `RectTransformUtility.WorldToScreenPoint` 経路に統一。
- **座標の数値ラベルを描画する案(1, 2, 3…)**: `uap_game_pointer` の
  `target`(階層パス)を主経路として推す方針と噛み合わないうえ、
  数字ラベルはラベル文字列と紛らわしくなるため、A/B/C… の文字ラベルに統一した。
- **`ui_only` を独立引数にせず `annotate_ui` のみにする案**: バッチモードや
  ビュー未オープン環境でも一覧だけ取りたい需要(§ブリーフの「実際の使われ方」)
  を考えると、ビュー解決を完全にスキップできる専用フラグの方が素直と判断。
- **Scene View 用に別の座標系(Scene カメラ基準)で二重に座標を返す案**:
  実装・テストの複雑さに見合わないとみて見送り。JSON は常に
  `uap_game_pointer` 向けの Game View 座標一本に統一し、Scene View では
  「画像には描画しない」ことで正直に済ませた。

### 検証済みシグネチャ(UnityCsReference 2022.3 ブランチ / Unity-Technologies/uGUI
main ブランチのソースを直接確認)

- `Component.GetComponentInChildren(Type t)` / `(Type t, bool includeInactive)`
  -- `Runtime/Export/Scripting/Component.bindings.cs`
- `RectTransform.GetWorldCorners(Vector3[] fourCornersArray)`
  -- `Runtime/Transform/ScriptBindings/RectTransform.bindings.cs`
  (`Runtime/Transform` 配下 = 常時コンパイル対象。`Modules/UI` 配下の
  Canvas 系とは別物であることも確認した)
- `RectTransformUtility.WorldToScreenPoint(Camera cam, Vector3 worldPoint)`
  -- `Modules/UI/ScriptBindings/RectTransformUtility.cs`。`cam == null` のとき
  `new Vector2(worldPoint.x, worldPoint.y)` を返す分岐を直接確認(§設計判断 1 の根拠)。
- `Canvas : Behaviour`、`public extern int sortingOrder { get; set; }`、
  `RenderMode.ScreenSpaceOverlay == 0` -- `Modules/UI/ScriptBindings/UICanvas.bindings.cs`
- `Handles.GetMainGameViewSize()` -- `Editor/Mono/Handles.cs`
- `UnityEngine.UI.Selectable`(`namespace UnityEngine.UI`)の `public bool interactable`、
  および `Button` / `Toggle` / `Slider` / `Scrollbar` / `Dropdown` / `InputField` /
  `TMPro.TMP_InputField` / `TMPro.TMP_Dropdown` が全て `: Selectable` であること
  -- `Unity-Technologies/uGUI` リポジトリ `com.unity.ugui/Runtime/UGUI/UI/Core/*.cs`
  および `Runtime/TMP/TMP_InputField.cs` / `TMP_Dropdown.cs`
- `UnityEngine.UI.Text : MaskableGraphic` の `public virtual string text`
  -- 同リポジトリ `Runtime/UGUI/UI/Core/Text.cs`
- `TMPro.TMP_Text : MaskableGraphic`(abstract)の `public virtual string text`
  -- 同リポジトリ `Runtime/TMP/TMP_Text.cs`
- `UnityEngine.EventSystems.IPointerClickHandler` / `IDragHandler` / `IDropHandler`
  (いずれも `IEventSystemHandler` を継承するマーカーインタフェース、
  `namespace UnityEngine.EventSystems`) -- 同リポジトリ
  `Runtime/UGUI/EventSystem/EventInterfaces.cs`

com.unity.ugui は公開レジストリ(`packages.unity.com`)に 2022.3 系の
配布タルボールが見当たらず(`3.0.0-exp.*` しか無い)、代わりに
`Unity-Technologies/uGUI` の GitHub ソース(main ブランチ)を直接参照した。
基礎的な継承関係・インタフェース名はこの規模のリファクタで変わる可能性が
低いクラスなので、バージョン固定でない参照でも判断材料として十分と判断した。

### 未検証(実機 Editor / 実エージェントでの確認が必要)

- 実際に `com.unity.ugui`(+ TextMeshPro)が入ったプロジェクトで、
  Button / Toggle / Slider / Dropdown / InputField / Scrollbar と TMP 版、
  および独自の `IPointerClickHandler` 実装が `Collect()` で正しく拾われるか。
- `view:"game"` で撮影した PNG に、枠線とラベルが実際に狙った要素の上へ
  正しい位置・スケールで焼き込まれるか(`ScaleMarks` のレンダー解像度 ↔
  実撮影ピクセル数の変換を含む)。
- Screen Space - Camera / World Space Canvas 配下の要素が、実際の
  Game View 撮影画像の中で正しい位置に枠線が付くか(Overlay 以外)。
- Play Mode 中に `annotate_ui` を呼んだときの挙動(ドメインリロードは
  絡まないはずだが、実機未確認)。
- 5x7 フォントの可読性(手書きで作った簡易フォントで、実際の PNG 上での
  視認性は未確認)。

## 2.2 `uap_game_mouse`(uLoop `simulate-mouse-input` 相当)

### 何を

Play Mode で動いているゲームに、Input System の `Mouse.current` へ直接
`MouseState` を注入するツール。`action`:

- `click` / `long_press`(`duration` 秒): ボタン(left/right/middle)を
  `UapGameInputPolicy.MinHoldPolls`(2)ティック以上押し続けてから離す。
  `x`/`y` を渡すと先にカーソルをそこへ動かしてからクリックする(省略時は
  今のカーソル位置のまま)。
- `move`(`x`, `y` 必須): カーソルの絶対位置をジャンプさせる。
- `move_delta`(`dx`, `dy` 必須、`duration` 任意): `Mouse.current.delta` へ
  生の移動量を注入する。**カーソル位置(`position`)は動かさない** -- FPS
  視点操作のように `delta` だけを読むゲームコード向け。`duration > 0` の
  ときは `UapGameMousePolicy.DurationProgress` で実時間ティックに分割して
  注入する(SmoothDelta。一括で 1 イベントに詰め込むと `delta` の
  「1 フレーム内は加算、フレームをまたぐと自動 0 リセット」という
  Input System の仕様上、ゲーム側のフレームレートによっては 1 フレームに
  収まって不自然な瞬間移動に見えることがあるため)。
- `scroll`(`dx`, `dy` 必須): ホイールのスクロール量を注入する。

加えて `dry_run:true` は Play Mode も Input System も不要で、
`Camera.main.ScreenPointToRay` から 3D は `Physics.Raycast`、次に 2D は
`Physics2D.GetRayIntersection`(リフレクション)を試し、当たったものの
階層パス・座標・法線・距離を返す(何も当たらなければ `hit:false`)。座標を
実際に注入する前に「そこに何があるか」を確認する用途。

座標系は `x`/`y` とも `uap_game_pointer` と同じ、Game View の
render pixel でスクリーンショットと同じ左上原点・下向き正
(`normalized:true` で 0..1)。`move_delta` の `dy` も同じ向き(下向き正)
で揃え、デバイスの `delta`(下から上原点)へは
`UapGameMousePolicy.ToDeviceDelta` で符号だけ反転して渡す。

### なぜこの設計か

- **`uap_game_pointer` との住み分け**: `uap_game_pointer` は uGUI の
  `EventSystem` へ `pointerDown`/`Click`/`Drag` を直接ディスパッチする
  (実際のデバイスは触らない)。Canvas のボタン/スライダーはこちらが正解で、
  旧 Input Manager のプロジェクトでも動く。本ツールは実際の
  `Mouse.current` を動かすので、ゲームコードが `Mouse.current` を
  直接読む場合(独自のレイキャスト選択、FPS 視点)と、uGUI が一切絡まない
  3D/2D シーンへの「本物のマウスと同じ当たり方」をさせたい場合に使う。
  説明文にこの使い分けを明記した(ブリーフの必須要求)。
- **Pollable にした理由は `uap_game_key` と同じ**: Input System へ
  キューした `StateEvent` は次の input update まで反映されない。押下と
  解放を同じティックでキューすると `wasPressedThisFrame` の立ち上がりを
  ゲームが一度も観測できないため、`click` も(`duration` 0 でも)
  `MinHoldPolls` 分のティックを空ける。`UapGameKeyTool`/
  `UapGamePointerTool` と全く同じ Begin/Continue の 2 段構成
  (Holding → AwaitReadback → Done)を踏襲した。
- **`MouseState` は毎回フルスナップショット**: `InputSystem.QueueStateEvent`
  は構造体をまるごと上書きする(部分マージしない、com.unity.inputsystem
  1.7.0 の `InputSystem.cs` のドキュメントコメントで明記)。そのため
  `position`/`delta`/`scroll`/`buttons` を毎回全て詰めて送る必要があり、
  `uap_game_key` の `HeldKeys` 辞書と同じ発想で `HeldButtonsMask`
  (静的 `ushort`)を持たせて「このツールが押していると思っているボタン」
  を維持し、送信のたびに現在のカーソル位置(`Mouse.current.position` を
  毎回読み直す。キャッシュしない)と合わせて送る。位置をキャッシュしない
  のは、クリックやスクロールのように「動かさない」操作で古い位置に
  スナップしてしまう事故を避けるため。
- **`move_delta` は位置を動かさない**: `MouseState.delta` は
  `MouseState.position` と独立に持てる(`Pointer.delta` のドキュメント
  コメント: "activity on delta is not necessarily correlated with activity
  on position" -- ロックしたカーソルでも delta だけ動く FPS 視点操作が
  例として挙がっている)。よって `move_delta` は `position` フィールドに
  現在値をそのまま入れて据え置き、`delta` フィールドだけ書き換える。
- **`HeldButtonsMask` のビット位置はハードコードしない**: `MouseButton`
  enum(Left=0/Right=1/Middle=2、実パッケージソースで確認済み)を
  `Enum.Parse` で毎回解決してからビット位置に変換する
  (`UapInputSystemMouseAccess.ButtonBit`)。`UapGamePointerButton` の
  メンバ名が `MouseButton` と綴り一致することを前提にしている(こちらは
  この Core パッケージが定義する固定の enum なので安全)。将来パッケージが
  ビット割り当てを変えても壊れないようにする防御的な実装で、
  `UapInputSystemKeyboardAccess.ParseKey` が `Key` enum を
  `Enum.Parse` するのと同じ方針。
- **Play Mode 終了時のクリーンアップ**: `uap_game_key` と同じく
  `EditorApplication.playModeStateChanged` を初回使用時に遅延購読し
  (`[InitializeOnLoadMethod]` は使わない)、`ExitingPlayMode` で
  `HeldButtonsMask` を 0 にするだけ(デバイス自体が無くなるので、送信は
  しない)。

### 却下した案

- **`dry_run` の座標系を `Camera.pixelWidth/pixelHeight` にする案**:
  `uap_game_pointer`/`click`/`move` と座標系がずれるとエージェントが
  混乱すると判断し、他の全アクションと同じ `Handles.GetMainGameViewSize()`
  基準に統一した。単一カメラがフルスクリーンでレンダリングする一般的な
  構成では両者はほぼ一致するはずだが、複数カメラ/ビューポート分割構成では
  ずれ得る(§「実機で未確認」参照)。
- **`Physics2D` へのリフレクション用に別ファイルを作る案**: ブリーフの
  「Owns」欄が `UapGameMouseTool.cs` / `UapGameMousePolicy.cs` /
  `UapInputSystemMouseAccess.cs` / テストと明示していたため、2D
  レイキャストのリフレクションは新規ファイルを増やさず
  `UapGameMouseTool.cs` 内のプライベートヘルパーとして実装した
  (`UapInputSystemMouseAccess` は Input System 専用の名前なので
  Physics2D を混ぜない)。
- **`HeldButtonsMask` をボタンごとの `Dictionary` にする案**:
  `uap_game_key` の `down`/`up` のように複数ボタンを跨いで長時間保持する
  API はブリーフのアクション一覧に無い(`click`/`long_press` は必ず
  1 呼び出しで完結する)ため、ビットマスク 1 個で十分と判断した。

### 検証したシグネチャ

- `com.unity.inputsystem` 1.7.0(`download.packages.unity.com` から取得、
  `/tmp/claude-0/pkgsrc/inputsystem-1.7.0/package/InputSystem/Devices/
  Mouse.cs`): `MouseState`(`position`/`delta`/`scroll`: `Vector2`、
  `buttons`: `ushort`、`WithButton(MouseButton, bool)`)、
  `MouseButton`(`Left=0, Right=1, Middle=2, Forward=3, Back=4`)、
  `Mouse.current`(`Pointer.current` を `new` で隠すが同名の静的プロパティ
  が `Mouse` 自身にも直接宣言されているため
  `Type.GetProperty("current", Public|Static)`(`FlattenHierarchy` 無し)
  は曖昧にならず `uap_game_key` の `Keyboard.current` と同じ取得コードが
  そのまま使える)。
- `InputSystem.cs` 同バージョン: `QueueStateEvent<TState>(InputDevice,
  TState, double time=-1) where TState : struct, IInputStateTypeInfo`
  (`uap_game_key` の `FindQueueStateEvent` と同じシグネチャ検索で拾える)、
  および `QueueStateEvent`/`QueueDeltaStateEvent` のドキュメントコメントに
  ある「フル state は毎回上書き、delta はフレーム内加算・フレームをまたぐと
  自動リセット」という仕様。
- `UnityCsReference` 2022.3(`Unity-Technologies/UnityCsReference` を
  clone): `Physics.Raycast(Ray, out RaycastHit, float, int)`(
  `Modules/Physics/ScriptBindings/Dynamics.bindings.cs`)、`RaycastHit`
  の `point`/`normal`/`distance`/`transform`/`collider`。
  `Physics2D.GetRayIntersection(Ray, float, int) : RaycastHit2D`
  (`Modules/Physics2D/ScriptBindings/Physics2D.bindings.cs`)、
  `RaycastHit2D` の `point`/`normal`/`distance`/`collider`(`collider` が
  null なら外れ -- 値型なので `Invoke` の戻りそのものは非 null)。
  `Camera.main`(`Runtime/Export/Camera/Camera.bindings.cs`)、
  `Camera.ScreenPointToRay(Vector3)`。
- Core が既に 3D `Physics`(`RaycastHit` 型)を直接参照していること
  (`Editor/Ops/Markers/SceneMeshRaycaster.cs`)を確認した上で、
  `Physics2D` はどこにも直接参照が無いことも確認(`grep`)。CI の両
  ホストプロジェクト(`ci/HostProject`, `ci/HostProjectCoreOnly`)の
  `manifest.json` は `com.unity.modules.physics`/`physics2d` を両方含む
  ため、CI 自体は 2D リフレクション経路を「モジュール有りの成功パス」
  でしか検証できない(モジュール無しパスは経路として存在を確認したのみ)。

### 実機で未確認

- `InputSystem.QueueStateEvent<MouseState>` を実際の Editor で呼んだときに
  `Mouse.current` が正しく更新され、ゲーム側の `wasPressedThisFrame` /
  `delta` が期待通り観測できるか(ソースコード上の契約からの推論であり、
  `uap_game_key` のキーボード版と同じ検証状態)。
- `move_delta` の `duration` 分割(SmoothDelta)が、複数ティックに跨る
  `delta` 注入としてゲーム側に「なめらかな動き」に見えるか。理論上は
  Input System の「フレーム内加算・フレームをまたいで自動リセット」
  仕様どおりに働くはずだが、実機での見え方は未検証。
- `dry_run` の `Handles.GetMainGameViewSize()` 基準の座標と
  `Camera.main.ScreenPointToRay` が要求する `Camera.pixelWidth/
  pixelHeight` 基準の座標が、単一カメラ以外の構成(複数カメラ、
  ビューポート分割、UI Toolkit の Panel カメラなど)でどれだけずれるか。
- バッチモードの EditMode テストで `UnityEditor.Handles.
  GetMainGameViewSize()` と `UnityEngine.Camera.main` が実際にどんな値を
  返すか(前者は 0 でない既定解像度を返すはず、後者は空シーンでは
  null になるはずという前提でテストを書いたが、CI 実行結果は未確認)。

## 2.3 `uap_compile`(uLoop `compile` 相当)

### 何を作ったか

`Editor/Ops/UapCompileTool.cs`(IUapTool、module "core")。`action: request`
(既定) / `status` の 2 択。

- `request`: `CompilationPipeline.RequestScriptCompilation()`(`clean:true` なら
  `RequestScriptCompilationOptions.CleanBuildCache`)を**応答の後に**予約して
  即座にリクエスト ID を返す。理由は `uap_play_mode` の「応答を先に書く」作法と
  同じ: 実際に変更を伴うコンパイルはドメインリロードを起こし、このリクエスト
  自身の応答を書いている HTTP ワーカーごと道連れにする。Play モード中、また
  既にこのツールが追跡しているリクエストが進行中(`Requested`/`Compiling`)
  なら拒否し、理由を返す。
- `status`: 直近/進行中の結果を報告する。**このツールから一度もリクエストして
  いなくても動作**し、その場合は `EditorUtility.scriptCompilationFailed` の
  ライブ値と「まだ何も知らない」旨を返す。

結果は `CompilationPipeline.compilationStarted` /
`assemblyCompilationFinished` / `compilationFinished` を購読して集める。
**購読はリクエストが pending(`Requested`/`Compiling`)の間だけ**行い、
`[InitializeOnLoadMethod]` からは一切登録しない(ハード ルール準拠)。
`compilationFinished` 側での `SessionState` への書き込みは同期的に行う
(`ConsoleErrorProvider.OnCompilationFinished` と同じ理由: コンパイル完了から
ドメインリロード開始まで `EditorApplication.update` が挟まらないことがある
ため)。

状態機械(idle/requested/compiling/succeeded/failed)と JSON 形状は
Unity 非依存の `Editor/Ops/UapCompilePolicy.cs` に切り出し、ライセンス不要の
smoke gate(`ci/SmokeTests`)でも実行されるようにした。`SessionState` の
読み書きだけを `Editor/Ops/UapCompileResultStore.cs`(1 本の JSON 文字列
キー)に分離した。

### なぜこの設計か

- **`action` の既定が `request`**: このパネルの他の action 付きツール
  (`uap_play_mode`/`uap_game_view_size`)は既定が読み取り系(`status`/`get`)
  だが、`uap_compile` は「確認しに行く」ことそのものが目的のツールなので、
  既定を読み取りにすると「まず状態を聞いて、それから改めてリクエストする」
  という無駄な 1 往復を全呼び出しに強制することになる。ロードマップの
  ブリーフでも明示されている挙動。
- **`DecideRequest` の 3 分岐**(Play モード中 / ライブでコンパイル中 /
  `Requested` のまま): `compilingLive`(`EditorApplication.isCompiling` の
  ライブ値)を「本当にコンパイル中か」の一次情報源として信頼し、永続化された
  `Compiling` 状態がライブでは偽(エディタ再起動などで孤立したレコード)の
  場合は **Proceed に落ちる**(自己修復)。一方 `Requested`(このツールが
  スケジュールしたがまだ Unity 側でコンパイルが始まっていない短い窓)は
  ライブフラグが立つ前でも拒否する ―― 二重にスケジュールしてしまうのを
  防ぐため。孤立した `Compiling` を放置すると次のリクエストが永遠に拒否され
  続けるという壊れ方を避けるための設計判断。
- **`ConsoleErrorProvider` を再利用しなかった**: 同じ `CompilationPipeline`
  イベントを既に購読しているが、(a) エラーのみを保持し警告を捨てる、
  (b) セッション全体でメッセージを重複排除する(1 回のコンパイル実行に
  スコープしない)、(c) `file:line` を 1 本の `Location` 文字列に潰していて
  列(column)を持たない ―― という 3 点が `uap_compile` の要件(file/line/
  column/message/type をメッセージ単位で保持し、リクエスト単位でスコープ
  する)と根本的に噛み合わない。`ConsoleErrorProvider` は既に非常に大きく
  ドキュメント過多なファイルで、そこに 2 つ目の非互換な消費者を足すより、
  このパネルの他のツール(`ConsoleErrorProvider` 自身、
  `UapScriptsCommitTool` の `AssemblyBuilder.buildFinished`)がそれぞれ
  自分専用の購読を持つのと同じパターンに従い、`UapCompileTool` 専用の
  購読を新設した。
- **`SessionStateBridge` に新フィールドを足さなかった**: そちらは複数の
  並行作業が触る共有ファイルで、`uap_compile` の結果は「唯一の読み手を持つ
  1 本の自己完結 JSON」なので、専用の `UapCompileResultStore` を新設する方が
  マージ衝突のリスクが小さい。
- **アクセス範囲**: `Editor/Integration/ConsoleErrorProvider.cs` /
  `ReloadLifecycle.cs` / `Model/SessionStateBridge.cs` /
  `Ops/ScriptCommitPipeline.cs` / `UapScriptsCommitTool.cs` /
  `UapPlayModeTool.cs` は指示どおり読み取りのみとし、実際に編集は不要
  だった(上記の理由で "既存の compile-message source の再利用" は
  `CompilationPipeline` イベント自体への直接購読という形で満たした)。

### 却下した案

- `ConsoleErrorProvider` の `Entry`/バッファをそのまま `uap_compile` の
  データソースにする案: warnings を落とす、column が無い、セッション全体で
  重複排除される、という 3 点が要件と合わず、`ConsoleErrorProvider.cs` への
  非小さな追加編集が必要になる。ブリーフの「最小限の追加編集で済むなら
  reuse」の条件に当てはまらないと判断し、既存パターン(ツールごとに専用
  購読)を選んだ。
- `SessionStateBridge` にフィールドを追加する案: 共有ファイルの並行編集
  リスクを避けるため専用ストアに分離。
- `IUapPollableTool` にする案: `uap_scripts_commit` は
  `AssemblyBuilder` の完了を**同じ呼び出し内で** busy-poll する必要がある
  ため Pollable だが、`uap_compile` は逆に「応答をすぐ返して、実際の
  コンパイルは応答の後に始まる」設計なので Pollable ではなく
  `uap_play_mode` と同じ「応答後にスケジュール」方式にした。

### 検証したシグネチャ

UnityCsReference の 2022.3 ブランチを直接 fetch して確認:
- `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()` /
  `RequestScriptCompilation(RequestScriptCompilationOptions)`、
  `RequestScriptCompilationOptions { None, CleanBuildCache }`
  (`Editor/Mono/Scripting/ScriptCompilation/CompilationPipeline.cs`)。
- `UnityEditor.Compilation.CompilerMessage { message, file, line, column, type }`、
  `CompilerMessageType { Error, Warning, Info }`
  (`Editor/Mono/Scripting/ScriptCompilation/CompilerMessage.cs`)。
- `CompilationPipeline.compilationStarted`/`compilationFinished`
  (`event Action<object>`)、`assemblyCompilationFinished`
  (`event Action<string, CompilerMessage[]>`)(同ファイル)。
- `UnityEditor.EditorUtility.scriptCompilationFailed`(static bool, get のみ、
  `Editor/Mono/EditorUtility.bindings.cs`)。
- `UnityEditor.EditorApplication.isCompiling`(static bool,
  `Editor/Mono/EditorApplication.bindings.cs`)。

既存の本パッケージ本番コードで既に使われていることから確認済みとした
シグネチャ: `EditorApplication.isPlayingOrWillChangePlaymode` /
`EditorApplication.update` / `EditorApplication.CallbackFunction` /
`EditorApplication.timeSinceStartup`(`UapPlayModeTool.cs`)、
`UnityEditor.SessionState.GetString`/`SetString`(`SessionStateBridge.cs`)。

### 未検証(実機 Unity Editor が必要)

- 実際の Unity Editor 上でコンパイル→ドメインリロードの一連の流れを通して
  `uap_compile` を動かしたことは無い(このタスクに Unity Editor が無い)。
- 「変更が無い状態で `RequestScriptCompilation()` を呼ぶと
  `compilationStarted`/`Finished` が一切発火しない」という前提
  (`Requested` のまま止まるケースの根拠)は Unity の一般的な挙動として
  想定しているが、このタスクでは直接確認していない。
- 2022.3 ブランチのみ照合し、Unity 6(6000.x)ブランチとの API 差分は
  照合していない(該当 API はいずれも長期間安定した公開 API で、本パッケージ
  の既存コードも同じ API を両フロアで使っている実績はあるが、6000.x の
  UnityCsReference は今回 fetch していない)。
- `Tests/Editor/UapCompileToolTests.cs` は実際に NUnit/Unity Test Runner 上で
  走らせていない(Unity Editor が無いため)。

## 2.4 `uap_project_info`(uLoop `get-version` + `get-project-info`)

### 何を

Unity バージョン(`Application.unityVersion`)、製品名/会社名/バージョン
(`PlayerSettings.productName` / `companyName` / `bundleVersion`)、アクティブ
ビルドターゲットとスクリプティングバックエンド
(`EditorUserBuildSettings.activeBuildTarget` +
`PlayerSettings.GetScriptingBackend(BuildTargetGroup)`)、Active Input
Handling(旧 Input Manager / 新 Input System / 両方)、Enter Play Mode
Settings(ドメイン/シーンリロードの有無、`EditorSettings.
enterPlayModeOptionsEnabled` / `enterPlayModeOptions`)、Run In Background
(`PlayerSettings.runInBackground`)、Color Space
(`PlayerSettings.colorSpace`)、そしてこのパネルの挙動を左右するパッケージ
(`com.unity.ugui` / `com.unity.inputsystem` / `com.unity.test-framework` /
`com.unity.textmeshpro` / uLoop / 本パネルの拡張パッケージ)の導入有無・バージョンを
1 回で返す読み取り専用ツール。`uap_ping` 同様 ReadOnly=true(自動承認可):
呼び出す API はすべて getter か読み取り専用の `File.ReadAllText` で、
書き込みは一切ない。

各フラグには「なぜ気にする必要があるか」を 1 行添える(`implications`
配列)。例: Active Input Handling が「Input System Package (New)」または
「Both」でなければ `uap_game_key` / `uap_game_mouse` は動かない、
`com.unity.ugui` が無ければ `uap_game_pointer` は動かない、
Run In Background が OFF なら `uap_editor_focus` を先に呼ぶ必要がある、
Enter Play Mode Settings でドメインリロードが無効なら
`uap_play_mode` の start/stop はシーンをリロードしても static フィールド
はリロードしない。

### なぜ

エージェントは「このツールが動くか」を毎回試行錯誤で確認するのではなく、
セッション冒頭で一度この情報を取得しておける。とくに Active Input
Handling は uLoop の `simulate-keyboard`/`simulate-mouse-input` と同様、
本パネルの `uap_game_key`/`uap_game_mouse` が機能するかどうかを直接左右
する前提条件であり、失敗してから原因を推測させるより先に渡す方が安い。

### 設計判断

- **Active Input Handling はテキストスキャン。** `PlayerSettings` には
  この値を読む public API が無い(Player Settings ウィンドウはネイティブ
  経由でしか読み書きしない)。`ProjectSettings/ProjectSettings.asset` の
  YAML に `activeInputHandler: N`(0=Old, 1=New, 2=Both)という行が
  直接書かれているため、`UapProjectInfoPolicy.ParseActiveInputHandler`
  で正規表現スキャンする。他の全フィールドは対応する public getter が
  あるため、そちらをそのまま使う(2 系統の情報源を持つ理由がない)。
  行末アンカーは付けない(CRLF の `\r` や行末コメントで不一致になる
  実害の方が、別の行の数字を誤って拾う実害より大きいと判断した)。
- **パッケージ解決は manifest.json より packages-lock.json を優先。**
  `manifest.json` の宣言はバージョン範囲・`file:` パス・git URL のことが
  あり、実際に解決されたバージョンではない。`packages-lock.json` の
  `dependencies[id].version` が解決済みバージョンなので、全候補 id を
  ロックファイルで先に探してから manifest にフォールバックする。
- **uLoop は 2 つの id を候補に持つ。** `io.github.hatayama.uloopmcp` は
  `unity-cli-loop` へのリネームで `io.github.hatayama.unitycliloop` にも
  なりうる(`UloopDetector.KnownPackageIdList()` と同じ組)。
  `UapProjectInfoPolicy.TrackedPackage` は「報告する id」と「候補 id の
  配列」を分けて持ち、リネーム後の id で見つかった場合は結果に
  `resolvedAs` を添える。`UloopDetector` を直接呼ぶのではなく候補 2 件を
  複製した(このファイルを Unity 非依存に保ち、smoke テストに追加の
  依存ファイルを要求しないため)。
- **却下した案**: PlayerSettings のリフレクションで内部フィールドを
  直接読む案は却下。ネイティブ実装依存で Unity バージョンごとに壊れる
  リスクがテキストスキャンより高い。

### 検証したシグネチャ

`Application.unityVersion` / `PlayerSettings.productName` /
`companyName` / `bundleVersion` / `runInBackground` / `colorSpace` /
`GetScriptingBackend(BuildTargetGroup)` /
`EditorUserBuildSettings.activeBuildTarget` /
`BuildPipeline.GetBuildTargetGroup(BuildTarget)` /
`BuildTargetGroup.Unknown == 0` /
`EditorSettings.enterPlayModeOptionsEnabled` / `enterPlayModeOptions` /
`enum EnterPlayModeOptions` の 3 フラグは、いずれも
UnityCsReference の 2022.3 ブランチ(Editor/Mono/PlayerSettings.
bindings.cs, Editor/Mono/EditorUserBuildSettings.bindings.cs,
Editor/Mono/BuildPipeline.bindings.cs, Editor/Mono/EditorSettings.
bindings.cs, Editor/Mono/BuildTargetGroup.cs)から取得して確認した。
PlayerSettings/EditorSettings 側は 6000.0 ブランチでも同一シグネチャ
であることを確認済み。`activeInputHandler: N` という YAML キー名と
値の対応(0/1/2)は実プロジェクトの `ProjectSettings.asset` 実物
(GitHub 上の公開リポジトリ)から確認した。

### 未検証

実際の Unity Editor(2022.3.22f1 / Unity 6)でのバッチモード EditMode
テスト実行と、`uap_project_info` を実プロジェクトで呼んだときの
`packages-lock.json` の実フォーマット揺れ(古い Unity バージョンでの
フィールド名差異など)は、この環境に Unity が無いため確認できていない。

---

## 2.5 `uap_editor_focus`(uLoop `focus-window`)

### 何を

Unity Editor を前面に出す ReadOnly=false のツール。2 段構成:

1. **エディタ内フォーカス**: `EditorWindow.focusedWindow` があれば
   `.Focus()` を呼ぶ(このパネル内でどのペインがアクティブかを整える。
   ドッキングされた別ウィンドウへの実質的な効果は薄いが無害)。
2. **OS レベルの前面化**(本命): Windows は user32.dll の
   `SetForegroundWindow` / `ShowWindow` を `Process.GetCurrentProcess().
   MainWindowHandle` に対して P/Invoke で直接呼ぶ。macOS は
   `osascript -e 'tell application "System Events" to set frontmost of
   first process whose unix id is <pid> to true'`。Linux は `wmctrl -i -a
   <id>`(`wmctrl -l` の出力からプロジェクト名(`PlayerSettings.
   productName`)を含むタイトル行の id を探して使う)。`wmctrl` が PATH
   に無ければ「未対応」と正直に返す。子プロセスは全て ~2 秒でタイムアウト
   して kill する。

`uap_play_mode` の「Run In Background が OFF だとタイムアウトすることが
ある」という既存の警告に対する**直接の対処ツール**として設計した(この
ツール自体が `docs/USER-GUIDE.md` のその警告文に追記される想定 --
USER-GUIDE.md はこのタスクの対象外なので `user_guide_text` として
別途渡す)。

### なぜ ReadOnly=false か

プロジェクト・シーン・アセットは一切変更しないが、ユーザーが今まさに
別のウィンドウ(ターミナル、ブラウザ)に入力していたところからキーボード
フォーカスを奪う、という現実に見える副作用があるため。`uap_console_clear`
や `uap_game_key` と同じ理由付けで、この種の「取り消せないが破壊的でも
ない」副作用は ReadOnly の自動承認から外す。

### 設計判断

- **Windows: `AttachThreadInput` を使う。** `SetForegroundWindow` は
  「呼び出し元が既にフォアグラウンドプロセスでない限り」失敗する
  foreground-lock ヒューリスティックが Windows にあるため、素朴に呼ぶ
  だけではタスクバーが点滅するだけで前面化しないことがある(Windows 7
  以降でよく知られたコミュニティパターン)。現在フォアグラウンドの
  ウィンドウを持つスレッドの id を `GetWindowThreadProcessId` で取得し、
  `AttachThreadInput` で自スレッドの入力キューを一時的にアタッチしてから
  `SetForegroundWindow` を呼び、`finally` で必ずデタッチする。
  `AllowSetForegroundWindow` は**ここでは使えない**: そのAPI は「pid に
  許可を与える」もので、呼べるのは今フォアグラウンドを持っている側
  (=ユーザーが直前まで使っていた別アプリ)だけであり、こちらからは
  呼びようがない。コード内コメントで明記した。
- **macOS: pid 指定。** タイトルではなく `unix id is <pid>` で対象を
  特定するため、Unity Editor のウィンドウが複数開いていても曖昧さが
  ない。
- **Linux: id 指定(`-i -a`)であってタイトル一致(`-a TITLE`)では
  ない。** 単純な `wmctrl -a TITLE` は "Unity Hub" のような無関係な
  ウィンドウも拾いうるため、`wmctrl -l` の出力を自前でパースして id を
  確定してから `-i -a <id>` で活性化する。id 抽出の正規表現
  (`UapEditorFocusPolicy.FindWindowId`)は Unity 非依存の Policy に
  置き、smoke テストと EditMode テストの両方でピン留めした。
- **Linux の PATH 解決はシェル任せ。** `.NET` の `Process.Start` 自身に
  bare コマンド名を探させず(`ClipboardImageReader` の xclip/wl-paste
  helper と同じ理由)、`/bin/sh -c "if command -v wmctrl ...; then
  wmctrl ...; else exit 3; fi"` という形にラップし、終了コード 3 を
  「wmctrl が PATH に無い」の専用シグナルとして予約した
  (`UapEditorFocusPolicy.LinuxWmctrlMissingExitCode`)。
- **子プロセスは WaitForExit を先に呼ぶ。** 先に stdout を
  `ReadToEnd()` してしまうと、子プロセスが出力をフラッシュしない/
  ハングした場合にタイムアウトへ辿り着く前にブロックしうる
  (`ClipboardImageReader.Read` の既存順序とは意図的に変えた)。ここでの
  出力はどれも小さい(1 行の osascript 応答、数十行の `wmctrl -l`)ため、
  パイプが詰まってこの順序自体がデッドロックする心配はない。
- **引数のクオートは Win32/CRT 規則を独自実装。** このリポジトリの
  `Arguments`(文字列 1 本)方式(`ArgumentList` は使わない -- リポジトリ
  内に前例が無く、.NET Framework 系プロファイルでの可用性が不確実な
  ため)を踏襲し、`AgentClient.QuoteArg` と同じ CommandLineToArgvW 規則
  (バックスラッシュの連続数によるエスケープ)を Policy 内に独立実装した
  (`AgentClient` は private メソッドで呼べず、Integration 層への依存も
  避けたかったため)。

### 検証したシグネチャ

`EditorWindow.focusedWindow`(静的 get)と `EditorWindow.Focus()`
(インスタンスメソッド)は UnityCsReference の 2022.3 と 6000.0 の両方
(`Editor/Mono/EditorWindow.cs`)で確認した。`Application.platform` +
`RuntimePlatform.WindowsEditor/OSXEditor/LinuxEditor` はこのリポジトリ
内の複数の既存箇所(`ClipboardImageReader.cs`, `AgentHub.cs`,
`GateHookInstaller.cs` など)と同じ使い方であることを確認した。
Win32 API(`SetForegroundWindow` / `ShowWindow` / `IsIconic` /
`GetForegroundWindow` / `GetWindowThreadProcessId` / `AttachThreadInput`
/ `GetCurrentThreadId`)はこのリポジトリにも UnityCsReference にも前例が
無いため、Microsoft Learn の `ShowWindow`(`SW_RESTORE = 9`)ドキュメント
と、`AttachThreadInput` + `SetForegroundWindow` の定番パターンを扱う
複数の技術記事(Rick Strahl の WPF ウィンドウアクティベーション記事、
shlomio.wordpress.com の関連記事)で P/Invoke シグネチャと手順を
クロスチェックした。`System.ComponentModel.Win32Exception` が
`Process.Start` の「実行ファイルが見つからない」失敗で投げられることは
.NET の標準的な文書化された挙動。

### 未検証

このタスクの実行環境には Unity Editor も Windows/macOS/Linux の GUI
セッションも無いため、次はすべて**実機での確認が必要**:
Windows での `AttachThreadInput` + `SetForegroundWindow` の実際の成否、
macOS の `osascript ... unix id is <pid>` によるフォーカス移動、
Linux 各ウィンドウマネージャ(GNOME/KDE/Xfce 等)での `wmctrl -l` の
出力フォーマットの揺れとタイトルマッチの実際の的中率、
`Process.GetCurrentProcess().MainWindowHandle` が実際の Unity Editor
メインウィンドウを取りこぼさずに返すかどうか(マルチウィンドウ
レイアウト、フローティングウィンドウ構成時)。

## 2.6 CI ホストプロジェクトの拡張

- `ci/HostProjectCoreOnly/Packages/manifest.json` に `com.unity.ugui: "1.0.0"` と
  `com.unity.inputsystem: "1.19.0"` を追加し、`UapUguiEventAccess.TryLoad` /
  `UapInputSystemKeyboardAccess.TryLoad` の **「パッケージが入っている」側の分岐**が
  CI のどこかで走るようにした。`ci/HostProject` 側には追加していない
  (CI 予算(`docs/design-notes/2026-09-18-ci-budget.md`)から第 3 の
  ホストプロジェクト変種は作れない): 既存の 2 プロジェクト構成のまま、
  `ci/HostProject` が「無い」側、`ci/HostProjectCoreOnly` が「ある」側を
  受け持つ形に役割分担した。`com.unity.inputsystem` は 1.19.0(レジストリの
  `unity` フィールドは最小版 `2022.3`)を選び、2022.3.22f1 / 6000.0.83f1 /
  6000.3.24f1 / 6000.5.11f1 のどれでも同一版のまま解決できることを、
  実際にパッケージの tarball を取得して `package.json` を確認した
  (`download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-1.19.0.tgz`)。
- `com.unity.ugui` は 1.0.0/2.0.0 とも `download.packages.unity.com` から
  tarball を取得できない(404)。これはレジストリ API
  (`packages.unity.com/com.unity.ugui`)が返す `versions` にもこの 2 つが
  含まれないことと合わせ、**built-in package**(エディタ本体に同梱され、
  外部配布されない版)である傍証。公開されている変更履歴
  (`docs.unity3d.com/Packages/com.unity.ugui@1.0` の
  `[1.0.0] - 2019-01-08 "first release of Unity UI as a built in package"`、
  `@2.0` の `[2.0.0] - 2023-03-08 "Merge of the com.unity.textmeshpro package"`)
  から、1.0.0 が 2022.3 系の同梱版、2.0.0(TextMeshPro 統合)が Unity 6 系の
  同梱版という整理に一致する。Unity の Package Manager は「manifest が
  built-in package の版違いを指定しても、エディタ同梱の版に差し替えて
  解決を壊さない」という趣旨のドキュメントがあり、1.0.0 のまま Unity 6 系を
  走らせても恐らく暗黙の版差し替えで解決はする。ただしそれに頼ると
  「マニフェストの版とエディタの版が食い違ったまま黙って動く」状態になり、
  このジョブが「各 Unity 系統が実際に出荷する版を検証している」という
  前提が崩れるため、明示的に揃える設計を選んだ:
  `.github/workflows/editmode-tests.yml` の Core-only 実行直前に
  `matrix.unity != '2022.3.22f1'` のときだけ `jq` で
  `ci/HostProjectCoreOnly/Packages/manifest.json` の `com.unity.ugui` を
  `"2.0.0"` に書き換えるステップを追加した(コミットされているファイルは
  常に 1.0.0 のまま。`ci/stamp-unity-version.sh` が `ProjectVersion.txt` を
  非コミットで書き換えるのと同じ作法)。`com.unity.inputsystem` は
  1 版で両系統をカバーできるため書き換えの対象にしていない。
  Docker イメージ側が実際にその同梱版を持っているかは、イメージを直接
  検証できていないため未確認(§「未検証」参照)。
- `ProjectSettings` の `activeInputHandler` は変更していない(既存は `0` =
  旧 Input Manager のみ)。`UapInputSystemKeyboardAccess.TryLoad` /
  `ParseKey` はアセンブリが読み込まれていれば型解決だけで完結し、
  `Keyboard.current` の実デバイス生成(これが `activeInputHandler` に左右
  される)には踏み込まない。新設テストもこの 2 つしか呼ばないため、
  変更の必要はないと判断した。変更すれば `uap_game_key` の
  `CurrentKeyboard()` 経路(`Keyboard.current` が実際にデバイスを返すか)
  まで CI が確認できるようになるが、それはこの項目のスコープ外で、
  ホストプロジェクト全体の入力設定を変える副作用(旧 `Input` クラスに
  依存する既存コードへの影響)を避けるため見送った。
- `ci/make-core-only-host.sh` のドリフトガード(Core-only の manifest が
  HostProject の manifest から拡張パッケージの行を引いたものと一致する
  ことを確認する)を、`com.unity.ugui` / `com.unity.inputsystem` の 2 行を
  Core-only 側だけの正当な差分として許容するように拡張した。加えて、
  Core-only に両パッケージが実在すること、HostProject 側に紛れ込んで
  いないことを別途明示チェックする行を足した。このファイルはブリーフの
  「Owns」欄には無いが、変更しないと本項目の変更そのものがこのガードで
  毎回 CI を落とす(意図的な非対称を「ドリフト」として検出してしまう)ため、
  やむを得ず最小限の変更をした。この Phase でこのスクリプトに触れる
  項目は他に無いはずで、コンフリクトは想定していない。
- `editmode-tests.yml` の `Cache Library (Core only)` ステップのキャッシュ
  キーが `ci/HostProject/Packages/manifest.json` をハッシュしていた
  (Core-only 自身の manifest ではなく HostProject のものを見ていた)バグを
  修正し、`ci/HostProjectCoreOnly/Packages/manifest.json` をハッシュする
  ようにした。このバグがあると、今回のように Core-only だけに依存を
  追加してもキャッシュキーが変わらず、古い(ugui/inputsystem 抜きの)
  `Library/` がヒットし続けて新しいパッケージがインポートされない
  おそれがあった。
- `push` / `pull_request` トリガーの `paths` フィルタに
  `ci/HostProjectCoreOnly/**` を追加した(既存は `ci/HostProject/**` の
  みで、Core-only 側だけの変更(今回の manifest 変更を含む)が単独では
  ワークフローを起動しない抜けだったため)。
- 新規テスト `Tests/Editor/UapOptionalPackagePresenceTests.cs` は
  「パッケージが入っている」側の 3 点を確認する: (1)
  `UapInputSystemKeyboardAccess.ParseKey("Space")` が非 null であること、
  (2) `UapGameKeyPolicy.KnownKeys`(手書きの `Key` 列挙子ミラー)の全項目が
  実際の `Key` 列挙子に対して解決できること(1.19.0 の `Keyboard.cs` を
  取得して全件突き合わせ、欠落は無かった)、(3)
  `UapUguiEventAccess.GetHandler` / `CreatePointerData` が、その場で作った
  `EventSystem` + `Button` の GameObject に対して正しく動くこと
  (`ExecuteEvents.GetEventHandler<IPointerClickHandler>` が Button 自身と、
  ハンドラを持たない子からの呼び出しの両方で正しいオブジェクトを返す
  ことまで確認)。いずれも `TryLoad` が偽を返せば `Assert.Ignore` して
  自己スキップするため、パッケージを持たないプロジェクト(開発者の
  サンドボックスや、当面の `ci/HostProject` 自身)でこのテストが失敗する
  ことはない。(4) パッケージ不在時のメッセージが両方とも
  パッケージ名を含み続けることの回帰ガードも追加した。
- `UapUguiEventAccess.cs` / `UapInputSystemKeyboardAccess.cs` /
  `UapGameKeyPolicy.cs` は指示どおり変更していない。読んだ範囲で
  明確なバグは見つからなかった(`UapGameKeyPolicy.KnownKeys` は
  1.19.0 の `Key` 列挙子と完全一致)。`UapInputSystemKeyboardAccess.ParseKey`
  は `TryLoad` を自分では呼ばず `_keyType` 他のフィールドが埋まっている
  前提で動く作りだが、現状の唯一の呼び出し元
  (`UapGameKeyTool.Begin`)は必ず先に `CurrentKeyboard`(内部で
  `TryLoad` を呼ぶ)を経由しているため、実害のあるバグではなく、
  設計上のわずかな結合(呼び出し順序への暗黙の依存)として留めておく。
  新設テストはこの結合を壊さないよう、`ParseKey` を呼ぶ前に必ず
  `TryLoad` を呼んでいる。
- 既存テスト 3 本(`UapGameKeyToolTests.InputSystemAccess_
  LoadsOrNamesThePackage`、`UapGamePointerToolTests.UguiAccess_
  LoadsOrNamesThePackage`、`UapUguiElementCatalogTests.Collect_
  LoadsOrNamesThePackage`)と `UapUguiElementCatalogTests` 冒頭の
  コメント("Neither CI host project installs com.unity.ugui")は、
  この変更後は `ci/HostProjectCoreOnly` について正確でなくなる
  (`ci/HostProject` については引き続き正確)。これらのファイルは
  この項目の Owns 欄に無く、他項目(2.1〜2.5)が触れているため、
  意図的に変更していない。フォローアップとして各所有者側でのコメント
  更新を推奨する。

## 2.7 stdio MCP 中継

### 何を作ったか

`AcpProtocolBridge.BuildMcpServers` は今まで UapOps を `{type:"http", url, headers}`
一本槍で渡しており、`agentCapabilities.mcpCapabilities.http` を名乗らない ACP
エージェントには何も渡らず、ユーザーには接続直後のログ 1 行しか見えなかった
(§1.2 の「実際に不都合が出る点」)。今回追加したのは:

1. **中継プログラム本体**(`jp.colloid.unity-agent-panel/Editor/Tools~/
   UapMcpStdioRelay/RelayCore.cs` + `RelayMain.cs`)。`Tools~` サフィックスの
   フォルダは Unity の AssetDatabase / コンパイラから完全に不可視なので、
   この 2 ファイルは本パッケージの asmdef に一切含まれない。`RelayCore.cs`
   は Unity 非依存の純粋なクラス(行フレーミング、リクエスト転送、SSE
   `data:` 行の結合、`Mcp-Session-Id` の学習と引き継ぎ、202 応答時に何も
   書かない処理、`ThreadPool` による同時実行 + `stdout` 書き込みの直列化、
   EOF 後の猶予付きシャットダウン)で、`ci/SmokeTests`(net8、
   LangVersion 9)にもそのままコンパイルされる。`RelayMain.cs` は
   `HttpClient` を使う実際の `Main` エントリポイントで、Bearer トークンは
   コマンドライン引数ではなく環境変数 `UAP_MCP_TOKEN` からのみ読む
   (`UapOpsMcpConfig` が `--mcp-config` を「インラインでなくファイルに
   書く」のと同じ SEC-2 の理由: 子プロセスの argv はどの環境でも
   `/proc/*/cmdline` 等から同一ユーザーの他プロセスに丸見えだが、
   自分の子に渡す環境変数はそうではない)。
2. **RelayCore はあえて JSON-RPC を解釈しない。** 受け取った行が壊れていても
   そのまま HTTP へフォワードするだけの「愚直な配管」にした。理由は
   `UapOpsRequestHandler.HandleCore` が既に壊れた本文に対して
   spec 準拠の JSON-RPC parse error(200 + `-32700`)を返す実装になっている
   ことを確認したため(§実装で確認済み)で、中継側で id を推測して
   合成エラーを作る必要がない。応答側は
   `StatusCode==202` → 何も書かない、`Content-Type` が
   `text/event-stream` → `data:` 行を連結してイベントごとに 1 行、
   それ以外に本文があれば 1 行、で判定する。
3. **Unity 側ビルダー**(`Editor/Integration/UapStdioRelayBuilder.cs`)。
   `EditorApplication.applicationContentsPath` から Unity 同梱の
   `dotnet`/`csc.dll`/共有フレームワークを探す(uLoop 3.6.3 の
   `ExternalCompilerPathResolver.cs` の実測レイアウトを学習し、そのまま
   コピーはしていない: `DotNetSdk/sdk/*/Roslyn/bincore` へのフォールバック
   や必須ランタイムメジャーバージョン判定は今回省略し、
   `<root>/NetCoreRuntime` + `<root>/DotNetSdkRoslyn` 直下と、macOS の
   `<root>/Resources/Scripting/...` の 2 レイアウトだけを見る)。
   内容ハッシュ(SHA-256 の先頭 16 桁、ソース 2 本 + 共有フレームワーク版数)
   ごとに `Library/UnityAgentPanel/McpStdioRelay/<hash>/relay.dll` へ
   コンパイルし、`csc.dll` 自体は `runtimeconfig.json` を書かないため
   ビルダー側で `{"runtimeOptions":{"tfm":"net<major>.<minor>",
   "framework":{"name":"Microsoft.NETCore.App","version":"<共有FW版>"}}}`
   を書き添える。コンパイル失敗はハッシュ単位でキャッシュし、次回以降は
   キャッシュされた失敗理由をそのまま返して再コンパイルしない
   (`CompileAttemptCountForTests` で EditMode テストから検証)。
   タイムアウトは 30 秒。例外は一切外へ投げない。
4. **スレッド境界。** `PackageInfo.FindForAssembly` は UnityCsReference
   自身のコメント(`RenderPipelineResourcesEditorUtils.cs`)に
   「ワーカースレッドでは常に null を返す」と明記されているため、
   `EditorApplication.applicationContentsPath` や `Application.dataPath` と
   合わせてパス解決一式(`UapStdioRelayBuilder.CapturePaths`)は**必ず
   メインスレッドで**呼ぶ。呼び出し元の `AgentHub.StartClient` はこの
   メソッドを spawn 準備中(まだメインスレッド)で 1 回だけ呼び、結果
   (プレーンデータ)をクロージャで `AcpLaunchSpec.McpStdioRelayFactory`
   に閉じ込める。このファクトリは実際には
   `AcpProtocolBridge.BuildMcpServers` から、つまり ACP ブリッジの
   バックグラウンド読み取りスレッド(`AcpProtocolBridge` 自身のクラス
   コメントに明記)から呼ばれるため、初回コンパイルが多少遅くても
   エディタのメインスレッドは止まらない。
5. **ACP ワイヤ形式。** `agent-client-protocol` リポジトリの
   `agent-client-protocol-schema/src/v2/agent.rs` を直接取得して
   `McpServerStdio`/`EnvVariable` の `#[serde(rename_all = "camelCase")]`
   と `McpServer` enum の `#[serde(tag = "type", rename_all =
   "snake_case")]` を確認し、
   `{"type":"stdio","name":...,"command":...,"args":[...],
   "env":[{"name":...,"value":...}]}` の形で送る
   (`AcpProtocolBridge.BuildStdioServerJson`)。Bearer トークンは
   `AcpStdioServer` 自体には持たせず、http 経路と同じ
   `AcpLaunchSpec.McpBearerToken` から `BuildStdioServerJson` が
   `env` に `UAP_MCP_TOKEN` として足す(ビルダー側はトークンを一切
   知らない設計)。
6. **ユーザー可視化。** `AcpProtocolBridge` に `McpUnavailable` イベントを
   追加し、`AcpBridgeTransport` を経由して `AgentHub` まで転送、
   spawn ごとに 1 回だけ(`_mcpUnavailableWarned`、
   `_scriptGateInertWarned` と同じパターンで `TearDownClient` がリセット)
   システムノートを会話に出す。UapOps 自体がオフ
   (`McpServerName`/`McpUrl` が未設定)の場合は `BuildMcpServers` が
   `_capMcpHttp` を見る前に早期リターンするため、このノートは出ない
   (テストで確認: `Handshake_UapOpsDisabled_NeverRaisesMcpUnavailable`)。

### 却下した選択肢

- **uLoop の Go 製 CLI をそのまま流用する案**: 本パッケージは MIT の
  Core で、uLoop の CLI を同梱・起動する経路を持ち込むとライセンス上も
  依存上も本ロードマップの §0 の方針(新規依存を増やさない)に反する。
- **Node.js での中継**: ブリッジ元のメモ(§1.2)通り、追加ランタイムを
  増やさないという制約のため不採用。Unity が既に dotnet ランタイムと
  Roslyn を同梱していることを利用した。
- **`DotNetSdk/sdk/*/Roslyn/bincore` への完全なフォールバック**
  (uLoop の実装にはある)は、必須ランタイムメジャーバージョンの
  ミスマッチが起きる Unity 6 の一部レイアウトでのみ必要になる稀なケース
  であり、今回はスコープ外とした。未対応の環境では
  `ResolveToolchainFromContentsPath` が失敗理由を返し、http 専用の
  既存挙動に自然にフォールバックする(壊れない)。
- **RelayCore に JSON-RPC パーサを持たせる案**: サーバ側が既に
  spec 準拠のエラー応答を返すため、パーサを持たないほうがコードは
  小さく、かつ「サーバの応答仕様が変わったら中継だけ直せばいい」
  という保守性も上がる。

### 検証済みの API / スキーマ

- `EditorApplication.applicationContentsPath`(`Editor/Mono/
  EditorApplication.bindings.cs` 2022.3 ブランチ、257 行目、`public
  static extern string applicationContentsPath`)。
- `UnityEditor.PackageManager.PackageInfo.FindForAssembly` /
  `.resolvedPath`(`Modules/PackageManager/Editor/Managed/PackageInfo.cs`
  2022.3 ブランチ、256〜291 行目)。ワーカースレッドで null を返す注記は
  `Editor/Mono/RenderPipelineResourcesEditorUtils.cs` のコメントで確認。
- ACP `McpServer::Stdio` / `EnvVariable` のワイヤ形式(上記参照、
  `agent-client-protocol-schema/src/v2/agent.rs` コミット
  `8391634`)。
- `UapOpsRequestHandler.HandleCore` の実応答形状(`application/json` /
  `202` のみ、SSE は未実装、`Mcp-Session-Id` はレスポンスヘッダ)を
  ソース読み込みで確認し、RelayCore の分岐と整合させた。

### 未検証(実 Unity Editor が必要)

- `UapStdioRelayBuilder` の実コンパイル経路そのもの(`csc.dll` 起動、
  参照 DLL 列挙、`runtimeconfig.json` の実際の互換性)は Unity Editor が
  無い環境では検証できていない。`ResolveToolchainFromContentsPath` /
  `ResolveHighestVersionDirectory` / キャッシュ挙動はフェイクディレクトリ
  ツリーで EditMode テスト化した一方、実トールチェーンでの end-to-end
  コンパイルは `Assert.Ignore` ガード付きの 1 本のテストのみで、CI の
  実 Unity 上で初めて実行される。
- 実際の ACP エージェント(stdio 専用のもの)を使った実機確認はできて
  いない(§1.2 の表に「該当エージェント未確認」とある通り、そもそも
  Claude Code / Codex / Grok は http を名乗るため、このコードパスは
  現状どの実機接続でも通らない)。
- macOS の `Contents/Resources/Scripting` レイアウトは uLoop のソース
  コメントを根拠にしており、実機の macOS Editor では未確認。

## 2.8 uap_help とコンパクトなツール一覧(A' + B)

### 何を作ったか

1. **`uap_help`**(新規、"core" モジュール、ReadOnly)。`names`(配列)または
   `name`(単数、簡易記法)でツールを名前指定し、登録済みかつ有効モジュールの
   ものだけ `name/module/description/inputSchema/readOnly/undoable` を返す。
   未知の名前・無効モジュールのツールは例外にせず `found:false` と理由を返す
   (バッチの 1 件が悪くても残りが死なない)。
2. **コンパクトモード**。MCP エンドポイントのパスで切り替える:
   `/mcp`(フル、従来どおり)と `/mcp/compact`(コンパクト)。`UapOpsRequestHandler.
   HandleCore` がリクエストごとに `request.Path` を見て `compact` を決め、
   `tools/list` にだけ効く。コンパクト時は `uapOpsModules` のうち
   `"core"` だけを従来どおり直接一覧し、`uap_tools` / `uap_call` の 2 本を
   追加する(`UapToolGateway.DirectlyListedModule = "core"` が単一の定義箇所)。
   **`uap_tools` / `uap_call` は `ToolRegistry` に一切登録しない** --
   `UapOpsRequestHandler` が `tools/list` へ直接合成し、`tools/call` では
   名前で特別扱いする。この設計だからこそ「フルモードの一覧には絶対出ない」
   「`uap_call` が自分自身や `uap_tools` を名指ししても
   `registry.Find` が null を返して自然に `Unknown tool` で止まる(自己再帰
   にならない)」の 2 つが**構造的に**成り立つ(if 文の特別扱いを 1 か所
   忘れる、という壊れ方をしない)。
   - `uap_tools`: `names` 無しなら `query`(名前/モジュール/一行要約への
     部分一致、大小無視)と `module`(完全一致)で有効モジュールを横断検索し、
     `name/module/summary/readOnly` を返す。`names` 付きなら `uap_help` と
     同じ完全版(`found:false` 込み)を返す。
   - `uap_call`: `{name, arguments}` を **`UapOpsRequestHandler.HandleToolsCall`
     の先頭、モジュール判定と実行ディスパッチより前**でアンラップし、以降は
     直接呼び出しと完全に同じコードパスを通す。これが必須な理由は
     `uap_scripts_commit` のような `IUapPollableTool`(コンパイル待ちの間
     `Execute` を跨いで tick される)を内側の `Execute` から呼ぶと、
     ディスパッチャが二重に入れ子になってデッドロックするため -- 「別ツールの
     中から `inner.Execute` を呼ぶ」実装は最初から選択肢になかった。
   - 一行要約は `UapToolSummary.FirstSentence(description)`(Unity 非依存、
     `--`・`e.g.`/`i.e.`・小数点付きバージョン番号を誤って文末と判定しない、
     160 文字上限で単語境界カット + 末尾のダッシュ/空白を trim してから `...`)。
     `UapToolSummary.MatchesQuery` も同ファイル、smoke 対象。
3. **`UapOpsServer.ResolveEffectiveTool(wireName, input, out effectiveInput)`**
   (新規)。`FindByWireName` の `uap_call` 対応版: `uap_call` なら
   `UapToolGateway.TryUnwrapCall` で内側のツール+引数を解決し、それ以外は
   `FindByWireName` と同じ。権限判定・表示が「どのツールが実際に動くか」に
   依存する箇所は全てこちらに切り替えた(下表)。
4. **`PanelSettings.uapToolListMode`**(`Auto`/`Full`/`Compact`、既定
   `Auto`)。`uloopAgentUseEnabled` と全く同じ配線(`SettingsChangeDetector.
   RequiresReconnect` に比較を追加、`AgentHub.CloneNextSpawnOnlyFields` に
   クローンを追加、`SettingsView` に `PopupField<UapToolListMode>` の行を
   `CommitSettingsChange()` 経由で追加)。`AgentHub.StartClient` が
   `UapToolListModePolicy.ResolveCompact(settings.uapToolListMode, isAcp)`
   を 1 回だけ評価し、`--mcp-config`(`UapOpsMcpConfig.BuildConfigJson`/
   `EnsureConfigFileWritten` の新しい `bool compact` 引数、2 引数版は
   `compact:false` に委譲して後方互換)と ACP の `acpSpec.McpUrl` の両方に
   同じ結果を反映する。Auto は Claude Code(`!isAcp`)で Full、それ以外の
   バックエンドで Compact -- `UapToolListModePolicy.ResolveCompact` 1 か所
   に集約。

### `AgentHub.CloneNextSpawnOnlyFieldsTests` が拾った(というより避けた)

`uloopAgentUseEnabled` は v0.59.0-beta.1 で `RequiresReconnect` には
足されたが `CloneNextSpawnOnlyFields` には足し忘れ、というリグレッションが
実際に起きていて、それを教訓に `CloneNextSpawnOnlyFieldsTests` は
**フィールド名を書かないリフレクション横断テスト**(enum も含め全フィールドを
1 個ずつ変えてクローンしても `RequiresReconnect` が差を見ないことを確認)に
なっている。`uapToolListMode` は両方に足したので、このテストは**追加コード
無しで**そのまま新フィールドを守る。逆に言えば、このテストの存在に助けられて
「うっかり片方だけ足す」を機械的に防げている。

### ステアリング文への反映

コンパクトモード時にエージェントへ足す一文
(`UapToolListModePolicy.SteeringSentence()`: 「core 以外は uap_tools /
uap_call 経由」)は、統合時に配線した。`AgentHub.StartClient` が
`UapToolListModePolicy.ResolveCompact(settings.uapToolListMode, isAcp)` の結果を
`ComposeUapOpsSteeringSection` の新しい引数 `compactToolList` に渡し、true のときだけ
モジュール一覧の直後にこの一文が入る(`AgentHubStartClientArgTests` で固定)。

### `FindByWireName` / 権限判定サイトの棚卸しと決定

grep した全サイトと決定:

| サイト | 決定 |
|---|---|
| `AgentHub.TrackNonUndoableToolIfNeeded`(非 Undoable 警告の記録) | `ResolveEffectiveTool` に切替(`ContentBlock.Input` を新たに渡す) |
| `AgentHub.TryTrackScriptsCommitAttribution`(コンパイル自動継続の起因判定) | `ResolveEffectiveTool` に切替(既に持っている `block.Input` を使う) |
| `AgentHub.TryAutoApproveUapOpsTool`(自動承認の可否) | `ResolveEffectiveTool` に切替(`CanUseToolRequest.Input` を渡す) |
| `PermissionCard`(Undo 不可バッジ) | `ResolveEffectiveTool` に切替 |
| `PermissionCard` / `ToolCardDescriber.Describe`(カードの要約表示) | `ToolCardDescriber` 側に `uap_call` 専用分岐を追加(レジストリ参照なしの純粋な文字列処理で、内側のツール名+引数に対して再帰的に同じテーブルを適用。再帰は `uap_call` が自分自身を再帰的に包んでも壊れないよう深さ上限 4 でガード) |
| `AutoApprovePolicy.cs`(コメントのみ、コードでは `FindByWireName` を呼んでいない) | 変更なし(このファイルは今回のオーナーではない。コメントは `AgentHub` 側の呼び出し規約の説明であり、事実として誤ってはいない) |
| `AcpProtocolBridge.MapToolName` | **変更不要と確認**。`ParseUapToolId` は `"uap_"` 接頭辞と識別子文字のみで判定し、既知ツール名のアローリストを持たない -- `uap_call`/`uap_tools`/`uap_help` はどれも自然に `mcp__unity-ops__uap_*` へマップされる |

### tools/list サイズの実測(A' + B 適用後、Core 既定 ON モジュール)

Unity Editor が無いため実際の `tools/list` を叩けず、以下は
**ソース中の `Description` 文字列を直接数えた実測値**と、設計ノート
1.2 節が既に出していた合算(37 ツール・約 9,000 トークン、説明文+スキーマ+
JSON 枠組み 1〜3 割増し込み)を組み合わせた**推定**であることを明記する:

- 説明文の文字数(直接カウント、スキーマ抜き): core 7,343 文字(24 ツール、
  `uap_help` 込み)/ editor 6,457(9)/ markers 949(4)/ web 892(2)、
  合計 15,641 文字 ≒ 3,910 トークン(chars/4)。
- 1.2 節の既存合算(37 ツール規模、約 9,000 トークン)に対し、上の内訳から
  core の取り分をおよそ 47%(7,343 / 15,641)と見積もると、**フルモード
  (`uap_help` 追加後)は約 9,200 トークン**、**コンパクトモード(core 直接
  一覧 + `uap_tools` 734 文字 + `uap_call` 431 文字のゲートウェイ 2 本、
  スキーマ込みで概算 470 トークン)は約 4,700 トークン** -- **フルの
  ほぼ半分**。
- 設計ノート 1.2 節が言う「A だけで 2〜3 倍、A+B で uLoop 以下」の A(説明文
  圧縮)は本タスクでは**意図的に実施していない**(ブリーフの決定通り、
  Claude Code の遅延読み込みへの配慮で説明文はそのまま)。B 単体では
  uLoop のスキル(約 1.5〜2k トークン)にはまだ届かない -- 予測どおりで、
  A を別タスクで足せばさらに縮む。
- **要フォローアップ**: 実際の Codex / Grok 接続で `/mcp/compact` の
  `tools/list` を測り、上の推定を裏取りすること(このタスクでは Unity
  Editor が使えず未実施)。

### 見送った/未検証

- `UapToolListModePolicy` は `UapToolGateway` の定数("core"/"uap_tools"/
  "uap_call")を直接参照せず**意図的にリテラルを複製**している(前者を
  Unity 非依存に保つため -- `UapToolGateway` の他メンバは `ToolRegistry`
  経由で `UnityEditor` に触れる)。同期は `UapToolListModePolicyTests` の
  1 本が 3 リテラルの一致を assert することで担保(コメント参照)。
- `tools/list_changed`(C 案、実験扱い)は今回のスコープ外のまま。
- 実際の Unity Editor / Codex・Grok 実機での動作確認は未実施(環境に
  Unity Editor が無い)。EditMode テストはコード上整合させたが、
  Unity のコンパイル・実行では検証していない。

---

## 3. 統合時に直したこと

- **§2.7 中継のビルドがメインスレッドを止めうる問題。** レビューで、初回のビルド
  (最大 30 秒)が ACP の読み取りスレッドで `AcpBridgeTransport` のロックを持ったまま
  走ることが分かった。メインスレッドはエージェントへ送るときに同じロックを取るので、
  最悪 30 秒エディタが止まる。ビルドは接続開始時に `UapStdioRelayBuilder.Warm` で
  バックグラウンドに出し(ソースのハッシュごとに 1 回、`Library/` にキャッシュ)、
  ブリッジから呼ばれる側は最大 3 秒(`FactoryWaitMillis`)だけ待つようにした。
  間に合わなければその接続は中継なしで、次の接続から使える。
- **§2.3 `uap_compile` が「requested」のまま固まる問題。** コンパイルイベントを
  一度も出さない要求(何も変わっていない、asmdef の設定エラーなど)が残ると、
  永続化された状態がドメインリロードを越えて残り、以後の要求を拒み続けていた。
  20 秒(`StaleRequestSeconds`)以上前の「requested」は、コンパイルが走っていなければ
  新しい要求を通すようにした(既存の「孤立した compiling」の自己回復と同じ扱い)。

- **入力ツールが「放棄」されたときにキーやボタンが押されたまま残る問題。**
  ディスパッチャは HTTP の待ちが切れた Pollable を二度と Poll しない(SEC-7)。
  エディタが非フォーカスで Play 中のときの待ちは 8 秒で、`long_press` / `press` の
  保持は最大 10 秒まで合法なので、正当な引数でも保持の途中で放棄されうる。そのとき
  `uap_game_mouse` / `uap_game_key` の保持集合からそのボタン・キーが消えず、以後の
  すべての注入に「押されたまま」が混ざっていた(`uap_game_pointer` は開いた長押し・
  ドラッグが閉じない)。任意インタフェース `IUapAbandonableTool.OnAbandoned` を足し、
  ディスパッチャが開始済みの放棄項目を捨てるときに 1 回だけ呼ぶようにした。各ツールは
  **その呼び出しが押したものだけ**を離す(完了済みの `down` で押したキーは離さない)。
  待ち時間の予算側を変える案は、非フォーカス時に速く失敗させるという 8 秒の目的を
  壊すので採らなかった。
- **`uap_compile` の status が孤立した compiling 記録に「Compiling now.」と答え続ける問題。**
  コンパイル中でないのに記録だけが compiling のときは、その旨と再要求の仕方を返す。
- **UapOps を切っていても ACP 接続のたびに中継ビルドが走る問題。** ビルドの開始と
  中継の用意を UapOps が有効なときだけに限った。

### レビューで検討して見送ったもの

- **`Library/` に置いた relay.dll をパスだけで信用している。** キャッシュのパスは公開
  ソースのハッシュと Unity のランタイム版から計算できるので、プロジェクトの `Library/`
  に書き込める者は差し替えた relay.dll をトークン付きで実行させられる、という指摘。
  採らなかった理由: プロジェクトフォルダに書き込める者は、`Assets/Editor/` に置いた
  スクリプトでエディタ内の任意コード実行が既にできる(Unity が開くたびにコンパイルして
  読み込む)。中継はそれ以上の権限を与えない。秘密値を混ぜる・生成物のハッシュを横に
  置く、はいずれも同じ書き込み権限で一緒に差し替えられる。

## 4. 検証の状況

| 項目 | 状態 |
|---|---|
| `ci/SmokeTests`(ライセンス不要) | 377 件全緑(230 → 377)。各ポリシー、中継の中核(JSON / SSE / 202 / セッション ID)、要約生成を含む |
| EditMode(Unity 2022.3.22f1、CI) | このセッションでは未実行。push 後の CI で確認する |
| 実機での動作 | 未確認。各節の「実機で未確認」を参照 |
| コンパクト一覧の実トークン数 | 未測定。説明文とスキーマの文字数からの概算で、Core の既定モジュールがフル約 9,200 → コンパクト約 4,700 トークン |
