# `/mcp` をパネル側で処理する

日付: 2026-09-27 / 対象: `jp.colloid.unity-agent-panel`(コンポーザーのスラッシュコマンド)
関連: `2026-09-07-slash-commands-and-compaction.md` 1 節(`/clear` のローカル処理の前例)

## 1. 症状

パネルの入力欄に `/mcp` と打つと、応答は
「1 MCP server(s): 1 connected, 0 not connected, 0 disabled. Use /mcp in the terminal
for details.」の 1 行だけで、その直下に赤い **「CLI エラー: 合成応答」** が出ていた。

## 2. 原因

CLI の `/mcp` は対話ターミナル向けの画面(サーバーごとの状態・ツール一覧・認証)で、
`--input-format stream-json` では画面を出せないため、CLI は上の要約文を自分で生成し、
`model: "<synthetic>"` の assistant メッセージとして返す。`AgentHub` は `<synthetic>`
を「CLI 内部のエラー応答」(認証失敗など、`error` フィールドを伴うもの)の目印として
見ていて、`error` が空でも一律に `HubCliErrorFmt` の赤ブロックを足す。つまり MCP
自体は正常で、赤表示は「モデルではなく CLI が作った応答」という印が誤ってエラー経路
に流れたもの。

## 3. 対処

`/clear` と同じく、`/mcp` は CLI に送らずパネルが答える。必要な情報は既に手元にある:
`system/init` の `mcp_servers[]`(name / status)と `tools[]`(MCP ツールは
`mcp__<server>__<tool>` で登録される)から、サーバーごとの状態とツール数を出せる。

- `SlashCommandCatalog.McpCommand` / `IsMcp`。`WithBuiltins` は `/compact`, `/clear`,
  `/mcp` の 3 つを先頭に保証し、`/mcp` の説明は常にパネル自身のもの
  (`SlashMcpDescription`)。CLI のカタログに `mcp` があっても重複させない。
- `ComposerView.SendSlashCommand`: `IsMcp` なら `AgentHub.ShowMcpStatus()` を呼んで
  終わり。コンテキストチップや画像を消費しないのは他のコマンドと同じ。
- `AgentHub.ShowMcpStatus`: `_client.InitMessage` を `McpStatusNote.Describe` に渡し、
  `RoleSystem` の system note としてトランスクリプトに足す(compaction の note と同じ
  形。保存・`RaiseChanged` も同じ)。ACP ブリッジが合成した init も同じ形なので、
  ACP バックエンドでも UapOps サーバーが載る。接続前(`InitMessage == null`)は
  「まだ接続していない」旨の note。
- `McpStatusNote`(Integration。`CompactionNote` と同じく L10n を読むので Model には
  置かない): 1 行目が件数の要約(接続済み / 失敗 / その他)、以降サーバーごとに
  `- name: 状態(ツール N 個)`。ツール 0 個の行はツール節を省く(ACP の合成 init は
  `tools` が空なので「0 個」と出すと誤解を招く)。状態は `connected` / `failed` /
  `pending` / `needs-auth` を訳し、未知の値はそのまま表示して「その他」に数える。

## 4. やらないこと

- CLI の `/mcp` 画面にある認証操作やツールの説明文は出さない。init に無い情報で、
  出すには `mcp_message` 等の別経路が要る。件数と状態が分かれば、詳細はターミナルの
  `/mcp` で見られる。
- `<synthetic>` メッセージをエラー扱いする `AgentHub` の条件は変えない。`/mcp` 以外の
  ローカルコマンド(`/cost` など)でも同じ赤表示は出るが、`<synthetic>` が本来のエラー
  (`error` 付き)以外でどんな形で来るかの fixture が無く、この変更の範囲外とする。
  必要になったら「`error` が空で本文がある `<synthetic>` は通常テキスト」に緩める。

## 5. テスト

- `SlashCommandCatalogTests`: `WithBuiltins` が 3 つを先頭に置くこと、CLI 側の `mcp`
  を重複させず説明をパネルのものにすること、`IsMcp`。
- `ComposerSlashCommandTests`: `/` で `mcp` が 3 番目に出ること。
- `McpStatusNoteTests`: 接続前 / サーバー無し / 複数サーバーの状態とツール数 /
  未知の状態 / `mcp__a__b__tool` の分割。
