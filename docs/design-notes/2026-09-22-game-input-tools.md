# Play Mode 入力ツール(`uap_game_pointer` / `uap_game_key`)

対象: `jp.colloid.unity-agent-panel`(Core)
関連: `docs/design-notes/2026-09-21-uloop-always-loaded-cost.md` §9.2(ギャップ表)/ §9.4、
`docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md` §6(残ギャップ)、
`docs/design-notes/2026-08-01-phase5-unity-ops-design.md` §7.2(能力マトリクス)

---

## 0. 結論(先に)

1. §9.2 のギャップ表で最後まで残っていた **「PlayMode 中の実入力」** を自前化した。
   uGUI へのポインタ操作(`uap_game_pointer`: click / long_press / drag)と、
   Input System 経由のキー入力(`uap_game_key`: press / down / up / release_all)の
   2 本。これで一般的な用途で uLoop を入れる理由は無くなり、`uloop` は
   hot-reload / pause-point / dynamic-code を使いたい人だけの任意パッケージに降格する。
2. **どちらもリフレクションで実装した。** uGUI(`com.unity.ugui`)も Input System
   (`com.unity.inputsystem`)も、この Core が依存を宣言できないパッケージで、
   CI のホストプロジェクト(`ci/HostProject*`)にはどちらも入っていない。
   `UapCanvasProbe` が Canvas を型名で読む前例と同じ理由(§3.3)。
3. **キー入力は Input System 専用**で、旧 Input Manager(`UnityEngine.Input`)には
   注入口が無い。ツールはその事実をそのまま答え、代替(uGUI ボタンを
   `uap_game_pointer` で押す)を名指しする(§4.2)。uLoop も同じ制約を持つ
   (`simulate-keyboard` の Prerequisites)。
4. **両ツールとも Pollable** にした。長押し・ドラッグ・キーの保持はフレームを跨ぐ
   操作で、`IUapTool.Execute` 1 回で完結させると main thread を止めるか、
   押した瞬間と離した瞬間が同じ入力更新に潰れてゲームが押下エッジを見ない(§4.3)。
5. **保持時間の上限は 10 秒**(`UapGameInputPolicy.MaxHoldSeconds`)。ディスパッチャの
   HTTP 待ちは 15 秒で、待ちが切れた Pollable は二度と tick されない(SEC-7)。
   キーを押したまま放置される事故を避けるには、release を待ちの内側に収める
   しかない。長く押したいときは `down` → `up` の 2 呼び出し(§4.4)。

---

## 1. 何が足りなかったか

`uap_play_mode` でシーンを走らせ、`uap_console_logs` でログを読めるようになっても、
「ジャンプが実際に効くか」「Start ボタンでゲームが始まるか」は人間がキーボードと
マウスを触らないと確かめられなかった。uLoop 側にはこのための
`simulate-mouse-ui` / `simulate-keyboard` / `simulate-mouse-input`(合計約 6,100 行、
uLoop 3.6.3 の `FirstPartyTools/Simulate*` を実測)があり、ここが
「uLoop を入れないと足りないもの」の最後の 1 本だった。

uLoop の 3 本のうち採ったのは 2 本:

| uLoop | 本パネル | 理由 |
|---|---|---|
| `simulate-mouse-ui`(EventSystem 経由の uGUI 操作) | `uap_game_pointer` | 入力バックエンドを問わず効く。メニュー・ボタン・スライダーはゲームを動かす主経路 |
| `simulate-keyboard`(Input System にキー状態を注入) | `uap_game_key` | WASD / Space などゲーム操作の主経路 |
| `simulate-mouse-input`(Input System の `Mouse.current` を注入) | **見送り** | 3D オブジェクトの「ゲーム内マウス」向け。Input System 専用で、しかも UI は上の 1 本で足りる。要望が出てから |
| `record-input` / `replay-input` | **見送り** | 決定論リプレイは E2E テスト基盤の話で、このパネルのスコープ外 |

pause-point 連携(uLoop の各ツールが持つ `InterruptedByPausePoint`)も無い。
本パネルには pause-point が無いので、対応する分岐も要らない。

---

## 2. 共通規則(`UapGameInputPolicy`、Unity 非依存)

- **Play Mode 中でなければ拒否**し、`uap_play_mode action:start` を名指しする。
  一時停止中も拒否(ゲームが再開するまで入力を処理しないので「何も起きない」に
  見える)。例外は回復用の `release_all` で、これは一時停止中でも通す。
  コンパイル中も拒否。
- **保持時間は 0〜10 秒**。超過は「単位は秒であってミリ秒ではない」と
  「長く押すなら down → up」を添えて拒否。NaN / 無限大 / 負も拒否。
- `MinHoldPolls = 2`: press は要求時間が 0 でも最低 2 tick は押下状態を保つ。
  同じ tick で down と up を queue すると次の入力更新で両方が処理され、
  `wasPressedThisFrame` のエッジがどのフレームにも立たない。

これらは `ci/SmokeTests`(`GameInputSmoke`)と EditMode の両方で留めている。

---

## 3. `uap_game_pointer`

### 3.1 何を送るか

