# ウィンドウリサイズ中の再レイアウト間引き(ScrollReflowThrottle)

日付: 2026-09-16 / 対象: v0.54.3 / 関連: 2026-07-31-scroll-container-shrink-fix.md、
2026-09-05-ui-redesign.md(2.5 narrow モード)、2026-09-13-toolcard-vertex-limit.md

## 1. 現象

ユーザー報告: 「ウィンドウサイズを変えた際に AgentPanel の Repaint がちょっと重い」。

パネルの端をドラッグしてリサイズすると、ドラッグ中のエディタのフレームが
目に見えて落ちる。会話が長い(数十〜数百メッセージ)ほど重く、プロファイラでは
パネルウィンドウの Repaint(UI Toolkit のレイアウト + テキスト生成)が
フレームを占める。

## 2. 根本原因

UI Toolkit は、折り返しラベル(`white-space: normal`)の利用可能幅が変わるたびに
Yoga の measure コールバックから TextCore のテキスト生成をやり直す。
リサイズのドラッグはフレームごとに幅を変えるので、
`MessageListController` が保持する最大 300 行の会話(Markdown、ツールカード、
コードブロック、`LongTextChunker` で分割されたラベル群)が **毎フレーム**
まるごと再計測・再メッシュ化されていた。Settings ビューも数百のラベルを持つため
同じ構造で重い。

ドラッグ中の各フレームのレイアウト結果はユーザーには最終形しか意味がなく、
中間フレームの再折り返しはほぼ全てが捨てられる仕事だった。

パネル自身の `GeometryChangedEvent` ハンドラ(narrow クラスの付け外し、
権限カードの高さ上限)は軽く、原因ではない。

## 3. 対応

`Editor/UI/ScrollReflowThrottle.cs` を追加し、縦 ScrollView の
`contentViewport` の `GeometryChangedEvent` を監視する。

1. **ピン留め**: 幅が実測値どうしで変わった最初のイベントで、
   `contentContainer` の `style.width` を「いま実際にレイアウトされた幅」
   (`contentContainer.layout.width`)に固定する。同じ幅で固定するので、
   Yoga はサブツリーを計測キャッシュで済ませ、テキスト要素には触れない。
   以降ビューポートの幅が変わっても、固定幅のサブツリーは制約が変わらないため
   Yoga がスキップする。
2. **静止で解放**: `schedule.Execute(Release).ExecuteLater(120ms)` を
   イベントのたびに再武装し、120ms 幅が変わらなければ `style.width` を
   `StyleKeyword.Null` に戻す。ここで内容が最終幅へ **一度だけ** 再折り返しされる。
   ドラッグはフレームごと(16〜33ms)にイベントを出すので、120ms の静止 =
   ドラッグ終了とみなせる。
3. **横スクロールバーの抑制**: ピン留め中は内容が縮んだビューポートより広く
   なり得るので、`horizontalScrollerVisibility` を `Hidden` にし、解放時に元へ戻す。
   放置すると横バーが一瞬出てビューポート高さが変わり、同じカスケードを
   誘発する。

結果、1 回のドラッグあたりの全量再折り返しは「フレーム数回」から「2 回
(ピン留め直前の 1 回 + 解放時の 1 回)」になる。

高さだけの変化はラベルを再折り返ししないため判定に含めない
(含めると stick-to-bottom のクランプが遅れるだけ)。
初回レイアウト(旧幅 0/NaN)や折りたたまれたホスト(新幅 0)でもピン留めしない。
これらは `ShouldFreeze(oldWidth, newWidth)` の純関数として切り出し、
`Tests/Editor/ScrollReflowThrottleTests.cs` で固定した。

## 4. 適用箇所

- `MessageListController`(会話): 最も重い。
- `SettingsView` のメインスクロール: 数百の折り返しラベル。
- `HistoryView`: 行数が多くなり得る。

いずれも `ScrollReflowThrottle.Attach(scroll)` の 1 行。参照を保持する必要は
無い(コールバックとスケジュール項目は ScrollView 側の要素が所有する)。

## 5. 見え方への影響

ドラッグ中、内容はドラッグ開始時の幅のまま(広げると右に余白、縮めると右端が
クリップ)で、マウスが止まって約 120ms 後に最終幅へ揃う。
ネイティブアプリのリフロー間引きと同じ挙動で、リサイズ中の 1 フレームごとの
折り返し変化より視覚的にも安定する。

stick-to-bottom は解放時の高さ変化で `OnContentGeometryChanged` が走り、
従来どおり末尾へ寄る。上へスクロールしていた場合の絶対オフセットも従来と同じ。

## 6. 検討して見送った案

- **仮想化(可視行だけを実体化)**: 根本的には最善だが、行の高さ推定と
  スクロール位置の維持を作り直す大きな変更になる。今回の間引きで
  ドラッグ中のコストがほぼ消えるので、まずこちらを採用した。
- **`MaxRenderedMessages` の削減**: 表示できる履歴が減る挙動変更で、
  ユーザーに見えるトレードオフになる。
