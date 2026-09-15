# uap_rect_transform_set: uGUI のレイアウト値を 1 回で読み書きする

日付: 2026-09-15 / 対象: `jp.colloid.unity-agent-panel`(Core)

## 背景

`uap_transform_set`(2026-09-08 の
`2026-09-08-menu-timeout-and-tool-steering.md`)は、実際のセッションで
エージェントが `uloop execute-dynamic-code` に逃げていたのを見て入れた
ツールだった。理由は 2 つ:

1. `uap_property_set` は **local しか書けず**、position + rotation で
   2 回呼ぶ必要があった。
2. エージェントが欲しかったのは world の `transform.position` だった。

その `uap_transform_set` が書くのは `localPosition` /
`localEulerAngles` / `localScale` の 3 つだけである。RectTransform に
対しては、これは**ほぼ何も書けていない**。

### localPosition は RectTransform の位置ではない

RectTransform の `localPosition` は、アンカー・ピボット・
`anchoredPosition` から Unity が**毎レイアウトパスで計算し直す**従属値
である。両者が一致するのは「親の rect の中心にアンカーが 1 点で置かれ、
ピボットが中心」という特殊な場合だけで、少しでもアンカーを動かした
瞬間にずれる。

つまり、アンカーされた uGUI 要素に対して `uap_transform_set` で
`position` を書くと、**その場では動いたように見えて、次のレイアウト
パスで元に戻る**。エージェントから見れば「ツールが黙って失敗した」に
等しく、`uap_transform_set` の説明文が
「Transform, RectTransform, Camera, lights, any object」と謳っていた
ぶん、そこへ誘導されやすい罠だった。

### 残る経路は uap_property_set の 5 連打

RectTransform のレイアウトを正しく書く手段は `uap_property_set` しか
なく、シリアライズプロパティ 1 つにつき 1 回呼ぶ:

- `m_AnchorMin` / `m_AnchorMax` / `m_Pivot` /
  `m_AnchoredPosition` / `m_SizeDelta`

2026-09-08 のノートが Transform について測った「2 回必要」という
call-count の議論が、ほぼ倍の広さのフィールド集合に対してそのまま
当てはまる。しかも Transform と違い、**呼ぶ前にレイアウト計算が要る**:

> 「このボタンを左上にアンカーして、見た目は動かさないで」

これを `uap_property_set` でやるには、親の rect のサイズを取得し、
アンカー比率から親空間の座標を出し、新しい `anchoredPosition` と
`sizeDelta` を自分で計算してから書く。**この「自分で計算する」が、
エージェントが動的コードに手を伸ばす動機そのもの**である
(2026-09-08 ノートの結論と同じ構造)。

なお `m_Pivot` は、この時点までリポジトリのどの説明文にも
一度も出てこなかった。エージェントがピボットを触る導線は事実上
存在しなかったことになる。

### 証拠の強さについて

正直に書いておくと、2026-09-08 のノートや `uap_editor_select`
(`2026-09-14-selection-set-tool.md`)と違い、**これが実セッションで
人を刺した記録はない**。根拠は「同じ call-count の議論の外挿」と、
上に書いた `localPosition` 罠がコード上実在することの 2 点である。
実測ではなく推論であることを、ここで明示しておく。

## 設計

`uap_rect_transform_set`(`core` モジュール、Core)を追加。
`path` 必須、書き込みフィールドを全部省略すると純粋な読み取りになる
のは `uap_transform_set` と同じ。

書けるのは RectTransform の**アンカー相対**の 6 値:
`anchorMin` / `anchorMax` / `pivot` / `anchoredPosition` /
`sizeDelta` / `offsetMin` / `offsetMax`。

**回転とスケールは入れない。** そこは `uap_transform_set` の担当で、
RectTransform でも素直に動く。2 つのツールが同じフィールドを
書けるようにすると、どちらを使うべきかという問いが毎回発生する。
境界は「アンカー相対か否か」で引いた。

### 部分マージ

各フィールドは `{x}` だけ、`{y}` だけを渡せる。渡さなかった軸は
現在値を保つ。`uap_transform_set` の `position: {y: 90}` と同じ規則。

マージの基準値は**そのフィールドを書く瞬間**に読む。アンカーを
変えたうえで `offsetMax: {x: -10}` を渡した場合、基準は
「アンカーを変えた後の offsets」になる。これが唯一自己整合的な
選択で、結果は呼び出し後に RectTransform が報告する値と必ず一致する。

