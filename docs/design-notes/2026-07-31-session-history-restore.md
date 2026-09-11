# 設計ノート: セッション履歴の列挙と復元(SessionIndex / TranscriptLoader)

- 日付: 2026-07-31 / ステータス: 採用済み
- 関連: ARCHITECTURE.md D5(正史はCLI側)/ Phase 3 履歴ブラウザ
- 対象: `Editor/Model/SessionIndex.cs`, `Editor/Model/TranscriptLoader.cs`

## 1. cwd変換規則(実機確認)

ARCHITECTURE.md D5 / R02 §7.1 は変換規則を「非英数字を `-` に置換」とだけ記すが、
ASCII限定か Unicode 文字(CJK等)も「文字」として保持するかは未確定だった。今回、
このマシンの実ディレクトリ3件で確認した:

| cwd(絶対パス) | 実ディレクトリ名 |
|---|---|
| `C:\Unity\UnityProjects\DevelopmentProject` | `C--Unity-UnityProjects-DevelopmentProject` |
| `C:\Users\colloid\Dev\UnityAgentPanel` | `C--Users-colloid-Dev-UnityAgentPanel` |
| `C:\Users\colloid\AppData\...\cli-e2e\workdir` | `C--Users-colloid-AppData-...-cli-e2e-workdir`(全区切り文字が`-`) |

3件とも ASCII パスのみで、CJK 等の非ASCII文字を含むパスの実例は確認できなかった
(このマシンにCJKパス名のプロジェクトが存在しないため)。

**決定**: `char.IsLetterOrDigit`(Unicode対応)ではなく **ASCII の `[a-zA-Z0-9]` のみ
を「保持」対象とし、それ以外(非ASCII文字を含む)を全て `-` に置換**する
(`SessionIndex.TransformCwdToProjectDirName`)。

**根拠**: Node/CLI 系ツールがこの種のパス→ディレクトリ名スラグ化を行う際は
`/[^a-zA-Z0-9]/g` 形式の ASCII 限定正規表現が広く使われる慣習であり、Unicode文字を
「保持」する実装は相対的に少ない。この点は実機で確証が取れていない**仮定**である
ため、ここに明記する: もし将来 CJK を含むパスを実機で確認でき、CLI が非ASCII文字を
保持することが分かった場合は、この一箇所(`TransformCwdToProjectDirName`)だけを
直せばよい。ASCII限定側に倒した理由は、逆(Unicode保持)を選んで外れた場合の被害が
大きいため: 日本語ユーザーが日本語パス配下でプロジェクトを開いた際に**間違った
ディレクトリ**(存在しないか、他セッションのディレクトリ)を見に行き、履歴が
「空」または無関係な内容に見えるという、無言で気づきにくい失敗モードになる。
ASCII限定側なら少なくとも「非ASCII文字はすべて `-` になる」という一貫した規則の
下で外れるため、CLI側の規則を後から突き止めた時に一箇所の修正で全面的に直る。

回帰ガード: `SessionIndexTests` に3件の実測値を固定テストとして収録。

## 2. TranscriptLoader: JSONL行 → ChatMessage マッピング表

正史のセッションJSONL(実機の4本、`~/.claude/projects/C--Unity-UnityProjects-
DevelopmentProject/*.jsonl` を読んで確認)は、ヘッドレスプロトコルのstdout capture
(`Tests/Editor/Fixtures/*.jsonl`)とは**別の envelope**を持つ:
`parentUuid` / `isSidechain` / `promptId` / `cwd` / `permissionMode` / `entrypoint`
/ `version` / `gitBranch` などが付き、かつ `"type":"result"` 行は**存在しない**
(result はプロトコル応答であり、CLIがディスクに永続化する行には現れない)。一方
`message.content` の中身(text/thinking/tool_use/tool_result ブロック)はstdout
capture と同一形式なので、`Integration/AgentHub.cs` のライブ処理ロジックをそのまま
参照仕様にできる。

実機で確認した、ドキュメント未記載の追加行タイプ: `attachment`
(`deferred_tools_delta` 等)、`last-prompt`、`ai-title`、`mode`、
`queue-operation`。いずれも表示に使わないため無視する(D8の「未知type無視」を
そのまま適用)。

