# 設計ノート: ViewContainer のビュー切替(Chat / History スタブ / Settings)

- 日付: 2026-07-31 / ステータス: 採用済み
- 関連: ARCHITECTURE.md Phase 3、R05 6.1(ヘッダーのタブ行成長構想)

## 課題

Phase 1-2 の `AgentPanelWindow` は `ViewContainer` に `ChatView` を直接 1 個だけ埋め込んでいた。Phase 3 では Settings ビュー(本ラウンド)と History ビュー(次段、今回はスタブ)を追加し、ヘッダーの ⚙ から Settings へ、Settings/History から Chat へ戻れるようにする必要がある。

制約:

1. Chat から離れても **CLI クライアントを再起動しない・トランスクリプトを再構築しない**(タスク要件)。
2. 本ラウンドの並列作業ではファイル所有権が明確に分離されており、`HeaderView.cs` / `StatusBarView.cs` は**両エンジニアとも今回は触らない**(次段で Engineer H が担当)。ところが R05 6.1 は「⚙ = Settings への遷移」を v1 の時点でヘッダーに置く前提で書かれており、両者に矛盾がある。

## 検討した選択肢

| 案 | 内容 | 判定 | 根拠 |
|---|---|---|---|
| A | `HeaderView.cs` の既存(無効化済み)⚙ボタンを有効化して直接配線 | 棄却 | 明示的なファイル所有権違反。次段の Engineer H の作業と衝突する |
| B | ViewContainer 内に独自の「切替ストリップ」(⚙ / ‹ Chat)を追加し、`AgentPanelWindow.cs`(自分の所有物)だけで完結させる | **採用** | ファイル所有権を守りつつ、今すぐ動く UI を提供できる。`AgentPanelWindow.ShowSettings()`/`ShowChat()` という public 静的 API も用意し、次段で HeaderView の⚙がこれを呼ぶだけで済むようにした |
| C | 今回は Settings への導線を一切 UI に出さず、テストと配線だけ用意する | 棄却 | 機能が完成しても誰も触れられず、受け入れ確認ができない |

## 決定(B)

- `PanelViewKind`(Chat/History/Settings)を `IAgentPanelView.cs` に追加。
- `AgentPanelWindow` は 3 ビューの root を **CreateGUI で 1 回だけ構築**し、`ViewContainer` に全部 Add した上で `style.display` の Flex/None だけを切り替える。**破棄・再構築は一切しない**。
- 表示中でないビューは `OnDeactivate()`(購読解除・リフレッシュループ停止)されるだけで、裏側の `AgentHub`/`ChatSession`/CLI クライアントは一切触らない(そもそも `ChatView.OnActivate/OnDeactivate` は元々 UI 購読の付け外しのみで、セッション状態は static な `AgentHub` 側に生きている)。切り替えて戻ると `OnActivate()` が `_dirty=true` にして即座に最新状態を再描画するので、**滞在中に届いたメッセージも消えない**(再描画が遅延されるだけ)。
- 切替導線は `ViewContainer` 内の薄いストリップ(⚙ / ‹ Chat)として `AgentPanelWindow.cs` 内に実装。`HeaderView.cs` は無変更。
- 副作用として気づいたエッジケース: **Settings/History 表示中に can_use_tool 許可待ちが発生すると、ChatView 自身のリフレッシュループが止まっているため許可カードの自動ウィンドウ化判定が走らない**。バッジ(タブアイコン)は `AgentPanelWindow` 自身のループが動き続けるので出るが、カード自体は見えないままになり得る。対策として「待機状態に **新規遷移** した瞬間、Chat 以外のビューにいたら自動で Chat に戻す」エッジトリガー処理を追加した(既存のバッジ用 `_badgeShown` 遷移検知を再利用)。

## 派生効果

- `AgentPanelWindow.ReapplyContentRootStyling()` という新しい共有ヘルパーが、CJK フォントとフォントサイズの両方を「開いている全パネル/許可ウィンドウ」に即座に再適用する経路として追加された(Settings のトグル/スライダーから呼ばれる)。
- History は本ラウンドでは `BuildHistoryStub()` が返す静的な `VisualElement` のみ(`IAgentPanelView` は実装しない)。次段で `HistoryView : IAgentPanelView` に差し替える際は `AgentPanelWindow.RootForView`/`ActivateView`/`DeactivateView` の `PanelViewKind.History` 分岐を実装で埋めるだけでよい。
