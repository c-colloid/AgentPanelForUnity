# 2026-09-10 -- Unity 公式プラグイン(unity-agent-plugin)との連携設計

発端: R12(`docs/research/12-unity-official-plugin.md`)の流用案 §3.1〜§3.4 を「計画はいい感じ」
として設計に進めよ、という指示。本ノートは R12 の各案を実装可能な粒度に落とし、順序と
テスト、見送り項目を決める。コードの根拠は `path:line` で示す(2026-09-10 時点の main)。

参照: `docs/research/12-unity-official-plugin.md`(R12)、
`docs/design-notes/2026-08-01-phase5-unity-ops-design.md`(§3b Extension Profiles、§7.2
capability matrix)、`docs/design-notes/2026-08-12-uloop-install-progress.md`(導入進捗の型)、
`docs/design-notes/2026-08-02-noise-permissions-and-uloop-preference.md`(steering 行の型)、
`Editor/Core/Client/AgentClient.cs`、`Editor/Integration/AgentHub.cs`、
`Editor/Ops/Profiles/*`、`Editor/Ops/Uloop*.cs`、`Editor/UI/SettingsView.cs`。

---

## 0. 結論(先に)

R12 §3 の 5 案のうち、**4 案を 3 つのストリームに束ねて実装し、1 案を見送る**。

| Stream | R12 | 内容 | 規模 |
|---|---|---|---|
| A 検出 | 3.1 前半 | プラグインの導入/ロード状態を **2 つの真実源**(静的: `~/.claude/plugins/installed_plugins.json` + `settings.json` の `enabledPlugins`、動的: `system/init` の `plugins[]`)から判定する純関数群 | 小 |
| B 設定カード | 3.1 後半 | 設定画面に「Unity 公式プラグイン」セクション。状態表示 + ワンクリック導入(`claude plugin ...` を子プロセスで実行、進捗表示) | 中 |
| C 誘導行 | 3.2 | 導入済みのとき `--append-system-prompt` に「公式スキルの使い分け + Unity 6 前提の但し書き + `unity` CLI を使わせない」行を足す | 小 |
| D 検索ツール | 3.4 の一部 | `uap_search`(Unity Search クエリ実行、読み取り専用)。公式スキル `generate-editor-search-query` が組み立てたクエリをパネル内で実行できるようにする | 小〜中 |
| 見送り | 3.3 | `unity mcp` の `--mcp-config` 併記。Unity 6 + `com.unity.pipeline` が前提で、検証環境 2022.3 では動かない。§6 に条件付きの下書きだけ残す | -- |
| 見送り | 3.4 残り | sprite/tilemap 系ツール。公式スキル本文を写す誘惑が強く UCL に触れやすい。需要が出てから個別設計 | -- |

設計の前提を 3 つ、調査で確定させた(R12 §5 の未確認事項の一部を潰した):

1. **`-p` でもプラグインは読まれる。** 公式 headless 文書の記述: "Without it (`--bare`), `claude -p`
   loads the same context an interactive session would, including anything configured in the working
   directory or `~/.claude`." パネルの起動引数(`AgentClient.cs:848-893`)に `--bare` は無い。
   → ユーザーが導入済みなら **パネル側の変更なしに** `/unity:*` は既に使える(実機確認は §7)。
2. **`system/init` に `plugins[]`(`name`, `path`)と `plugin_errors[]` が載る。** パネルが持つ
   v2.1.218 相当のキャプチャ(`Tests/Editor/Fixtures/out1.jsonl` ほか)に既に `"plugins":[]` が
   含まれている。`SystemInitMessage`(`Core/Protocol/SystemInitMessage.cs:24-36`)はこのキーを
   まだ読んでいない。→ 「このセッションが実際に何を読み込んだか」の真実源として使える。
