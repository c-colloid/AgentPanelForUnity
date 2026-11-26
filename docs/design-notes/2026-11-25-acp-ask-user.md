# ACP エージェント向けの質問カード `uap_ask_user` (2026-11-25)

Issue #122「[ACP] AskUserQuestion 準拠の機能の実装」への対応。

## 1. 背景

質問カード(選択肢つきの質問。2026-08-05 のステッパー、2026-08-12 の「その他...」)は
Claude Code の組み込みツール `AskUserQuestion` が出す `can_use_tool` を描画している。
ACP(Gemini CLI / Codex / Qwen Code / Grok ほか)にこのツールは無く、ACP 自体にも
「ユーザーに選ばせる」メッセージは無い。ACP エージェントは方針を確認したいとき
文章で質問してターンを終えるしかなく、カードは一度も出ない。

Issue の設計イメージは「JSON で質問と回答候補を出してもらい、それを整形する」。

## 2. 設計

### 2a. JSON の受け口は MCP ツールにする

エージェントに JSON を出してもらう経路は 2 つ考えられる。

- 返答の文章にコードブロックで JSON を書かせ、パネルが拾う。
- MCP ツールの引数として受け取る。

後者を採った。ツール引数はスキーマつきでエージェント側が検証してから送ってくるうえ、
回答を**ツールの結果**として同じターンの中に返せる(Claude Code の AskUserQuestion と
同じ流れ)。文章から拾う方式は、書式の崩れを検出する手段が無く、回答も次のユーザー
メッセージとして送り直すしかない。

ツール名は `uap_ask_user`(core モジュール)。入力は AskUserQuestion と同じ形にした:

```json
{"questions":[{"question":"どれを対象にしますか?","header":"対象",
  "options":[{"label":"Cube","description":"..."},{"label":"Sphere"}],
  "multiSelect":false}]}
```

同じ形なので、カードは既存の `PermissionCard` の質問モードをそのまま使う。新しい UI は
作っていない。制約も AskUserQuestion と同じ(1 回 1〜4 問、選択肢 2〜4 個、「その他...」は
カードが自動で足す)。質問文は回答のキーなので重複不可。

結果は JSON 文字列で返す:

| status | 意味 |
|---|---|
| `answered` | `answers` に `{"<質問文>":"<選んだラベル>"}`。複数選択は `", "` 連結、自由回答はその文字列 |
| `skipped` | ユーザーが「スキップ」した |
| `pending` | まだ回答されていない(2c)。同じ質問でもう一度呼ぶと同じカードを待つ |
| `dismissed` | カードが開いたままターンが止められた |
| `unavailable` | カードを出せる接続が無い(Claude Code 接続など)。文章で質問するよう案内する |

### 2b. 回答の運び方(ACP の許可応答には回答を載せられない)

Claude Code では、カードの回答は `can_use_tool` への allow の `updatedInput.answers` に
載って CLI に戻る。ACP の `session/request_permission` の応答は**選択肢 ID 1 つ**だけで、
入力を書き換える手段が無い。

そこで回答は `AskUserQuestionBroker`(Core/Protocol、static、スレッドセーフ)を経由させる。
キーは質問文の並び(= 回答オブジェクトのキーと同じもの)。

通常の順序(エージェントが先に許可を求める):

1. エージェントが `uap_ask_user` の `session/request_permission` を送る。
2. `AcpProtocolBridge` はこれを**許可カードではなく質問カード**としてパネルに渡す
   (`requires_user_interaction: true`、入力は質問そのもの。`uap_call` 経由や Codex / Grok の
   封筒も剥がす)。
3. ユーザーが答えると、ブリッジは `updatedInput.answers` をブローカーに預けてから
   エージェントに allow_once を返す。
4. 続いて届くツール呼び出しはブローカーから回答を受け取り、即座に返る。

