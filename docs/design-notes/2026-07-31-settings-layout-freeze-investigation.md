# Settings/テーブル レイアウト崩壊 — 原因究明レポート（2026-07-31）

## 結論（証明済みの因果連鎖）

**フォントは無関係。** 原因は `AgentPanelWindow.CreateGUI()` 内の
`root.styleSheets.Clear()`（AgentPanelWindow.cs:120、コミット 746156c
"Phase 3: history browser, settings view, model picker, context meter" で導入）が、
パッケージの 4 シートだけでなく **Unity が EditorWindow の rootVisualElement に暗黙に
付与しているエディタ既定スタイルシート `DefaultCommonDark_inter.uss` まで削除している**こと。

既定シートには `.unity-scroll-view__content-container { flex-shrink: 0; }` を含む
ScrollView 動作の前提ルールが入っている。これが消えると content-container は
UI Toolkit の初期値 **flex-shrink: 1** に落ち、コンテンツがビューポートより大きい
ScrollView では「はみ出してスクロール」ではなく「**ビューポート高さまで flex 圧縮**」
される。Yoga は CSS の min-content 自動下限を実装しないため、子要素（既定 shrink:1 の
Label など）は比例配分でほぼ 0px まで潰れる。**テキストは潰れた 1〜4px の箱に対して
フォントサイズどおり描画される**ため、行同士が重なった「崩壊」に見える。

因果連鎖:

```
CreateGUI: root.styleSheets.Clear()                      (746156c で追加)
  → DefaultCommonDark_inter.uss がウィンドウ root から消える
  → .unity-scroll-view__content-container { flex-shrink: 0 } が失われる
  → content-container が初期値 flex-shrink: 1 になる
  → コンテンツ > ビューポートの ScrollView で content が viewport 高さに圧縮
  → 子（shrink:1 の Label 等）が比例圧縮で h=1〜4px に
  → グリフは通常サイズで描画 → 重なり（Settings / History / md テーブル / コードブロック）
```

Settings が最も酷いのは、縦 ScrollView 直下の全コンテンツが圧縮対象で、かつ
TextField / Toggle / Dropdown などネイティブコントロールの見た目も既定シート由来
（これも同時に消えている）だから。チャット本文が概ね無事なのは、AgentPanel.uss が
メッセージ行に明示 `flex-shrink: 0` を多用しており、本文サーフェスはパッケージ USS で
完結しているため。ステータスバーも同様（明示 shrink:0）。Markdown テーブルは
`uap-md-tablewrap`（横 ScrollView）なので同じ機構が**横軸**で発火し列が潰れる。

## プローブ証跡（すべて本日、ライブエディタで採取・原文引用）

### 1. フォント資産は健全・最新インスタンス（H1 棄却）

esc1-inspect.cs（ウィンドウはフォーカス状態 winFocused=True）:

```
loaderSource=osasset:Yu Gothic UI
loaderAsset: csnull=False fakenull=False id=-7104 name= atlas=1024x1024 srcRef=null
rootInline.fontAsset: csnull=False fakenull=False id=-7104 ...
rootResolved.fontAsset: csnull=False fakenull=False id=-7104 ...
settingsTitle: h=1.5 w=300.0 y=0.0 fontSize=14 display=Flex
sectionTitle: h=3.0 ... fontSize=11 ...
  sectionTitle.resolvedFa: csnull=False fakenull=False id=-7104 ...   ← 壊れたラベルも最新の健全アセットを解決
statusText: h=15.0 ...
  statusText.resolvedFa: ... id=-7104 ...                              ← 同じアセットで正常高さ
winFocused=True hasFocus=True
```

root のインライン fontAsset・FontLoader.JapaneseUiFontAsset・壊れたラベルの解決値は
**すべて同一インスタンス（id=-7104）で fake-null でもない**。stale フォント説（H1）は棄却。
（PM 計測時の「ReferenceEquals 不一致」は、ドメインリロードを跨いだ計測の可能性が高い。）

### 2. テキスト計測は今この瞬間も正しい — レイアウトだけが潰れている

esc2-measure.cs:

