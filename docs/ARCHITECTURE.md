# Agent Panel for Unity アーキテクチャ決定文書 (ADD)

- 作成日: 2026-07-29
- ステータス: 承認済み(Phase 1〜5 の実装根拠。以降の個別判断は `docs/design-notes/` に記録)
- 注記: 本文中の検証環境(作者のローカルプロジェクトやサンドボックスのパス)は執筆当時の作者環境をそのまま残した歴史的記録で、パッケージの動作要件ではない。現在の開発手順は [CONTRIBUTING.md](../CONTRIBUTING.md) を参照
- 対象 Unity: **2022.3 LTS**（検証環境 2022.3.22f1）/ Windows 優先・macOS/Linux の継ぎ目を確保
- 前提: Claude Code CLI **v2.1.218**。認証はサブスクリプション(Pro/Max)を推奨するが、v0.40.0 以降 `ANTHROPIC_API_KEY` を設定してもブロックしない(docs/design-notes/2026-09-10-claude-api-key-auth-passthrough.md)。CLI 自身の認証選択に委ね、どちらが使われているかを設定 > アカウントに表示する
- 根拠資料: `docs/research/01-reference-comfyui-mcp-panel.md`（以下 R01）、`02-claude-cli-protocol.md`（R02）、`03-unity-integration.md`（R03）、`04-target-project.md`（R04）、`05-ux-spec.md`（R05）

---

## 1. 決定事項サマリ

### D1. CLI 起動方式（Windows）: `claude.exe` をフルパスで直接スポーン

**決定**: `System.Diagnostics.Process` で **`%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe` を直接起動**する。シム（claude.cmd / .ps1 / sh）や `node.exe` は使わない。

**根拠**:
- v2.1.218 は Bun コンパイルの単一ネイティブ exe（約 264MB）であり `cli.js` が存在しない。3 つのシムは exe への薄い委譲に過ぎず、間に cmd.exe / powershell.exe を挟むとウィンドウ抑止・stdin 転送・ツリーキルが全て複雑化する（R02 §1, §8.1）。
- `.cmd` シムは `UseShellExecute=false` で起動できない環境がある（R03 §1.4）。

**パス解決順**（`CliLocator` に実装）:
1. EditorPrefs のユーザー手動指定パス（最優先の脱出ハッチ。R03 リスク表）
2. Windows: `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`（実在確認済み。R02 §8.1）
3. Windows: PATH 上の `claude.cmd` を見つけ、隣の `node_modules` を辿って exe を解決
4. Windows ネイティブインストーラ: `%USERPROFILE%\.local\bin\claude.exe`
5. **macOS/Linux の継ぎ目**: `which claude` 相当（`/usr/local/bin`, `~/.local/bin`, `~/.npm-global/bin` を探索）。探索リストは `ICliPathProbe` のプラットフォーム別実装に分離し、Windows 実装以外はスタブでも構造は最初から用意する。

**ProcessStartInfo の要点**（R02 §8.2, R03 §1.1/1.4）:
- `UseShellExecute=false` / `CreateNoWindow=true` / stdio 3 本リダイレクト / `WorkingDirectory = Unity プロジェクトルート`（セッション保存先・CLAUDE.md 探索基準になる）。
- `StandardOutputEncoding = StandardErrorEncoding = new UTF8Encoding(false)`。
- **stdin**: Unity 2022.3 の Mono プロファイルに `StandardInputEncoding` が存在しないため、**`process.StandardInput.BaseStream` に UTF-8 バイトを直接書く**。これを怠ると日本語 Windows（CP932）で CJK 入力が壊れ、不正 JSON 行 → プロセス即死（D2 参照）に直結する（R02 §8.3, R03 §1.4）。
- 環境変数の掃除: `CLAUDECODE` / `CLAUDE_CODE_ENTRYPOINT` / `CLAUDE_CODE_SESSION_ID` を常に Remove（親環境からの汚染防止。R02 §8.2）。`ANTHROPIC_API_KEY` は v0.40.0 以降デフォルトでは触らない -- Claude Code の組み込み認証方式を無効化・制限してはならないという Anthropic の利用条件に基づき、CLI 自身の判断に委ねる。設定 > アカウントの「APIキー認証」を「サブスクリプションのみ」にした場合のみ Remove する(`PanelSettings.claudeAuth`, `ClaudeCliProcess.ComputeEnvVarsToRemove`, docs/design-notes/2026-09-10-claude-api-key-auth-passthrough.md)。
- 終了: graceful = `interrupt` 制御リクエスト → stdin close → 短い WaitForExit。強制 = **`taskkill /PID <pid> /T /F`（ツリーキル必須**。claude.exe は bash/ripgrep/MCP の子を持つ。R02 §8.4）。macOS/Linux 継ぎ目は `IProcessKiller`（Windows=taskkill、Unix=`kill -TERM` プロセスグループ）。

### D2. プロトコル: 双方向 stream-json 常駐プロセス（1 チャット = 1 プロセス）

**決定**: `-p` 単発実行ではなく、**常駐双方向モード**を採用する。

```
claude.exe -p --input-format stream-json --output-format stream-json --verbose \
  --include-partial-messages --replay-user-messages --permission-prompt-tool stdio \
  [--resume <session_id>] [--model <model>] [--permission-mode <mode>]
```

**根拠**:
- 双方向モードは実測で検証済み: stdin に user イベントを流し続けると同一プロセス・同一セッションでマルチターンが走り、EOF で終了する（R02 §4.1）。単発 `-p` はターン毎のコールドスタート（数秒）とセッション状態の分断を招く。
- `--verbose` は stream-json 出力の必須条件（省略時のエラーを実測確認。R02 §3.1）。
- `--include-partial-messages` で `stream_event`（SSE ラップ）が得られ、タイプライター描画（R01 §9 のパターン）の入力になる。
- `--replay-user-messages` のエコーバック（`isReplay:true`）を **配達確認 ack**（R01 §12.3 の Sending→受領モデルの簡易版）として使う。
- 起動直後に `initialize` control_request を送り、**モデル一覧（価格付き）・スラッシュコマンド・アカウント状態・PID** を取得する（実測確認。R02 §5.1）。ComfyUI パネルの「`models` フレーム受信こそが真のハンドシェイク」原則（R01 §5.1, §14-1）を踏襲し、**「接続済み」表示は initialize 応答（または system/init）受信後のみ**とする。
- 停止ボタン = `interrupt` control_request（`{"still_queued":[]}` 応答を実測確認。R02 §5.2）。