uGUI の `StandaloneInputModule` が実マウスに対して行うのと同じ順序で
`ExecuteEvents` を叩く:

| action | 送るイベント |
|---|---|
| `click` | `pointerDown`(`ExecuteHierarchy`)→ `pointerUp` → `pointerClick` |
| `long_press` | `pointerDown` → `duration` 秒保持 → `pointerUp`(`pointerClick` は送らない) |
| `drag` | `pointerDown` → `initializePotentialDrag` → `beginDrag` → `drag` × N → `pointerUp` → `drop` → `endDrag` |

`pointerDown` だけ `ExecuteHierarchy` で親へ遡るのは、Button の子の Text に
raycast が当たるのが普通で、ハンドラは親にあるため。`pointerClick` は
`GetEventHandler<IPointerClickHandler>` で解決した相手に `Execute` する
(uGUI 自身の `ProcessMousePress` と同じ)。

`drop` は `endDrag` の**前**。uGUI の release 処理の順序
(`pointerUp` → `drop` → `endDrag` → `pointerDrag = null`)に合わせた。

### 3.2 どこを指すか — 2 つの指定方法

1. **`target`(階層パス)** — 推奨。`uap_query_hierarchy` が返すパスをそのまま渡す。
   raycast を行わず、その GameObject に直接ディスパッチする(uLoop の
   `--bypass-raycast --target-path` に相当)。x/y を省くと要素の中心
   (`RectTransform.rect.center` を Canvas のカメラで screen 座標にしたもの)を
   `PointerEventData.position` に入れる。縮小されたスクリーンショットから
   ピクセルを推測するより正確で、透明なオーバーレイに raycast を吸われない。
2. **`x` / `y`** — スクリーンショットの読み方と同じ **左上原点**、Game View の
   **描画解像度**(`Handles.GetMainGameViewSize()` = `uap_game_view_size` が返す
   `width`/`height`)のピクセル。`normalized:true` なら 0..1 の割合。
   uGUI の座標系は左下原点なので `UapGamePointerPolicy.TryToScreenSpace` で
   y を反転する。**この反転が唯一のバグの温床**なので純関数にして smoke で留めた。
   範囲外は「空振り」ではなく引数エラーにする(直し方が違うため)。

`uap_editor_screenshot capture:"game"` はウィンドウのポイントサイズで描画するので、
描画解像度と一致するとは限らない。返答には `viewWidth` / `viewHeight` を必ず
載せ、説明文で `normalized:true` を案内している。

### 3.3 リフレクションの構成(`UapUguiEventAccess`)

`UnityEngine.EventSystems.EventSystem` / `PointerEventData` / `RaycastResult` /
`ExecuteEvents` をアセンブリ横断の型名検索で解決し、`ExecuteEvents.Execute<T>` /
`ExecuteHierarchy<T>` / `GetEventHandler<T>` は `MakeGenericMethod` で
`IPointerDownHandler` などの型を与えて呼ぶ。functor は `ExecuteEvents.pointerDownHandler`
等の静的プロパティをそのまま渡す。

各段階で「どこが解決できなかったか」を文で返す(`uap_game_view_size` の
`UapGameViewSizeAccess` と同じ作法)。パッケージ自体が無いときのメッセージは
`MissingPackageMessage` に固定し、テストがそれを留める。

### 3.4 答えないもの

- **raycast に何も当たらない click / long_press はエラーにしない**(uLoop と同じ)。
  `hit: null` と「何も送っていない」旨のメッセージを返す。空の場所をクリックする
  のは正当な操作(ポップアップを閉じる等)で、失敗と断定できない。
  drag の始点に何も無いのはエラー(ドラッグする物が無い)。
- **uGUI ハンドラを持たない 3D オブジェクト**には届かない。説明文に明記。
  `simulate-mouse-input` 相当が要る場合は §1 のとおり見送り。
- `Mouse.current.position` の同期はしていない(uLoop はする)。ハンドラが
  `PointerEventData` の代わりに `Mouse.current` を読む実装は稀で、要る場合は
  Input System へのもう一段のリフレクションになるので、要望が出てから。

---

## 4. `uap_game_key`

### 4.1 何を送るか

`InputSystem.QueueStateEvent(keyboard, new KeyboardState(押下中の全キー))`。
Input System 自身のテストフィクスチャ(`InputTestFixture.Press`)と同じ経路で、
次の入力更新でデバイス状態になる。`KeyboardState` は**差分ではなく全体の
スナップショット**なので、新しいキーだけ入れて queue すると他のキーが全部
離される。ツールは保持中のキー集合(`HeldKeys`)を持ち、常に集合全体を送る。

`InputSettings.updateMode == ProcessEventsManually` のプロジェクトでは誰も
queue を消費しないので、queue 直後に `InputSystem.Update()` を呼ぶ
(`FlushIfManual`)。それ以外は次の PlayerLoop 更新に任せる。

### 4.2 Input System 専用であることを言う

