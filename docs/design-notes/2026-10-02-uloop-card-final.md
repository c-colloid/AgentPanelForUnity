# uLoop カードの最終形(導入ボタンなどの撤去と、エージェント使用トグルの既定オフ)

Date: 2026-10-02
対象: `jp.colloid.unity-agent-panel`(Core)v0.61.0-beta.5(同じブランチのベータに同梱)
関連: `docs/design-notes/2026-09-22-uloop-full-native-roadmap.md` §6(カードの段階表。この変更が最終行)、
`docs/design-notes/2026-09-21-uloop-always-loaded-cost.md`(常駐コストの計測と、トグル・削除導線の根拠)、
`docs/design-notes/2026-09-22-uloop-remove-from-panel.md`(削除導線。この変更でも挙動は変えない)

## 0. 結論(先に)

1. 設定の uLoop カードは **uLoop を検出したプロジェクトでだけ** 表示する。検出できなければ、設定に uLoop の
   文字は一切出ない(導入の宣伝もしない)。
2. 検出時のカードは 3 つだけ: 注意書き 1 行(uLoop が入っている / このパネルの Unity ツールで代替できる /
   常駐するサーバとティックポンプとセッションごとのスキル説明を足す / パネル外で使わなければ削除を推奨)、
   「エージェントに uloop コマンドを使わせる」スイッチ、「uLoop を削除」ボタンとその確認カード。
3. 導入ボタン、導入の進捗行、許可プリセット、指示スニペット、長いコスト説明、機能一覧を撤去し、
   それ専用のコードとテストと文言も削除した。
4. スイッチ(`PanelSettings.uloopAgentUseEnabled`)の既定を **オフ** にした。**保存済みの値は移行しない。**
5. `UloopCapabilityMatrix` を削除し、`ToolRegistry` から「uLoop が覆うツールは登録しない」経路を外した。
   全ての `uap_*` ツールを常に登録する。
6. `UloopDetector`、`AgentHub.TryAutoDenyUloopCommand`、`UloopAgentUsePolicy`、削除導線(`UloopUninstaller`)は残す。

## 1. なぜ今か

ロードマップ §6 の表は、カードを段階的に縮める計画だった。フェーズ 1〜4 が実装済みで、Play 操作・入力・
ログ・コンパイル・スクリーンショットと動画・ウォッチ・入力の記録と再生・動的コード・hot-reload・pause-point
は `uap_*` で賄える(`uap_help` のプレイブックも付いた)。パネルが uLoop の導入を勧める理由はもう無く、
むしろ 2026-09-21 の計測どおり、導入すると 16ms のティックポンプと自前サーバとスキル説明
(約 1.5〜2k トークン/セッション)が常駐する。だから表の最終行どおり、
「検出時の注意 1 行 + 削除ボタン(+ 実際に挙動を左右するスイッチ)」まで落とす。

## 2. 何を消して、何を残したか

| 対象 | 扱い | 理由 |
|---|---|---|
| 「uLoop を導入」ボタンと確認カードの導入側 | 削除 | 勧める理由が無い。`UloopInstaller` の Plan / Apply / ネットワーク判定、`UloopInstallPlan`、`UloopInstallMethod`、`UloopInstallApplyResult` も削除 |
| 導入の進捗行(Installing... Ns) | 削除 | 導入が無いので不要。`SessionStateBridge` の導入用フラグ 2 つと、ビューの `AddRequest` 保持・ポーリングも削除 |
| 「uloop コマンドを許可」プリセット | 削除 | `allowedTools` に `Bash(uloop ...)` を書く導線。もう勧めない(`StripBroadUloopAllowEntries` などの補助も削除) |
| 「指示スニペットを挿入」 | 削除 | `uloop execute-dynamic-code` を勧める文面だった。`uap_execute_code` がある |
| 常駐コストの長い説明、「止め方」の説明 | 削除 | 注意書き 1 行とそのホバーに畳んだ |
| 機能一覧 | 削除 | 検出時に出すのは「代替できる」の一言だけ |
| `UloopCapabilityMatrix` | 削除 | 中身が空のまま使われていなかった(ロードマップ §6 の指摘どおり) |
| 状態ピル(導入済み / 未導入) | 削除 | カードは導入済みのときしか出ない。ピルは冗長 |
| エージェント使用トグル | **残す(既定オフ)** | `TryAutoDenyUloopCommand`・ステアリング文・起動引数の拒否パターンを実際に左右する |
| 「uLoop を削除」ボタンと確認カード・2 段階の後始末 | **残す(変更なし)** | 常駐を本当に止める唯一の方法 |
| `UloopDetector`、`TryAutoDenyUloopCommand`、`UloopAgentUsePolicy` | **残す** | uLoop が存在する限り、エージェントが `uloop` に流れるのを止める層は要る(§6 末尾) |

