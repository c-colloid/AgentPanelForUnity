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
- `AgentHub.ShowMcpStatus`: `_client.InitMessage` から答えを組み立ててトランスクリプト
  に足す(当初は system note、6 節でカードに変更)。ACP ブリッジが合成した init も同じ
  形なので、ACP バックエンドでも UapOps サーバーが載る。接続前(`InitMessage == null`)
  は「まだ接続していない」旨の note。
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

## 6. 追記(同日): コマンドのエコーと、選べるカード

最初の版を使った指摘が 2 つ。

1. `/mcp` は CLI に送らないので user 吹き出しが出ず、**自分が何かを打った痕跡が無い**。
   `/clear` は画面ごと変わるので気にならなかったが、答えだけが増える `/mcp` では困る。
2. CLI の `/mcp` はサーバーを**選んで**ツール一覧を見たり再接続したりする画面なのに、
   パネルは文字 2 行で終わり。

対処:

- **エコー**: `ShowMcpStatus` は先に `RoleUser` の吹き出し `/mcp` を足す(`delivered`
  = true。CLI に届いたわけではないが「未送信」の印を出す対象でもない)。
- **カード**: 新しい `ChatBlockKind.McpStatus`。`ChatMessageBlock.mcpServers`
  (`McpServerEntry { name, status, tools[] }`、init の `mcp_servers[]` と `tools[]`
  から `McpStatusNote.BuildEntries` が作る)を持ち、`text` には同じ内容の平文
  (`McpStatusNote.Describe(entries)`)を入れておく。`SessionCacheFile` は
  `mcpServers` を McpStatus ブロックのときだけ書く(他の kind の JSON は不変)。
  `MessageBlockFactory` は `McpStatusCard` を出す: 1 行目が件数の要約、以降サーバー
  ごとに状態アイコン(ツールカードと同じ check / cross / bullet の語彙)・名前・状態
  ラベル・ツール数・「再接続」ボタン。ツールがあるサーバーは Foldout でツール名
  (`mcp__server__` を落とした短い名前、hover で wire 名)を並べる。Foldout の開閉は
  `ExpandStateMemory`(キーはブロック + サーバー名)で覚える。`needs-auth` の行には
  「認証はターミナルで」の一言(パネルから送れる認証要求は無い)。
- **再接続**: ボタンは `AgentHub.ReconnectMcpServer(name)`。CLI の `mcp_reconnect`
  (R08 で計測済み、UapOps の安全網が既に使っている)を送り、「再接続しています」の
  note を足す。`control_response` にはサーバー名が無いので、ハブは送信順の FIFO
  (`_pendingMcpReconnects`: 名前と「ユーザー起点か」)で対応付ける。UapOps の
  自動再接続 2 箇所も同じ FIFO を通す(`SendMcpReconnectTracked`)ようにし、順番が
  ずれないようにした。クライアントの teardown で FIFO は空にする。解決時
  (`OnControlRequestResolved` の kind `mcp_reconnect`): 最新の McpStatus ブロックの
  該当行を `connected` / `failed` に書き換え、`text` も作り直す(`MessageListController`
  の署名が変わって行が再構築される)。ユーザー起点なら成否の note、自動なら失敗時だけ
  warn の note。CLI は再接続後に `system/init` を送り直さないので、これが行の状態を
  正しく保つ唯一の手段。ライブなクライアントが無い間はボタンを disabled にし、
  tooltip で理由を言う。
- テスト: `McpStatusHubTests`(FakeCliProcess 経由の init からのエコー・ブロック・
  カードの行数と Foldout・再接続の往復と失敗・未接続時の no-op)、
  `SessionCacheFileTests.RoundTrip_McpStatusBlock_PreservesServersAndTools`、
  `McpStatusNoteTests` に短い名前と entries 版 `Describe`。
