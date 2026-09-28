# `uap_property_set` が JSON 文字列で来た構造体値を受け付ける

日付: 2026-09-28 / 対象: `jp.colloid.unity-agent-panel`(`UapPropertySetTool` / `UapPropertyValueWriter`)
関連: `2026-09-08-ops-audit-fixes.md`(OPS-7: 型に合わない値を黙って書かず拒否する方針)

## 1. 症状

2026-09-28 の実エージェント観察(`docs/examples/agent-tools-request.md` の依頼)で、
Light の色を変えようとした呼び出しが 5 回続けて失敗した:

```json
{"path":"WarmPointLight","componentType":"Light","propertyPath":"m_Color",
 "value":"{\"r\": 1.0, \"g\": 0.75, \"b\": 0.45, \"a\": 1.0}"}
```

応答は毎回
`Property 'm_Color' (Color) needs an object with its component fields (e.g. {"x": 1, "y": 2}), but was given a string.`
で、モデルは `{r,g,b,a}` → `[r,g,b,a]` → `{x,y,z,w}` と形を変えて再試行したが、
どれも **JSON 文字列として** 送られていたので全部同じ拒否になった。同じターンで
`"1.5"` / `"true"` / `"Point"` のスカラーも文字列で来ていて、そちらは通っている。

## 2. 原因(2 つ、どちらも実測)

1. **スキーマ側**: `UapPropertySetTool.InputSchema` の `value` は型によって形が変わる
   ので `type` を宣言していない(`description` だけ)。CLI とモデルはこの
   「型なし」プロパティを文字列として扱い、オブジェクトも `JSON.stringify` した
   文字列で送る。スカラーが `"1.5"` で届くのも同じ経路で、こちらは OPS-7 の
   `RequireNumber` が「数値として読める文字列」を許していたので問題にならなかった。
2. **ライター側**: `UapPropertyValueWriter.RequireObject` は `IsObject` 以外を
   一律に拒否する。OPS-7 で「スカラーが来たら全成分が現在値に解決されて無言の
   no-op になる」のを防ぐために入れたガードで、その判断自体は正しいが、
   「JSON 文字列に包まれたオブジェクト」という入力を想定していなかった。

スキーマに `type` を足す案は採らない。`value` は number / bool / string / object /
array のどれも正当で、`anyOf` を書いても CLI 側の直列化が変わる保証はない。
文字列で来ることを前提に、ライター側で剥がすのが確実で、スカラーの前例とも揃う。

## 3. 対処

`UapPropertyValueWriter`:

- `UnwrapEncodedJson(value, prop)` を追加。値が文字列で、trim した先頭が `{` か
  `[` なら `JsonParser.TryParse` で解析し、以後はその結果を値として扱う。
  Color / Vector2 / Vector3 / Vector4 / Quaternion / Rect / LayerMask の各 case の
  先頭で呼ぶ。
  - 先頭が `{` `[` でない文字列(`"red"`、`"5,6,7"`)はそのまま返し、従来どおり
    `RequireObject` が「was given a string」で拒否する。
  - `{` で始まるのに解析できない文字列は、パーサの理由を添えて拒否する
    (`looks like JSON but does not parse (...)`)。黙って従来の文言に落とすと
    「オブジェクトを送ったのに string と言われる」堂々巡りに戻るため。
- ついでに **位置指定の数値配列** を受ける: Color は `[r,g,b]` / `[r,g,b,a]`、
  Vector2/3/4 と Quaternion(オイラー角)は要素数ぴったりの配列。要素数違いや
  数値以外の要素は拒否(部分配列には一意な意味がないので、部分オブジェクトの
  マージとは扱いを分ける)。Rect は `{x,y,width,height}` のみ。

`UapPropertySetTool` のスキーマ `description` に、配列形と
「JSON 文字列で送っても解析して受け付ける(ただしオブジェクトそのものを送るのが
望ましい)」旨を追記した。

## 4. 回帰テスト

`Tests/Editor/UapPropertyValueWriterTests.cs` に追加:

- Color に `"{\"r\": 1.0, \"g\": 0.75, \"b\": 0.45, \"a\": 1.0}"`(観察された入力そのもの)
- Vector3 に `"  {\"x\": 5, \"y\": 6, \"z\": 7} "`(前後空白つき)、部分オブジェクト文字列のマージ
- `"warm orange"` / `"5,6,7"` は従来どおり拒否(値は変わらない)
- `"{\"x\": 5, \"y\": "` は「looks like JSON but does not parse」で拒否
- Color の 4 要素配列、配列の JSON 文字列、Vector3 の要素数違い配列は拒否

AITemp サンドボックス(2022.3.22f1)のバッチモードで EditMode を実行して確認する。
