# MCP サーバーの追加・ツール説明・認証をパネルから

日付: 2026-09-27 / 対象: `jp.colloid.unity-agent-panel`(設定画面、`/mcp` カード、spawn 引数)
関連: `2026-09-27-mcp-slash-command.md`(`/mcp` カード)、`docs/research/08-mcp-transport.md`
(`--mcp-config` / `--strict-mcp-config` の実測、`~/.claude.json` の保存形式)

## 1. 前提と範囲

CLI の `/mcp` 画面でできることのうち、パネルの `/mcp` カード(前ノート)に無かったのは
「サーバーの追加・削除」「ツールの説明文」「OAuth 認証」。決め手になる前提が 1 つ:
**パネルは CLI を `--strict-mcp-config` で起動し、自前の設定(UapOps だけ)を渡している**
(08 研究ノート 2 節の判断。ユーザーのグローバル MCP 設定が意図せず紛れ込むのを防ぐ)。
つまりユーザーが `claude mcp add` や `.mcp.json` で登録したサーバーは、パネルのセッション
には最初から載っていない。この前提を保ったまま、次の 3 つを入れる。

| 項目 | 方式 | 範囲 |
|---|---|---|
| 追加・削除・設定 | パネル自身の一覧(`PanelSettings.mcpServers`)を `--mcp-config` に同梱 | 全部 |
| ツールの説明文 | UapOps はレジストリから。他サーバーは名前のみ | 部分 |
| OAuth 認証 | パネル内では行わず、同じ設定で対話 CLI をターミナルに開く | 導線のみ |

ACP バックエンドは対象外(`session/new` に渡す `mcpServers` は UapOps のまま)。

## 2. サーバー一覧と spawn

- `McpServerConfig`(Model): `name` / `transport`(stdio | http | sse)/ `enabled` /
  `command` + `args[]` + `env[]`(stdio)/ `url` + `headers[]`(http, sse)。`args` `env`
  `headers` は JsonUtility が持てるよう「1 行 1 項目」の文字列リスト(`KEY=VALUE`、
  `Name: value`)。`ToJson()` が CLI の `mcpServers[name]` オブジェクトにする(08 研究
  ノート 2 節で実測した形)。`IsComplete`(名前 + コマンドか URL)を満たさない項目は
  spawn を失敗させずに黙って外す。
- `UapOpsMcpConfig.BuildConfigJson(includeUapOps, port, token, extraServers)`: UapOps の
  項目の後に、有効で完全な項目を一覧順に足す。`unity-ops` と同名の項目と、重複した名前の
  2 つ目以降は捨てる(パネル自身のサーバーがその名前で届く必要がある)。既存の 2 引数版は
  この委譲で不変(既存テストが契約を固定)。`EnsureConfigFileWritten` も同じ形で拡張。
- `AgentHub.ComputeMcpConfigValue(..., extraServers, log)`: UapOps が OFF でも使える
  ユーザーサーバーがあれば設定ファイルを書いて `--mcp-config` を渡す(`HasUsableServer`)。
  どちらも無いときだけ `null`(従来どおり `--mcp-config` 自体を付けない)。書いたパスは
  `_lastMcpConfigPath` に覚えておく(4 節)。
- 次回 spawn 限定の項目なので、`SettingsChangeDetector.RequiresReconnect` が
  `McpServerConfig.ListsEqual` で比較し、`CloneNextSpawnOnlyFields` が深い複製を取る。
  これで設定画面の編集は既存の「自動で再接続」の経路(`CommitSettingsChange` →
  `RequestAutoApplyReconnect`)に乗り、会話は `--resume` で引き継がれる。
  `CloneNextSpawnOnlyFieldsTests` の反射スイープにこの型の mutator を足した。

## 3. 設定画面「MCP サーバー」(Unity タブ、UapOps の次)

- 行ごとに: 有効スイッチ、名前、transport のポップアップ、「削除」。その下に transport
  に応じた欄(stdio: コマンド / 引数 / 環境変数、http・sse: URL / ヘッダー。複数行欄は
  1 行 1 項目)。transport を変えると行を組み直す。名前とコマンド / URL が揃うまで
  「使うには…が必要です」の注記を出す。
- 「+ サーバーを追加」「Claude Code から取り込む」「/mcp 用にターミナルを開く」。
- 取り込み(`McpServerImport.ReadFromDisk`): プロジェクトの `.mcp.json` と、
  `~/.claude.json` の最上位 `mcpServers` および `projects[<root>].mcpServers`
  (`claude mcp add` の保存先。08 研究ノート 2 節)を読み、まだ無い名前だけ足す
  (`MergeNew`)。読むだけで、CLI の設定ファイルには書かない。取り込み後は独立した
  コピーなので、どちらかを直しても相手には反映されない(tooltip でその旨を言う)。
- `/mcp` カードの見出し行に「サーバーを追加...」ボタン
  (`AgentPanelWindow.ShowSettings(Unity, McpServersCardId)`)。

## 4. 認証: ターミナルを開く

OAuth の流れとトークンの保存先は CLI の内部実装で、stream-json モードから起動する
control request は公開されていない。パネルが自前で OAuth を回しても、CLI が読む場所と
形式で保存する必要があり、CLI の更新で壊れる作りになる。よって:

- `TerminalLauncher.Build(platform, cliPath, mcpConfigPath)`(純粋、テスト済み)が OS
  ごとのコマンドを作る。Windows は `cmd /c start "Claude Code" cmd /k <cli> ...`、macOS は
  `osascript` で Terminal.app に `do script` + `activate`、Linux は `x-terminal-emulator -e
  bash -c "...; exec bash"`(CLI 終了後も窓を残す)。
- 起動する CLI は **パネルと同じ設定ファイルで `--strict-mcp-config`**。ターミナル側の
  `/mcp` に見えるサーバーがパネルと一致し、そこで認証したトークンは CLI 側に保存されて
  次の接続からパネルの CLI も使う。`AgentHub.OpenTerminalForMcp` は起動前に設定ファイルを
  書き直す(次の spawn を待たずに現在の一覧を見せる)。
- 導線は `/mcp` カードの `needs-auth` 行の「ターミナルを開く」と、設定画面のボタン。
  開いたら「そこで /mcp から認証し、こちらで再接続」の note。

## 5. ツールの説明文

stream-json にはツール名しか流れない(08 研究ノート 3 節、実測)。UapOps はパネル自身の
サーバーなので `UapOpsServer.FindByWireName` の `Description` を、`/mcp` カードのツール
一覧で名前の下に薄く出す。他のサーバーは名前のみ。http サーバーへ `tools/list` を自前で
問い合わせる案は別タスク(stdio の二重起動、認証付きサーバーのトークンが取れない問題を
先に決める必要がある)。

## 6. やらないこと

- CLI の設定ファイル(`~/.claude.json`、`.mcp.json`)への書き込み。
- `mcp_set_servers` 等の未計測な control request での動的追加(再接続で十分)。
- ACP バックエンドへのユーザーサーバーの受け渡し。

## 7. テスト

`McpServerConfigTests`(ToJson の形、`IsComplete`、`ReadAll` の最上位 + プロジェクト
スコープ、`MergeNew`、`BuildConfigJson` の合成と除外、旧オーバーロード不変)、
`TerminalLauncherTests`(3 OS のコマンド)、`SettingsChangeDetectorTests`
(`mcpServers` の差分)、`CloneNextSpawnOnlyFieldsTests`(反射スイープ)、
`McpStatusHubTests`(カードの追加 / ターミナルボタン、UapOps ツールの説明文)。