### アンカーとピボットは raw に書く

Unity の `anchorMin` / `anchorMax` / `pivot` の setter は、
**位置やサイズを補正しない**。`anchoredPosition` と `sizeDelta` の
数値はそのまま残り、結果として矩形が動く・伸びる。

一方 Inspector のアンカーウィジェットは補正する(プリセットを
クリックしても見た目は動かない)。エージェントは Inspector の挙動を
基準に考えるので、ここは食い違う一番危ない場所である。

このツールは **Unity の setter に合わせて raw に書く**方を選んだ。
「賢く」補正するツールは、補正してほしくない場面で驚きを生み、
どちらの挙動だったか呼ぶ側が覚えていられない。代わりに:

- スキーマの説明文で「補正しない」と明言する
- 補正が欲しい場合のために `keepRect` を用意する(次節)

### keepRect: 動かさずにアンカーし直す

`keepRect: true` は、アンカー/ピボットの変更を挟んで**矩形を厳密に
保つ**。実装は単純で、変更前に矩形の 4 辺を**親の rect のローカル
座標**で記録し、変更後にその位置へ戻るよう `offsetMin` /
`offsetMax` を書き直す:

```
anchorPoint(a) = parent.rect.min + a * parent.rect.size
記録:   min = anchorPoint(anchorMin) + offsetMin
        max = anchorPoint(anchorMax) + offsetMax
復元:   offsetMin = min - anchorPoint(新 anchorMin)
        offsetMax = max - anchorPoint(新 anchorMax)
```

辺の位置はピボットに依存しないので、**アンカー変更とピボット変更が
同じ呼び出しに混ざっていても 1 回のスナップショットで足りる**。

これが冒頭の「左上にアンカーして、見た目は動かさないで」を 1 回の
呼び出しにする。親に RectTransform が無ければ計算できないので、
その場合は黙って無視せず例外にする。

書き順は **アンカー/ピボット → keepRect の補正 → 明示的な
position/size** で、明示的に渡された値が最後に勝つ。
`preset` + `keepRect` + `anchoredPosition: {x: 8}` は
「左上にアンカーし直し、y は動かさず、x だけ 8 に」と読める。

### preset: 16 セル、pivot も設定する

Inspector のアンカープリセット 4x4 を
`<垂直>-<水平>` の名前で持つ(`top-left` … `stretch-stretch`)。
垂直を先に書くのは Inspector の行の並びに合わせるためで、
`stretch-left` が「左寄せの縦一杯」だと一意に読めるようにするため
でもある(`left-stretch` と両方あると、どちらの軸が stretch なのか
毎回迷う)。

**`preset` は pivot も設定する。** Inspector では Shift を押した
ときの挙動にあたるが、`top-left` と言ったエージェントが期待する
のは普通の左上ピボットであって、無関係な古いピボットが残った
「アンカーと測り方が食い違う矩形」ではない。残したい場合のために
`keepPivot: true` を用意した。

`preset` は**アンカーとピボット以外に触らない**。矩形の保存も
オフセットのゼロ化もしない。前者が欲しければ `keepRect`、後者が
欲しければ `offsetMin`/`offsetMax` を同じ呼び出しで渡す。

### 排他は「拒否」であって「優先順位」ではない

3 組を同時に渡すと例外になる:

- `preset` と `anchorMin`/`anchorMax`/`pivot`
- `anchoredPosition`/`sizeDelta` と `offsetMin`/`offsetMax`
- `keepPivot`/`keepRect` を、対応する変更なしで渡す

特に 2 番目は重要で、両者は**同じ 4 つの数値の別表現**である。
優先順位を決めて片方を黙って捨てると、部分マージと組み合わさった
ときに「x は片方、y はもう片方」という誰も意図しない矩形になる。
検証は**書き込み前に全部済ませる**ので、拒否された呼び出しが
中途半端な矩形を残すことはない。

### 読み出しは rect と parentRect も返す

戻り値には 7 つの入力フィールドの現在値に加えて:

- `rect`(`RectTransform.rect`): **解決後の実サイズ**。アンカーが
  伸びている軸では `sizeDelta` はサイズではなく「アンカーが張る幅への
  加算値」なので、`sizeDelta` だけ見ても大きさは分からない。
  `stretch-stretch` + offsets 0 の要素は `sizeDelta` が `(0,0)` で
  `rect` が親と同じ 400x300、という具合に食い違う。
