# 2026-09-10 -- 会話更新のたびに折りたたみが閉じる問題

発端: ユーザー報告「各種フォールドアウトが会話内容の更新の度に閉じてしまうので、
ツールの内容を確認するのが困難な状態になっている」。

## 1. 原因

`MessageListController.Refresh` は、メッセージの構造シグネチャ(ブロック数・種別・
streaming フラグ・ツール status など、`ComputeSignature`)が変わるたびに、そのメッセージの
VisualElement を **丸ごと捨てて作り直す**(`MessageListController.cs:162`)。ターン中の
アシスタントメッセージは、モデルがツールを 1 回呼ぶごとにブロックが 1 つ増え、
ツールが完了するごとに status が変わるので、この再生成は数秒おきに起きる。

再生成後の要素が「開いていた状態」を復元できるのは、`toolUseId` をキーに静的辞書で
覚えている `ToolActivityCard` と `SubagentCard` だけだった。次の 3 つは開閉状態を
捨てられる VisualElement のローカル変数にしか持っておらず、再生成のたびに閉じた:

| 要素 | 場所 | 従来 |
|---|---|---|
| 思考(Thinking)Foldout | `MessageBlockFactory.CreateThinkingBlock` | `foldout.value = false` 固定 |
| 「N tools」グループ行 | `ToolActivityCard.CreateGroupRow` | クロージャ内 `bool expanded` |
| コンテキスト添付チップ | `MessageBlockFactory.CreateContextAttachmentBlock` | クロージャ内 `bool expanded` |

さらにグループ行には別の閉じ方があった。完了済みツールカードが 3 枚連続すると
グループ行に畳み込まれる(R05 §2.4)が、ユーザーが 2 枚目のカードを開いて読んでいる
最中に 3 枚目が完了すると、開いていたカードは **閉じたグループ行の中に隠れる**。
カード自身の展開状態は `ToolActivityCard.ExpandedByToolUseId` で残っているのに、
外側のグループが閉じているので見えない。

## 2. 決定

| # | 決定 | 根拠 |
|---|---|---|
| F1 | `Editor/UI/ExpandStateMemory.cs` を追加。`messageId/blockIndex` をキーにした
エディタセッション寿命の静的辞書で、`ToolActivityCard.ExpandedByToolUseId` と同じ機構 | ブロックはメッセージへ追記されるだけで挿入・削除されないので、インデックスはメッセージ寿命の間安定。ChatMessage.id はパネルローカル GUID |
| F2 | `MessageBlockFactory.CreateBlockElement` / `CreateContextAttachmentBlock` /
`ToolActivityCard.CreateGroupRow` に `stateKey` 引数のオーバーロードを追加。既存シグネチャは
`null`(記憶なし)へ委譲するので呼び出し側は壊れない | `CreateBlockElement` は public API |
| F3 | 思考 Foldout は `SetValueWithoutNotify(記憶値)` で生成し、`RegisterValueChangedCallback`
(target == foldout のもののみ)で記憶を更新 | Foldout 内部 Toggle の ChangeEvent は Foldout 自身が止めるが、念のため target で絞る |
| F4 | グループ行の初期状態は「自分の記憶があればそれ、なければ **子カードのどれかが展開中なら開く**」
(`ResolveGroupInitialExpanded`) | 開いていたカードを閉じたグループの裏に隠さない。ユーザーが明示的にグループを閉じた記憶は子の状態より優先 |
| F5 | `SubagentCard.PopulateDetails` のネストブロックは `subagent.toolUseId/index` をキーに渡す | ネストされた思考 Foldout も SubagentCard の再生成を跨いで残る |
| F6 | 永続化(SessionState/ファイル)はしない | `SubagentCard.ExpandedByToolUseId` と同じ判断。ドメインリロードで生の transcript ごと捨てられるため復元先がない |

既定値は変えない: 初めて描画される要素は従来どおり閉じた状態から始まる(ツールカードの
「成功時に自動で畳む」挙動もそのまま)。記憶が効くのはユーザーが一度開閉した要素だけ。

## 3. 検証

`Tests/Editor/MessageBlockFactoryExpandStateTests.cs`(EditMode)。パネル無しの `SendEvent` は
EditMode では no-op なので、クリック/トグルは `ExpandStateMemory.Set` を直接書いて模擬し、
再生成した要素を構造的に検査する(`ToolActivityCardExpandStateTests` と同じ流儀)。

- 思考 Foldout: ブロック追記後の再生成 / streaming→確定の再生成で開いたまま。別メッセージには波及しない。キー無しは閉じたまま
- グループ行: 再生成で開いたまま。2 枚目を開いた状態で 3 枚目が完了しグループ化されると、グループは開き中のカードも展開のまま。グループ自身の記憶が子の記憶より優先
- 添付チップ: 記憶ありで再生成するとペイロードが生成・表示され、シェブロンが下向き
