# Scene ビューのツールバーを「Agent Tools」1 本にまとめる(2026-09-18)

## 1. 問題

ピン(`2026-09-07` §1.3.4)とスケッチ(`2026-09-17-scene-sketch-strokes.md` §3)が
それぞれ別の `ToolbarOverlay` として Scene ビューに登録されていた:

| Overlay id | 表示名 | 中身 |
|---|---|---|
| `uap-agent-pin` | Agent Pin | ピンのトグル 1 つ |
| `uap-agent-sketch` | Agent Sketch | 平面 / 表面の 2 トグル |

Unity はオーバーレイごとに独立したストリップ(ドラッグハンドル付き)を置くので、Scene
ビューの上部に「ハンドル + ボタン 1 個」「ハンドル + ボタン 2 個」が並んで見た目が
ゴチャゴチャしていた。ユーザーの視点ではどちらも「エージェントに場所を伝えるための
マーキング道具」で、分かれている理由はない。

## 2. 変更

- `SceneMarkerPinOverlay.cs` と `SceneStrokeOverlay.cs` を **`SceneAgentToolsOverlay.cs`** に
  統合。Overlay id は **`uap-agent-tools`**、表示名は **「Agent Tools」**、既定表示。
  中身は左からピン / 平面スケッチ / 表面スケッチの 3 トグル。
- 3 トグルは共通基底 `SceneAgentToolToggle`(アイコン or 文字、ツールチップ、
  `AttachToPanel` / `DetachFromPanel` での双方向同期)を継承する。ピンは
  `SceneMarkerPin.Armed`、スケッチは `SceneStrokeSketch.IsArmedIn(mode)` を鏡写しにする
  (ここは従来どおり)。
- **排他**: ピンとスケッチは同じ Scene ビューの左クリックを取り合うので、
  `SceneMarkerPin.Armed = true` は `SceneStrokeSketch.Armed = false` を、
  `SceneStrokeSketch.Arm(mode)` / `Armed = true` は `SceneMarkerPin.Armed = false` を
  呼ぶ。false 側の setter は相手に触らないので再帰しない。1 本のツールバーに並ぶ
  3 トグルが常に高々 1 つだけ点灯する。
- 旧 id `uap-agent-pin` / `uap-agent-sketch` はなくなる。Unity のオーバーレイ配置は
  id ごとに保存されるので、旧 id を動かしていたユーザーは新しいストリップが既定位置に
  出る(1 回だけ置き直し)。ツールバー要素 id(`uap/agent-pin`,
  `uap/agent-sketch-plane`, `uap/agent-sketch-surface`)は変えていない。
- `ci/HostProject/Assets/Editor/UapShotMarkers.cs`(撮影ハーネス)の
  `TryGetOverlay` は新 id を参照する。

## 3. テスト

`SceneStrokeTests.Arm_DisarmsThePin_AndArmingThePinDisarmsTheSketch` を追加(排他の
3 パターン)。オーバーレイ自体は `EditorToolbarElement` の登録なので EditMode テストの
対象外(従来どおり撮影ハーネスで `registered` / `displayed` を確認)。

## 4. 実機確認と撮影(2026-09-18、GameCI `ubuntu-2022.3.62f3-base-3` / Xvfb)

CI と同じ GameCI のエディタイメージを docker で起動し(`ci/shop-images/agent-tools-capture.sh`)、
Xvfb 上の GUI エディタで撮影ドライバ `ci/HostProject/Assets/Editor/UapShotAgentTools.cs` を
走らせた。ドライバはピン / 平面 / 表面を静的状態経由でアームし、クリックとドラッグは
`ci/shop-images/drive-x11.py` が **xdotool の実ポインタ**で行う(ワールド座標を
`duringSceneGui` 内でスクリーン座標に投影して `<n>.act` に書き、ホストが実行して消す)。
フレームは `docs/images/guide/13-agent-tools-*.png`(操作ガイド §13)。

### 確認できたこと

- `TryGetOverlay("uap-agent-tools")` は registered / displayed とも true。ツールバーは
  Scene ビュー**右下**に「Agent Tools」ヘッダー付きの 1 本のストリップとして出る
  (`ToolbarOverlay` の既定は `Layout.Panel` のフローティング。以前の 2 本はこれが
  2 つ並んでいた)。
- ピン: アーム → ホバーのプレビュー → クリックで P1、トグルは自動で戻る。
- 平面スケッチ: F でカーソル下の面に深度を合わせ、ドラッグで S1(7 点)。輪郭線・格子・
  読み出しが出る。表面スケッチ: 床にドラッグで S2(7 点)。
- 排他: スケッチ中にピンをアームすると表面 / 平面のトグルが消え、ピンだけ点灯
  (`07-pin-excludes-sketch`)。
- パネルのコンテキストバーに「ピン P1」「スケッチ S1」「スケッチ S2」「マーカー 3 個」。

### 直した点(同ブランチ)

