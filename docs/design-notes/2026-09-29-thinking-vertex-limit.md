# 思考ブロックを開くと 65535 頂点上限の例外が出る(長い thinking の分割描画)

日付: 2026-09-29 / 対象: v0.60.1(beta 系列) / 関連: 2026-09-13-toolcard-vertex-limit.md、
2026-08-01-thinking-content-loss.md

## 1. 現象

ユーザー報告: 「思考の内容を開いた際に、思考が長すぎると Unity が以下のエラーを起こす」

```
A VisualElement must not allocate more than 65535 vertices.
UnityEngine.GUIUtility:ProcessEvent (int,intptr,bool&)
```

思考 Foldout を展開すると本文が描かれず、コンソールに上の例外が出る。

## 2. 原因

2026-09-13 の設計ノートと同じ上限。UI Toolkit のテキスト描画は 1 グリフにつき
4 頂点を使い、1 つの `VisualElement` は 65535 頂点までしか確保できないので、
約 16,000 文字を超える文字列を 1 つの `Label` に入れると再描画の途中で例外が
投げられ、その要素は何も描かれない。

前回の修正はツールカード・コードブロック・添付ペイロードを `LongTextChunker`
で分割したが、`MessageBlockFactory.CreateThinkingBlock` は思考本文を 1 つの
`Label`(`.uap-thinking-text`)に流し込んだままだった。CLI v2.1.218 以降は
実際の思考テキストが届く(2026-08-01 ノート第 7 節)ため、長考するモデルでは
容易に上限を超える。

さらに、ストリーミング中の思考・本文は `StreamingLabelPump` が 1 つの
`Label` を書き換え続けるので、確定前でも同じ上限に当たり得る。

## 3. 修正

### 3.1 確定済みの思考: チャンクごとに Label を積む

`CreateThinkingBlock` の非ストリーミング経路は `LongTextChunker.Split` で分け、
チャンクごとに `.uap-thinking-text` の `Label` を Foldout に積む。2 つ目以降は
`uap-stream-cont` を付け、USS で上側の margin / padding を 0 にして継ぎ目を
見せない。

### 3.2 ストリーミング: `StreamingLabelPump` をチャンク対応に

Pump は追跡中の `Label` に `LongTextChunker.DefaultMaxChars`(8,000 文字)を
超える文字列を入れない。表示文字列を `LongTextChunker.Split` し、先頭チャンクを
追跡 Label に、残りを **継続 Label** に入れる。継続 Label は追跡 Label の直後
(同じ親)に挿入し、追跡 Label の USS クラスをそのまま複製して
`uap-stream-cont` を足す(`enableRichText = false`、`parseEscapeSequences =
false` も揃える)。

- チャンク境界は「そこまでの文字列」だけで決まるので、テキストが伸びても以前の
  チャンクの内容は変わらず、最後の Label と新規 Label だけが書き換わる。
- 対象が縮んだとき(ブロック差し替え)は不要になった継続 Label を取り除く。
- 同じ Label を再 Track したときは旧エントリの継続 Label を先に取り除く
  (残すと同じ文字列が二重に出る)。
- 追跡 Label に親が無い場合(単体テスト等)は継続 Label を置く場所が無いので
  全文をそのまま入れる(黙って切り詰めない)。
- 確定フラッシュ(`streaming == false`)もチャンク経由で全文を書き、その直後に
  MessageListController の構造再構築が確定版(3.1 / Markdown)に置き換える。

ストリーミング中の本文 `Text` ブロック(`.uap-text` 1 枚)も同じ Pump を通る
ので、副作用として同じ保護が掛かる。

## 4. テスト

- `StreamingLabelPumpTests`: 長文 Track で継続 Label が増え、連結すると元の
  文字列+カーソルに戻る / 伸長で増え・縮小で減る / 確定フラッシュ後も分割
  されたまま全文 / 再 Track で旧継続 Label が消える / 親無しは全文保持。
  `FlushNowForTests` はインターバルを無視して 1 回フラッシュし、テストの
  detached ツリー(panel == null)を捨てないようにする。
- `MessageBlockFactoryThinkingTests`: 51,000 文字の thinking が複数の
  `.uap-thinking-text` に分かれ、1 枚ずつ上限以下で、連結すると元の文字列。
  短文は 1 枚のまま。

## 5. 残る既知の制限

`MarkdownRenderer.CreateRichLabel`(確定後の段落)はリッチテキストのタグを
またいで分割できないため引き続き対象外(2026-09-13 ノート第 2 節と同じ)。
