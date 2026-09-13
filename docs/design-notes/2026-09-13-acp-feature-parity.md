# 2026-09-13 -- Claude Code 専用だった 4 機能を ACP エージェントにも広げる

発端: 「履歴ブラウザ・パネル内ログイン・サブエージェントのモデル設定・思考ブロックは
Claude Code 専用です。この部分を他のエージェントにも拡張して」。USER-GUIDE 1.1 節に
そう書いてあった 4 項目を、ACP バックエンド(Gemini CLI / Codex / Grok Build /
カスタム ACP)でも使えるようにする。

参照: `2026-09-10-acp-backends.md`(ブリッジと劣化表。§6 で履歴とサブエージェントを
「見送り」にした)、`2026-09-10-in-panel-install-and-sign-in.md`(ACP のサインイン、
`authenticate` の順位付け)、`2026-09-10-backend-switch-session.md`(セッションの
所有者記録と会話の引き継ぎ)、`2026-08-02-auth-in-panel.md`(Claude の `auth login`
セッション)。

---

## 0. 結論(先に)

| 項目 | 実態(調査結果) | やったこと |
|---|---|---|
| 履歴ブラウザ | `SessionIndex` が `~/.claude/projects/*.jsonl` しか見ない。ACP セッションはどこにも残らず、一覧には過去の Claude セッションだけが出ていた | パネル自身が ACP セッションを `UserSettings/AgentPanel/Sessions/<id>.json` に保存し(`PanelSessionStore`)、同じ一覧にエージェント名付きで並べる。再開は `session/load`、できなければ新セッション + 会話ログの引き継ぎ(§1) |
| パネル内ログイン | Claude の `claude auth login` 専用。ACP は「再接続」でブリッジの `authenticate` をやり直すだけで、URL は stderr から拾えた場合のみ | `AuthLoginSession` を任意コマンドに一般化し、`codex login` / `grok login` をパネル内で実行。リンク・最終出力行・キャンセルをアカウントカードに、失敗時は会話欄に「{agent} にサインイン」カード(§2) |
| サブエージェントのモデル設定 | `CLAUDE_CODE_SUBAGENT_MODEL` と `.claude/agents/*.md` は Claude 固有。ACP では黙って捨てていた(設定 UI は出たまま) | ACP には指示文として渡す(新セッションの最初のプロンプトに前置する標準ブロック)。コスト方針の文も Claude の道具名を含まない言い回しに差し替え。設定欄にその旨のヒント(§3) |
| 思考ブロック | ブリッジは `agent_thought_chunk` を既に thinking に変換しており、実は動いていた。`!isAcp` のゲートは無意味、文言だけが Claude 専用と言っていた | 思考→本文→思考の順序が崩れるバグを直し、無意味なゲートを外し、ツールチップと USER-GUIDE を訂正(§4) |

## 1. 履歴ブラウザ

### 1.1 なぜパネル側に保存するか

ACP v1 に安定した `session/list` は無く、あっても題名程度で本文は返らない。各 CLI の
履歴形式(Gemini の `~/.gemini/tmp`、Codex の `~/.codex/sessions`)を個別に読むのは
ACP を選んだ理由(N 社 × N 実装を避ける)に反する。一方、パネルは表示用の
`ChatSession`(全メッセージ・ツールカード・使用量)を `SessionCacheFile` の形で既に
持っている。それを**セッションごとに 1 ファイル**として残せば、一覧も復元も
エージェントに頼らずに済む。

- `Editor/Model/PanelSessionStore.cs`(Unity 非依存)。場所は
  `UserSettings/AgentPanel/Sessions/<sessionId>.json`、書式は `SessionCacheFile` と
  同一(`agentBackend` を含む)。id は `[A-Za-z0-9._-]` 以外を `_` に置換して
  ファイル名にする。