**防御的シリアライズ（最重要制約）**: 不正な JSON 行を 1 行でも書くと CLI は stdout 無出力のまま exit 1 で即死する（実測。R02 §4.2）。よって stdin へ書くものは**必ずシリアライザ生成の 1 行 JSON + `\n` + 行毎 Flush** に限定し、生テキストを書く経路をコードレベルで存在させない。プロセス死亡時は保存済み session_id で `--resume` 再起動する回復路を常備する（R02 リスク）。

### D3. パーミッション処理: `--permission-prompt-tool stdio` + `can_use_tool` インラインカード

**決定**: v2.1.218 で実測確認済みの **`--permission-prompt-tool stdio`**（隠しフラグだが受理を確認）を使い、CLI からの `can_use_tool` control_request をパネルの**インライン許可カード**（非モーダル。R05 §3.7）で処理する。

- 許可: `{"behavior":"allow","updatedInput":{...},"updatedPermissions":[...]}` を control_response で返す。**「常に許可（スコープ付き）」は `updatedPermissions` で CLI 側に永続ルールを渡す**（R02 §5.3。R05 が懸念した settings.json 直接書き込みは不要になる — これが v2.1.218 で実際にサポートされている方式）。
- 拒否: `{"behavior":"deny","message":"...","interrupt":false}`。拒否 + 代替指示（入力欄は無効化しない）を 1 アクションで返せる UI にする（R05 §3.7）。
- 補助として起動時の `--permission-mode`（default/plan/acceptEdits 等。init への反映を実測確認。R02 §6）と `--allowedTools` / `--disallowedTools` 静的リストを設定 UI から構成可能にする。
- **`--dangerously-skip-permissions` は既定で使わない**。設定画面での明示オプトイン（警告付き）のみ（R02 §6, R03 §5.3）。
- 応答なしのままではツールは実行されない（CLI 側タイムアウト 10 分）。非フォーカス時は EditorWindow の titleContent に黄ドットバッジ（R05 §3.7）。
- 隠しフラグはバージョン間で変わり得るため、**起動時に `claude --version` / init の `claude_code_version` を検証**し、想定外バージョンでは警告 + 静的 allowlist モードへフォールバックする（R02 リスク）。

### D4. ドメインリロード戦略: 「kill(tree) + `--resume`」を一級 UI ステートとして扱う

**決定**: リロード前に子プロセスをツリーキルし、リロード後に `--resume <session_id>` で新プロセスを起こす。**Resuming をエラーではなく日常の UI ステートとして設計する**（R05 §3.9）。

手順（R03 §2.2, R05 §3.9, R01 §7.2）:
1. `AssemblyReloadEvents.beforeAssemblyReload`（同期のみ可）: session_id・「ターン実行中」フラグ・PID を SessionState に保存 → interrupt 送信 → stdin close → 短い WaitForExit → taskkill /T /F。トランスクリプトは ScriptableSingleton に保存済み。
2. リロード後 `[InitializeOnLoadMethod]`: トランスクリプトを即時再描画（体感を途切れさせない）→ バックグラウンドで `--resume` 再接続 → バナー「⟳ 再コンパイル → ✓ セッションを再開しました」。
3. **mid-turn 中断だった場合**: ComfyUI パネルの MID_TASK ナッジ（R01 §7.2）を翻案し、「⚠ コンパイルにより中断されました [続きを実行]」ボタンをワンクリック continue にする（自動送信はしない — ツール実行の途中状態が不定のため。R03 §2.2）。
4. **ゾンビ掃除**: 起動 PID + プロセス開始時刻を SessionState/一時ファイルに記録し、次回初期化時に「PID 生存 + 開始時刻一致 + プロセス名 claude」なら kill（PID 再利用の誤爆防止。R03 §2.3）。
5. 補助: ストリーミング中の `EditorApplication.LockReloadAssemblies()` は**必ず try/finally + タイムアウト付き**のオプション機能とし、安全網はあくまで kill+resume に置く（R03 §2.2）。

WS ブリッジ分離（R01 §15 の「エディタ再起動してもエージェント生存」案）は v1 では**採用しない**。kill+resume がネイティブサポートされており（R02 §7.2 で同一 session_id 継続を実測）、中継プロセスの複雑さに見合わないため。将来の拡張シームとして `IAgentTransport` 抽象だけ残す。

### D5. 永続化戦略: 3 層分担 + 「正史は CLI 側」

**決定**（R03 §2.4）:

| 層 | API | 保存対象 |
|---|---|---|
| リロード生存（エディタ終了で消える） | `SessionState` | 現在の session_id、実行中フラグ、PID + 開始時刻、入力途中テキスト、スクロール位置 |
| マシン単位（全プロジェクト共有） | `EditorPrefs` | CLI 手動パス、Ctrl+Enter 送信設定、Learn チェックリスト既読。キーにプロジェクト識別子を含める |
| プロジェクト単位（エディタ再起動生存） | `ScriptableSingleton<T>` + `[FilePath("UserSettings/AgentPanel/State.asset", ProjectFolder)]` | チャット履歴キャッシュ（直近 N 件）、セッション一覧メタ、パネル設定 |

- **トランスクリプトの正史は `~/.claude/projects/<cwd変換名>/<session-uuid>.jsonl`**（変換規則・中身の形式を実測確認。R02 §7.1）。パネル側履歴は表示キャッシュに徹し、履歴ブラウザ（Phase 3）はこの JSONL を直接列挙・パースする。IndexedDB 全体を自前で再発明しない（R01 §8 の 1,800 行ストアは Unity では不要）。
- `Save(true)` はメッセージ確定時のみ（毎デルタで YAML 書き込みしない。R03 §2.4）。

### D6. UI スタック: UI Toolkit (UXML/USS) + ScrollView 手動管理

**決定**: `EditorWindow` + UI Toolkit。**ListView は使わず ScrollView + 手動要素追加 + 古メッセージ間引き**。