- `parentRect`: アンカー比率が解決される先。親に RectTransform が
  無ければ `null`(その旨の警告付き)。
- `layoutDriver` / `warnings`: 次節。

`localPosition` や world 座標は**返さない**。そこは
`uap_transform_set` の担当で、「現在の状態」を返すツールが 2 つあって
内容が重なると、どちらが正なのか分からなくなる。

## レイアウトドライバの検出

`ContentSizeFitter` が付いた要素に `sizeDelta` を書いても、次の
レイアウトリビルドで上書きされる。エージェントから見ると
「書いたのに戻った = ツールが壊れている」に見える一番厄介な失敗で、
これは黙っていてはいけない。

そこで、自分に付いた `ContentSizeFitter` / `AspectRatioFitter`、
または親に付いた `LayoutGroup` 派生を探し、`layoutDriver` として
**どのコンポーネントがどのパスに付いているか**を返し、書き込みが
その支配下のフィールドに当たっていれば `warnings` に出す。

判定は**型名**(基底クラスまで遡る)で行う。理由:

- uGUI(`com.unity.ugui`)は本パッケージの依存ではなく、CI の
  ホストプロジェクトにも入っていない。`UnityEngine.UI.LayoutGroup` を
  型として書いた時点でコンパイルが通らない。
- `LayoutGroup` という 1 つの名前で Horizontal / Vertical / Grid と
  ユーザー定義のサブクラスまで覆える。

`RectTransform` 自身が持つ driven プロパティのフラグは使わなかった
(後述)。

## テスト

`UapRectTransformSetToolTests`(Core)。RectTransform だけで組み、
uGUI の型は 1 つも参照しない(CI に無いため)。

- 各フィールドの書き込みと、軸ごとの部分マージ
- **アンカーを書いても `anchoredPosition`/`sizeDelta` が変わらない**
  こと = raw 契約そのもの
- `keepRect`: 400x300 の親の中央にある 100x50 の子を `top-left` に
  アンカーし直して、`sizeDelta` が `(100,50)` のまま
  `anchoredPosition` が `(150,-125)` になること(= 矩形は不動)
- `keepRect` の後に明示的な `anchoredPosition` が勝つこと
- `offsetMin`/`offsetMax` を両方書いて両方着地すること、片方だけの
  部分更新でもう片方が動かないこと
- 16 プリセットが**スキーマの enum と実装で一致**すること
  (enum を読んで全部 Execute する)、`stretch-left` と
  `bottom-stretch` でどちらの軸が伸びるかを固定
- 排他 3 組が例外になり、**矩形が変わっていない**こと
- 読み出し: `sizeDelta` が `(0,0)` でも `rect` が 400x300 と報告される
  こと(このツールが `rect` を返す理由そのもの)
- `parentRect` が `null` になる場合と、その警告
- RectTransform が無いオブジェクトで例外、かつ文面が
  `uap_transform_set` を案内すること
- Undo が 5 フィールドすべてを 1 回の `RecordObject` で戻すこと
- `sizeDelta` の警告が**軸ごと**に判定されること(縦だけ伸びている
  要素に `sizeDelta: {x}` を書くのは正しい操作なので警告しない)
- ドライバ判定は**純粋な `Type` 述語として**テストする
  (uGUI が無いので、実際に `LayoutGroup` を付けた end-to-end は
  組めない。この限界は自覚している)

既存の `UapReadOnlyToolMetadataTests`(登録済みツールが 2 集合の
どちらか一方に必ず属する)、`UapCoreToolsMetadataTests`、
`AgentHubStartClientArgTests` にも追加した。

## 誘導の追随

- `AgentHub` の L2 誘導文の core ブロックに、既存の
  `uap_transform_set` の文を**延長する形で**1 句足した。
  この節には「10 行以内」というテストがあり現在 8 行なので、
  行を増やさない書き方を選んでいる。
- `uap_property_set` の説明文の RectTransform の列挙に `m_Pivot` を
  足し、`uap_rect_transform_set` を案内するようにした
  (Transform の列挙が `uap_transform_set` を案内しているのと同じ形)。

## VR(World Space Canvas)の経路

uGUI のレイアウトツールを VR で使うと、前提が 1 つ裏返る。