3. **`--bare` は将来 `-p` の既定になると予告されている**("will become the default for `-p` in a
   future release")。そうなるとプラグインもフックも自動では読まれない。→ Stream A は
   「設定上は有効なのにセッションが読んでいない」状態を区別して表示し、その日が来たら
   `--plugin-dir` で明示ロードに切り替えられるよう、真実源を 2 つ持つ。

---

## 1. Stream A -- 検出

### 1.1 真実源と優先順位

| 真実源 | 何が分かるか | いつ使えるか | 根拠 |
|---|---|---|---|
| 静的: `~/.claude/settings.json` の `enabledPlugins` | ユーザースコープで **有効化されている** か | 常時(セッション無しでも) | 公式 plugins-reference: `enabledPlugins: {"unity@unity-agent-plugin": true}` がユーザースコープ `~/.claude/settings.json` に書かれる |
| 静的: `~/.claude/plugins/installed_plugins.json` | **導入されている** か、`installPath` / `version` / `gitCommitSha` | 常時 | 実測(検証報告 §2): `version: 2`、`plugins["unity@unity-agent-plugin"][0]` に scope=user のエントリ |
| 動的: `system/init` の `plugins[]` | **このセッションが実際にロードした** か、その `path` | 接続後 | headless 文書 "Read session metadata"、フィクスチャに `"plugins":[]` |
| 動的: `system/init` の `plugin_errors[]` | ロード失敗と理由 | 接続後 | 同上 |

優先順位: **接続中は動的が勝つ**(スラッシュコマンド候補が init を真実源にしているのと同じ
原則、`AgentHub.cs:4706`)。セッションが無いときだけ静的で表示する。両者が食い違う
(静的=有効、動的=未ロード)ときは「導入済みだが CLI が読み込んでいない」という第 3 の
状態として **見せる**(HUB-9 の「動いていない保護を信じるより、止まっていると知る方が良い」
と同じ規則、`AgentHub.cs:2920-2927`)。

### 1.2 状態モデル

```
enum UnityPluginState { CliUnavailable, NotInstalled, Disabled, EnabledNotLoaded, Loaded, LoadError }
```

実装時に `Disabled` を足した: `installed_plugins.json` に記録はあるが `enabledPlugins` が真でない
状態で、`claude plugin disable` が残す形。「未導入」と同じ導入ボタンを出すのは利用者の意図に
反するので、別の語にして「有効化はターミナルで `claude plugin enable unity@unity-agent-plugin`」
と案内する。`enabledPlugins` だけ真で記録が無いときは `NotInstalled`(キャッシュを消した後の
古いフラグ。導入ボタンを出してよい)。

| 状態 | 判定 | 表示(§2) |
|---|---|---|
| `CliUnavailable` | `CliLocator` が CLI を見つけられない | 「CLI が見つかりません」。導入ボタン無効 |
| `NotInstalled` | 導入記録が無く、(接続中なら)動的にも無し | 「未導入」+ 導入ボタン |
| `Disabled` | 導入記録はあるが `enabledPlugins` が真でない | 「無効化されています」+ 有効化コマンドの案内 |
| `EnabledNotLoaded` | 静的で有効だが、接続中の init `plugins[]` に無い | 「導入済み。ただし現在のチャットは読み込んでいません(新規チャットで反映)」 |
| `Loaded` | init `plugins[]` に一致するエントリあり | 「読み込み済み v{ver}」 |
| `LoadError` | init `plugin_errors[]` に一致するエントリあり | 「読み込み失敗: {message}」 |

`EnabledNotLoaded` は 2 つの原因を区別しない: (a) 導入直後でまだ再スポーンしていない、
(b) 将来 `--bare` が既定になった。文言は (a) を主に書き、(b) は §7 の実機確認で判明した時点
で `--plugin-dir` 経路(§1.5)を有効化する。

### 1.3 純関数(テスト対象)

すべて `Editor/Ops/UnityPlugin/` に置く(uLoop と同じく Ops 配下、ただし名前空間は
`Colloid.AgentPanel.Ops.UnityPlugin` で分離)。

```
static class UnityPluginIdentity
  const string MarketplaceName = "unity-agent-plugin";
  const string PluginName      = "unity";
  const string QualifiedName   = "unity@unity-agent-plugin";
  const string MarketplaceSource = "Unity-Technologies/unity-agent-plugin";
  static bool Matches(string name, string source) // 実測: init の plugins[] は name="unity", source="unity@unity-agent-plugin"(2026-09-10 検証報告 §1)。どちらか一致で真

static class UnityPluginDetector
  // 静的 1: ~/.claude/plugins/installed_plugins.json(version 2)の plugins["unity@unity-agent-plugin"][]
  //          から scope=="user" のエントリ(installPath / version / gitCommitSha)。malformed → null
  static InstalledPluginEntry ParseInstalledPlugins(string installedPluginsJsonText)
  // 静的 2: settings.json の enabledPlugins から。malformed → false、キー無し → false
  static bool IsEnabledInSettingsJson(string settingsJsonText)
  // 動的: init の plugins[] / plugin_errors[] から状態を決める
  static UnityPluginState Resolve(bool cliAvailable, InstalledPluginEntry installed, bool enabledInSettings,
                                  IList<PluginEntry> initPlugins, IList<PluginError> initErrors)
```

`Resolve` の規則: `!cliAvailable → CliUnavailable`。`initErrors` に一致 → `LoadError`。
`initPlugins` に一致 → `Loaded`。init が **無い**(未接続、`initPlugins == null`)なら
静的のみで `NotInstalled` / `EnabledNotLoaded`(このとき `EnabledNotLoaded` の表示は
「導入済み」とだけ出し、"読み込んでいない" は言わない -- 未接続なのだから)。
init が **ある**のに一致が無ければ、静的が有効なら `EnabledNotLoaded`、無効なら `NotInstalled`。

`IsEnabledInSettingsJson` は `enabledPlugins["unity@unity-agent-plugin"] == true` に加え
`enabledPlugins["unity"] == true` も真とする(公式文書は両形式を例示。実測で書かれたのは前者)。
`false` は明示的無効化なので偽。JSON は既存の `Core/Json` パーサで読む。「導入済み」は
`installed != null && enabledInSettings` で判定する。`installed_plugins.json` を第一にする理由は
検証報告 §2: バージョンと commit を持ち、`enabledPlugins` だけでは「有効だがキャッシュが消えた」を
見分けられない。

### 1.4 プロトコル側の変更

`SystemInitMessage` に `Plugins`(`PluginEntry { Name, Path, Source, Version }[]`、実測フィールド)と `PluginErrors`
(`PluginError { Plugin, Type, Message }[]`)を追加する。キー欠落は空配列(`plugin_errors` は
「エラーが無いとき省略される」と文書にあり、実測でもキー自体が無かった)。既存フィクスチャ 5 本の
`"plugins":[]` がそのまま「空でパースできる」回帰テストになる。`Plugins` に 1 件入ったフィクスチャは
実測の init(`docs/verify/2026-09-10-unity-plugin-init-event.json`)から切り出す
(`Fixtures/init_plugins.json`)。

環境ファイルの場所: `~/.claude` は `CLAUDE_CONFIG_DIR` で移動できる。`ClaudeCliProcess` は
親環境から `CLAUDECODE` 等を **削除**するが `CLAUDE_CONFIG_DIR` は触っていない
(R02 §8.2 の掃除リスト)。静的検出も同じ規則で `CLAUDE_CONFIG_DIR` があればそれを、無ければ
`%USERPROFILE%`/`$HOME` 直下の `.claude` を見る。解決は `UnityPluginPaths.ResolveConfigDir(
IDictionary env, string homeDir)` の純関数にして、本番は `Environment` を渡す。

### 1.5 `--plugin-dir` 経路(実装するが既定 OFF)

`AgentClientOptions.PluginDirs`(`List<string>`)を追加し、`ComposeArgs` が `--plugin-dir <path>` を
1 つずつ付ける(`AppendToolList` と同じ「値ごとに 1 フラグ」の書き方、`AgentClient.cs:899-913`)。
本番で値を入れるのは **`EnabledNotLoaded` が 2 回連続のスポーンで観測されたとき** に限る
(`--bare` 既定化の検出)。そのときは静的で見つけた cache パスを渡す。今回は引数合成と
その単体テストまでを実装し、自動投入は `PanelSettings.unityPluginForcePluginDir`(既定 false、
上級者向け、設定 UI には出さない)で手動有効化できる状態に留める。理由: 未来の CLI 挙動を
先回りして自動化すると、予告が外れたとき誰も気づかない。

---

## 2. Stream B -- 設定カード

### 2.1 配置

`SettingsView.BuildUloopSection` の **直後**に `BuildUnityPluginSection` を足す
(`SettingsView.cs:312` の呼び出し列)。両方とも「外部ツールの導入」で、UapOps セクションと
Extension Profiles セクションの間に並ぶ。折りたたみセクション、アイコンは uLoop と同じ
`d_Package Manager`。

### 2.2 中身(既定の折りたたみ状態で 3 行)

```
Unity 公式プラグイン                                  [状態バッジ]
  {状態文 1 行}                                       ← §1.2 の表示
  [導入]  {進捗 1 行、非表示が既定}
  ▸ 詳細(折りたたみ、既定閉)
      提供元 Unity Technologies / ライセンス Unity Companion License / Unity 6+ 向け
      導入先はユーザースコープ(~/.claude)。他プロジェクトの Claude Code にも効きます。
      リポジトリ: github.com/Unity-Technologies/unity-agent-plugin(コピー用 TextField)
  [x] Claude に公式スキルの使い分けを指示する(Stream C の切替、既定 ON)
```

2026-08-12 ノート §2b の規則をそのまま適用する: パスと長文は折りたたみかツールチップへ、
警告(スコープがユーザー全体に及ぶこと、Unity 6 前提)は **画面に残す**。

### 2.3 導入ボタンの挙動

1. クリック → 確認カード展開(uLoop の `_uloopInstallConfirmCard` と同じ型、`SettingsView.cs:134`)。
   本文は 1 行: 「Claude Code のユーザー設定に Unity 公式プラグインを追加します。」+ caveat 2 行
   (ユーザースコープ / Unity 6 向け)。[導入] [キャンセル]。
2. [導入] → `UnityPluginInstaller.Run(cliPath, onProgress, onComplete)`。ThreadPool で **2 段階**を
   直列実行(`AuthCli.Logout` と同じ ThreadPool + main-thread 完了通知の型、`AuthCli.cs:126-157`):
   ```
   claude plugin marketplace add Unity-Technologies/unity-agent-plugin
   claude plugin install unity@unity-agent-plugin --scope user --yes
   ```
   `--yes` はヘルプに「stdin/stdout が TTY でないとき必須」とある。実測(検証報告 §2)では
   1 段目 3.0 秒、2 段目 1.1 秒、**再実行はどちらも exit 0** で「already on disk」「already
   installed」を返すので、既登録ユーザー向けの失敗分類は不要。判定は exit code のみ。
   `--yes` が「unknown option」で拒否された(v2.1.218 で未確認)ときだけ、`--yes` 無しで
   1 回再試行する。
3. 進捗: 「導入中... {n}秒」(2026-08-12 §2a と同じ `EditorApplication.update` 経由の秒表示)。
   marketplace add は git clone を伴う(実測 3 秒、回線次第で数十秒)。タイムアウトは各段 120 秒
   (`OneShotCli.Run` の `timeoutMillis`)。
4. 完了: 静的検出をやり直して状態を更新。成功時は「導入しました。**新規チャットから**有効に
   なります」。CLI はプラグインを起動時に読むので、現在のセッションには効かない。既存の
   「次回スポーン時に反映」機構(`_lastSpawnedSettingsSnapshot` / `CloneNextSpawnOnlyFields`、
   `AgentHub.cs:5033-5072`)には乗せない -- これはパネル設定ではなく CLI 側の状態なので、
   文言で伝えるだけにする。
5. 失敗: exit code と stderr の末尾 1 行を表示。よくある 2 件だけ専用文言を持つ:
   git が無い(marketplace add が git を要求する可能性、§7)/ ネットワーク不通。

**ドメインリロード横断**: ThreadPool 上の子プロセスはリロードで完了通知先を失う。uLoop と
同じく `SessionState` に「導入進行中 + 開始時刻」を置き、リロード後に (a) 静的検出が有効 →
成功扱い、(b) 5 分未満 → 「導入中...」継続、(c) それ以上 → 「`claude plugin list` で確認して
ください」。

### 2.3b 実装メモ(2026-09-10)

- 導入ボタンは `NotInstalled` のときだけ出す。`Disabled` は状態文で `claude plugin enable` を案内し、
  ボタンは出さない(再導入で有効化されるかは未確認で、意図しない上書きを避ける)。
- 進捗カウンタは `EditorApplication.update` ではなく `_root.schedule.Execute(...).Every(500)`。
  実行中だけ動かし、完了・失敗・Stalled で止める。リロード後は `SessionState` の flag を見て
  `RefreshUnityPluginSection` が自分で再開する。
- `--yes` 拒否のフォールバックは「install 段の exit code が非 0 なら `--yes` 無しで 1 回だけ再実行」。
  `OneShotCli` は stderr を捕らえないので「unknown option」の文字列判定はできない。2 回目の
  verdict が最終。
- 動的真実源は `AgentHub.LastKnownInitMessage`。切断後も最後の init が残るため、
  「現在のチャット」という表現は厳密には「最後に接続したチャット」。次の接続で更新される。
- 状態文 7 種は `SettingsView.DescribeUnityPluginStatus`(純関数)に閉じ込め、
  `UnityPluginSettingsCardTests` が全行の非空・相異を固定する。

### 2.4 やらないこと

- アンインストール / 無効化ボタン。導入は「入れる」方向だけをワンクリック化し、外すのは
  ターミナル(`claude plugin uninstall unity@unity-agent-plugin`)に任せる。カードの詳細に
  そのコマンドを書く。
- プロジェクトスコープ導入(`--scope project`)。`.claude/settings.json` を書き換えると
  リポジトリに載る。ユーザーが意図して選ぶべきもので、既定の導線にはしない。
- 更新チェック。`claude plugin update` の自動実行はしない。cache のバージョン名を表示する
  だけ。

---

## 3. Stream C -- 誘導行(system prompt)

### 3.1 なぜ Extension Profile ではなく専用の steering 行か

Extension Profile は「プロジェクト内の SDK を検出して、その SDK の扱い方を注入する」仕組みで、
検出軸は `packageIds` / `typeNames`(`ExtensionProfileDetector.cs:33-60`)。公式プラグインは
プロジェクトではなく **ユーザー環境**にあり、検出軸が違う。プロファイル JSON に第 3 の
検出軸を足す(R12 §3.2 の案)よりも、UapOps steering 行と同じ「純関数で 1 セクションを組み、
`ComposeAppendSystemPrompt` に渡す」型(`AgentHub.cs:3150-3170`、
`ComposeUapOpsSteeringSection`)の方が既存の 4 引数オーバーロードにそのまま並ぶ。

### 3.2 合成規則

```
internal static string ComposeUnityPluginSteeringSection(
    bool pluginEnabled,        // 静的検出(スポーン時点では init が無いので静的のみ)
    bool steeringEnabled,      // 設定トグル(§2.2 の最終行)
    bool uapOpsEnabled,        // settings.uapOpsEnabled
    string unityVersion)       // Application.unityVersion
```

- `!pluginEnabled || !steeringEnabled` → 空文字。
- 順序: custom instructions → cost-policy → **UapOps steering → Unity plugin steering** →
  profiles。5 引数オーバーロードを **新設**し、4 引数版は無変更(既存テストを壊さない、
  `AgentHub.cs:3146-3149` と同じ流儀)。
- 本文(英語、他の steering 行と同じ):
  1. 「Unity's official plugin is installed: skills named `/unity:*` are available. Route UI work
     through `/unity:ui`, package selection through `/unity:unity-package-management`, and asset or
     scene lookups through `/unity:generate-editor-search-query`.」
  2. `uapOpsEnabled` のとき: 「Editor control is already provided by the `uap_*` tools of the
     `unity-ops` MCP server. Do NOT install or invoke the `unity` CLI, `unity mcp`, `unity command
     eval`, or `com.unity.pipeline` for Editor control, even when `/unity:unity-cli` suggests it.」
     -- `unity-cli` スキルは「Editor が開いていれば `unity command` で操作せよ」「無ければ
     `curl | bash` で CLI を入れよ」と指示する(R12 §1.3)。パネルの権限カードは Bash を止められる
     が、先に「使うな」と言う方が安い。
  3. `unityVersion` のメジャーが 6000 未満のとき: 「This project runs Unity {version}. The plugin's
     skills target Unity 6+; skip guidance that requires Unity 6 (UI Toolkit runtime data binding,
     Render Graph, `com.unity.pipeline`) and prefer the 2022-LTS equivalents.」
     メジャー判定は `UnityVersionParser.Major(string)` の純関数(`"2022.3.22f1"` → 2022、
     `"6000.0.1f1"` → 6000、解釈不能 → -1 で但し書きを **出す**側に倒す)。
  4. `uapOpsEnabled` かつ D 実装後: 「When `/unity:generate-editor-search-query` produces a query,
     run it with `uap_search` instead of opening the Search window.」

### 3.2b 実装メモ(2026-09-10)

- 純関数は `AgentHub.ComposeUnityPluginSteeringSection(bool installed, bool steering, bool uapOps, string version)`。
  `UnityPluginStatus` を受けるオーバーロードが「installed = EnabledNotLoaded または Loaded」に畳む。
  スポーン時は `UnityPluginProbe.ReadStatic(true)` を渡す(CLI が無ければそもそもスポーンしない)。
- 3 行はいずれも `internal const`(`UnityPluginSteeringRoutingLine` / `...EditorControlLine` /
  `...Unity6CaveatFmt`)で、テストは本文の一致ではなく定数参照で固定する。ASCII のみ。
- 4 行目(`uap_search` への誘導)は Stream D で足す。

### 3.3 設定

`PanelSettings.unityPluginSteeringEnabled`(bool、既定 true)。next-spawn-only フィールドとして
`CloneNextSpawnOnlyFields` に追加(反映は次回スポーン)。設定 UI は §2.2 の最終行。

---

## 4. Stream D -- `uap_search`

### 4.1 位置づけ

`uap_asset_find`(`Ops/UapAssetFindTool.cs`)は `AssetDatabase.FindAssets` のフィルタ文字列
(`t:Material Red`)しか受けない。公式スキル `generate-editor-search-query` は Unity Search の
クエリ(`t:prefab ref:Player.prefab`、`h: t:Light -isstatic` など)を組み立て、Editor 側の
スニペットで Search ウィンドウを開くことを前提にしている。パネル内では **結果をチャットに返す**
方が価値がある。`SearchService`(`UnityEditor.Search`)は 2021.1 から組み込みなので 2022.3 で使える。

### 4.2 仕様

| 項目 | 値 |
|---|---|
| 名前 | `uap_search` |
| Module | `core` |
| ReadOnly / Undoable | true / false |
| 入力 | `query`(必須、Unity Search 構文)、`providers`(任意、`["asset","scene"]` の部分集合、既定は両方)、`maxResults`(任意、既定 50) |
| 出力 | `{ query, providers, totalMatches, items: [{ provider, id, label, path }] }` |

- `path`: asset provider は `AssetDatabase` のアセットパス、scene provider は `UapAddressing` の
  階層パス(`item.ToObject<GameObject>()` を経由)。どちらも既存ツールが返す形式と同じにして、
  結果をそのまま `uap_object_inspect` / `uap_property_set` の `target` に渡せるようにする。
- 実行: `SearchService.CreateContext(providerIds, query)` → `SearchService.Request(context,
  SearchFlags.Synchronous)`。同期フラグで main thread 上で完結させる(`UapMainThreadDispatcher` の
  既存待ち時間内)。
- **アセット側のプロバイダ id は `asset` ではなく `adb`**(検証報告 §3)。`asset` は検索インデックス
  依存で、batchmode では作成直後のアセットを 25 秒待っても拾えなかった(インデックス文書数が
  増えない)。`adb` は `SearchService.Providers` 上は inactive だがコンテキストに明示すれば動き、
  `AssetDatabase.FindAssets` と同じ結果を初回 1.0 秒、以後 10ms 前後で返す。入力の `providers`
  は `["asset","scene"]` の語彙のまま受け、内部で `asset → adb` に写像する(エージェントに Unity
  内部の id を覚えさせない)。
- 実測(Material 200 + Prefab 50 + シーン 300 オブジェクト): `scene` 初回 130ms、以後 3〜10ms。
  よって打ち切りは 2 秒で十分。打ち切ったときは `truncated: true` を返す。
- `SearchItem` からの取り出し: `item.ToObject()` が `Material` / `GameObject` を返す。アセットは
  `AssetDatabase.GetAssetPath`、シーンは `transform.parent` を辿った階層パス。`id` は
  アセットが `GlobalObjectId_V1-...`、シーンがインスタンス ID(負数)なので、`id` は返すが
  エージェントには `path` を使わせる。
- 空シーンでの `scene` は 0 件で例外なし。未知のプロバイダ id も例外にならず 0 件になるので、
  ツール側で `providers` の語彙を検証して `ArgumentException` にする(黙って 0 件を返さない)。

### 4.2b 実装メモ(2026-09-10)

- 打ち切りは `maxResults` のみ。`SearchFlags.Synchronous` は途中で止められないので、2 秒の
  時間打ち切りは実装しない。`truncated` は「総件数 > 返した件数」の意味。
- **scene プロバイダはキャッシュを自分では更新しない**(検証報告 §7)。コンテキストの寿命に
  関係なく、最初のクエリ以降に作られたオブジェクトは見えない。`uap_search` は scene を含む
  クエリの前に `SceneProvider.InvalidateScene()` をリフレクションで呼ぶ。メソッドが無い将来の
  Unity では何もしない(その場合はプロバイダ自身の hierarchyChanged 頼み)。
- 共有コンテキスト方式(`searchText` の差し替え)は `adb` まで 0 件になったので不採用。
  コンテキストは呼び出しごとに作って捨てる。
- 結果の `provider` は Unity の id ではなく入力語彙(`asset` / `scene`)で返す。
- `adb` の項目は `provider.id` が `"asset"`(組み込みリソースは `"_group_provider_Resources"`)で
  返る(検証報告 §8)。要求 id でフィルタしてはいけない。コンテキストを要求プロバイダだけで
  作り、`scene` か否かだけで分類する。

### 4.3 steering への追加

`UapOpsModuleFamilies` の `core` 説明(`AgentHub.cs:3195`)に "search" を足す。
Stream C §3.2 の 4 行目で公式スキルとの接続を言う。

---

## 5. テスト

すべて EditMode(`Tests/Editor/`)、CI は既存の GameCI ジョブ(2026-08-22 ノート)。

| テスト | 何を固定するか |
|---|---|
| `UnityPluginDetectorTests` | `IsEnabledInSettingsJson`: 両キー形式 / `false` / キー無し / 壊れた JSON / 空。`FindCachedVersion`: 複数バージョンから最新、無し → null。`Resolve`: 状態表(§1.2)の全行 + 「init 無し」列 |
| `UnityPluginPathsTests` | `CLAUDE_CONFIG_DIR` あり/なし、Windows / POSIX の home |
| `SystemInitMessagePluginsTests` | `"plugins":[]` の既存 5 フィクスチャが空配列で通る。`init_plugins.json` で 1 件 + `plugin_errors` 1 件を読む。キー欠落 → 空配列 |
| `UnityPluginInstallerTests` | 2 コマンドの引数文字列を固定(`--scope user --yes` を含む)。`ClassifyMarketplaceAddFailure` の「既に登録済み → 続行」「git 無し」「その他」 |
| `AgentClientOptionsPluginDirTests` | `PluginDirs` 0 / 1 / 2 件の `--plugin-dir` 合成。`--bare` が **決して**含まれないことを既存の `AgentHubStartClientArgTests` に 1 本足す |
| `AgentHubUnityPluginSteeringTests` | 無効 2 通り → 空。有効 + uapOps ON/OFF。バージョン 2022 / 6000 / 解釈不能 の但し書き有無。5 引数オーバーロードの順序(UapOps steering の後、profiles の前) |
| `UnityVersionParserTests` | `2022.3.22f1` / `6000.0.1f1` / `2021.3` / 空 / ゴミ |
| `UapSearchToolTests` | スキーマ(必須 `query`、`additionalProperties:false`)。実行は一時フォルダに Material を 1 つ作って `t:Material` で拾う(既存の `UapAssetFindTool` テストと同じ段取り)。未知 provider → `ArgumentException` |
| `SettingsView` | 既存の「合成状態でカードを構築して高さを測る」型(2026-08-12 §1)で、既定折りたたみ時の行数が 3 行であることを固定 |

---

## 6. 見送り: `unity mcp` 併記(条件付き下書き)

実装しない。ただし将来のために条件と形だけ残す。

- **条件**: プロジェクトが Unity 6000 以上、`Packages/manifest.json` に `com.unity.pipeline` が
  依存キーとして存在(`UloopDetector.DeclaresDependencyKey` と同じ「キー + コロン」判定)、
  `unity` バイナリが PATH にある。
- **形**: `--mcp-config` に `{"unity": {"command": "unity", "args": ["mcp", "--project-path",
  "<projectRoot>"]}}` を `unity-ops` と並べる。`UloopCapabilityMatrix` に「Pipeline が担う
  ツール名」を宣言する場所は既にある(`UloopCapabilityMatrix.cs:19-25`)。
- **未解決**: `eval` は任意 C# を Undo なしで走らせる。パネルの「ターン単位 Undo」と
  「破壊的操作は `confirm:true`」の保証が外れるので、`eval` を `--disallowedTools` に入れるか
  権限カードで毎回止めるかを決めてから。Stream C の 2 行目(「`unity` CLI を使うな」)と
  正面から矛盾するので、併記するなら steering 行も条件分岐が要る。

---

## 7. 実機確認(実装前に 1 回、実装後に 1 回)

実施済み(2026-09-10、`docs/verify/2026-09-10-unity-plugin-verification-report.md`)。CLI v2.1.267、
Unity 2022.3.22f1(GameCI イメージ、batchmode)。

| # | 確認 | 結果 | 設計への反映 |
|---|---|---|---|
| 1 | パネルと同じ引数の `-p` で `/unity:*` が出るか | **出る。** init に `plugins` 1 件、`slash_commands` に `unity:*` 31 本、Skill ツールで本文も展開 | 変更なし。§1.5 は既定 OFF のまま |
| 2 | `plugins[].name` の実際の文字列 | `name:"unity"`, `source:"unity@unity-agent-plugin"`, `version`, `path` | §1.3 `Matches(name, source)`、§1.4 のフィールド |
| 3 | `claude plugin marketplace add` の非 TTY 動作 | 3.0 秒 + 1.1 秒、再実行は exit 0 | §2.3 の失敗分類表を削除 |
| 4 | `--yes` | v2.1.267 にあり。v2.1.218 は未確認 | §2.3 のフォールバックは残す |
| 5 | `SearchService` 同期リクエスト | `asset` は新規アセットを拾えず、`adb` が 1.0 秒 / 10ms、`scene` が 130ms / 5ms | §4.2 を `adb` 前提に書き換え |
| 6 | `CLAUDE_CONFIG_DIR` | CLI は追随(空ディレクトリで `plugins: []`) | §1.4 のとおり |

残る未確認: v2.1.218 での `--yes`(#4)、GUI エディタでの `asset` インデックス増分更新(#5、`adb` を
使うので設計上は影響なし)。パネル本体での補完表示は `SystemInitMessage` 拡張後の回帰テストで見る。

---

## 8. 実装順序と版

1. **v0.36.0**: Stream A(検出 + プロトコル)→ B(カード + 導入)→ C(誘導行)。A の純関数と
   テストを先に入れ、B は A の状態表示だけを先に出してから導入ボタンを足す。C は B のトグルに
   依存する。§7 の #1〜#4 は済み。
2. **v0.37.0**: Stream D(`uap_search`)+ Stream C の 4 行目。§7 の #5 は済み(`adb` 前提)。
3. **未定**: §6。

CHANGELOG は Keep a Changelog の `Added` に 3 項目(設定カード、誘導行、`uap_search`)、
`Changed` に `SystemInitMessage` のフィールド追加。README の「特徴」には「Unity 公式プラグイン
の検出と導入」を 1 行足す。

---

## 9. 却下した代替案

- **公式スキル本文の同梱(`.uap-profiles` や `Documentation~` に写す)**: UCL と MIT の混在
  (R12 §4.1)。導入案内で足りる。
- **Extension Profile JSON に「プラグイン検出」軸を追加**: §3.1 のとおり検出対象が違う。
  プロファイルは「プロジェクトの SDK」、steering は「パネルとツールの使い方」という既存の
  役割分担を崩さない。
- **導入後に自動で新規チャットを開始**: ユーザーの会話を勝手に閉じる。文言で「新規チャット
  から有効」と伝えるだけにする。
- **`claude plugin list --json` を検出の真実源にする**: 子プロセス起動を設定画面の描画ごとに
  走らせることになる。静的ファイル読み + init の 2 源で足り、プロセスは導入時にしか起こさない。