| JSONL行 | 条件 | ChatMessageへの反映 | ライブ処理との対応 |
|---|---|---|---|
| `"type":"user"` | `isMeta:true` | **行全体を無視** | ライブでもmetaテキストは事実上不可視(AgentHubの`HandleUserEcho`はcontentが`tool_result`の時しか反応しない。人間が実際に送った文以外のテキストは元々どこにも表示されない) |
| `"type":"user"` | `message.content`に`text`項目あり | 新規`ChatMessage`(role=user, delivered=true, turnId=0)。**現在組み立て中のassistantバブルを閉じる**(下記4節) | `AgentHub.SendUserMessage`が送信の都度`_streamingAssistant = null`にする挙動を再現 |
| `"type":"user"` | `message.content`に`tool_result`項目あり | 新規メッセージは作らず、`tool_use_id`が一致する開いている`ToolCallRecord`を`Complete()`で確定 | `AgentHub.OnToolResultReceived`と同一 |
| `"type":"assistant"` | 任意 | **現在組み立て中のassistant `ChatMessage`**(無ければ新規作成)に、`text`→`MakeText`、`thinking`→`MakeThinking`、`tool_use`→新規`ToolCallRecord`(Running)+`MakeToolCall`を追記。`message.model=="<synthetic>"`または`error`フィールドありなら`MakeError`を追記 | `AgentHub.OnAssistantMessageCompleted` / `OnToolUseStarted`と同一 |
| それ以外全て(`system`, `summary`, `queue-operation`, `attachment`, `last-prompt`, `ai-title`, `mode`, `compact_boundary`, `control_request`/`control_response`, `stream_event`, `result`, 未知type) | - | **無視**(D8「未知type無視」の適用範囲を「行全体」に拡大) | - |
| 壊れた行(パース失敗) | JSONとして不正、または末尾行が切り詰め | **その行だけスキップして継続**(例外を投げない) | 「壊れた行1つでCLIが即死する」書き込み側の制約(D2)とは無関係の**読み取り**側の話。切り詰め末尾行と単純な壊れた行を区別する必要は無い: どちらも同じ`JsonParseException`捕捉で吸収される |

## 3. assistantバブルの区切り(非自明な決定)

ライブ動作(`AgentHub`)では、1つの `ChatMessage`(assistantロール)は
「ユーザーが実際に送信するまで」ブロックを蓄積し続ける。`_streamingAssistant`は
`SendUserMessage`が呼ばれた時と、`FinalizeStreamingMessage`(`result`/interrupt/
プロセス死)が呼ばれた時にしかリセットされない。ところが永続化されたJSONLには
`result`行が存在しない(§2)。つまりディスク上では **assistant バブルを閉じる
唯一のシグナルは「次の(meta でない)ユーザーテキスト行」** であり、これは実は
ライブの規則と完全に一致する(ライブでも`result`受信は`_streamingAssistant`を
nullにするだけで、その後 assistant バブルが実際に「新しく開き直される」のは次の
`SendUserMessage`か次の`OnAssistantMessageCompleted`のどちらか早い方であり、
`result`単独では新メッセージは作られない)。

結果として: 1回のツール呼び出しラウンドトリップを挟む複数の`assistant`行
(text→tool_use→[tool_result]→text...)は、次のユーザー送信が来るまで**1つの
ChatMessageに merge**される。これは実機フィクスチャ`success_bidi_inbound.jsonl`
(Bashツール1回を挟む1往復)で実証済み: 最終的な assistant メッセージは
`[Text("I'll run the command."), ToolCall(Bash, Succeeded), Text("DONE")]`の
3ブロックを持つ1メッセージになる。

ファイル終端で開いたままの`ToolCallRecord`(対応する`tool_result`行が来なかった
= 中断されたターンかクラッシュ時の切り詰め)は、ライブの
`FinalizeStreamingMessage`と同じく `Running` → `Pending` に降格する。

## 4. コンテキスト添付チップの復元は非可逆(既知の劣化)

`Integration/ContextBlockFormatter.Compose`はチップ本文(payload)のみをワイヤに
乗せ、チップの`title`(表示ラベル)は**送信時点で破棄**される。つまりJSONLからは
元のチップタイトルを絶対に復元できない。`TranscriptLoader`は
`"\n\n===== ATTACHED UNITY EDITOR CONTEXT ====="` マーカーを検出したら、それより
前をユーザーの生テキストとして`MakeText`、区切り以降(フッターまで)を1個の
`MakeContextAttachment("Restored context", payload)`にまとめる(元のチップ数や
個別タイトルには分解しない)。無言で生テキストに区切り文字ごと出すよりは、
折りたたみチップとして提示する方がまだ読みやすいという判断。

レイヤ制約により、`TranscriptLoader`(Model層)は`ContextBlockFormatter`
(Integration層)を参照できない(D9: `UI → Model → Core` と `UI → Integration →
UnityEditor` は独立した一方向の依存であり、`Model → Integration`は許可されない
辺)。そのためヘッダー/フッターの文字列リテラルを`TranscriptLoader`内に**複製**
した。同期ずれの検知として、テスト側(Integration層にもアクセスできる
Testsアセンブリ)で`TranscriptLoader`の private const をリフレクションで読み、
`ContextBlockFormatter.Header`/`Footer`と一致することを assert する
(`TranscriptLoaderTests.ContextMarkers_StaySyncedWithContextBlockFormatter`)。