**根拠**:
- 2022.3 の DynamicHeight 仮想化は既知バグ持ち（ScrollToItem 誤位置、bindItem 全件呼び出し）で、ストリーミング中の毎フレーム高さ変化はその最悪ケース（R03 §3.2）。
- IMGUI は表現力不足（codexbridge の自認。R03 §5.1）。
- stick-to-bottom は `contentContainer` の GeometryChangedEvent + `verticalScroller.highValue`、追従解除判定つき（R03 §3.3）。
- Enter=送信 / Shift+Enter=改行は `KeyDownEvent` を `TrickleDown` で捕捉し `StopPropagation+PreventDefault`（2022.3 公式パターン。R03 §3.4）。IME 変換確定 Enter の実機テスト必須 + **Ctrl+Enter 送信オプション**を逃げ道として用意（R03/R05）。
- テーマ: `--uap-*` USS カスタム変数一式（R05 §5.2 のトークン表をそのまま採用）+ `EditorGUIUtility.isProSkin` で root に `uap-theme-dark/light` クラス。ハードコード色禁止。
- スレッドモデル: `OutputDataReceived`（ThreadPool）→ `ConcurrentQueue<string>` → `EditorApplication.update` でフレーム毎上限付きポンプ（例: 20 行 or 2ms）。SynchronizationContext 捕捉はドメインリロードで無効化されるため主経路にしない（R03 §1.3）。
- ストリーミング描画: R01 §9 のタイプライター（target/shown カーソル + 単一更新ループ）を `EditorApplication.update` で同型実装。UI 更新は 50–100ms バッチ（R05 §3.5）。

### D7. Markdown レンダリング: 自前の安全サブセット変換（依存ゼロ）

**決定**: **自前実装**。インライン要素（強調/斜体/インラインコード/リンク）は Unity リッチテキストタグへ変換して Label に、ブロック要素（見出し/リスト/引用/コードブロック）は USS クラス付き VisualElement に変換する（R03 §4.1）。

- **エスケープは単一チョークポイント**: Label へ流す前に必ず `<` `>` `&` をエスケープ。モデル出力の `List<int>` がタグインジェクションで描画を壊すのを防ぐ（R03 §4.1, リスク表）。
- **ストリーミング中はプレーン（軽量整形）表示、メッセージ確定時に一度だけフル Markdown レンダリング**（R01 §9 と R03 §3.5 の一致した結論）。
- コードブロック: 等幅フォント + `--uap-bg-sunken` + 言語バッジ + Copy ボタン（`EditorGUIUtility.systemCopyBuffer`）。選択可能テキストが必要なら read-only multiline TextField をスタイルで擬装（R03 §4.1）。
- リンク: `<link>` タグ + クリックイベントで `Application.OpenURL` / `Assets/...` パスは PingObject リンク（D9 の Integration 層。R05 §4.3）。
- Markdig（BSD-2）はフル互換が必要になった時点での将来オプション。UnityGuillaume/MarkdownRenderer（UCL）は設計参考のみ、**GPL の UnityClaudeCLI のコードは読まない・写さない**（R03 §4.2, §5.3 — クリーンルーム徹底）。

### D8. JSON パーサ: 自前ミニ JSON（依存ゼロ）を採用

**決定**: **手書きの寛容な最小 JSON パーサ + 型付きマッパー**を Core に置く。Newtonsoft には依存しない。

**根拠**:
- R04 の調査でターゲットプロジェクトの manifest に `com.unity.nuget.newtonsoft-json` の直接依存は**確認されていない**（VRChat SDK 経由で間接的に存在する可能性はあるが保証できず、asmdef から参照するには明示依存が必要になり「依存ゼロ・フォルダドロップで動く」配布方針と矛盾する）。
- `JsonUtility` は多相・未知フィールド・辞書・null 許容に対応できず、stream-json の可変スキーマ（`content[]` の text/thinking/tool_use 混在、未知 type の無視）に根本的に不向き（R03 §6）。
- CLI の 1 行 JSON は数十 KB〜になり得る（initialize 応答。R02 リスク）ため、行長無制限・アロケーション控えめのトークナイザを自前で管理する価値がある。
- パーサは純 C# の独立クラスにし、Unity API 非依存 → **EditMode テストで実キャプチャ JSONL（R02 の検証ログ）をフィクスチャとして回帰テスト**できる（R01 §14-15 の MockBridge 戦略の Unity 版）。

**方針**: `JsonNode`（object/array/string/number/bool/null の軽量 DOM）+ 各メッセージ型の `FromJson(JsonNode)` 手書きマッパー。**未知の type / subtype / フィールドは黙って無視**する（R02 §3.2(4) の明示指針）。

### D9. パッケージレイアウト: 埋め込み UPM パッケージ・Editor 専用・依存ゼロ

**決定**: `Packages/jp.colloid.unity-agent-panel/`（embedded）。`package.json` は `"unity": "2022.3"`、依存パッケージゼロ（UI Toolkit は 2022.3 エンジン組込み。R03 §6, R04 §2）。

- asmdef: `Colloid.AgentPanel.Editor`（`includePlatforms:["Editor"]`, `autoReferenced:false`）+ `Colloid.AgentPanel.Editor.Tests`。
- ターゲットプロジェクトは既に embedded パッケージ 5 つを運用しており（R04 §2）、**manifest.json は VPM/VCC 管理のため触らない**。フォルダドロップ + `uloop compile` が配置検証の正規ルート（R04 §6）。
- ライセンス: MIT（GPL 先行実装に対する採用障壁での優位。R03 §5.3）。
- レイヤ間参照は一方向のみ: `UI → Model → Core` / `UI → Integration → (UnityEditor)` / **Core は UnityEngine/UnityEditor に依存しない**（テスト容易性の核）。

---

## 2. モジュール構成

