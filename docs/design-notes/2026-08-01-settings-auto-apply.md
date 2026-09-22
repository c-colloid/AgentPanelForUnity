# 次回接続待ち設定の自動適用(auto-apply reconnect)

日付: 2026-08-01 / ユーザー要望「会話やカスタム指示の設定がClaudeの再接続を経ないと
適用されないのは不便」

## 1. 背景(なぜ再接続が必要か)

- カスタム指示(`--append-system-prompt`)・ツール許可/拒否リスト(`--allowedTools` 等)・
  CLIパス・危険スキップは**スポーン時フラグ**であり、実測プロトコル(R02/R02b/R02c)に
  ライブ変更用の control_request は存在しない(あるのは `set_permission_mode` /
  `set_model` / `interrupt` のみ)。
- 一方 kill+`--resume` は同一 session_id で会話を完全保持することが Phase 1 から実測済み
  (ドメインリロードで毎回起きている操作と同じ)。

## 2. 選択肢と決定

| 案 | 評価 |
|---|---|
| 現状維持(手動 Reconnect now) | ユーザーが不便と明言。却下 |
| ライブプロトコル追加を待つ | CLI側に存在しない機能。却下 |
| **アイドル時自動再接続** ✅ | 会話は保持され、体感は「設定が効いた」だけ。ターン中は終了まで遅延 |

## 3. 挙動仕様

1. 変更検出は既存の `SettingsChangeDetector.RequiresReconnect`(スナップショット比較)を流用。
2. **確定タイミング**で発火(キーストローク毎ではない): テキスト系はフォーカスアウト/
   change確定、トグル/リストは変更即。さらに **1.5s デバウンス**で連続変更を1回に纏める。
3. 発火時の分岐:
   - クライアントが **アイドル**(ターン非実行・権限待ちなし・接続済み) → 自動で
     Reconnect(kill+`--resume`)。ステータスバーに一時表示「設定を適用しています...」→
     完了で通常表示へ。
   - **ターン実行中/権限待ち** → 適用は保留し、Pending ピルの文言を
     「ターン終了後に適用」に変更。`TurnCompleted`(かつ権限待ち解消)で自動適用。
   - **未接続**(未スポーン) → 何もしない(次回スポーンで自然に適用)。
4. 危険スキップ(dangerouslySkipPermissions)も対象に含める — トグル操作自体が明示的
   意思表示であり、警告フォルドアウト内にある。適用時のステータス表示は同一。
5. 手動「今すぐ再接続」ボタンは**存置**(自動が効かない状況の脱出ハッチ+ユーザーの
   メンタルモデル維持)。
6. 設定トグルは追加しない(常時オン)。自動適用が不都合になる具体的シナリオが
   出た時点で opt-out を検討する(YAGNI)。

## 4. 実装ポイント

- AgentHub に `RequestAutoApplyReconnect()`: デバウンスタイマー+アイドル判定+
  `TurnCompleted` フック。Reconnect は既存の `AgentHub.Reconnect()`(同一セッション)を使用。
- SettingsView: 変更確定イベントから上記を呼ぶ。Pending ピル/ヒント文言の状態分岐
  (L10n カタログにフィールド追加 — en/ja 両方)。
- ステータスバー: 適用中の一時テキスト(既存の状態表示機構に相乗り)。

## 5. 回帰ガード

- デバウンス/アイドル判定の純ロジックをテスト可能な形に切り出し
  (busy中に変更→発火しない/TurnCompletedで発火、アイドル中に変更→1.5s後に1回だけ発火、
  連続変更→合流)。
- LiveCli カテゴリ(任意実行)に「カスタム指示変更→自動再接続→同一session_id継続」の
  実CLIラウンドトリップを1本。

---

## 6. 2026-09-22 追補: 実機報告 2 件と、全スイッチの監査

「保留中が消えない」という実機報告から、**別々の 2 つの欠陥**が出た。
どちらも「自動適用が走らないまま、走ると予告し続ける」という同じ見え方をする。

### 6.1 spawn 中の変更が捨てられていた（v0.59.0-beta.3）

`RequestAutoApplyReconnect` / `TryAdvanceAutoApply` の両方が
「未接続なら破棄」で一括りにしていた。しかし `Starting` の client は
**二度と起動しない client ではなく、たいていはこの機能自身の 1 回目の再接続**である。

1 回目の適用が spawn 中に 2 回目の変更が来ると要求が捨てられ、
**`Ready` になっても再評価する経路が無い**（ティックは外れており、
呼び戻すのはターン完了だけ）。結果、スナップショットは 1 回目の値のまま固定され、
誰も実行しない再接続を永久に予告する。手動再接続だけが解ける。

→ 「一時的に使えない（spawn 中 → 保持）」と「恒久的に使えない
（未起動 / エラー → 破棄）」を分離（`AutoApplySettingsPolicy.ShouldArm`）。
§3 の「未接続なら何もしない」という規則自体は後者として維持している。

### 6.2 Web モジュールのトグルだけ auto-apply を呼んでいなかった（同版）

13 個の UapOps モジュールトグルは互いのコピーで、12 個は末尾に
`AgentHub.RequestAutoApplyReconnect()` を持つが、v0.57.0 で 13 個目として
足された `web` だけ落ちていた。`RefreshReconnectHint()` は呼ぶので
**ピルは出る**が、適用は誰もしない。`uapOpsModules` はステアリング文が
spawn ペイロードに焼かれるため再接続が要る（`ApplyUapOpsModulesChanged` は
ライブのツールカタログしか更新しない）ので、これは実際に失われた変更だった。

