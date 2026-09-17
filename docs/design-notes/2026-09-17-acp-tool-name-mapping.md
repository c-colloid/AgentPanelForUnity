# ACP のツール名写像: Grok / Codex の実形で取りこぼす + 自由文から拾う穴

日付: 2026-09-17 / 対象: v0.54.6 / 関連: 2026-09-10-acp-backends.md(§3.3)、
2026-09-17-agent-screenshots.md(「見つかった不具合」1)、
2026-09-10-auto-approve-all-tools.md

## 1. 現象

実機の Grok Build(`grok agent stdio`)と codex-acp を `AcpBridgeTransport` 越しに
使ったとき(2026-09-17 の撮影セッション):

- 許可カードの表題が生の `unity-ops__uap_ping` / `unity-ops__uap_property_set`。
  自動承認レベル「全 Unity 操作」にしても `uap_*` 呼び出しのたびにカードが出た。
- Grok 固有のツール(ツール検索、X 検索、画像生成)はカードに「Tool」とだけ出て、
  何のツールか分からない。

## 2. 根本原因(実フレームで確認)

当時の Unity ログには stderr しか残っておらず(`tool_name="use_tool"
effective_tool_name="unity-ops__uap_property_set"`)、`tool_call` の生フレームは
無かったので、取り直した。スタブの `unity-ops` HTTP MCP サーバ(`uap_ping` /
`uap_property_set`)と最小 ACP クライアントを node で書き、Grok Build 1.0.34 と
codex-acp 1.12.0(Codex 0.154.0、モード `read-only` = パネルの既定が選ぶもの)に
同じ依頼を投げて全フレームを記録した。Grok は撮影時の 1.0.30 から自動更新されて
いたが、許可カードの表題は撮影時と同じ形だった。

### 2.1 Grok の形

```
tool_call         title:"use_tool"  (kind 無し)
                  rawInput:{tool_name:"unity-ops__uap_ping", tool_input:{}}
                  _meta:{"x.ai/tool":{name:"use_tool", kind:"use_tool", label:"Use Tool", ...}}
tool_call_update  kind:"other" title:"unity-ops__uap_ping"
                  rawInput:{variant:"UseTool", tool_name:..., tool_input:{}}
request_permission toolCall:{kind:"other", title:"unity-ops__uap_ping", rawInput:同上}
```

MCP ツールは Grok 自身のディスパッチャ `use_tool` 経由で、ツール id は
`<server>__<tool>`、引数は `tool_input` に入れ子。旧 `MapToolName` は 3 箇所で外した:

1. ブリッジは**最初のフレームで名前を確定**する(`AnnounceToolCall`)。その時点の
   title は `use_tool` で `uap_` を含まない。
2. rawInput は `name` / `tool` / `toolName` しか見ておらず、`tool_name` を読まない。
3. 仮に読んでも `ExtractUapToolName` は「`uap_` の直前が識別子文字でないこと」を
   要求する。`unity-ops__uap_ping` では直前が `_`(識別子文字)なので null。
   2 フレーム目以降の title も同じ理由で拾えない。

結果は `Tool`。`UapOpsServer.FindByWireName("Tool")` は null なので
`AutoApprovePolicy` は isUapOpsTool=false で常にカードを出し、カードの表題には
`state.Title`(= `unity-ops__uap_ping`)がそのまま出た。

Grok 固有ツールは最初のフレームが `title:"search_tool"`(kind 無し)などで、
kind が無い → `Tool`。シェル(`run_terminal_command`)も最初のフレームに kind が
無く `Tool` になっていた(= ScriptGate のシェル事前フィルタにも掛からない)。
ACP の kind は 1 フレーム遅れて届くが、`_meta["x.ai/tool"].kind` には最初から
`execute` が入っている。X 検索は `kind:"search"` `title:"X search:"` で `Search`。

### 2.2 Codex の形

```
tool_call  kind:"execute" title:"mcp.unity-ops.uap_ping"
           rawInput:{server:"unity-ops", tool:"uap_ping", arguments:{}}
           _meta:{is_mcp_tool_call:true}
request_permission toolCall:{toolCallId, kind:"execute", status:"pending"}   ← title も rawInput も無い
```

こちらは `.` が境界になるので旧コードでも `mcp__unity-ops__uap_ping` に写る
(取り直した 1.12.0 では再現しなかった。撮影ノートの「Codex も」は Grok と
同じセッション群での観察で、Codex 単体の生フレームは残っていない)。ただし
UapOps 以外の MCP サーバの呼び出しは kind が `execute` なので **「Bash」** と
表示され、コマンドも無いカードになる。引数は `arguments` に入れ子。

### 2.3 調査中に見つけた穴: 自由文からツール id を拾っていた

