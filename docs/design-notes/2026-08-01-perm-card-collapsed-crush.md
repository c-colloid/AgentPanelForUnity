# 折りたたみ権限カードの圧壊(チップ行との重なり)修正

日付: 2026-08-01 / 症状report: 「権限カード・AskUserQuestionカードが折りたたみ状態でセレクションUIと
被って表示され、ボタンが押しづらい」

## 1. 根本原因(実機で数値実証)

フェイクcan_use_tool(実測フィクスチャ由来)を折りたたみ状態で表示し、worldBoundをダンプ:

```
permRoot:    y=373..376 h=2     ← カードroot圧壊
permSummary: y=374..398 h=24    ← サマリ行がカード外へオーバーフロー
allowBtn:    h=0                ← ボタンのレイアウトボックス消滅
ctxBar:      y=380..406         ← サマリ行と約18px交差(=見た目の重なり)
```

- `.uap-perm { flex-shrink: 1 }` は「高さ逼迫時はカードを縮め、詳細は内部スクロール」という
  **展開状態向け**の設計(コメントにも明記)。
- その床である `min-height: var(--uap-perm-card-min)` は **`.uap-perm--expanded` にしか無い**。
- 折りたたみ状態では床なし+overflow可視 → Yogaがカードを圧壊し、
  min-height 24px を持つサマリ行だけが外にはみ出して後続のチップ行と交差する。
- AskUserQuestionも同じ `.uap-perm` を使う(展開始動だがシェブロンで折りたたむと同症状)。

## 2. 選択肢と決定

| 案 | 内容 | 評価 |
|---|---|---|
| **A: 折りたたみ=flex-shrink:0、展開のみshrink:1+床** ✅ | 折りたたみは~30pxの単一行であり縮める意味がない。極小パネルでは既存のハイブリッド判定が浮動ウィンドウへ逃がす | 挙動が状態ごとに明確。新トークン不要 |
| B: 折りたたみにも min-height 床を追加 | `--uap-perm-collapsed-min` 新設 | 実高(フォントスケール依存)と床の二重管理になる。shrink自体は残るので境界条件が残る |
| C: ChatView側でカードをshrink不可コンテナに隔離 | 構造変更 | 影響範囲が広すぎる |

併せて **`.uap-perm { overflow: hidden }`** を防御として追加(圧縮が再発しても「はみ出して隣と重なる」
のではなく「クリップされる」に劣化させる。トランスクリプトはみ出し事件と同じ教訓の適用)。

## 3. 変更

- `AgentPanel.uss`: `.uap-perm` の `flex-shrink: 1` → `0` + `overflow: hidden`。
  `.uap-perm--expanded` に `flex-shrink: 1` を移設(既存の min-height 床はそのまま)。
- C#変更なし(`uap-perm--expanded` クラスの付け外しは既存の UpdateExpansion が実施済み)。

## 4. 回帰ガード

- `UssHygieneTests` 系のソース走査テストに追加: `.uap-perm` ブロックが `flex-shrink: 0` と
  `overflow: hidden` を含み、`.uap-perm--expanded` が `flex-shrink: 1` と `min-height` を含むこと
  (機構が消える方向のリファクタを検知する意図の宣言的ピン)。
