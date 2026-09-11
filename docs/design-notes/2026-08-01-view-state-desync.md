# ビュー切替ロックアウト(表示と_activeViewの非同期)修正

日付: 2026-08-01 / 発見経緯: v0.4.0 実機スモーク中、ShowSettings() が「settings shown」を
返すのにチャットが表示され続け、ストリップだけ「< Chat」になる状態を観測。

## 1. 根本原因(実機で実証)

数値ダンプ: `_activeView=Settings` / strip表示中 / `uap-settings` display=**None** /
`uap-chat` display=**Flex**。

- `CreateGUI` はビュー3枚を**チャット可視・他不可視のハードコード既定**で構築し、
  `_activeView` を表示状態へ再適用しない(旧 AgentPanelWindow.cs:157-169)。
- 一方 `UpdateViewSwitchStrip()` は `_activeView` を参照する → ストリップだけ非チャット表示。
- `SetActiveView` は `_activeView == kind` で早期リターン → **以後どの ShowSettings も恒久 no-op**
  (リフレクションで Chat→Settings を強制トグルすると復旧することを確認 = ロックアウトの実証)。
- 発火条件: `_activeView` が非 Chat の状態で CreateGUI が(再入等で)走ること。
- 経路は2つ: (a) `_built==true` の再入ティアダウン — 旧実装は `_activeView = Chat` に
  強制リセットしており(ハードコード構築に状態を合わせる妥協)、**リビルドのたびに設定画面から
  追い出される**。(b) **OnDisable→OnEnable 経路**(タブ非表示→再表示等) — `_built` が既に
  false のためティアダウン分岐がスキップされ、リセットも走らず `_activeView=Settings` のまま
  ハードコード構築(チャット可視)が実行される = 実機で観測した非同期+ロックアウトそのもの。
  修正(ビルドが `_activeView` を尊重+リセット撤去)は両経路を同時に塞ぐ。

## 2. 修正

不変条件を導入: **「CreateGUI 完了時、表示フラグは必ず `_activeView` と一致する」**。
ハードコード既定を廃止し、ビルド末尾で `SetViewVisible(kind, kind == _activeView)` ×3 +
`ActivateView(_activeView)` を適用(既定ケース Chat では従来挙動と同一)。

却下案: 「CreateGUI 冒頭で `_activeView = Chat` にリセット」 — 再入前のユーザーのビュー選択を
破壊する(リビルドのたびに設定画面から追い出される)。状態を表示に合わせるのではなく、
表示を状態に合わせるのが正しい方向。

## 3. 回帰ガード

`AgentPanelWindowViewStateTests`(ヘッドレス、既存 StyleSheetTests と同じ
CreateInstance+CreateGUI 方式):
1. Settings アクティブ中の再入リビルドで Settings 表示が保持される
2. リビルド後も双方向のビュー切替が機能する(早期リターンのロックアウト再発検知)