| 症状 | 原因 | 修正 |
|---|---|---|
| 平面モードの深度読み出しが Unity の Tools オーバーレイ(左上)に隠れる | `SceneStrokeDepthGuide.DrawReadout` が GUI 座標 (12, 12) 固定 | 上端中央に置く(`camera.pixelRect.width / pixelsPerPoint` で中央寄せ) |
| 平面トグルのアイコンがただの矢印カーソル | `d_Grid.Default` は Grid オーバーレイの「既定ツール」= 選択矢印 | 候補 36 個を Scene ビューに並べて撮り(`UAP_SHOT_ICONS=1`)、ピン `d_ToolHandlePivot`(押しピン状)、平面 `d_Mesh Icon`(格子)、表面 `d_TerrainInspector.TerrainToolRaise`(面に塗るブラシ)に変更 |

### 撮影で分かったこと(キット側)

- **`ubuntu-2022.3.22f1-base-3`(CI の下限)は llvmpipe で UI Toolkit を一切描かない。**
  メインツールバー行・全オーバーレイ・パネルが真っ暗で、IMGUI のウィンドウとシーンカメラは
  描ける(エラーログなし)。同じ手順の `2022.3.62f3` は描ける。撮影はこちらで行い、
  `ci/stamp-unity-version.sh` で一時的に版を合わせて後で戻した。
- ドラッグはエディタが 1 フレームに 1 回しか MouseDrag を拾わず、llvmpipe + オーバーレイの
  フレームは遅いので、速いドラッグだと 1 ストローク 1〜4 点になった。頂点ごとに 1.5 秒止め、
  区間も 0.3 秒刻みにして 7 点そろう。
- ウィンドウマネージャが無いので、メインウィンドウは保存レイアウトの大きさのまま。
  最初のフレームの前に `xdotool windowmove / windowsize` で全画面にし、Scene と Agent Panel
  を前面に上げる(`drive-x11.py` の `arrange`)。
- このコンテナの外向きプロキシは `127.0.0.1` で待ち受けるため、Licensing Client が
  `Connection refused (127.0.0.1:port)` で失敗する。`--network host` で解決。

### 使用例の採取(2026-09-19、ホスト上の 2022.3.62f3)

操作ガイド §13.6 と `docs/examples/agent-tools-request.md` の「エージェントが受け取るもの」は、
ドライバの `DumpExample` が採った実データ: コンテキストバーのチップをリフレクションで
覗いて(消費せずに)payload を集め、`ContextBlockFormatter.Compose` で送信本文を組み、
`uap_marker_list` / `uap_stroke_list` を本番の `UapOpsRequestHandler` 経由で呼んだ結果。
入力欄に依頼文を入れた状態のフレーム(`11-request-typed`)も同じ走行で撮った(送信はしない)。

この回はコンテナではなく**ホスト**で走らせた(`ci/shop-images/agent-tools-live-host.sh`、
エディタは GameCI イメージから `/opt/unity` に `docker cp`)。本物の CLI で 1 ターン送る
`UAP_SHOT_LIVE=1` も同スクリプトにあるが、この作業環境では自動実行を許可できなかったので
返答は採っていない(ガイドにもそう書いた)。既定では各ツール呼び出しに権限カードが出る。

### Windows 実機での走行(2026-09-20、2022.3.22f1、2880x1800 / 200%)

`ci/shop-images/windows/agent-tools-live.ps1` を追加し、同じドライバを Windows の実機で回した。
つまずいた点と対処:

- `-executeMethod` で起動するとエディタのウィンドウが一度も描画されない(メインは白、
  フローティングは黒。素の起動では正常)。ドライバに `UAP_SHOT_AUTORUN=1` の
  `[InitializeOnLoadMethod]` を足し、通常起動の 15 秒後に自分で歩き始める形にした。
- ドライバのレイアウトは 1920x1080 前提。`UAP_SHOT_SCREEN=WxH`(ポイント)で小さい画面に収める。
  Windows では `position` の 1 回目の代入はドックから外すだけなので、`Show()` の後にもう一度当てる。
- 走行中にスクリーンセーバー(ロック)に入ると入力デスクトップが切り替わり、`SetCursorPos` が
  失敗して合成入力が全部落ちる(ピン 0・線 0)。画面コピーも `PrintWindow` も白紙になる。
  対処は 2 段: `-InputMode os` は開始時に検出して止まり、走行中はスリープを抑止する。
  `-InputMode event` はポインタと F キーを `EditorWindow.SendEvent` で Scene ビューへ送り
  (パッケージ側の `duringSceneGui` ハンドラをそのまま通るので、ピン・線・チップは本物)、
  フレームは `GUIView.GrabPixels` でウィンドウのバックバッファから読む。ロック中でも
  ピン 1・S1 7 点・S2 13 点・チップ 3 個と全フレームが採れた。
- 実エージェントへの送信(`-Live 1 -Approve 0`)も通った: チップ 3 個付きで届き、エージェントは
  ToolSearch で `uap_*` を読み込み、最初の `uap_ping` の権限カードで止まった(想定どおり)。
  返答まで進めるには誰かがカードに答えるか `-Approve` を上げる必要があり、後者の起動は
  この作業セッションでは許可されなかったので、返答の採取は手元での実行待ち。
- この回のターンは `UAP_SHOT_MODEL` ではなく既定モデルで走った(`Run` より前にパネルが
  接続済みのため)。`-Live 1` のとき `AgentHub.StartFresh()` を呼ぶよう直した(未実走)。
