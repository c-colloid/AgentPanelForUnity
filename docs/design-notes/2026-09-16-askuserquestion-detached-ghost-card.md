# AskUserQuestion カード: ウィンドウへ分離すると背の高い残像カードが残る

日付: 2026-09-16 / 対象: v0.54.2 / 関連: 2026-09-12-askuserquestion-prompt-visibility.md、
2026-08-01-perm-card-collapsed-crush.md、2026-08-05-askuserquestion-stepper.md

## 1. 現象

ユーザー報告: 「AskUserQuestion のカードを分離した際に高さの高い残像カードが
残ってしまい、会話履歴を隠してしまう」。

インラインの質問カードで「ウィンドウで開く」(↗)を押す、またはパネルが小さく
自動でフローティングウィンドウへ回された直後、パネル側には本来
「権限待ち - ウィンドウに表示中 [ここに表示]」の細い待機バー(約 30px)だけが
残るはずが、実際には約 220px の空のカード枠が居座り、その分だけ会話履歴が
押し上げられて隠れる。ウィンドウで回答するまで消えない。ツールの権限カードでは
起きず、質問カードだけで起きる。

## 2. 根本原因

2026-09-12 のノートで導入した質問カード用の高さ下限 `.uap-perm--question`
(`min-height: var(--uap-perm-question-min)` = 220px、`flex-shrink: 0`)が、
「質問 variant かどうか」だけで付け外しされていた(`Render` 内で
`EnableInClassList("uap-perm--question", _isQuestionVariant)`)。

一方、以前からあるツールカード共通の下限 `.uap-perm--expanded`(96px)は
`UpdateExpansion` で **インラインホスト かつ 展開中 かつ ウィンドウ非表示中**
という可視性条件でゲートされている。`SetShownInWindow(true)` で
`.uap-perm-body` が `display: none` になると `.uap-perm--expanded` は外れるが、
`.uap-perm--question` は外れないため、ルートが 220px の下限を保ったまま中身が
待機バーだけになる。これが残像の正体。

同じゲート抜けは「シェブロンで質問カードを折りたたむ」経路でも出る
(要約行 1 行のはずが 220px)。ウィンドウホストでは `.uap-perm--windowhost`
(`min-height: 0`、後方宣言)が勝つので実害はなかった。

## 3. 検討した選択肢

1. **USS 側で打ち消す**: `.uap-perm--question .uap-perm-waitbar` のような
   子孫セレクタでは親の min-height は消せない。ルートに待機バー用の別クラスを
   足して `min-height: 0` で上書きする案は、状態クラスが 3 つ(expanded /
   question / waitbar)に増え、宣言順依存がさらに積み上がる。却下。
2. **C# 側でゲートを揃える(採用)**: `.uap-perm--question` の付け外しを
   `UpdateExpansion` に移し、`.uap-perm--expanded` と同じ条件
   (`expanded && Inline && !_shownInWindow`)に `_isQuestionVariant` を AND する。
   下限は「見えている本体を守るもの」であり、本体が無いときに残す理由がない。
   `ApplyWindowMode` / `SetExpanded` / `Refresh` はいずれも `UpdateExpansion` を
   通るので、3 経路すべてが一箇所で揃う。

## 4. 決定と実装

- `PermissionCard.UpdateExpansion`: `floorApplies = expanded && Inline &&
  !_shownInWindow` を計算し、`uap-perm--expanded` は `floorApplies`、
  `uap-perm--question` は `floorApplies && _isQuestionVariant` で切り替える。
  `Render` からは無条件の付与を削除。
- ウィンドウホストの質問カードは両クラスとも持たなくなる。USS 上は
  `.uap-perm--windowhost` が既に勝っていたので見た目は不変。
- USS のコメントに可視性ゲートの条件を明記。

## 5. 回帰ガード

`PermissionCardTests`:

- `QuestionFloor_LeavesWithTheBody_WhenShownInWindow`: 分離で
  `uap-perm--question` / `uap-perm--expanded` が外れ、maxHeight も解除され、
  「ここに表示」で両方戻る。
- `QuestionFloor_LeavesWithTheBody_WhenCollapsed`: 折りたたみでも外れ、展開で戻る。
- `QuestionFloor_IsInlineOnly_WindowHostCarriesNeitherFloor`: ウィンドウホストは
  どちらの下限クラスも持たない。

既存の `QuestionVariant_CarriesTheHigherFloorClass_ToolVariantDoesNot`
(インライン展開中は付く)と `SourceScan_QuestionFloor_OutranksTheToolCardFloor`
(USS の宣言順と値)はそのまま。

## 6. 教訓

状態クラスを新設するときは、既存の同種クラスと**同じ切替関数・同じゲート**に
乗せる。2026-09-12 は `Render` に単独で足したため、`SetShownInWindow` という
別経路の存在を見落とした。「下限」系の USS クラスは見えている本体の付属物として
扱い、本体の display と一緒に付け外しする。
