# ツールカードの詳細が大きな入力で空白になる(65535 頂点上限)と Write/Edit の変更内容ビュー

日付: 2026-09-13 / 対象: v0.46.0 / 関連: 2026-09-12-tool-result-image-preview.md、
ux-spec 3.7(Edit の diff プレビュー、`PermissionEditPreview`)

## 1. 現象

ユーザー報告: 「Write の内容を確認しようとしたら以下のエラーが出て、内容が空白に
なっていた」

```
A VisualElement must not allocate more than 65535 vertices.
UnityEngine.GUIUtility:ProcessEvent (int,intptr,bool&)
```

ツールカードを展開すると「入力」セクションが空で、コンソールに上の例外が出る。

## 2. 原因

UI Toolkit のテキスト描画は 1 グリフにつき 4 頂点を使い、1 つの `VisualElement`
が確保できる頂点数は 65535 まで。つまり 1 つの `Label` / `TextField` に
約 16,000 文字(65535 / 4 = 16383 グリフ)を超える文字列を入れると、再描画の
途中で上の例外が投げられ、その要素は何も描かれない。2022.3 と Unity 6 の既定
テキストジェネレータの両方に同じ上限がある。

`ToolActivityCard.CreateSection` は `record.inputJson` を丸ごと 1 つの `Label`
に流し込んでいた。Write は `content` にファイル全文を持つので、少し大きな
ファイルを書くだけで上限を超える。Result セクションも同じ経路。

同じ構造が他にもあった。

- `CodeBlockElement`: Markdown のコードブロック本文を 1 つの read-only
  `TextField` に入れている(TextField の内部テキスト要素も同じ上限)。
- `MessageBlockFactory` の添付ペイロード(貼り付けたテキスト)を 1 つの `Label`
  に入れている。
- `PermissionCard` は 800 文字でキャップ済みなので対象外。
- `MarkdownRenderer.CreateRichLabel`(段落)はリッチテキストのタグをまたいで
  分割できないため今回は対象外。モデルの出力は段落単位に分かれるので、1 段落で
  16,000 文字を超えることは実質無い。既知の制限として記録する。

## 3. 修正: `LongTextChunker` で分割して要素を積む

`Editor/UI/LongTextChunker.cs`(Unity API 非依存、純粋関数)。

- `Split(text, maxChars = 8000)`: 上限以内で改行(改行は前のチャンクの末尾に
  残す)、無ければ空白、無ければ硬分割。サロゲートペアの途中では切らない。
  8,000 は 16,383 の約半分で、フォールバックフォントや下線などの追加クアッドの
  余裕を取りつつ、1 要素あたりのレイアウトコストも抑える値。
- 先頭以外のチャンクは `--cont`、末尾以外は `--more` 修飾クラスを付け、USS で
  上下 padding を 0 にして継ぎ目を見せない。

適用箇所:

| 場所 | 変更 |
|---|---|
| `ToolActivityCard.CreateSection` | 1 Label → チャンクごとの `Label`(`.uap-toolcard-pre`)。200,000 文字超は先頭だけ描画し「… あと N 文字(コピーで全文)」のフッター + タイトル行のコピーボタン(全文をクリップボードへ) |
| `CodeBlockElement` | 1 TextField → チャンクごとの read-only `TextField` を `.uap-code-column` に縦積み。コピーボタンは従来どおり raw 本文 |
| `MessageBlockFactory`(添付ペイロード) | チャンクごとの `Label`(`.uap-attach-pre`) |

## 4. Write/Edit/MultiEdit の「変更内容」ビュー

ついでの要望「Diff を使ったファイル確認機能」。完了したファイル系ツールの
カードを展開したとき、`{"file_path":..., "content":"...\n..."}` という JSON
のダンプではなく、ファイルへの変更そのものを見せる。

`Editor/UI/ToolCardFileDiff.cs`(純粋関数):

- `IsFileChangeTool`: Write + Edit 系(`PermissionEditPreview.IsEditTool`)。
- `BuildLines`: Edit/MultiEdit は権限カードと同じ `PermissionEditPreview.
  BuildEditDiff`(承認時に見た diff と完了後の表示が一致する)。Write は
  「前」のテキストが無い(CLI がファイル全体を書く)ので、本文を全行 `Add`
  として返す。これは新規ファイルの unified diff と同じ読み方になる。
  入力が想定の形でなければ空を返し、呼び出し側は従来の raw JSON 表示に
  フォールバックする(間違った diff は決して出さない)。
- `FilePathOf` / `CopyTextOf`(Write の raw `content` のみ)。

描画(`ToolActivityCard.TryCreateFileChangeSection`):

- タイトル「変更内容」+ 対象パス(`.uap-toolcard-path`、ツールチップに全文)。
- `+`/`-` 行は `PermissionCard.DiffLineStyle`(今回 `MakeDiffLineLabel` から
  抽出)で権限カードと同じ `.uap-perm-diff-*` の色を使う。
- 最初は 40 行(`DiffPreviewMaxLines`)、「全 N 行を表示」で 2,000 行
  (`DiffHardMaxLines`)まで。それ以上は「… あと N 行(コピーで全文)」。
  1 行 = 1 Label なので 10 万行の Write で 10 万要素を作らないための上限。
- 各行も `LongTextChunker` を通す(minify された 1 行ファイル対策)。
- Write はタイトル行にコピーボタン(raw `content`)。Edit 系は old/new が
  ファイルではないのでボタン無し。

## 5. 検証

この環境に Unity 実機は無いため、EditMode テストとコードレビューで担保する。

- `LongTextChunkerTests`: 空/上限ちょうど/改行優先/空白フォールバック/硬分割/
  サロゲートペア/実ファイル風テキストで欠落無し。
- `ToolCardFileDiffTests`: Write の全行 Add(末尾改行は行に数えない)、
  Edit の委譲、非対象ツール・不正 JSON で空。
- `ToolActivityCardLongTextTests`: 51,000 文字のセクションが複数 Label に
  分かれ各要素が上限以下で内容が欠けない、200,000 文字超のフッターとコピー、
  Write/Edit カードの変更内容ビューと raw へのフォールバック、diff の
  「全 N 行を表示」→ 硬上限フッター、1 行 24,000 文字の分割、長いコード
  ブロックが複数 TextField になる。

## 6. 既知の制限

- `MarkdownRenderer.CreateRichLabel` の段落は未分割(2 節参照)。
- Write の diff は「前」の内容を持たないので常に全行追加。既存ファイルの
  上書きでも同じ表示になる。行番号は出さない。