Screen Space の Canvas は、ルートの rect を **Unity が毎フレーム画面から
計算し直す**。だからそこへの書き込みは残らない。ところが VR の UI は
World Space Canvas で、これは**シーン内の実在オブジェクト**である:
rect は作者が決めるもので、書き込みは残る。

初版はこの区別をしておらず、ルート Canvas なら**render mode を見ずに**
「書いても残らない」と警告していた。つまり **VR の一番普通の操作に対して、
正しい書き込みを「効かない」と言う**という、このツールが出しうる最悪の
誤報になっていた。

### 判定

`UapCanvasProbe` が、自分から親へ遡って最も近い Canvas を探し、
`renderMode` を**リフレクションで**読む。

- Canvas は `UnityEngine.UIModule`(組み込みモジュール)にあり、
  プロジェクト側で剥がすことが許されている。`UapRectTransformLayoutDriver`
  が uGUI の型を避けているのと同じ理由で、型として書かない。
- **読めなかった場合は「不明」であって「Screen Space」ではない。**
  `IsKnownScreenSpace` が null に対して false を返すのはそのためで、
  推測で「効かない」と言わないことを型で担保している。

警告は render mode が Screen Space と**分かっている**ときだけ出す。
World Space では出さない。

### 戻り値

- `canvas`: `path` / `type` / `renderMode` / `worldSpace` / `scope`
  (`self` か `ancestor`)。Canvas 配下でなければ `null`。
- `worldSize`: `rect` のサイズに `lossyScale` を掛けたもの。World Space
  なら**メートル**。VR ではパネルが読めるか・手が届くかを決めるのは
  ピクセルではなくこの値なので、常に返す。

### worldSize による書き込み

VR では「このパネルを 1.2m 幅に」と考える。canvas-local の `sizeDelta`
では考えない。そこで `worldSize` を書き込みにも受ける:

```
目標 rect = worldSize / lossyScale
sizeDelta = 目標 rect - (anchorMax - anchorMin) * 親 rect
```

アンカーが伸びている場合に `sizeDelta` がサイズではない、という
このツールがそもそも説明している事情がここでも効くので、
**アンカーが張る幅を引く**。1000x600 のキャンバスに張り付いた子を
0.5m にすると `sizeDelta.x` は `500` ではなく `-500` になる。
この計算を呼ぶ側にさせないことが、このツールの存在理由そのものである。

`sizeDelta` / `offsetMin` / `offsetMax` とは排他。`lossyScale` が 0 の軸が
あると変換が定義できないので、**書き込み前に**例外にする
(スケールはこのツールが書き換えないので、事前に判定できる)。

Screen Space の Canvas 配下で `worldSize` を使った場合は拒否せず警告に
した。ワールド座標自体は定義できるが、見る人にとって意味が無いため。

### 変えなかったところ

`keepRect` やアンカー計算は**親 rect 空間**で完結しており、
Canvas の render mode に依存しない。VR でもそのまま正しいので、
分岐は足していない。

## 意図的にやらなかったこと

- **`RectTransform.drivenProperties` / `drivenByObject` は使わない。**
  「何かが支配している」ことしか分からず、**どのコンポーネントを
  直せばいいか**を答えられない。しかも遅延されるキャンバスの
  リビルドで更新されるため、Edit モードで書いた直後という、
  このツールが報告するまさにその瞬間に古い値を返す。
- **レイアウトの強制リビルドはしない。** `LayoutRebuilder` は
  uGUI の型で参照できないうえ、同期リビルドは**親や兄弟**の
  シリアライズ値まで書き換える。それらは `Undo.RecordObject` して
  いないので、Ctrl+Z が対象だけ戻して周りを戻さない、という
  最悪の壊れ方をする。`rect` が 1 フレーム古いかもしれない方が
  ずっとましである。
- **`anchoredPosition3D` は入れない。** z は `localPosition.z` そのもの
  で、`uap_transform_set` から届く。
- **`GetWorldCorners` は返さない。** Canvas が無ければ意味がなく、
  読み取りのたびに 12 個の数値が増えるだけになる。
- **回転・スケール**は上述のとおり `uap_transform_set` の担当。
- **VR 用のサイズの「妥当性」判定はしない。** 「このパネルは VR には
  大きすぎる/小さすぎる」は閾値を決め打ちする話になり、用途(壁の
  看板か、手元のメニューか)で答えが変わる。`worldSize` を返すところ
  までが事実で、その先は判断なので入れない。`lossyScale` が 0 という
  客観的に壊れている場合だけ警告する。