- 書くのは **ACP セッションだけ**(`AgentHub.PersistPanelSession`。`SaveSessionCache`
  の唯一の書き込み経路から呼ぶ)。Claude のセッションは CLI 自身の jsonl があるので
  書かない -- 二重に並ぶのを避ける。
- 一覧の読み取り `ReadSummary` は壊れたファイルを**削除しない**(`SessionCacheFile.Load`
  は破損ファイルを消す契約だが、一覧のスキャンで消すのは越権)。

### 1.2 一覧への統合

`SessionIndex.Refresh(cwd)` が jsonl に加えてパネルストアも列挙し、
`SessionIndexEntry.IsPanelStore` / `AgentBackend` で区別する(jsonl は
`AgentBackend = 0`)。`HistoryRow.AgentBackend` を足し、行のメタ行(時刻・サイズ・
モデル)の末尾に **エージェント名**(`AgentBackends.DisplayName`)を付ける。Claude の
行は従来どおり無印。検索・グループ化・ピン・アーカイブ・名前変更・削除
(`MoveTranscriptToDeleted` はパスを問わない)は共通。プロジェクト横断の一覧
(`ProjectSessionScanner`)は `~/.claude` 専用のまま -- 他プロジェクトの行は元々
再開不可なので、ここは広げない。

### 1.3 再開

- 行をクリック → `IsPanelStore` なら `SessionCacheFile.Load` で `ChatSession` を
  そのまま復元し `AgentHub.SwitchToStoredSession`(所有バックエンド・使用量込み)。
  jsonl 行は従来の `SwitchToSession` だが、**`agentBackend = ClaudeCode` を記録する**
  ようにした。これまでは -1 のままだったため、ACP を選んでいる状態で Claude の行を
  開くと Claude の id を ACP エージェントに渡し、黙って新セッションになっていた。
  記録したことで `StartClient` の既存の所有者チェック(backend-switch-session ノート)
  が働き、「ここまでの会話は Claude Code とのものです。Gemini CLI は新しいセッションを
  開始し…」のノートと会話の引き継ぎが同じ経路で起きる。
- 同じ ACP エージェントの行: `AcpLaunchSpec.ResumeSessionId` → `session/load`。
  エージェントが `loadSession` を持たない/失敗した場合、ブリッジは `session/new` に
  倒すが、これまでパネルはそれを**知らなかった**(画面には会話が残るのにエージェントは
  白紙)。`StartClient` が要求した id を `_resumeRequestedSessionId` に覚え、
  `OnSessionIdChanged` で報告 id が違えば「再開できなかった」と判定する
  (`OnAcpSessionIdChanged`):
  1. ストアのファイルを新 id に **rename**(`PanelSessionStore.Rename`)し、ピン等の
     メタも `SessionMetaStoreAccess.CarryOver` で移す。これが無いと、`session/load`
     非対応のエージェントはドメインリロードのたびに同じ会話が別 id で増殖する。
  2. 会話に本文があれば「{agent} はこのセッションを再開できなかったため…上の会話ログは
     次のメッセージと一緒に送られます」のノートを出し、`HandoverPendingFrom` に
     **同じ**バックエンドを入れる。`ConversationHandover.Build` は前後のエージェント名が
     同じとき「あなた({agent})と以前のセッションで話していたが再開できなかった」
     という書き出しに変わる(「別のエージェントに切り替えた」と嘘を言わない)。
- 所有バックエンドが違う行(現在 Codex、行は Gemini)は既存の所有者チェックで
  新セッション + 引き継ぎ。`_resumeRequestedSessionId` は null なので 1.3 の処理と
  二重にはならない。

## 2. パネル内ログイン

### 2.1 何を「ログイン」と呼ぶか

ACP の `authenticate` はブリッジが既に順位付きで試している(in-panel-install ノート
2.4)。足りなかったのは、**未認証のときに何をすればよいかをパネル内で完結させる手段**。
Claude では `claude auth login` を子プロセスとして起動し URL を拾っている
(`AuthLoginSession`)ので、同じ器で各 CLI のログインコマンドを走らせる。