## 5. 件数キャップと "空==失敗" の方針

- キャップ: `TranscriptLoader.DefaultMaxMessages = 300`。ARCHITECTURE.mdのリスク#9
  (300件閾値でのScrollView間引き)と揃え、復元直後から同じ間引き前提のUIに乗せる。
  末尾(直近)300件を残す(`messages.GetRange(count-300, 300)`)。
- **失敗は例外ではなく空リスト**: `SessionIndex.Refresh`はディレクトリが無い/
  読めない場合に空の`Entries`を返し、`TranscriptLoader.Load`はファイルが無い/
  読めない/丸ごと壊れている場合に空の`List<ChatMessage>`を返す。理由:
  1. 履歴ブラウザは「まだセッションが無い」プロジェクトで**必ず**呼ばれる
     (`Directory.Exists==false`が通常運転の一部であり例外的事象ではない)。
  2. 1個の壊れたファイル/1行の壊れたJSONで履歴ビュー全体やパネル初期化が落ちる
     ことは、実際に使えるセッションが他に無い場合でもパネルを使用不能にする
     ため、実害がリスクを大きく上回る(SessionCacheFileの「壊れたキャッシュは
     削除してnullを返す」という既存方針と同じ考え方)。
  3. クラッシュ時に末尾行が切り詰められたJSONLを開くケース(D4のドメイン
     リロード/エディタクラッシュ復旧シナリオ)は、「本文の9割は正常、最後の1行
     だけ壊れている」が最頻出パターンであり、行単位のtry/catchで自然に対応
     できる(§2表の「壊れた行」参照)。

## 6. 実装時に踏んだ地雷: `DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal`

`ToolCallRecord.startedAtUtcTicks` / 完了時刻を行の `timestamp` フィールドから
求めるヘルパーで、当初 `DateTime.TryParse(s, CultureInfo.InvariantCulture,
DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out parsed)`
と書いた。**この2フラグの組み合わせは `ArgumentException` を投げる**
(`TryParse`であっても、不正な入力文字列に対する「false を返す」契約とは別に、
呼び出し側のスタイル指定そのものが不正な場合は例外を投げる、というBCLの仕様)。

これが `TranscriptLoader.Load`最外周の `catch (Exception)`(当時)に無言で
飲み込まれ、**tool_use を含む行の処理がその場で丸ごと中断される**という重大な
バグを生んだ: 以降の行が一切処理されず、`success_bidi_inbound.jsonl`
実行結果が「3ブロックのはずが1ブロックしか無い」という、例外もログも出ない
サイレント破損として現れた。TranscriptLoaderTests(このラウンドで新規作成)を
実際に実行して初めて発見できた(Unity実機の `csc.exe` + 実 NUnit assembly で
コンパイル・実行して検証: 詳細は本ラウンドの実装ログ参照)。

**対策(2点)**:
1. `DateTimeStyles.RoundtripKind` 単体を使う(`AdjustToUniversal`は付けない)。
   このプロジェクトが扱うタイムスタンプは全て末尾`Z`付きのUTC形式なので、
   `RoundtripKind`単体で`Kind=Utc`に解決される。パース後の`ToUniversalTime()`
   はローカル/オフセット付きタイムスタンプが紛れ込んだ場合の安全網に過ぎない。
2. `Load()`最外周の catch を **`catch (Exception)` から `catch (IOException)` /
   `catch (UnauthorizedAccessException)` に絞った**。この2つ以外の例外
   (今回のようなコーディングミス)はここで握りつぶさず、テストや実行時に
   **見える形で失敗させる**。「読み込み失敗は空リストに縮退させる」という
   5節の方針は「壊れたファイル/行」が対象であり、「ローダー自身のバグ」を
   無言で握りつぶす方針ではない、という区別を明文化した。

## 7. 検討したが採らなかった案

| 案 | 内容 | 却下理由 |
|---|---|---|
| `result`行の有無でassistantバブルを閉じる | プロトコルの意味論に忠実 | ディスク上のJSONLに`result`行は存在しない(実測)。実装不能 |
| `isSidechain:true`の行を除外する | サブエージェント発話を隠す | ライブの`AgentClient`自体が`parent_tool_use_id`で一切フィルタしておらず、サブエージェントの発話も現状は普通にトップレベルへ描画される。復元だけ挙動を変えると「識別に再現できない」という本タスクの目的に反する |
| Unicode対応の`char.IsLetterOrDigit`でcwd変換 | 非ASCII文字も「保持」 | §1参照。実機で反証も確証もできず、外れた時の被害が大きい側を避けた |
