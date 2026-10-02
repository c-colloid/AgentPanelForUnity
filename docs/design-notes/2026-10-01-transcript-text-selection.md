# 2026-10-01 -- 会話ログのテキスト選択・コピー・「選択部分について質問」

## 動機

会話ログの本文は Label で描いているため、エラーメッセージの一部や応答の
一文をコピーしたり、「ここの意味は?」と続けて聞いたりするのに、いったん
全文を読み直して打ち直すしかなかった。コードブロックだけは読み取り専用
TextField(`CodeBlockElement`)で選択できたが、それ以外の文章・思考・
ノート・エラー・ツール結果は選べなかった。AI エージェントとの会話では
「相手の言葉を引用して聞き返す」のが基本動作なので、
(1) ドラッグで選択、(2) コピー、(3) 選択部分を引用して質問、の 3 つを
会話ログに付ける。

## 設計

### 選択は Label 自身に任せる

2022.3 の `TextElement` は `selection.isSelectable = true` でマウス選択
(ドラッグ・ダブルクリック単語・トリプルクリック行)と、フォーカス中の
Ctrl/Cmd+C コピーをサポートする(docs/research/03-unity-integration.md
4.1 の「不安定」という当時の懸念は 2022.2 以前との API 差異の話で、
2022.3 以降は `ITextSelection` に固定されている)。コードブロックのように
TextField に置き換えると、Markdown のリッチテキスト(太字・リンク・
インラインコード)が失われるので、既存の Label をそのまま選択可能にする。

`TranscriptSelection.MakeSelectable` が opt-in の入口で、会話テキストを
出す場所だけが呼ぶ:

- `MarkdownRenderer.CreateRichLabel`(段落・見出し・リスト項目・表のセル・引用)
- `MessageBlockFactory` の本文(ストリーミング中の plain ラベル)・思考・
  システムノート・エラー・添付コンテキストの本文
- `ToolActivityCard` の入力/結果の pre テキスト
- `StreamingLabelPump` の継続ラベル(元ラベルが選択可能なら引き継ぐ)

役割行・ツールカードのヘッダー・チップなどクリックで動く要素は選択可能に
しない(選択マニピュレータは左ボタンの PointerDown を止めるので、
クリックハンドラと競合する)。

### 選択テキストの読み出し

`ITextSelection` は選択範囲の文字列を返さない(`cursorIndex` /
`selectIndex` のみ)。インデックスは TextCore の描画文字単位 --
リッチテキストのタグは数えず、サロゲートペアは 1 つ -- なので、
`TranscriptSelection.ExtractSelection` がラベルの `text` を描画文字列に
直してから切り出す。リッチテキストは `StripRichText` で
`InlineMarkupConverter` が出すタグの逆変換(noparse の中身は字義どおり、
それ以外の `<...>` は捨てる。変換器は生の `<` を必ず noparse で包むので、
noparse の外の `<` は常にタグ)を行う。純関数なので EditMode テストで固定
(`TranscriptSelectionTests`)。

UI Toolkit の選択は要素単位でまたがらない。最後に選択操作をした
ラベル(PointerUp / KeyUp で `HasSelection()` のもの)を
`TranscriptSelection` が 1 つだけ覚え、メニューとショートカットは
それを使う。仮想化で行が解放されたラベル(`panel == null`)や他の
メッセージのラベルは無視する。

### メッセージの右クリックメニュー

`MessageBlockFactory.CreateMessageElement` がメッセージのルートに
`ContextualMenuManipulator` を付ける:

| 項目 | 動作 | 無効になる条件 |
|---|---|---|
| 選択をコピー | 選択テキストをクリップボードへ | このメッセージ内に選択が無い |
| メッセージをコピー | テキストブロックの Markdown 原文を空行区切りで結合してコピー(ツールカード・思考・添付は含めない) | テキストブロックが無い |
| 選択部分について質問 | 選択テキストを `> ` 引用にして入力欄の末尾へ挿入し、その下の空行にキャレットを置く | 選択が無い |

Ctrl/Cmd+C はラベル自身が処理するが、保険としてメッセージのルートでも
同じ選択をコピーする(同じ文字列を 2 回書くだけなので無害)。

「質問」は入力欄に見える形で引用を入れるだけで、コンテキストチップのような
隠れた添付にはしない。利用者が引用を削って整えられるのと、送信文の
どこに何が入るかを見たままにするため。`ComposerView.InsertText` は
挿入後にキャレットを末尾へ置くようにした(TextField はフォーカスで
全選択するので、そのまま打つと引用が消えていた)。

### 見た目

`.uap-selectable` で `--unity-selection-color` を TextField と同じ青に、
`--unity-cursor-color` を透明にする。読み取り専用の文章で点滅カーソルが
出ると編集できるように見えるため。

## 残課題

- 複数ラベルにまたがる選択(段落をまたぐドラッグ)はできない。
  必要なら「メッセージをコピー」で全文を取る。
- 選択ハイライトはフォーカスが別の要素に移っても残る(UI Toolkit の仕様)。