| バックエンド | `LoginExecutable` / `LoginArguments` | 備考 |
|---|---|---|
| Codex | `codex` / `login` | ACP コマンド `codex-acp` とは別の実行ファイル。PATH とベンダー既定ディレクトリを `AcpCommandProbe` で探す |
| Grok Build | `grok` / `login` | ACP コマンドと同じ実行ファイルなので、設定のコマンド上書きとプローブをそのまま使う(`ResolveAcpLoginPath`) |
| Gemini CLI | なし | ログインは TUI 内のダイアログで、非対話のサブコマンドが無い。個人向けは API キーのみ(auth-guidance ノート) |
| カスタム ACP | なし | コマンドを知りようがない |

### 2.2 実装

- `AuthLoginSession.Begin(cliPath, killer, logger, arguments, captureStderr)`。
  引数を可変にし、stderr も同じバッファに合流させる(`codex login` は案内を stderr に
  出す実装があり得る)。最後の非空行を `LatestOutputLine` に保持(終了コードは Claude
  同様に信用しない、という既存契約の代わりに「CLI が最後に言ったこと」を見せる)。
  Claude の「Paste code here」検出はそのまま残るが他 CLI では発火しない。
- `AgentHub.BeginAcpLogin`: 実行ファイル解決 → 起動 → `UrlAvailable` で
  `AcpSignInUrl` にリンク(会話欄にも 1 行)→ `Exited` で破棄し `RecoverConnection`
  (ブリッジが新しい資格情報で `session/new` を通す)。`BeginLogin` は ACP なら
  ここに委譲するので、初回カードの「ログイン」ボタンは共通。見つからない場合は
  `AcpLoginError`(**`AcpSignInError` とは別**: あれはブリッジの判定で、プロセス死亡時の
  再接続可否にも使われるため、コマンド不在で汚してはいけない)。
- アカウントカード(ACP 分岐): 「サインイン」(ログインコマンドがある場合のみ、主ボタン)
  / 「再接続」(旧「サインイン / 再接続」を改名)/ 実行中は「キャンセル」+ リンクの
  「ブラウザで開く」「コピー」+ 最終出力行(等幅)。ヒントは「サインインは `{cmd}` を
  パネル内で実行…」に切り替わる。
- 初回カード: `ChatView.ResolveFirstRunMode` に `acpSignInRequired`
  (`AgentHub.AcpSignInRequired` = ブリッジのサインイン失敗 かつ ログインコマンドあり)
  を足し、ACP でも `NotLoggedIn` を返せるようにした。カードの文言は
  `FirstRunView.ApplyLoginCardBackend` がバックエンドごとに差し替える(表題
  「{agent} にサインイン」、本文にコマンド、折りたたみにターミナル用のコマンドと
  「もう一度確認」)。Claude の `auth status` キャッシュと会話欄の認証エラー推定は
  引き続き Claude 専用。

## 3. サブエージェントのモデル設定

ACP にはサブエージェントのモデルを指定するワイヤが無い(Claude の
`CLAUDE_CODE_SUBAGENT_MODEL` / `.claude/agents/*.md` は CLI 固有)。カスタム指示と
同じく、**新セッションの最初のプロンプトに前置される標準ブロック**(`AcpLaunchSpec.
SystemPrompt`)に指示文として載せる。

- `AgentHub.ComposeAcpSystemPrompt(claudeWorded, subagentModel, overrides)`:
  1. コスト方針の文(`HaikuForSimpleTasksInstructionLine`。「Agent (Task) tool」「haiku」
     を名指し)を `AcpCheapModelForSimpleTasksInstructionLine`(「委譲するとき、モデルを
     選べるなら最も安い/速いものを選べ」)に置換。
  2. `ComposeSubagentModelSteering`: 強制固定があればそれ 1 文(タイプ別より優先。Claude
     の env が agents ファイルに勝つのと同じ順)。無ければ名前とモデルが揃った行だけを
     1 行ずつ。どちらも無ければ空。