```
settingsTitle: text='Settings' layoutH=1.5 measUndef=74.5x18.5 ... flexShrink=1
sectionTitle: text='CLI' layoutH=3.0 measUndef=16.5x15.0 ... flexShrink=1
statusText: text='Idle' layoutH=15.0 measUndef=18.0x15.0 ... flexShrink=0   ← 唯一の差は flexShrink
secTitle 'CLI' h=3.0 ... parentH=51.5 parentY=13.5
secTitle 'Conversation' h=3.0 ... parentH=188.0 parentY=81.0
secTitle 'Appearance' h=4.5 ... parentH=58.5 parentY=285.0
secTitle 'Diagnostics' h=3.0 ... parentH=26.0 parentY=359.5
```

MeasureTextSize は 18.5px / 15px と正しい値を返す。フォント・テキストエンジンは健全で、
**flex 圧縮だけが原因**であることを示す（セクションの高さ合計はビューポート 401.5px に
きっちり収まるよう比例圧縮されている）。

### 3. ScrollView の content-container が viewport に圧縮されている

esc3/esc4:

```
settingsSv: svH=401.5 viewportH=401.5 contentH=401.5 contentShrink=1 ... vscrollerHigh=0.0
（ライブパネル内の全 ScrollView — uap-msg-scroll / uap-toolcard-scroll /
  uap-md-tablewrap(横) / uap-code-scroll(横) / uap-history / uap-perm-details —
  すべて contentShrink=1）
```

content 高さ = viewport 高さ、スクローラ無効（high=0）。「スクロールせず圧縮」状態。

### 4. 介入実験: contentContainer.style.flexShrink = 0 で即時全快

esc4 で settings の content-container にのみ `flexShrink=0` を注入 → esc5-recheck.cs:

```
settingsSv: viewportH=377.0 contentH=3638.5 contentShrink=0 vscrollerHigh=3261.5 vscrollerDisp=Flex
settingsTitle h=17.0
secTitle 'CLI' h=16.0 parentH=550.0 ...
secTitle 'Conversation' h=16.5 parentH=2183.5 ...
```

contentH 401.5 → 3638.5、スクローラ復活、全ラベルが自然高さに回復
（スクリーンショット: docs/verify/Agent Panel_20260731_140400_270.png）。
※これは因果証明のためのメモリ内注入であり、製品コードは未変更。次回リビルドで消える。
※ただし TextField 等ネイティブコントロールの見た目は依然崩れている
（既定シート全体が消えているため。shrink だけの問題ではない）。

### 5. 犯人の同定: パッケージ USS は無罪、`styleSheets.Clear()` が既定シートを消している

新規ユーティリティウィンドウに 5 列の ScrollView（素 / AgentPanel.uss のみ /
ThemeDark.uss のみ / FontScale.uss のみ / 4 シート+テーマクラス）を構築（esc6/esc7）:

```
vanilla:    viewportH=100.0 contentH=300.0 ccShrink=0 label0H=15.0 vscrollHigh=200.0
agentpanel: viewportH=100.0 contentH=300.0 ccShrink=0 ...
themedark:  ... ccShrink=0 ...
fontscale:  ... ccShrink=0 ...
all4:       ... ccShrink=0 ...            ← パッケージシートを全部載せても正常
```

次に同ウィンドウで CreateGUI と同じ操作（root.styleSheets.Clear() + 4 シート追加）を
再現（esc8/esc9）:

```
agentPanelRoot sheetCount=4: [AgentPanel][ThemeDark][ThemeLight][FontScale]   ← ライブパネル: 既定シートが無い
bisectRoot(before) sheetCount=1: [DefaultCommonDark_inter.uss]                ← 通常ウィンドウには暗黙付与されている
bisectRoot(after clear+4) sheetCount=4: [AgentPanel][ThemeDark][ThemeLight][FontScale]

（Clear 後の再計測）
vanilla: svH=100.0 viewportH=0.0 contentH=0.0 ccShrink=1 label0H=0.0 vscrollHigh=0.0
（5 列すべて ccShrink 0→1 に反転し崩壊）
```