### 6.3 他のスイッチの監査結果

| 観点 | 結果 |
|---|---|
| `RequiresReconnect` が比較するのにスナップショットが落とすフィールド | 0 件（`CloneNextSpawnOnlyFieldsTests` が全フィールドを走査して恒久的に保証） |
| `StartClient` が読むのに比較されないフィールド | `model` / `agentModelOverrides`（再接続では変えられないので意図的除外）、`permissionMode`（`client.SetPermissionMode` でライブ適用、経路を確認済み）のみ |
| Web 取得 / 検索の設定 | `PublishWebFetchHostRules` / `PublishWebSearchConfig` でライブ公開。比較対象外で正しい |
| ピルを出すのに適用を予約しないハンドラ | §6.2 の 1 件のみ（`OnReconnectClicked` は自分で再接続するので対象外） |

再発防止は**名前を書かないテスト**で行う。`SettingsHandlerAuditTests` は
SettingsView のソースを走査し、ハンドラを名前ではなく**形**で見つける。
14 個目のモジュールが同じテンプレートから足された日から効く。
（§6.2 の時点では「12 個が呼んでいる呼び出しを 13 個目も呼ぶか」を検査していた。
§7 の集約後は検査対象が変わっている — 下記参照。）

---

## 7. 2026-09-22 追補 2: 全ハンドラを 1 箇所に集約

§6.2 は「13 個のコピーのうち 1 個が末尾の 1 行を落とした」というバグだった。
同じ形のコピーが増え続ける限り、テストで見張っても**次のコピーが同じ落とし方を
する余地は残る**。そこで構造の方を変えた。

### 7.1 何が冗長だったか

各ハンドラは「この変更は再接続が要るか」を自分で判断し、要るものだけ
`RefreshReconnectHint()` と `AgentHub.RequestAutoApplyReconnect()` を呼んでいた。
しかしその判断は `SettingsChangeDetector` が既に持っている
— `RequestAutoApplyReconnect` は必ず detector に問い合わせるので、
**比較対象外のフィールドについて呼んでも no-op**である。
つまり ~100 個のハンドラが、detector が所有する答えを手で再導出していた。

### 7.2 集約

- **`SettingsView.CommitSettingsChange()`** — この View が設定変更を確定する
  唯一の出口。`SaveNow()` → `RefreshReconnectHint(text)` →
  `AgentHub.RequestAutoApplyReconnect(text)` を順に行う。
  設定を書くハンドラはすべて「フィールドに代入してこれを呼ぶ」だけになった。
  判断は detector 1 箇所に戻り、ハンドラ側が忘れる余地が消える。
- **`SettingsView.ToggleUapOpsModule(moduleId, enabled)`** — 13 個のモジュール
  トグルの共通実体。各ハンドラは 1 行になった（`markers` だけは
  オフ時に `SceneMarkerStore.Clear(false)` を先に足す）。
  1 つの本体は自分自身からは乖離しない。
- **`AgentHub.RequestAutoApplyReconnect(string)`** — カスタム指示のサイドカーの
  テキストを呼び元から受け取るオーバーロード。集約により**すべての**設定変更が
  この経路を通るようになったため、引数なし版のようにその都度ファイルを読むと
  スライダーのドラッグ 1 フレームごとにディスク読み出しが発生する。
  View は生きた TextField の値を持っているのでそれを渡す。`null` なら従来通り
  ディスクから読む。

### 7.3 テストの作り直し

`SettingsHandlerAuditTests` は「チョークポイントの排他性」を検査する形に変えた。
どれもハンドラ名を 1 つも書かない。

| テスト | 不変条件 |
|---|---|
| `EveryUapOpsModuleToggle_RoutesThroughTheSharedHelper` | `OnUapOps*ModuleToggleChanged` の形のハンドラはすべて `ToggleUapOpsModule(` を呼ぶ（13 個未満しか見つからなければテスト自体が失敗する） |
| `EveryHandlerThatWritesASetting_EndsAtTheChokepoint` | `PanelStateStore.instance.Settings.X =` を書く `On*` ハンドラはすべて `CommitSettingsChange()` を呼ぶ |
| `OnlyTheChokepointSavesAndSchedulesTheReconnect` | ソース全体で `AgentHub.RequestAutoApplyReconnect(` と `PanelStateStore.instance.SaveNow()` の呼び出しがそれぞれ厳密に 1 箇所 |
| `NoHandler_ShowsThePendingPill_WithoutSchedulingTheWork` | ピルを出すのに確定しないハンドラが存在しない（`OnReconnectClicked` は自分で再接続するので対象外） |

§6.2 の欠陥を手で書き戻すミューテーションで、1 番目と 4 番目が落ちることを確認済み。
`CommitSettingsChange()` を 1 つのハンドラから外すミューテーションで 2 番目が落ちる。

### 7.4 意図的に据え置いたもの

- **`PanelStateStore.SaveNow()` 自体をフックする案**は採らなかった。
  SaveNow の呼び出しは SettingsView 以外にもあり（`PermissionCard` の権限応答中、
  `AgentHub` の内部、起動時マイグレーション）、そこから再接続を予約するのは
  `ApplyAutoApproveLevelChanged` のコメントが警告している状況そのものになる。
  集約したのは **View の中だけ**である。
- 検査対象外のフィールド（`model` / `agentModelOverrides` / `permissionMode` 等）は
  §6.3 の通り据え置き。集約後もこれらはチョークポイントを通るが、
  detector が比較しないのでピルは出ない — 判断が detector 側にある、という
  §7.1 の性質がそのまま効いている。