```
Packages/jp.colloid.unity-agent-panel/
├── package.json                          # UPM マニフェスト（unity: 2022.3, 依存ゼロ）
├── README.md                             # 導入 3 方式（Git URL / embedded / OpenUPM 予定）+ /login 前提の明記
├── CHANGELOG.md                          # Keep a Changelog 形式
├── LICENSE.md                            # MIT
├── Editor/
│   ├── Colloid.AgentPanel.Editor.asmdef  # Editor 専用・autoReferenced:false
│   │
│   ├── Core/                             # ── プロセス/プロトコル層（UnityEngine/Editor 非依存）──
│   │   ├── Json/
│   │   │   ├── JsonNode.cs               # 軽量 JSON DOM（object/array/string/number/bool/null）
│   │   │   ├── JsonParser.cs             # 寛容トークナイザ（行長無制限・未知フィールド許容）
│   │   │   └── JsonWriter.cs             # 送信用シリアライザ（1 行 JSON 保証・改行/制御文字エスケープ）
│   │   ├── Protocol/
│   │   │   ├── StreamJsonMessage.cs      # 受信メッセージ基底 + type 判別ディスパッチ（未知 type は Ignore）
│   │   │   ├── SystemInitMessage.cs      # system/init（session_id, model, permissionMode, version, tools）
│   │   │   ├── AssistantMessage.cs       # assistant（content blocks: text/thinking/tool_use, usage, error）
│   │   │   ├── UserEchoMessage.cs        # user（isReplay ack / tool_result）
│   │   │   ├── ResultMessage.cs          # result（is_error, subtype, total_cost_usd, usage, modelUsage, permission_denials）
│   │   │   ├── StreamEventMessage.cs     # stream_event（SSE ラップの content_block_delta 等 → デルタ抽出）
│   │   │   ├── ControlMessages.cs        # control_request/response/cancel（initialize, interrupt, can_use_tool, set_permission_mode, set_model）
│   │   │   ├── PermissionModels.cs       # can_use_tool 要求 / allow(updatedInput,updatedPermissions) / deny(message,interrupt) の型
│   │   │   └── OutboundMessages.cs       # user メッセージ・control_request の組み立て（生文字列送信 API を持たない）
│   │   ├── Process/
│   │   │   ├── ICliPathProbe.cs          # OS 別 CLI 探索の抽象（Win/macOS/Linux の継ぎ目）
│   │   │   ├── WindowsCliPathProbe.cs    # %APPDATA%\npm → PATH シム追跡 → ~/.local/bin の順で claude.exe 解決
│   │   │   ├── UnixCliPathProbe.cs       # which claude 相当（v1 は最小実装）
│   │   │   ├── IProcessKiller.cs         # ツリーキル抽象
│   │   │   ├── WindowsProcessKiller.cs   # taskkill /PID /T /F
│   │   │   ├── ClaudeCliProcess.cs       # ProcessStartInfo 構築・起動・BaseStream UTF-8 stdin・stdout/err 行イベント・環境変数掃除
│   │   │   ├── LineChannel.cs            # ConcurrentQueue<string> ラッパ（ワーカー→メイン、フレーム毎上限デキュー）
│   │   │   └── ZombieReaper.cs           # PID+開始時刻記録と次回起動時の孤児 kill
│   │   └── Client/
│   │       ├── AgentClient.cs            # 中核ステートマシン: 起動→initialize→ターン管理→interrupt→resume。イベント発火（Unity 非依存）
│   │       ├── AgentClientState.cs       # NotStarted/Starting/Ready/Streaming/ToolRunning/WaitingPermission/Errored の enum + 遷移検証
│   │       ├── TurnTracker.cs            # ターン id 採番・mid 配達追跡（isReplay ack）・silence backstop タイマー
│   │       └── PendingRequestMap.cs      # control_request の request_id 相関（タイムアウト付き）
│   │
│   ├── Model/                            # ── セッション/メッセージのデータモデル ──
│   │   ├── ChatMessage.cs                # role + blocks[]（text/thinking/tool/system-note/permission/error）直列化可能
│   │   ├── ToolCallRecord.cs             # ツール名・入力要約・状態・所要時間・結果（アクティビティカードの元）
│   │   ├── ChatSession.cs                # session_id・タイトル・メッセージ列・累計 usage/cost
│   │   ├── SessionIndex.cs               # ~/.claude/projects/<cwd変換>/ の JSONL 列挙・mtime ソート（履歴ブラウザ用）
│   │   ├── TranscriptLoader.cs           # CLI セッション JSONL → ChatMessage 列の復元パーサ
│   │   ├── PanelStateStore.cs            # ScriptableSingleton<T> [FilePath UserSettings/AgentPanel/State.asset]
│   │   ├── SessionStateBridge.cs         # SessionState キー集約（session_id, PID, mid-task フラグ, 入力ドラフト）
│   │   └── PanelSettings.cs              # 設定データ（CLI パス, モデル, permissionMode, Ctrl+Enter, allowedTools）
│   │
│   ├── UI/                               # ── EditorWindow / ビュー / USS ──
│   │   ├── AgentPanelWindow.cs           # EditorWindow 本体。Header/ViewContainer/StatusBar の 3 層 root、titleContent バッジ
│   │   ├── IAgentPanelView.cs            # ビュー登録制（CreateGUI/OnActivate/OnDeactivate/SerializeState）
│   │   ├── ChatView.cs                   # メインビュー: メッセージリスト+入力+状態遷移の配線
│   │   ├── SettingsView.cs               # 設定ビュー（CLI パス、権限ルール、ログビュー）
│   │   ├── MessageListController.cs      # ScrollView 手動 append・stick-to-bottom・古メッセージ間引き・[↓]ピル
│   │   ├── MessageBlockFactory.cs        # ChatMessage.blocks → VisualElement（ブロック型レジストリ）
│   │   ├── StreamingLabelPump.cs         # target/shown カーソルのタイプライター（EditorApplication.update、50–100ms バッチ）
│   │   ├── ToolActivityCard.cs           # 折りたたみカード（スピナー/✓/✗、対象要約、所要時間、3+ 連続のグループ化）
│   │   ├── ToolCardDescriber.cs          # ツール名+入力 → アイコン/一行要約の翻訳テーブル（describeCommand 相当）
│   │   ├── PermissionCard.cs             # can_use_tool → ハイブリッド許可カード（コンパクト行+内部スクロール展開。Inline/Window の 2 ホストで共有。Y/N キー、Always▾ スコープ）
│   │   ├── PermissionCardLayout.cs       # 純ロジック: inline/window サイズ判定（<300w or <380h）と 40% 高さキャップ計算（EditMode テスト対象）
│   │   ├── PermissionWindow.cs           # 許可カードのフローティングホスト（ShowUtility・非モーダル・単一インスタンス・リロード後は pending 無しなら自動クローズ）
│   │   ├── ComposerView.cs               # multiline TextField、Enter/Shift+Enter/Ctrl+Enter、送信⇄停止交代、キュー表示
│   │   ├── StatusBarView.cs              # 状態ドット+文言、モデル名、ctx メータ、トークン/コスト
│   │   ├── HeaderView.cs                 # セッションセレクタ、モデルピッカー（initialize 応答由来）、＋/⟳/⚙
│   │   ├── ResumeBanner.cs               # ドメインリロード後のバナー + [続きを実行] ナッジ
│   │   ├── FirstRunView.cs               # CLI 未検出 / 未ログインのセットアップカード（手動パス指定・再検出）
│   │   ├── EmptyStateView.cs             # 文脈依存の提案チップ（Console エラー時のみ「修正して」等）
│   │   ├── IconLoader.cs                 # EditorGUIUtility.IconContent 名前引き + テキストグリフへのフォールバック
│   │   ├── Markdown/
│   │   │   ├── MarkdownRenderer.cs       # 確定メッセージ → VisualElement ツリー（ブロック分解）
│   │   │   ├── InlineMarkupConverter.cs  # インライン md → Unity リッチテキスト（**エスケープの単一チョークポイント**）
│   │   │   └── CodeBlockElement.cs       # 等幅+Copy ボタン+言語バッジ
│   │   └── Uss/
│   │       ├── AgentPanel.uxml           # ルートレイアウト
│   │       ├── AgentPanel.uss            # 構造スタイル（トークン参照のみ、色ハードコード禁止）
│   │       ├── ThemeDark.uss             # --uap-* 変数の Pro 値（R05 §5.2 の表）
│   │       └── ThemeLight.uss            # 同 Light 値
│   │
│   ├── Integration/                      # ── Unity コンテキスト提供（UnityEditor 依存を隔離）──
│   │   ├── ReloadLifecycle.cs            # [InitializeOnLoad]: beforeAssemblyReload の kill+保存、after の復元+resume、EditorApplication.quitting
│   │   ├── EditorUpdatePump.cs           # EditorApplication.update 購読と LineChannel デキュー、Repaint 集約
│   │   ├── SelectionContextProvider.cs   # Hierarchy/Project 選択 → コンテキストチップ（GameObject 要約テキスト化）
│   │   ├── ConsoleErrorProvider.cs       # Console のエラー取得 → ⚠ チップ / 「修正を依頼」ワンクリックバー
│   │   ├── SceneContextProvider.cs       # アクティブシーン名・ルート構造の要約
│   │   ├── AssetLinkResolver.cs          # 本文中 Assets/Packages パス検出 → 実在確認 → PingObject/OpenAsset リンク（非実在はリンク化しない）
│   │   ├── DragDropAttachHandler.cs      # Project/Hierarchy からの D&D → チップ化
│   │   └── CompileGate.cs                # EditorApplication.isCompiling 中の送信キュー、（オプション）タイムアウト付き LockReloadAssemblies
│   │
│   └── Boot/
│       └── MenuItems.cs                  # "Window/Agent Panel" メニュー、初回起動フロー
│
├── Tests/
│   └── Editor/
│       ├── Colloid.AgentPanel.Editor.Tests.asmdef
│       ├── JsonParserTests.cs            # 実キャプチャ JSONL（R02 検証ログ）フィクスチャの回帰
│       ├── ProtocolMappingTests.cs       # init/assistant/result/control の型マッピング・未知 type 無視
│       ├── FakeCliProcess.cs             # MockBridge 戦略の Unity 版: スクリプト化した応答を吐く偽プロセス
│       ├── AgentClientStateTests.cs      # ステートマシン遷移（interrupt、プロセス死、resume）
│       ├── MarkdownConverterTests.cs     # エスケープ（<List<int>> 注入）・インライン変換
│       └── TranscriptLoaderTests.cs      # セッション JSONL → ChatMessage 復元
├── Documentation~/
│   └── index.md
└── Samples~/
```