許可待ちには時間制限が無いので、この順序ではユーザーが何分考えても問題ない。
`requires_user_interaction` が付くため、どの自動承認レベルでもカードは必ず出る
(`AutoApprovePolicy` の既存の規則)。「常に許可」は出さず、応答も常に allow_once にする
(質問は 1 つずつ答えるもの)。

### 2c. 許可を求めずに呼ばれた場合

エージェント側が全ツール自動承認のモードだと、許可要求なしでツールが直接呼ばれる。
このときブローカーに回答は無いので、ツールがブローカー経由でブリッジにカード表示を頼み
(`PresentAskUser`。ブリッジが自分で `can_use_tool` を合成してパネルへ流す)、回答を待つ。

待ち時間は 1 回 20 秒まで。stdio リレーの HTTP タイムアウト(30 秒)と Codex の MCP ツール
タイムアウト既定値(60 秒)の内側に収めるため。超えたら `pending` を返し、エージェントが
同じ質問で呼び直すと同じカードを待ち続ける(カードは 1 枚のまま)。

このためツールは `IUapOffThreadTool`(HTTP ワーカースレッドで実行)にした。メインスレッドで
待つとエディタが固まり、`UapMainThreadDispatcher` の 15 秒で打ち切られる。

### 2d. 後始末

- ターンが終わる・中断される・エージェントが落ちると、開いているカードは `dismissed` に
  なり、受け取られなかった回答は捨てる(次のターンの同じ質問に古い回答が返らないように)。
- 検証に通らない入力(選択肢 1 個など)は質問カードにせず普通の呼び出しとして通し、
  ツール自身のエラーで直し方を伝える。カードを出しても回答を返す先が無いため。

### 2e. エージェントへの案内

ACP 接続かつ core モジュール有効のときだけ、初回プロンプトの常設指示に 1 行足す
(`ComposeAskUserSteeringSection`):ユーザーが決めることで選択肢を挙げられるなら
`uap_ask_user` で尋ねる。Claude Code には足さない(AskUserQuestion があり、手段を 2 つ
示すと選択が割れる)。ツールの説明にも「Claude Code は AskUserQuestion を使う」と書いた。

(2026-11-26 追記)この案内は「選択肢を挙げられるときだけ」から「質問は原則カードで」に
強めた。`2026-11-26-ask-user-by-default.md` を参照。

会話ログのツール行は AskUserQuestion と同じく質問文を表示する(`ToolCardDescriber`)。

## 3. 却下した代替案

- **返答の文章中の JSON を拾う**: 2a のとおり。
- **ツールは即座に返し、回答は次のユーザーメッセージとして送る**: タイムアウトの心配は
  無くなるが、AgentHub の許可スロットに「control_request に紐づかないカード」という
  新しい状態が要り、回答がターンをまたぐ。2b の順序なら同じターンで返せる。
- **回答が来るまでツールを無期限に待たせる**: MCP クライアント側のタイムアウトが
  エージェントごとに違い、切れるとカードだけが残る。
- **Claude Code 接続でもカードを出す**: 組み込みの AskUserQuestion と二重になる。
  `unavailable` で組み込みツールへ誘導する。

## 4. 検証

- `ci/SmokeTests`(Unity 不要): 入力検証、許可が先に来る順序、`uap_call` 経由、スキップ、
  許可なしの順序(`pending` → 呼び直しでカードが増えない → 回答で待機中の呼び出しが返る)、
  ターン終了での `dismissed`。
- EditMode: `AcpAskUserTests`(同じ経路を NUnit で。Codex / Grok の封筒、自動承認されない
  こと、案内文の出し分け)、`ToolCardDescriberTests`、ツールのメタデータ固定リスト。
- **未実施**: 実エージェントでの通し確認。各エージェントが `uap_ask_user` に対して許可要求を
  送るか・`rawInput` を付けるかは既存ブリッジの対応状況からの推定で、付けないエージェントは
  2c の経路になる。
