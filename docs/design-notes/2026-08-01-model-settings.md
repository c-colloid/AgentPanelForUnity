# モデル設定機能(v0.8.0)設計

日付: 2026-08-01 / ユーザー要望「Defaultモデルの変更、サブエージェントモデルの詳細設定機能がほしい」
根拠: [R07 実測](../research/07-model-configuration.md)(--model継承規則・--agents上書き・既定=親継承、全て実証済み)

## 1. Defaultモデル設定

| 決定 | 内容 | 根拠 |
|---|---|---|
| 真実源はヘッダーピッカーと**共有** | 既存の「ピッカー選択値の永続化」フィールドをそのまま設定画面にも出す(二重管理しない)。実装前に既存フィールド名を確認し、無ければ新設して**ピッカー側もそこへ統一** | 「Defaultモデル」と「現在のモデル」が乖離すると混乱する |
| UI | 設定に「Model」セクション新設: Default model ドロップダウン。候補は `init.models[]`(value/displayName/description)を**最後に受信した時点でキャッシュ**(SessionStateではなくPanelSettingsへ軽量リスト保存 — エディタ再起動後もドロップダウンが機能するように)。未取得時は "default" のみ+ヒント | R07 §8.1。ハードコード禁止 |
| 適用 | スポーン時に常に `--model <value>` を明示(空="default"なら省略)。**接続中の変更は `set_model` で即時適用**(既存のAgentHub.SwitchModelを流用 — 再接続不要) | R07 §3(明示が勝つ)/ 既存ピッカーと同一経路 |

## 2. サブエージェントモデル対応表

| 決定 | 内容 | 根拠 |
|---|---|---|
| データ | `PanelSettings` に `List<AgentModelOverride{agentName, modelAlias}>`(YAML安全: 短い英数文字列のみ) | brace-heavyでないためサイドカー不要 |
| UI | 「Model」セクション内の詳細リスト: 行=エージェント名(TextField、placeholder "general-purpose")+モデル(ドロップダウン、Defaultと同じ候補+「(Default継承)」)+削除。+追加ボタン。ヒント「表に無いエージェントはDefaultモデルを継承します」 | R07 §6(差分のみ渡せば良い) |
| 適用 | 対応表が非空なら `--agents '{"<name>":{"description":...,"prompt":...,"model":"<alias>"}}'` をスポーン引数に付与(パッケージのJsonWriterで生成、QuoteArgのWin32規則を通す)。**次回接続から適用** → SettingsChangeDetector+自動適用(v0.6.0)に載せる | R07 §4/§5 実証形 |
| 真実源 | 上書きの有無・値は**パネル設定のみを真実源**とする(`init.agents` からの逆算は不可能 — R07 §5) | 実測済みの罠 |

## 3. 実装前の未検証項目(ワークフロー内で最初に実測、R07 §8.3)

1. `--agents` エントリの `description`/`prompt` 省略・空文字の許容(→ 許容なら最小JSONに、必須なら
   組み込み説明の流用文字列を定数化)
2. 壊れたJSON/未知モデルaliasのエラー形(起動即死 or result.is_error)→ 設定画面バリデーションの設計
   (最低限: alias候補外の自由入力は許容しつつ、スポーン失敗時は既存のエラーバナーが受け止める)
3. 複数エージェント同時上書きの受理

## 4. 回帰ガード

- AgentClientOptionsExtensionTests: 対応表なし=引数バイト同一 / Default設定時 `--model` 付与 /
  対応表1件・複数件の `--agents` JSON形(実測で確定した最小形)・QuoteArg往復
- SettingsChangeDetectorTests: 対応表変更・Default変更(非接続時)が reconnect 対象
- モデル候補キャッシュのYAMLラウンドトリップ(PanelSettingsTests)
- LiveCli(任意): general-purpose→haiku 上書きでサブエージェント応答モデルが haiku になる

## 5. 非スコープ

- サブエージェントカードへの resolvedModel 表示(次候補。tool_use_result に実測データあり)
- カスタムエージェント定義(prompt/description の編集UI) — モデル上書きに限定

## 6. 追記(2026-08-01、出荷後の緊急追試): `--resume` 併用で `--agents` が無効化される問題と機構変更

### 6.1 発見