（`~` なしフォルダの `.meta` は全コミットで GUID 安定化。R03 §6）

---

## 3. プロトコル層仕様

### 3.1 受信メッセージ型（実測スキーマ準拠。R02 §3–5）

パーサは **1 行 = 1 JSON** を前提に `LineChannel` から行を受け取り、`JsonParser` で `JsonNode` 化 → トップレベル `type` でディスパッチする。**未知の type / subtype / フィールドは黙って無視**（前方互換の第一原則）。

```csharp
// 判別キー: msg["type"]
enum InboundType { SystemInit, System, SystemTaskEvent, SystemThinkingTokens, SystemCompactBoundary, Assistant, User, Result, StreamEvent, ControlRequest, ControlResponse, Unknown }

sealed class SystemCompactBoundaryMessage {  // type=system, subtype=compact_boundary — /compact または自動圧縮の直後に 1 回
    string Trigger;                        // "manual" | "auto"
    long PreTokens;                        // 圧縮前のコンテキスト(未報告なら -1)。wire の compact_metadata と
                                           // ディスク上の compactMetadata の両方の綴りを読む(TranscriptLoader と共有)
}

sealed class SystemInitMessage {           // type=system, subtype=init — 必ず最初に 1 回
    string SessionId;                      // --resume 用に即 SessionState へ保存
    string Model;                          // 例 "claude-sonnet-5"
    string PermissionMode;                 // "default" / "plan" / ...
    string ClaudeCodeVersion;              // バージョン検証（D3）
    string[] Tools; string[] SlashCommands;
    McpServerStatus[] McpServers;          // {name, status: pending|connected|failed}
}

sealed class AssistantMessage {            // type=assistant — ターン中に複数回
    string MessageId; string Model;        // model=="<synthetic>" は CLI 内部エラー応答
    ContentBlock[] Content;                // Text / Thinking / ToolUse(id,name,input) の混在配列
    UsageInfo Usage;                       // ターン中リアルタイム集計はこれを積算
    string ParentToolUseId;                // 非 null = サブエージェント由来
    string Error;                          // 例 "authentication_failed"（実測）→ FirstRun-B へ遷移
}

sealed class UserMessage {                 // type=user
    bool IsReplay;                         // true = 送信 ack（配達確認）
    ContentBlock[] Content;                // tool_result ブロック（ツール完了 → カード確定）
}

sealed class ResultMessage {               // type=result — 必ず最後に 1 回
    string Subtype;                        // success / error_max_turns / error_during_execution / ...
    bool IsError; string ResultText;
    double TotalCostUsd; UsageInfo Usage;
    Dictionary<string, ModelUsage> ModelUsage;   // costUSD, contextWindow 等（ctx メータの分母）
    PermissionDenial[] PermissionDenials;
}

sealed class StreamEventMessage {          // type=stream_event（--include-partial-messages）
    JsonNode Event;                        // SSE そのまま。content_block_delta の text/thinking デルタのみ抽出、他は無視
}

sealed class ControlRequestMessage {       // type=control_request（CLI → パネル）
    string RequestId;
    string Subtype;                        // "can_use_tool" が本命。hook_callback / request_user_dialog は v1 無視+ログ
    CanUseToolRequest CanUseTool;          // tool_name, input, tool_use_id, permission_suggestions, ...
}

sealed class ControlResponseMessage {      // type=control_response（initialize / interrupt の応答）
    string RequestId; bool Success;
    JsonNode Response;                     // initialize → models[]/commands[]/account/pid
}
```

