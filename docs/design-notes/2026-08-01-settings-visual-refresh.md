# Settings ビジュアル刷新(v0.4.0)

日付: 2026-08-01 / ユーザー要望「設定画面UIのブラッシュアップ。Meta XR Building Blocks を筆頭に
ヴィジュアル面やユーザビリティを意識したものがトレンド」
根拠調査: [R06 モダンエディタUI調査](../research/06-modern-editor-ui.md)(Building Blocks 一次情報+
Package Manager / Muse / Odin / Hot Reload 横断+2022.3 可否検証済み)

## 1. 選択肢と決定

| 案 | 評価 |
|---|---|
| フラット見出し維持(現状) | 「粗削り」と感じられた主因が箱の欠如(R06 §4.1)。却下 |
| **ヘッダーストリップ式セクションカード化** ✅ | 調査対象ほぼ全てに共通するパターン#1。設定フォームに適合 |
| Building Blocks 風カードグリッド | 設定フォームには不適(グリッドは「選んで追加する」ギャラリー向け)。却下 |

**採用スコープ = R06 §5 のフェーズ A+B+C**(D=狭幅縦積みは現状破綻していないため v0.4.0 では見送り):

- **A**: 全セクションをカード化(ヘッダーストリップ: アイコン+タイトル / 本体: 既存行)。
  Danger zone の警告文を `HelpBox(Warning)` 化+Foldout ヘッダーに warn アイコン常時表示
- **B**: カードホバー遷移(`background-color` / `border-color` のみ — 2022.3 で animatable
  確認済みのプロパティに限定。`box-shadow` は不可なので枠線・背景の明暗差で代替)、
  reconnect pending にピルバッジ併置(**テキストは残す** — 色のみ依存はアクセシビリティNG)
- **C**: Toggle のスイッチ化(`.unity-toggle__input`/`__checkmark` 実クラス確認済み、
  USS+`AddToClassList("uap-switch")` のみ、`translate` 遷移でノブ移動)

新設定画面の全セクション(機能拡充後: CLI / Conversation / Display / Quick actions /
Notifications / Diagnostics(stderr) / About)に統一適用。About はバージョン2つを中立色ピル、
リンクは Package Manager 風の抑制表現(R06 §4.5)。

## 2. 制約(全て実測・調査済みの再確認)

- アクセント色は1画面最大2系統(05-ux-spec §5.2)。カテゴリ別色分けは**採用しない**(R06 §4.6)
- `box-shadow` / grid / calc() / メディアクエリ不可。`!important`・shorthand 内 var() 禁止
  (UssHygieneTests が既にガード)
- ダーク/ライト両テーマ対応(トークン追加: `--uap-radius-card`、カードヘッダー地色、
  スイッチ トラック/ノブ色)
- アイコンは既存 IconLoader 経由のビルトインのみ(名前引き失敗時のグリフフォールバック維持)

## 3. 検証

- サンドボックス全緑(UssHygiene 自動適用)+ **実機スクリーンショット反復**
  (PM がuloopで狭幅350px/標準500pxの両方を目視、ダークテーマ。ライトはトークン定義の
  静的確認+可能ならエディタ切替で確認)
- 回帰ピン: スイッチ化は `uap-switch` クラス付与の有無に依存するため、
  SettingsView ソース走査で bool トグル生成箇所に `uap-switch` が付いていることを確認する
  テストは**作らない**(実装詳細に過剰結合)。視覚回帰はスクリーンショット運用でカバー