旧 Input Manager(`UnityEngine.Input.GetKey`)は Editor から注入する API を
持たない。uLoop が Harmony パッチで頑張っているのは hot-reload / pause-point で
あって、キー注入は uLoop も Input System 前提である。パッケージが無い /
`Keyboard.current` が null(Active Input Handling が Old)のときは、それぞれ
何を確認すべきかと、UI ボタン経由(`uap_game_pointer`)の代替を返す。

### 4.3 Pollable の状態機械

| action | tick 1(Begin) | tick 2 | tick 3 |
|---|---|---|---|
| `press` | 集合に追加して queue | (`MinHoldPolls` と `duration` を満たすまで待つ)集合から外して queue | デバイスを読み戻して返答 |
| `down` | 追加して queue | 読み戻して返答 | |
| `up` | 外して queue | 読み戻して返答 | |
| `release_all` | 集合を空にして queue | 読み戻して返答 | |

読み戻し(`Keyboard[key].isPressed` を全キー走査)は返答の `devicePressed` に
載る。ツールが離したのにデバイスがまだ押下を報告していれば「他の何か(物理キー、
別の注入元)が押している」と答える。uLoop の `ReleasedKeyStates` /
`DeviceIsPressedAfterRelease` に相当する。

### 4.4 保持の後始末

- 保持集合は **Play Mode 終了時に空にする**(`playModeStateChanged` の
  `ExitingPlayMode`)。デバイスごと消えるので状態も捨てる。フックは初回使用時に
  張り、`[InitializeOnLoadMethod]` からは張らない(2026-09-21 ノートの
  「常駐させない」規則)。
- 保持中に Play Mode が終わった Poll は例外で返し、「ゲームは入力全体を
  観測していない」と言う。
- `down` を重ねる / 保持していないキーを `up` は拒否(`CheckHeldState`)。
  黙って繰り返すと、エージェントのキー状態の認識が間違ったまま次の呼び出しも
  間違う。

### 4.5 キー名

Input System の `Key` 列挙の綴りが正(`Digit1`、`LeftShift`、`UpArrow`)。
エージェントはゲームのコード(`Key.Space`)や自然言語(「W を押す」)から書くので、
大文字小文字を無視し、`shift` / `esc` / `1` / `up` / `num5` のような略記を
`UapGameKeyPolicy.Aliases` で正規名に写す。未知の名前には部分一致の候補を添える。
既知名の一覧(`KnownKeys`)は Unity 非依存にするための写しで、実際の列挙は
Editor 側で `Enum.Parse` する(将来 Input System が名前を増やしても写しの
更新だけで済む)。

---

## 5. 能力マトリクスとの関係

`UloopCapabilityMatrix` には**登録しない**(2026-09-21 ノート §9.4 項 4 のとおり)。
uLoop が検出されても自前版を登録する。理由は play-mode ツールと同じで、
型付き引数・権限カード・ジョブ台帳・Play Mode 終了時の自動解放は
`uloop simulate-*` を Bash 経由で叩いても得られない。

`ReadOnly` は両方 false(`UapReadOnlyToolMetadataTests` に固定)。プロジェクトは
何も変わらないが、クリックが購入フローを始めたりセーブを消したりするのは
ゲーム次第で、権限カードを出さずに通してよいものではない。

`UloopAgentUsePolicy.DenyMessage`(uloop を切ったときの代替案内)と
`AgentHub` のステアリング文(editor モジュールの説明と「自分で確かめる」ループ)に
2 本を足した。

---

## 6. 検証

- **`ci/SmokeTests`**(ライセンス不要): `GameInputSmoke` 30 チェック。
  座標反転・正規化・範囲外・ドラッグ進捗・キー名正規化・保持規則・
  Play 状態判定を留める。本タスクで 200 → 230 チェック、全緑。
- **EditMode**(`UapGamePointerToolTests` / `UapGameKeyToolTests` /
  `UapEditorToolsMetadataTests` / `UapReadOnlyToolMetadataTests`): 引数規則、
  Play Mode 外での拒否文、引数エラーが Play 状態判定より先に出ること、
  パッケージ不在時のメッセージ。**このセッションでは Unity を起動できないため
  未実行**。CI の EditMode 実行で確認する。
- **未確認(実機の手動確認が要る)**: Play Mode に入り、Canvas の Button を
  `uap_game_pointer target:"Canvas/Button"` で押して `onClick` のログが
  `uap_console_logs` に出ること、Input System を入れたプロジェクトで
  `uap_game_key key:Space` に `Keyboard.current.spaceKey.wasPressedThisFrame` が
  1 フレームだけ true になること、`down` → `up` の間 `isPressed` が true のままで
  あること。バッチモードの EditMode では Play Mode に入れず、CI のホスト
  プロジェクトには両パッケージが無いので、ここは自動化できない。

---

## 7. 見送ったもの(再掲)

- `simulate-mouse-input` 相当(Input System の `Mouse.current` 注入、3D 向け)。
- `record-input` / `replay-input`。
- `Mouse.current.position` の同期(§3.4)。
- 押下エッジの観測診断(uLoop の `PressEdgeObserved` / 非フォーカス時の警告)。
  本パネルは `uap_play_mode` の start 時に Run In Background の警告を出しているので、
  同じ原因はそこで先に知らせている。