### 3.2 送信メッセージ（`OutboundMessages` のみが stdin へ書ける）

```csharp
// (1) ユーザーメッセージ — 実測で受理確認済みの 2 形式のうち配列形式を常用
{"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."}]}}

// (2) 制御要求
{"type":"control_request","request_id":"req_<n>","request":{"subtype":"initialize"}}
{"type":"control_request","request_id":"int_<n>","request":{"subtype":"interrupt"}}
{"type":"control_request","request_id":"pm_<n>","request":{"subtype":"set_permission_mode","mode":"acceptEdits"}}

// (3) can_use_tool への応答
{"type":"control_response","response":{"subtype":"success","request_id":"<同一id>",
  "response":{"behavior":"allow","updatedInput":{...},"updatedPermissions":[...]}}}
{"type":"control_response","response":{"subtype":"success","request_id":"<同一id>",
  "response":{"behavior":"deny","message":"...","interrupt":false}}}
```

書き込み規約（**違反 = プロセス即死**。R02 §4.2）:
1. `JsonWriter` 生成の 1 行 JSON のみ。文字列中の改行・制御文字は必ずエスケープ。
2. `Encoding.UTF8.GetBytes(json + "\n")` を `StandardInput.BaseStream` へ直接 Write + 行毎 Flush（D1）。
3. 生文字列を受け取る public API を Protocol 層に作らない。

### 3.3 パーサ戦略の決定（D8 の詳細）

- **自前ミニ JSON（JsonNode DOM）を採用**。理由: (a) `JsonUtility` は多相 content 配列・未知フィールド・辞書に非対応、(b) Newtonsoft はターゲットプロジェクトでの存在が保証されず（R04）、依存ゼロ方針と矛盾、(c) 数十 KB の 1 行（initialize 応答）に対して行長無制限のトークナイザを自分で保証したい。
- 実装規模の目安: パーサ + ライタで 500 行程度。`sdk-tools.d.ts`（CLI 同梱の型定義。R02 §1）をマッピングの参照仕様とする。
- 全マッパーは `JsonNode` から**存在すれば読む**（optional-first）。必須フィールド欠落は例外ではなくログ + メッセージ破棄（ストリームを止めない）。
- テスト: R02 の実キャプチャログ（out1.jsonl / out_bidi.jsonl）をフィクスチャ化し、EditMode テストで round-trip を回す。

### 3.4 ターン状態機械（AgentClient）

```
NotStarted → Starting(spawn+init待ち) → Ready
Ready --user送信--> Streaming --tool_use--> ToolRunning --can_use_tool--> WaitingPermission
Streaming/ToolRunning/WaitingPermission --result--> Ready
任意 --プロセスexit--> Errored --resume再起動--> Starting
```

- ターン id はパネル側で採番し、interrupt 直後の残留イベントを弾く（R01 §16-4 の教訓「turn フレームにターン id が無く 300ms ヒューリスティックで凌いだ」を最初から回避）。
- silence backstop: result 未着のまま最終受信から 10 分経過で強制 Ready 復帰 + エラーノート（R01 §10）。

---

## 4. 実装フェーズ計画

検証環境: **`C:/Unity/UnityProjects/DevelopmentProject`**（起動中エディタ。R04）。デプロイは `Packages/jp.colloid.unity-agent-panel/` フォルダドロップ、コンパイルは `uloop compile`、エラー取得は `uloop get-logs --log-type Error`、EditMode テストは `uloop run-tests --test-mode EditMode`（R04 §6）。**前提: ユーザーが `claude /login` を一度実行しておくこと**（R02 §0 のブロッカー）。

### Phase 1 — MVP（スポーン / チャット / ストリーミング / 停止 / セッション）

**ファイル**: Core 全部（Json/, Protocol/, Process/, Client/）、Model の ChatMessage / ChatSession / PanelStateStore / SessionStateBridge / PanelSettings、UI の AgentPanelWindow / ChatView / MessageListController / MessageBlockFactory / StreamingLabelPump / ComposerView / StatusBarView / HeaderView(簡易) / FirstRunView / ResumeBanner / Uss 一式（Markdown はプレーン表示 + コードフェンスのみ）、Integration の ReloadLifecycle / EditorUpdatePump、Boot/MenuItems、Tests の JsonParserTests / ProtocolMappingTests / FakeCliProcess / AgentClientStateTests。

**受け入れ基準**（DevelopmentProject 上で検証可能）:
1. `Window > Agent Panel` でパネルが開き、CLI 未検出時は FirstRun カード、検出+ログイン済みなら接続して initialize 応答由来のモデル名がステータスバーに出る（「接続済み」表示は init 受信後のみ）。
2. 日本語メッセージ（例:「このプロジェクトの Packages 構成を説明して」）を Enter 送信 → ストリーミングでアシスタント本文が流れ、result 到達でターン終了。**日本語 Windows で文字化け・プロセス死なし**。
3. ストリーミング中に ■ 停止（または Esc）→ interrupt が効き、途中まで表示が確定、システムノートが出る。
4. パネル内で .cs 編集を依頼するなどしてドメインリロードを発生させる（または `uloop compile` を叩く）→ リロード後にトランスクリプトが即時再描画され、`--resume` で同一 session_id に再接続、バナーが「✓ 再開しました」になる。孤児 claude.exe がタスクマネージャに残らない。
5. Unity エディタを終了 → claude.exe が残存しない。再起動 → 直近セッションの履歴キャッシュが表示される。
6. `uloop compile` が Success / `uloop run-tests --test-mode EditMode` で Core テストが全緑。uLoopMCP と同居してポート/InitializeOnLoad 競合が起きない（パネルはポートを使わないので原理的に安全だが、起動時間への影響を目視確認）。

### Phase 2 — パーミッション UI / ツールカード / Unity コンテキスト

**ファイル**: UI の PermissionCard / ToolActivityCard / ToolCardDescriber / Markdown 3 ファイル / EmptyStateView / IconLoader、Integration の SelectionContextProvider / ConsoleErrorProvider / SceneContextProvider / AssetLinkResolver / DragDropAttachHandler / CompileGate、Model の ToolCallRecord、Tests の MarkdownConverterTests。