§2で設計・出荷した `--agents '{"<name>":{"model":"<alias>"}}'` 方式は、実運用のE2E検証で
**`--resume` と併用すると上書きがサイレントに無効化される**ことが判明した(パネルは
セッションが一度でも確立すれば常に `--resume` でスポーンするため、出荷した機能は
実運用では常に無効という重大な齟齬)。エラー・警告は一切出ない(`is_error:false`、
`exitCode:0`)。実測の全証跡は
[R07 §10](../research/07-model-configuration.md#10-resume-併用時に-agents-が無効化される問題の実測v080出荷後の緊急追試2026-08-01)
を参照。

### 6.2 追試した回避策と却下した案

| 案 | 結果 | 却下/採用理由 |
|---|---|---|
| `--agents` を `--resume` より前に置く(引数順序の変更) | 効果なし(R07 Run 1) | 順序は原因ではない。却下 |
| `--agents` を諦めてカスタムエージェント名 + system prompt へ誘導文を追記する(タスク指示にあった「弱い」代替案) | 未実施 | より強い証拠(ファイルベース定義)が最初の追試で見つかったため不要と判断 |
| **`<project>/.claude/agents/<AgentName>.md` をスポーン前に生成**(ファイルベースのエージェント定義) | **`--resume` を跨いで効く**(R07 Run 3/4)。組み込み名(`general-purpose`)の上書きにも使え、最小形フロントマターでも組み込みのツール権限を壊さない(Run 5/6) | **採用** |

### 6.3 採用した機構

- `--agents` CLI引数は完全に廃止(`AgentClientOptions.AgentModelOverrides`/
  `AgentClient.AppendAgentModelOverrides` を削除)。`--resume` 時に常に無視される
  死んだ引数を残す理由がなく、実装を2系統に分ける方が保守コストが高いと判断。
- 代わりに `Colloid.AgentPanel.Model.AgentDefinitionFileWriter`(新規、Unity側
  `Editor/Model`)が `AgentHub.StartClient` から `client.Start(...)` 直前に呼ばれ、
  `PanelSettings.agentModelOverrides` の各有効エントリを

  ```
  ---
  model: <ModelAlias>
  # unity-agent-panel:managed - regenerated from Settings > Model overrides; do not edit by hand
  ---
  ```

  という最小フォーマット(R07 §10.6実証済み、`name`/`description`省略・本文空)で
  `<projectRoot>/.claude/agents/<AgentName>.md` に書き出す。`name`/`description`を
  省略しても組み込みエージェント名の上書きが正しく効き、Bashツール等のフル機能も
  維持されることを実測確認済み(R07 Run 5)。
- **ファイル所有権のマーカー**: 生成する全ファイルにYAMLコメント行
  `# unity-agent-panel:managed ...` を埋め込む。`Sync()` は
  (a) このマーカーを含まない既存ファイルには絶対に上書き・削除しない(ユーザーが
  手で書いた同名のカスタムエージェント定義を保護)、
  (b) マーカーを含む既存ファイルで対応表から消えたものは削除する(テーブルが
  縮んだときの掃除)、(c) マーカーを含み内容が変わらないファイルは書き換えない
  (不要なファイル変更を避ける)。
- エージェント名はそのままファイル名になるため、パス区切り文字や `..` を含む
  不正な値はファイル名として使えない -- `[A-Za-z0-9_-]` のみを許可し、それ以外は
  1行ログを出して当該エントリをスキップする(スポーンや他エントリは継続)。
  UI側のツールチップ(`SettingsAgentOverrideNameTooltip`)にこの制約を追記した
  (英語/日本語)。モデルの選択自体はドロップダウン(自由入力不可)のままなので
  同様の制約は不要。
- `SettingsChangeDetector`/「Reconnect now」ヒントの判定ロジック自体は変更なし
  (`PanelSettings.agentModelOverrides` の値を直接比較しているだけで、`--agents`
  引数の組み立て方法には依存していなかったため)。

### 6.4 テスト

- `AgentClientOptionsExtensionTests.cs`: `--agents` 引数構築のテスト群を削除
  (対象コードごと削除されたため)。
- 新規 `AgentDefinitionFileWriterTests.cs`: フロントマター生成の厳密一致、
  マーカー検出、エージェント名のファイル名安全性検証、新規書き込み/複数件/
  重複名(最後勝ち)/不正名や改行入りモデル名のスキップ、冪等性(2回目のSyncで
  ファイルを変更しない)、対応表が縮んだ場合の掃除、テーブル全消去後もユーザー
  所有ファイルは残ることの確認をカバー。
- 既存の `SettingsChangeDetectorTests`/`PanelSettingsTests`/`SettingsViewLogicTests`
  はロジック変更なしのため無改修でグリーンを維持。

### 6.5 追記(capture15): 反映タイミングの訂正 -- 「reconnectで有効」ではなく「新しいセッションから有効」

**§6.3末尾および§6.4末尾の記述を本節で訂正する**(該当箇所を書き換えず追記するのは、
R07自体が§8→§9→§10と追試結果を追記形式で積み重ねてきた本ドキュメント群の慣例に倣うため。
§6.3/§6.4は「当時そう判断した」という履歴として残す):

> §6.3: 「`SettingsChangeDetector`/『Reconnect now』ヒントの判定ロジック自体は変更なし
> (`PanelSettings.agentModelOverrides` の値を直接比較しているだけで、`--agents` 引数の
> 組み立て方法には依存していなかったため)。」
>
> §6.4: 「既存の `SettingsChangeDetectorTests`/`PanelSettingsTests`/`SettingsViewLogicTests`
> はロジック変更なしのため無改修でグリーンを維持。」

**この2箇所は誤り。** 6.1〜6.4で採用した「`.claude/agents/*.md` をスポーン前に生成する」機構
自体(`AgentDefinitionFileWriter`、スポーン直前に毎回 `Sync()` を呼ぶ)は正しいが、
「対応表を編集 → reconnect(同一セッションの`--resume`)すれば反映される」という想定は
誤りだったことが、出荷後の追加追試(`capture15`、R07
[§10.8](../research/07-model-configuration.md#108-claudeagentsmd-は-セッション作成時点-でスナップショットされるcapture15決定的))
で判明した。

CLI は `.claude/agents/*.md` の内容を**セッション作成時点でスナップショット**しており、
`--resume` は既存セッションのスナップショットを読み直すだけで、cwdのファイルを都度
再スキャンしない。§10.3(Run 3)が「同じファイルのまま`--resume`しても上書きが効き続けた」
のは、そのファイルがセッションの**作成より前**に既に存在していたからであり、
「作成後に対応表を変更してから同じセッションを`--resume`する」というパネルの実運用パターン
(Settings画面での編集→reconnect)はこの検証がそもそもカバーしていなかった。

結論:

- 対応表を変更した後に既存セッションを reconnect(= 同一 session_id での `--resume`、
  Settings画面の「今すぐ再接続」も auto-apply reconnect も含む)しても、変更は**絶対に**
  反映されない。
- 変更が反映されるのは、`AgentDefinitionFileWriter.Sync`(スポーン直前に毎回実行される点は
  変わらない -- `AgentHub.StartClient` の呼び出し自体は維持)が最新の対応表を書き出した
  **後に新規作成されたセッション**(ヘッダーの「+」で開始する新規セッション、
  `resumeSessionId == null` での spawn)のみ。

このため、`agentModelOverrides` を「reconnectで解決する pending 変更」として扱う現行UXは
ミスリーディングであり、次の変更を行う:

1. `SettingsChangeDetector.RequiresReconnect` から `agentModelOverrides` の比較を削除する
   (reconnectは効果を持たないため、pending 扱いにして「今すぐ再接続」ヒントや auto-apply
   reconnect を誘発する意味がない -- ユーザーに「再接続すれば直る」という誤った期待を
   持たせるだけで実際には何も変わらない)。値の比較コード自体(旧
   `AgentModelOverridesEqual`)はどこからも呼ばれなくなり、テストからの直接参照も無い
   (privateかつ未使用)ため削除した。
2. Settings 画面の対応表編集ハンドラ(行の追加・エージェント名編集・モデル選択・削除)は
   `AgentHub.RequestAutoApplyReconnect` の呼び出しをすべてやめる。ペア設定 pill/ヒント機構
   (`_reconnectHintRow`/`_reconnectPendingPillLabel`)はこの項目専用の配線を最初から
   持っていなかった(`SettingsChangeDetector.RequiresReconnect` の戻り値だけで駆動される
   単一の共有UI)ため、上記1.の変更だけで自動的に「この項目では発火しなくなる」。
3. 対応表の下に、pending状態に関わらず常時表示される専用ヒントを追加する: 新規 L10n ペア
   `SettingsAgentOverridesNewSessionHint`
   (en: "Applies to new sessions only -- start a new session (+) to use the updated
   overrides."; ja: 「変更は新しいセッションから有効です。ヘッダーの + から新規セッションを
   開始すると適用されます。」)。既存の `SettingsAgentOverridesHint`(表の説明文)からも
   「次に Claude が再接続したときに適用されます」という誤った一文を削除した。
4. `SettingsChangeDetectorTests` の agentModelOverrides 関連テストは、旧来の
   「差分があれば `RequiresReconnect` は true」というアサーションを反転し、
   「どう差分があっても false のまま」という**回帰ピン**に置き換える(コメントで
   capture15/R07 §10.8を参照)。`AgentDefinitionFileWriterTests`(ファイル生成ロジックそのもの
   は無変更)と `L10nTests`(パリティ)は無改修でグリーンを維持する。

**未検証の周辺事項(次点)**: 既にオーバーライドを反映済みの状態で作成されたセッションが、
その後さらに対応表を変更してから reconnect した場合に何が起きるか(スナップショットモデルが
正しければ「変更前の値のまま」になるはずだが、確証は無い)は次ラウンドに持ち越す
(R07 §10.8の「未検証の周辺事項」と同一)。
