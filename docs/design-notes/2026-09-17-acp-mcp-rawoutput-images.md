# codex-acp の `rawOutput`(MCP 結果形)をブロックに戻す

日付: 2026-09-17 / 対象: v0.54.7 → v0.54.8 / 関連: 2026-09-17-modal-menu-and-base64-scan.md §2
(同日の別ブランチ。base64 の O(n²) 走査を検出側で直した件。§2.2 で「ブリッジ側は別途やる価値がある」と
残した分がこれ)、2026-09-12-tool-result-image-preview.md、2026-09-13-acp-feature-parity.md

## 1. 現象

Codex(codex-acp)で `uap_editor_screenshot(return_image:true)` を呼ぶと、

- ツールカードにスクリーンショットが**埋め込み画像として出ない**(テキスト中のファイルパスを
  `ToolResultImages` が拾えたときだけ、ディスク上のファイルから出る。パスが無い・消えた・
  リモートだと何も出ない)。
- ツール結果の「テキスト」が約 500 KB の JSON 文字列(base64 入り)になり、そのままトランスクリプト、
  結果要約、パス走査へ流れる。2026-09-17 のフリーズ B(256 s)の入力がこれだった。

## 2. 根本原因(実測)

codex-acp は MCP ツールの結果を ACP `tool_call_update` の **`content` には入れず**(空配列)、
`rawOutput` に MCP の `CallToolResult` を包んだ形で渡す。

インシデントのセッション JSON(`AITemp/UserSettings/AgentPanel/Sessions/01a0ae8f-dc6a-….json`)に
残っていたツール結果テキスト(= `JsonWriter.Write(rawOutput)` の出力)が全件この形:

```
{"result":{"content":[{"type":"text","text":"pong"}],"structuredContent":null,"_meta":null},"error":null}
{"result":{"content":[{"type":"text","text":"Captured Scene view to C:/…/uap_scene_20260917_085826_593.png (8…
```

同じ呼び出しの Codex ロールアウト(`~/.codex/sessions/2026/09/17/rollout-2026-09-17T17-50-58-….jsonl`、
ordinal 72 の `McpToolCall`)で `result.content` の中身を確認:

```
"result":{"content":[
  {"type":"text","text":"Captured Scene view to C:/Unity/…/uap_scene_20260917_085826_593.png …"},
  {"type":"image","data":"iVBORw0KGgo…<560,068 chars>","mimeType":"image/png"}],
 "isError":false}
```

`AcpProtocolBridge.AppendToolOutput` は `content` を訳して何も得られなければ
`rawOutput` を `JsonWriter.Write(raw)` で**丸ごと文字列化**していた。image ブロックは
`state.Images` に入らず、base64 は結果テキストの一部になる。

MCP の content ブロック(text / image / audio / resource_link / resource)は ACP の ContentBlock と
同じ形なので、既存の `ContentBlockText` / `ImageBlockFromAcp` がそのまま使える。足りなかったのは
「`rawOutput` がこの形なら中のブロックを見る」という分岐だけ。

## 3. 検討した選択肢

| 案 | 評価 |
|---|---|
| (a) `rawOutput.result.content` が配列なら `content` 経路と同じ訳し方をする | 実測した形に一致。既存ヘルパーの再利用で済み、他の形は従来どおり文字列化に落ちる。**採用** |
| (b) 文字列化の前に長い文字列値を切り詰める | フリーズは防げるが画像は出ない。どのキーが base64 かは推測になる |
| (c) `rawOutput` が文字列で中身が JSON の場合もパースして見る | codex-acp の `rawOutput` はオブジェクト(`error:null` を持つ JSON 値)で、文字列で来る実例が無い。実例が出てから。不採用 |
| (d) 旧 Codex の `{"result":{"Ok":{…}}}` 形も受ける | 手元の記録に実例が無い。推測での対応はしない。不採用(その形は今まで通り文字列化される) |
| (e) 検出側(`ToolResultImages`)だけで吸収 | 走査の O(n²) は別ブランチで直したが、結果テキストに 500 KB が乗る点と、埋め込み画像として出ない点は残る |

## 4. 実装

`AcpProtocolBridge`:

- `AppendMcpResult(state, raw)`: `raw.result.content` が配列のときだけ動く。各ブロックを
  image なら `state.Images`、それ以外は `ContentBlockText` で `state.Output` へ。何も得られなければ
  false を返し、呼び出し側の文字列化フォールバックが従来どおり効く
  (`{"result":{"content":[]},"error":"…"}` のような形で情報を落とさないため)。
- `TryAddImage(state, block)`: `content` 経路と共用。**パネルが結局デコードしない画像は運ばない**:
  1 結果あたり `MaxToolResultImages`(4)枚まで、base64 が `MaxToolResultImageBase64Chars`(8 MiB)を
  超えるものは捨てる。捨てた分は従来の代替テキスト `[image]` が残る。
  値は `ToolResultImages.MaxImagesPerResult` / `MaxBase64Chars` と同じ。ブリッジは
  `ci/SmokeTests` で Model 層なしにコンパイルされるので定数を参照できず、複製してテストで一致を固定した。
- `rawOutput` を見る条件に `state.Images.Count == 0` を追加(画像だけの結果で、後続の update が
  同じ `rawOutput` を再送しても二重に積まない)。
- `content` が何か出したときは従来どおり `rawOutput` を見ない。`rawOutput` が文字列・他の形の
  オブジェクトのときも従来どおり。

結果として Codex のスクリーンショットは Claude の `Read`(PNG)と同じ
`[{"type":"text"},{"type":"image","source":{"type":"base64",…}}]` で `CompleteToolCall` から出て、
`ToolResultImages.Resolve` は埋め込み画像の経路(Attachments へ保存)に入る。テキストは
`Captured Scene view to …` の 1 行だけになる。

## 5. テスト

`AcpProtocolBridgeTests`(EditMode、9 本追加):

- 実測形(text + image、`structuredContent`/`_meta`/`error` が null)→ 結果はブロック配列、
  base64 はテキストに入らない、`ToolResultImages.Resolve` が埋め込み経路で 1 枚保存しパス走査に入らない
- text のみ → ただの文字列(`pong`)。text 複数 → 改行で連結
- 上限: 8 MiB 超 1 枚 + 小 6 枚 → 画像 4 枚、`[image]` 3 行、巨大 base64 は結果に無い
- ブリッジの定数 2 つが `ToolResultImages` の定数と一致
- 他の形のオブジェクト → 文字列化、文字列 → そのまま、`result.content` が空 → 文字列化
- `content` が出力を作ったら `rawOutput` は無視

AITemp(2022.3.22f1、バッチモード、junction をワークツリーへ付け替え→復元)で
`AcpProtocolBridgeTests;ToolResultImagesTests` を実行して全件成功。`dotnet` SDK はこの機に無いので
`ci/SmokeTests` は CI 任せ(ブリッジに Model 層への参照を足していないことは上記のとおり)。

## 6. 版

修正のみなので patch。作業中に main が v0.54.7 を出したので、main をマージして **v0.54.8**。
Pro は変更なし。同日の `claude/kind-hawking-54ba2c`(v0.55.0 予定、未マージ)とは
版番号が前後し得る: 後から入る側が CLAUDE.md の手順どおり切り直す。