**受け入れ基準**:
1. 「Packages/jp.colloid.unity-agent-panel/README.md に 1 行追記して」→ **can_use_tool の許可カード**が入力欄直上に出て、diff プレビュー表示、[許可(Y)]で実行、[拒否(N)]+代替指示入力が 1 アクションで返る。「常に許可 ▾（このファイル/パターン/ツール）」で以後同種の確認が出ない。
2. Read/Edit/Bash がアクティビティカード（スピナー→✓/✗、対象要約、所要時間、完了時自動折りたたみ）で表示され、3 個以上連続はグループ行に圧縮。
3. 確定メッセージが Markdown レンダリング（見出し/リスト/インラインコード/コードブロック+Copy）され、モデル出力に `List<int>` を含めても描画が壊れない（タグインジェクション回帰テスト）。
4. Hierarchy で HAOLAN アバターの GameObject を選択 → チップ「⬚ <名前>」が付き、「選択中のオブジェクトを説明して」で構成が答えに反映される。Console にエラーがある時のみ「⚠ Claude に修正を依頼」バーが出る。
5. 応答中の `Assets/HAOLAN/...` パスがリンク化され、単クリックで PingObject、非実在パスはリンク化されない。
6. パネル非フォーカスで許可待ちになると EditorWindow タブに黄ドット、完了でオレンジドット。

### Phase 3 — 磨き込み（履歴ブラウザ / 設定 / コスト表示）

**ファイル**: Model の SessionIndex / TranscriptLoader、UI の SettingsView / IAgentPanelView 本格化（History ビュー追加）、StatusBarView 拡張（ctx メータ・usage ポップアップ）、Tests の TranscriptLoaderTests。

**受け入れ基準**:
1. History ビューで `~/.claude/projects/C--Unity-UnityProjects-DevelopmentProject/` の過去セッションが mtime 順に列挙され、選択でトランスクリプト復元 + `--resume` で会話継続できる。
2. Settings ビューで CLI パス手動指定・permissionMode・Ctrl+Enter 切替・allowedTools が編集でき、stderr ログビューが見られる。
3. ステータスバーに ctx 使用率メータ（modelUsage.contextWindow 由来）と累計トークンが出る。**サブスク認証で cost が 0/無意味な場合はトークンのみ表示**にフォールバック（R05 リスクへの対応）。
4. ヘッダーのモデルピッカーが initialize 応答の models[]（表示名・価格説明つき）を反映し、`set_model` で切替できる。
5. 300 メッセージ超の会話でスクロールが破綻しない（古メッセージ間引きの動作確認）。

### Phase 4 — サブエージェント表示（エージェント可視化）

**根拠**: R02c（実測キャプチャ 2026-07-31）。スポーンツールの実名は `Agent`（init の tools[] は `Task` 表記）、
サブエージェント本文は `parent_tool_use_id` 付き完成形メッセージで届きストリーミングされない、
進捗は `system/task_started|task_progress|task_updated|task_notification`、
復元は `<session-uuid>/subagents/agent-<taskId>.jsonl` + `.meta.json`（toolUseId で結線）。

**ファイル**: Model の ChatMessage 拡張（SubagentGroup）、UI の SubagentCard / MessageListController 振り分け、
Core/Protocol の task_* サブタイプ写像、TranscriptLoader の subagents ディレクトリ対応、
Tests の SubagentGroupingTests / task_subagent フィクスチャ再生。

**受け入れ基準**:
1. パネルから「Task ツールでサブエージェントを使って〜して」と依頼 → Agent tool_use の位置に
   **サブエージェントカード**が出る（種別バッジ + description + スピナー）。サブエージェントの発話が
   トップレベルのチャット欄に紛れ込まない。
2. 実行中は task_progress 由来の**現在の活動 1 行**（description / last_tool_name）と
   トークン・経過時間カウンタがカード上でライブ更新される。
3. 完了で ✓ + 最終サマリ（task_notification.summary の Markdown）に確定し、
   カードを展開するとサブエージェント内部のツール呼び出し・最終本文が入れ子表示される
   （既定は折りたたみ）。失敗/停止時は ✗ とステータス。
4. ドメインリロード（kill+resume）を跨いでもカードの状態と入れ子内容が SessionCacheFile から復元される。
5. History から過去のサブエージェント入りセッションを復元 → カードが復元され、
   展開時に `subagents/agent-*.jsonl` から内部トランスクリプトが遅延ロードされる
   （ディレクトリ欠損時はサマリのみ表示に劣化し、エラーを出さない）。
6. 未知の task_* サブタイプ・親不明の `parent_tool_use_id` が来ても例外なく従来どおり
   トップレベル表示にフォールバック（前方互換）。既存 380 テスト全緑 + 新規回帰テスト。

### Phase 5 — Unity操作特化(コンパイルゼロの型付き操作)

**設計書**: `docs/design-notes/2026-08-01-phase5-unity-ops-design.md`(根拠: R08〜R11 実測)。

3層アーキテクチャ: **L1 = パネル内蔵MCPサーバ「UapOps」**(Streamable HTTP + Bearer、
`mcp_reconnect` 制御リクエストでリロード復旧、型付きツール群を SerializedProperty/ObjectFactory/
PrefabUtility/AnimatorController 等のエディタAPIで実行 — コンパイルゼロ・ターン単位Undo・
許可カード統合)/ **L2 = uloop CLI 連携**(検出+allowedToolsプリセット+指示スニペット。
MCP接続不要 — uLoopは現在CLIバイナリ+Named Pipe、MIT)/ **L3 = 実コード生成の
コンパイルバッチング**(DisallowAutoRefreshによるターン内抑制、チルダ末尾ステージング、
ガードレール付き自動継続・既定OFF)。

サードパーティ拡張(VRCSDK/VRM/MagicaCloth/FinalIK等)は SerializedProperty 経路が型非依存で
既に効くことを前提に、`query_component_types`(型発見)+**拡張プロファイル**(SDK別の作法を
データとして同梱・自動検出でカスタム指示に注入・ユーザー定義可)で対応(設計書§3b)。
uLoop未導入時は Git URL `Client.Add` 第一候補+確認カード付き manifest 編集フォールバックの
ワンクリック導入(設計書§2.5)。

