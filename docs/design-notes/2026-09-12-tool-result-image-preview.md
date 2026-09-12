# ツール結果の画像プレビュー(スクリーンショット / 画像生成の結果を会話欄に表示)

2026-09-12。Core v0.43.0。

## 1. 症状

`uap_editor_screenshot`(`return_image:true` でも、パスだけ返す既定でも)、
`Read` で PNG を開いたとき、MCP の画像生成ツール(comfy-cloud 等)が画像を
返したとき、いずれもパネル側には**何も表示されない**。ツールカードの Result
欄は `content` が配列(MCP ツールは常に配列)だと空のまま(`AgentHub.
OnToolResultReceived` は `ResultContent.IsString` のときしか要約を作らなかった)、
画像ブロックは読み捨てられ、ユーザーは Temp/ のパスを自分で開くしかなかった。
モデルは画像を「見て」いるのに、隣にいる人間だけが見られない状態。

ユーザーからの報告: 「画像生成やスクリーンショット等で画像が返ってきたときに
プレビューする仕組みが無い」。

## 2. 設計

### 2.1 画像の出どころ(`ToolResultImages`、`Editor/Model/`、Unity 非依存)

tool_result の `content` から画像を 2 系統で拾い、**ファイルパスの列**に解決する
(`ToolCallRecord.resultImagePaths`、上限 `MaxImagesPerResult = 4`)。

1. **埋め込み画像ブロック**。ストリームが運ぶ API 形
   `{"type":"image","source":{"type":"base64","media_type","data"}}`(PNG の
   `Read`、`return_image:true`、MCP 画像生成ツール。CLI は MCP 形を API 形に
   変換して流す)と、ブリッジがそのまま転送した場合に備えた MCP 形
   `{"type":"image","data","mimeType"}` の両方。注入された saver
   (エディタでは `ImageAttachmentStore.Save`、履歴復元では
   `TranscriptLoader.ImageSaver` と同じシーム)で
   `Library/AgentPanel/Attachments/<sha1>.<ext>` に書き、パスだけを持つ。
   貼り付け画像と同じ置き場・同じ 7 日保持なので、掃除の仕組みは増えない。
   PNG / JPEG 以外(`ImageConversion.LoadImage` が読めない gif / webp)、URL
   ソース、空・8 MiB 超・壊れた base64 は**その 1 枚だけ**を飛ばす。
2. **結果テキストが名指しした既存ファイル**。`return_image` なしの
   `uap_editor_screenshot` は `Captured Game view to C:/proj/Temp/UapScreenshots/
   x.png (1920x1080).` と返す。絶対パス・UNC・`Assets|Library|Temp|Packages/`
   始まりで `.png|.jpg|.jpeg` で終わるトークンを正規表現で拾い、注入された
   resolver(`File.Exists`、AgentHub ではプロジェクト相対も試す)が存在を
   確かめたものだけ採用する。厳密パターン(空白で切る)で 1 件も解決しない
   ときだけ、空白を含むパス(`/Users/me/My Project/Temp/x.png`)向けの緩い
   パターンを試す。緩いパターンは散文を巻き込みうるが、存在しないパスは
   捨てられるので誤表示にはならない。末尾の先読みで `x.png.bak` /
   `x.pngfoo` を除外しつつ文末の `x.png.` は通す。

**埋め込みがあればテキストのパスは見ない**。`return_image:true` は同じ 1 枚を
パスでも本文でも返すので、二重表示を避けるための規則。

### 2.2 経路

| 経路 | 変更 |
|---|---|
| ライブ(`AgentHub.OnToolResultReceived`) | 要約を `TranscriptLoader.ExtractResultSummary`(文字列 or 最初の text ブロック)に統一 → 配列結果でも Result 欄が埋まる。続けて `ToolResultImages.Resolve(content, ImageAttachmentStore.Save, 存在確認)` を `resultImagePaths` に。例外は空リスト(絵の失敗はツール失敗ではない) |
| 履歴復元(`TranscriptLoader.CompleteToolCall` / `CompleteSubagentToolCall`) | 同じ `Resolve` を `ImageSaver` + `File.Exists` で。saver 未設定(テスト)なら埋め込みは飛ばし空リスト |
| キャッシュ(`SessionCacheFile`) | `resultImages` 配列を**画像があるときだけ**書く(加算的、旧キャッシュはバイト一致のまま読める) |
| ACP(`AcpProtocolBridge`) | `content` 項目の `image` を `[image]` 文字列に潰さず API 形で `ToolCallState.Images` に保持し、`CompleteToolCall` で `content` を配列(text + images)にする。画像が無いときは従来どおり文字列 |

### 2.3 表示(`ToolActivityCard`)

ヘッダー直下、**折りたたみ details の外**に `uap-toolcard-images` の帯を置き、
1 枚ずつ `MessageBlockFactory.CreateImageBlock`(ユーザーバブルの添付画像と
同じ要素: ScaleToFit、クリックで OS ビューア、ファイルが消えていれば
「(image file no longer available)」、キャプションはファイル名 + 実寸)を並べる。
サムネイルはカード内なので 240×150 に抑える(バブル側は 320×200)。

画像を持つカードは **3 枚以上の連続完了カードを畳む「N tools」グループから
除外**する(`MessageBlockFactory.IsCompletedToolBlock`)。サムネイルこそが
見たい結果であり、グループ行の裏に隠れてはいけない。`MessageListController`
の行シグネチャに `resultImagePaths.Count` を足し、画像が付いた時点で行を
作り直す(実際には完了時に status も変わるので冗長だが、安全側)。

## 3. 採らなかった案

- **カードを開いたときだけ表示**: 開かないと気づけないのでは報告された症状
  の解決にならない。
- **画像ブロックを別メッセージとして出す**: どのツールの結果か分からなく
  なる。カード内に置く方が因果が読める。
- **テキスト中のパスを無条件に採用**: 存在確認なしだと、モデルが「後で
  `/tmp/out.png` に書きます」と言っただけで空のプレースホルダが並ぶ。
- **gif / webp も保存**: サムネイルが出せず「missing」表示になるだけ。
  デコードできる形式に絞る。

## 4. 検証

- `Tests/Editor/ToolResultImagesTests.cs`: 両ワイヤ形、壊れたブロックの
  スキップ、上限、saver 例外、埋め込み優先、空白パスの緩い解決、相対パスの
  resolver 経由、厳密パターンの形(Windows / UNIX / 引用 / `Assets/` /
  `.gif` 除外 / `.png.bak` 除外 / 重複)、media type 正規化、
  `SessionCacheFile` 往復(キーの省略含む)、`TranscriptLoader` 復元
  (saver あり / なし)。
- `Tests/Editor/AgentHubToolResultImageTests.cs`: FakeCliProcess 経由で
  埋め込み画像がスクラッチ store に落ちて record に載る、配列結果の要約、
  テキストのパス採用、テキストのみは空、カードの帯が details 外に出る /
  無ければ出ない。
- 正規表現と `Resolve` の分岐は .NET 8 のコンソールハーネスでも同じ入力で
  通した(Unity のない環境でのコンパイル + 実行確認)。EditMode スイートは
  CI(2022.3 / 6000.x)で回る。