旧 `ExtractUapToolName` は title の**どこにあっても** `uap_<snake>` を拾う。
ところが Codex のシェル実行は title がコマンド行そのもの(`echo hi > out.txt`)。
つまり `uap_ping && <任意のコマンド>` というシェル実行は
`mcp__unity-ops__uap_ping` に写り、**読み取り専用レベルでも自動承認される**。
Grok の `Search tools: "uap_ping"` のように「id に言及しているだけ」の title も
同様に誤写像される。rawInput の `name` も見ていたが、Gemini 形では rawInput は
ツール引数そのもの(`uap_scene_create_object` の `name:"Cube"`)なので、
引数 `name` に別の `uap_*` を入れると別ツールのポリシーで判定されうる。
写像結果は自動承認の入力なので、境界条件を緩めるだけの修正はこの穴を広げる。

## 3. 検討した選択肢

A. **境界条件を緩める + `tool_name` を読む**(`__` の後ろも可にする)。最小だが
   2.3 の穴を残す(広げる)。却下。

B. **構造で読む(採用)**。ツール id は「id が入る場所」からだけ読み、自由文を
   検索しない:
   - rawInput に `command` / `script` 文字列がある呼び出し(シェル)は、文面に
     何が書いてあっても UapOps にしない。
   - title は**先頭トークン**(最初の空白まで)が id のときだけ。kind が
     `execute` のときは title を見ない(title = コマンド行)。
   - rawInput は `tool_name` / `tool` / `toolName` の**値全体**が id のとき。
     `name` は引数と衝突するので外す。`server` が付いていれば `unity-ops` 限定。
   - id は `uap_x` / `unity-ops__uap_x` / `mcp__unity-ops__uap_x` /
     `mcp.unity-ops.uap_x` のいずれか。他サーバ名の修飾は不一致。
   - title を先に見る(Gemini 形で引数 `tool` より title が正)。

C. ブリッジで名前を確定せず、後続フレームで名前を差し替える。パネル側の
   tool_use は id で一度だけ作られるので、差し替えにはパネル全体の変更が要る。
   Grok は最初のフレームの rawInput に id があるので不要。見送り。

「Tool」表示については:

D. **kind が other / 無しのとき、エージェントの title をツール名にする(採用)**。
   ただしパネルは Claude のツール名(`Write` `Bash` `Task` `AskUserQuestion`
   `mcp__*`)で分岐するので、title がそれらを名乗れてはいけない。特別扱いされる
   名前はすべて「英字だけの 1 語」か `mcp__` 始まりなので、**その形の title は
   採用せず `Tool` のまま**にする(予約語リストを持たず、形で排除する)。
   1 行目だけ・48 文字まで。制限: 英字 1 語の title(例 `Imagine`)は `Tool` の
   ままになる。
E. `ToolCardDescriber` の汎用行で `input.title` を要約に出す。ACP は全ツールの
   input に title を足しているので、`uap_*` カードの「名前を繰り返さない」判定を
   壊す。却下。

あわせて:

- kind が無いフレームでは `_meta["x.ai/tool"].kind` を、ACP の kind 値
  (read/edit/delete/move/search/execute/fetch/think)のときだけ採用する。
  Grok のシェルが最初から `Bash` になり、コマンドが表示され、ScriptGate の
  事前フィルタにも掛かる。
- Codex 形 `{server, tool}` で UapOps 以外のものは `mcp__<server>__<tool>` に
  写す(「Bash」ではなく MCP カードになる。`FindByWireName` は解決しないので
  自動承認はされない)。
- MCP 名に写った呼び出しは、入れ子の引数(Grok `tool_input` / Codex
  `arguments`)を input として見せる(Claude と同じ見え方)。許可要求の
  `display_name` は MCP 名そのものにする(PermissionCard が
  「unity-ops: uap_ping」に整形する。生の `unity-ops__uap_ping` は出さない)。

## 4. 実装

`AcpProtocolBridge`: `MapToolName` を上記 B/D に書き換え、`ExtractUapToolName` を
`ParseUapToolId(text, allowTrailingText)` に置換。`AgentTitleAsToolName`、
`GrokMetaKind`、`McpArguments` を追加(いずれも純関数、internal)。

## 5. 回帰ガード(`AcpProtocolBridgeTests`)

- 実フレーム(id だけ短縮): Grok `use_tool` の 1 フレーム目 → 2 フレーム目 →
  許可要求、事前 tool_call 無しの許可要求、Grok の `search_tool` / X 検索 /
  `run_terminal_command`、Codex の MCP 呼び出し + title 無し許可要求、Codex の
  シェル実行と他サーバ MCP。
- 穴のガード: `uap_ping && rm -rf Assets` のシェルは `Bash` のまま、
  `command` と `tool:"uap_ping"` が同居しても `Bash`、他サーバの `uap_ping` は
  unity-ops にならない、`Call uap_x now` / `Search tools: "uap_ping"` は null。
- title 採用の形ガード: `Write` / `AskUserQuestion` / `mcp__…` は不採用。
- 旧 `ExtractUapToolName_FindsWholeIdentifiers` の「文中から拾う」期待値は
  意図的に反転、`MapToolName_UapInRawInputName…` は `tool` キーに変更。

## 6. 残課題

- Codex の画像生成(`image_gen`)と Grok の画像生成の生フレームは今回取っていない
  (クォータ消費)。kind/title の形が分かり次第テストに足す。
- 撮影ノートの不具合 2(モーダルを開くメニュー)と 3(Ignored ログ)は別タスク。