サブフェーズ: 5a=サーバ基盤+scene/component/asset/queryツール+型発見+ターンUndo(v0.9.0)、
5b=anim/animator/rig/material/prefab/screenshot+**拡張プロファイル基盤**(v0.10.0)、
5c=uloop連携(ワンクリック導入込み)+L3(v0.11.0)。受け入れ基準は設計書§5。

---

## 5. リスクと対策

| # | リスク | 深刻度 | 対策 | 根拠 |
|---|---|---|---|---|
| 1 | **CLI 未ログイン**（現マシンは loggedIn:false）。全モデル呼び出しが失敗する | 高（ブロッカー） | FirstRun-B 画面で `claude /login` を案内。起動前に `claude auth status` の JSON を確認。`assistant.error=="authentication_failed"` / `<synthetic>` モデル検出でも同画面へ。サブスクリプションログインを推奨として案内するが、`ANTHROPIC_API_KEY` が設定されていればそれも CLI 自身の判断でそのまま使う(v0.40.0 以降。設定 > アカウントに表示) | R02 §0 |
| 2 | 不正 stdin 行 1 つで CLI が exit 1 即死 | 高 | JsonWriter 経由以外の書き込み経路を作らない。UTF-8 BaseStream 直書き。死亡検出 → `--resume` 自動再起動 + エラーバナー | R02 §4.2 |
| 3 | ドメインリロードで子プロセス孤児化 → パイプ詰まりハング | 高 | beforeAssemblyReload で interrupt→ツリーキル、SessionState 保存、after で `--resume`。PID+開始時刻による次回起動時の掃除 | R03 §2 |
| 4 | エージェントの .cs 編集 → 再コンパイル → 自パネル消滅の自己破壊ループ | 高 | D4 の kill+resume を高速化し Resuming を通常 UI 化。mid-turn はワンクリック [続きを実行] ナッジ（自動継続しない）。タイムアウト付き LockReloadAssemblies はオプション | R03 §2.2, R05 §3.9, R01 §7.2 |
| 5 | 日本語 Windows の stdin が CP932 になり CJK 破壊（→リスク2 に連鎖） | 高 | `StandardInputEncoding` 非存在を前提に BaseStream UTF-8 直書きで統一。日本語実機テストを Phase 1 受け入れ基準に含める | R02 §8.3, R03 §1.4 |
| 6 | 隠しフラグ（--permission-prompt-tool stdio, --max-turns）の将来変更 | 中 | 起動時に `claude_code_version` を検証、想定外は警告 + 静的 allowedTools モードへ縮退。プロトコル層は未知 type 無視で前方互換 | R02 リスク |
| 7 | 1 行 JSON が数十 KB（initialize 応答）で行バッファ溢れ | 中 | 自前トークナイザは行長無制限。OutputDataReceived の行単位読みで実測上問題ないことをテスト済みログで確認 | R02 リスク |
| 8 | リッチテキストタグインジェクション（`List<int>` 等） | 中 | InlineMarkupConverter を唯一のエスケープチョークポイントにし回帰テスト常備 | R03 §4.1 |
| 9 | ListView DynamicHeight のバグ / 数百メッセージでの UI 劣化 | 中 | ScrollView + 手動間引き（300 件閾値）。毎デルタ再パースせず 50–100ms バッチ + 確定時のみフル Markdown | R03 §3.2/3.5, R05 §3.5 |
| 10 | GPL（UnityClaudeCLI）コード混入によるライセンス汚染 | 中 | クリーンルーム徹底: ソースは読まない・写さない。参照は機能スコープのみ。本文書と R03/R05 を設計ソースとする | R03 §4.2/5.3 |
| 11 | IME 変換確定 Enter と送信 Enter の衝突 | 中 | 日本語 IME 実機テスト（Phase 1）。Ctrl+Enter 送信オプションを設定に常備 | R03 §3.4, R05 §2.5 |
| 12 | ターゲットプロジェクトが VPM 管理 + Git なし | 中 | manifest.json 非編集（embedded フォルダのみ）。フットプリントを単一フォルダに限定し、Library/Temp へ書かない | R04 §2/リスク |
| 13 | uLoopMCP（beta）との同居競合 | 低 | Phase 5 以降、パネルは UapOps の loopback HTTP サーバ（動的ポート、127.0.0.1 限定）のみを保有する（design-notes/2026-08-01-phase5-unity-ops-design.md §8.3 で従来の「サーバを持たない」記述を更新）。uLoopMCP は Named Pipe 方式で TCP ポートを使わずトランスポート独立のため直接の資源競合なし。InitializeOnLoad は軽量（イベント購読のみ）。beta 更新時の stale PackageCache エラーは `uloop compile` で解消実績あり | R04 §4/7, Phase5 §8 |
| 14 | エディタクラッシュ時のゾンビ claude.exe | 低 | ZombieReaper（PID+開始時刻照合）。将来 Win32 Job Object 化のシームを IProcessKiller に確保 | R03 §2.3 |
| 15 | ビルトインアイコン名のバージョン差異 | 低 | IconLoader で名前引き失敗時テキストグリフへフォールバック | R05 §5.5 |
| 16 | サブスク認証でコスト表示が無意味 | 低 | result.total_cost_usd が 0/欠落ならトークンのみ表示に自動フォールバック | R05 リスク, R02 §9 |

---

## 付録: 本文書が意図的に採用しなかった選択肢

| 選択肢 | 不採用理由 |
|---|---|
| WS ブリッジ + 別プロセスオーケストレータ（R01 の構成） | あの分離は ComfyUI Registry の「スポーン禁止」制約由来。Unity は Process を直接持てるため薄い C# クラスに畳む（R01 §15）。将来必要なら `IAgentTransport` で差し替え |
| `-p` 単発実行のターン毎スポーン | コールドスタート毎回発生・partial 表示や can_use_tool 対話が成立しない。双方向常駐が実測で安定（R02 §4） |
| Newtonsoft.Json 依存 | ターゲット環境での存在保証なし + 依存ゼロ配布方針と矛盾（R04, D8） |
| ListView 仮想化 | 2022.3 の DynamicHeight は既知バグ + ストリーミングが最悪ケース（R03 §3.2） |
| `--dangerously-skip-permissions` 既定運用 | 先行実装（GPL）の安全性の穴と同じ轍。can_use_tool ハイブリッドが正攻法（R02 §6, R03 §5.3） |
| IndexedDB 相当の自前フル履歴ストア | 正史は CLI のセッション JSONL。パネルは表示キャッシュ + JSONL 読み出しで足りる（R02 §7.1, R03 §2.4） |