- 設定 > モデル: ACP 選択中だけ「{agent} では、新しいセッションの最初に指示文として
  送られます」のヒントを「サブエージェントモデルを強制固定」の下に出す
  (`RefreshSubagentModelAcpHint`、バックエンド切替時に更新)。
- エージェントが本当に従うかはエージェント次第(Codex の multi-agent、Gemini の
  subagents はそれぞれ実装途上)。設定 UI はそのことをヒントで言う。
- サブエージェント**カード**(Task ツールの入れ子表示)は今回も見送り。ACP には
  子エージェント起動を示す更新が無く、`tool_call` の `kind: think` 程度しか手掛かりが
  無いのは前ノートのとおり。

## 4. 思考ブロック

- 調査結果: `AcpProtocolBridge.OnAgentThoughtChunk` は `content_block_start(thinking)` +
  `thinking_delta` を出し、ターン末やツール呼び出しで thinking ブロックに畳む。
  `MessageBlockFactory` は `showThinking` だけで判定する。つまり ACP でも思考は
  **既に表示されていた**。`AgentHub` の `ThinkingDisplaySummarized = !isAcp && ...` は
  ブリッジがフラグ文字列を無視するため無意味だった。
- 直した不具合: 本文の後に思考が来る(段落の間で考えるエージェント)と
  `_thinkingStarted` が立ったまま同じバッファに追記され、畳んだとき
  [thinking(全部), text(全部)] と順序が入れ替わっていた。思考チャンクが来た時点で
  本文バッファが空でなければ先に `FlushAssistantText()` で 1 メッセージに畳み、
  新しい thinking ブロックを開く(`ThoughtAfterText_FoldsTheTextFirst_...`)。
- 文言: ツールチップ「実際の思考本文を表示するには CLI v2.1.218 以降」に「ACP
  エージェントの思考はそのまま逐次表示」を追記。設定 > CLI の制限事項ヒントと
  USER-GUIDE 1.1 の「Claude Code 専用」の一文を書き換え。

## 5. 検証

- `Tests/Editor/PanelSessionStoreTests.cs`: 保存/列挙/読込/rename/破損ファイル非削除、
  `SessionIndex` が両ソースを並べること(パネル行の cwd はプロジェクト)、行の
  エージェント名、同一エージェントの引き継ぎ文言、`HasHandoverContent`。
- `AgentBackendsTests`: `LoginExecutable` / `LoginArguments` / `HasInPanelLogin`、
  初回カード判定(`acpSignInRequired`)、`ComposeSubagentModelSteering`、
  `ComposeAcpSystemPrompt`。
- `AuthCliTests`: 任意引数の `Begin`、codex 風の複数行出力から URL を拾い
  「コード貼り付け」は発火しないこと、`LastNonEmptyLine`。
- `AcpProtocolBridgeTests`: 本文→思考→本文の順序。
- Unity 非依存部分(`PanelSessionStore` / `SessionIndex` / `ConversationHandover` /
  ブリッジ / `AgentBackend`)は dotnet 8 で verbatim コンパイルし、ストアの往復と
  `SessionIndex` の列挙を実行して確認した。**Unity 側(AgentHub / SettingsView /
  HistoryView / FirstRunView)はこの環境ではコンパイルできず、EditMode CI 任せ。**
  Codex / Grok Build との実接続(`codex login` の実際の出力、`session/load` の有無)も
  未検証で、最初の実機接続で文言と抽出を見直す前提。

## 6. 残件

- ACP の `session/list`(unstable)が安定したら、パネルストアに無い過去セッション
  (ターミナルで作ったもの)も列挙する余地がある。
- Gemini CLI のパネル内ログイン(TUI 依存のため見送り)。
- サブエージェントカードの ACP 対応(§3 末尾)。