### 2.1 削除側コードの整理(残ったものの名前)

`UloopInstaller` は、削除導線と別の機能(manifest.json のレジストリ設定を書く側)が使う補助を抱えていた
(URL でスコープ付きレジストリを探す、2 スペースの整形出力、改行コードの維持、ファイル読み取りの薄いラッパ)。
これらは **`UloopManifestSupport`** として残し、導入専用の部分(計画・適用・ネットワーク判定・ロールバック)だけ消した。
`UloopInstallCaveat` は削除導線の注意書きでも使うので **`UloopCaveat`** に改名して `UloopUninstallPlan.cs` へ移した。
`UloopInstallProgress`(進捗の優先順位を決める純関数)は、削除の進捗が `EvaluateRemoval` として使っているので
**残した**(名前もそのまま。優先順位表のテストが名前を固定している。列挙値の「Installing」は「進行中」の意味)。
テストは導入側(`Plan` / `Apply` / ネットワーク)を削除し、共有補助のテスト(URL 一致、改行コード)は
`UloopManifestSupportTests` に移した。

### 2.2 文言(L10n)

EN/JA 同数で、導入・プリセット・スニペット・コスト説明・状態ピルの組を削除した。確認カードの
「キャンセル」「パスのホバー」「変更を表示」は削除側でだけ使うので `SettingsUloopRemove*` に改名した。
新規は注意書きの 2 組(`SettingsUloopNoteHint`: 110 文字以内の 1 行、`SettingsUloopNoteTooltip`: 詳細)。
トグルのヒントとホバーは「既定オフ」に合わせて書き直した(「上の行を参照」の指示も消した)。
`SettingsUloopStatusMissing`(「未導入」)は Unity 公式プラグインのピルが流用しているので残した。

## 3. カードの表示条件

- カードは常に組み立てるが、`RefreshUloopSection`(従来どおり 2Hz で `UloopDetector.DetectInProject`)が
  `SettingsView.ShouldShowUloopCard(検出, 削除進行中, 削除結果表示中)` で表示を決める。
- 削除を実行すると依存が消えた時点でカードも消えるはずだが、それでは「uLoop を削除し、OpenUPM の設定も片付けました」
  という結果が読めない。そのため **削除の進行中と、結果を表示している間だけ** カードを残す。
  このときは本体(注意書き・スイッチ・ボタン)を畳み、結果の 1 行だけを見せる(「uLoop が入っています」と
  言い続けないため)。表示は次のドメインリロード / ビュー再構築で消える。
- 設定検索は、非表示のカードの文言にヒットしても開かない(`SettingsSearchCard.IsAvailable`)。
- ここまでの純粋な規則は `ShouldShowUloopCard` のテスト、組み立てたカードの表示は
  `SettingsSectionStatusTests.UloopCard_IsShownOnlyWhenUloopIsDetectedInTheProject`、検索は `SettingsSearchTests` で固定した。

## 4. トグルの既定オフと、既存ユーザーを移行しない理由

- `uloopAgentUseEnabled` のフィールド初期値を `true` から `false` にした。新しい設定ファイル(このフィールドを
  持たないもの)では、ステアリング文が「uloop は拒否される」と書き、起動引数に拒否パターンが付き、
  許可レイヤーが `uloop` を拒否する。uLoop が無いプロジェクトの新規ユーザーに、逃げ道として uloop を
  案内しない、というロードマップ §6 の意図どおり。