**Clear() 一発で健全ウィンドウが本症状そのものを再現。** 逆方向（shrink=0 注入で回復）と
合わせ、双方向で因果が閉じた。

## 各仮説の判定

| 仮説 | 判定 | 根拠 |
|------|------|------|
| H1 stale インラインフォント | **棄却** | 証跡 1: 全経路が同一の生きたアセット id=-7104。fake-null なし。健全アセットを解決している sectionTitle も h=3.0 のまま |
| H2 hidden ビルドの計測不能 | **棄却** | 証跡 5: 可視構築の素 ScrollView も Clear() 後に崩壊。また証跡 4: 表示中の Settings が再構築なしで回復 |
| H3 バックグラウンドエディタ起因の見かけ上の凍結 | **部分的に該当だが本質ではない** | 症状はフォーカス状態（winFocused=True）で再現・計測済み。PM の「ポーク後も回復しない」観測のみ delayCall 飢餓の影響を受けた可能性（本調査では毎呼び出し＝毎 tick 方式で回避） |
| H4 その他（実際の原因） | **確定** | styleSheets.Clear() による既定シート喪失 → content-container flex-shrink 0→1 → flex 圧縮 |

## 修正方針（本ステージでは未実装）

1. **根本修正**: AgentPanelWindow.cs:120 の `root.styleSheets.Clear()` をやめ、
   **パッケージ自身の 4 シートだけを選択的に remove** する
   （`root.styleSheets.Remove(sheet)` を LoadStyleSheets 対象の 4 アセット参照に対して実行、
   または初回ロード時に保持した参照リストで除去）。Unity 暗黙の
   DefaultCommonDark/Light_inter.uss には触れない。これで ScrollView の
   flex-shrink:0 も、TextField/Toggle 等ネイティブコントロールの既定スタイルも戻る。
2. 再発防止として、`unity-scroll-view__content-container` の flex-shrink:0 を
   AgentPanel.uss にも冗長に明記する防御は可（ただし 1. が本命。既定シート喪失は
   shrink 以外の広範な崩れも引き起こすため、防御だけでは不十分）。
3. 検証: 修正後、esc5 相当のダンプで contentShrink=0 / スクローラ有効 /
   ラベル自然高さを確認し、Settings・History・md テーブル（横軸）・コードブロックの
   スクリーンショットを取る。再入 CreateGUI（Clear の導入動機だった二重 CloneTree ガード）が
   選択的 remove でも成立することを確認する。

## 未証明のまま残る点

- `DefaultCommonDark_inter.uss` 内に `.unity-scroll-view__content-container
  { flex-shrink: 0 }` が字面どおり存在することはアセット内容として直接読んでいない
  （存在＝ccShrink 0、除去＝ccShrink 1 という双方向の実測で機能的には証明済み）。
- PM 計測時の「fontAsset が ReferenceEquals 不一致」の正確な発生機序
  （ドメインリロード跨ぎと推定）。本日の再計測では不一致は再現せず、原因とも無関係。
- チャットのメッセージリスト ScrollView も contentShrink=1 のため、トランスクリプトが
  ビューポートを超えた際のスクロール量計算（vscrollerHigh）が壊れている可能性が高い
  （行自体は明示 shrink:0 で潰れないが、スクロール不能・クリップの形で現れうる）。
  修正 1. で同時に解消される見込みだが、個別の実測はしていない。

## プローブ側の後始末・残置状態

- PM の残置ウィンドウ `UAPFontRouteProbe` をクローズ（`UAPFontProbe` は既に不在）。
- 本調査の `UAPEscBisect` ウィンドウはクローズ済み。
- ライブパネルの Settings content-container にはメモリ内 `flexShrink=0` が残っている
  （製品コード無変更。次のドメインリロード / ウィンドウ再構築で消える一時緩和）。

プローブスクリプト: scratchpad/esc1-inspect.cs 〜 esc9-close.cs
（C:/Users/colloid/AppData/Local/Temp/claude/C--Users-colloid-Dev-UnityAgentPanel/2449ec47-d696-4a62-b24e-19275bad5486/scratchpad/）