- **既存ユーザーの保存値は触らない。** それまでの既定は `true` で、そのまま保存されている人がいる。そこから
  黙って `false` に変えると、uloop を使って仕事をしている人の作業を、本人の知らないうちに止める。
  Unity のシリアライズはフィールドが無いときだけ初期値を使い、あれば保存値を読むので、初期値を変えるだけで
  「新規は既定オフ、既存は据え置き」になる(`PanelSettingsTests.UloopAgentUse_SavedValueIsKeptAndNotMigrated`)。
  既存ユーザーが uLoop を使っていないなら、カードの注意書きが削除を勧める。使い続けるなら、そのまま使える。
- 副作用として、uLoop パッケージが無いのに `uloop` を呼ぶ環境(CLI だけが PATH にある)では、新規設定で
  拒否される。uLoop の CLI は Unity 側のパッケージのサーバが無いと動かないので実害は無いはずだが、
  拒否メッセージが指す「Settings > uLoop integration」は、パッケージが無いとカードごと出ない。
  メッセージは `uap_*` の代替を主に伝える文面なので、そのままにした(必要なら文言を足す)。
- ステアリング文(`ComposeUapOpsSteeringSection` ほか)は従来から `uloopAgentUseEnabled` だけに従う。
  新しい文面は足していない。

## 5. ツール登録

- `ToolRegistry.CreateDefault(bool uloopDetected)` と `RegisterUnlessCovered` を削除し、`CreateDefault()` だけにした。
  呼び出し元は `UapOpsServer.Registry`、Core / 拡張パッケージのテスト、CI ホストプロジェクト(Core+拡張 /
  Core のみの 2 系統)で、全て機械的に引数を外した。`RegisterProviderTools` の第 3 引数も同様。
- テスト: `ToolRegistryTests.CreateDefault_RegistersEveryUloopPhaseTool_WithNoUloopInput` が、
  フェーズ 1〜4 で足したツールが常に登録されること、uLoop フラグを取るオーバーロードと
  `UloopCapabilityMatrix` の型が存在しないことを固定する。

## 6. 検証と、未確認のこと

- `ci/SmokeTests`(`dotnet run`)は 641 件が全て通った(この変更で対象ファイルの増減は無い。
  smoke が取り込む uLoop 関連は `UloopAgentUsePolicy.cs` だけで、挙動は変えていない)。
- **Unity 実機ではコンパイルもテスト実行もしていない。** Editor / Tests の変更は構文チェックだけを通し、
  参照解決とテスト実行は CI(EditMode)に任せる。特に次は実機で確認が要る:
  uLoop 入りのプロジェクトでカードが出ること、削除を実行して結果の 1 行が読めてからカードが消えること、
  uLoop 無しのプロジェクトで設定にも検索にも uLoop が出ないこと。
- `SettingsHandlerAuditTests`(ハンドラの形を走査する)は、トグルのハンドラが従来どおり
  `CommitSettingsChange()` で終わるので変わらず通る想定。

## 7. 触っていないもの

- `package.json` のバージョン(ベータ番号は同じブランチの v0.61.0-beta.5 のまま。`docs/RELEASING.md` の項目を追記した)。
- 既存の uLoop 設計ノートの本文(履歴として残す。導入側の記述は過去の状態の説明になる)。
- 削除導線の挙動、`UloopDetector` の判定。

## 統合時の修正: uLoop が無いプロジェクトでは指示文に uloop を書かない

トグルの既定を OFF にしたため、uLoop が入っていないプロジェクトの新しい設定でも、
指示文に「Do NOT call uloop」の 1 行が入り、ライトマップの段落と `uap_ping` の文も
uloop に触れる形になっていた。持っていないツールについての文は固定費になるだけで、
エージェントを混乱させかねない。そこで `ComposeUapOpsSteeringSection` に
`uloopInstalled` を足し(`AgentHub` は `UloopDetector.DetectInProject` を渡す)、
uLoop が無ければ uloop という語を一切出さないようにした。トグルが決めるのは、
uLoop が入っているときの書き方(代替手段として示すか、拒否すると書くか)だけになる。
`--disallowedTools` の拒否パターンは、副作用が無いので従来どおり残す。拒否メッセージには、
設定カードが「uLoop が入っているときだけ表示される」ことを書き足した。
