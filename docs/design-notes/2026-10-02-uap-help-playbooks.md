# uap_help のプレイブック: 複数ツールの手順をオンデマンドで返す

Date: 2026-10-02
対象: `jp.colloid.unity-agent-panel`(Core)v0.61.0-beta.1(同じベータに入る)
関連: `docs/design-notes/2026-09-22-uloop-phase1.md` §2.8(`uap_help` / コンパクト一覧)、
`docs/design-notes/2026-09-21-uloop-always-loaded-cost.md`(常時読み込みのコスト)、
`docs/design-notes/2026-09-23-uloop-phase2.md`、`docs/design-notes/2026-09-28-uloop-phase3.md`

---

## 0. 結論(先に)

1. **スキル(`.claude/skills` 等のファイル)の配置層は作らない。** 代わりに、複数ツールに
   またがる手順を「プレイブック」として `uap_help topic:"<id>"` で必要なときだけ返す。
   常時読み込まれるステアリング文には、使えるプレイブックの ID を並べた 1 行だけを足す
   (オーナー判断: 案 C)。
2. プレイブックは Core の組み込み 6 本(§4)と、追加パッケージが
   `IUapPlaybookProvider` で足せる口で構成する。**手順に出てくるツールがすべて登録済み
   かつ有効なモジュールにあるときだけ**提供するので、本文が存在しないツールへ誘導することはない。
3. ステアリング文は、手順を外して能力の要約と安全上の文だけにした。**長さは正味で 356〜472 字減った**
   (§5。残りの大きな塊は動かしていないので、削減は 1 割に満たない)。
4. Unity でのコンパイルと EditMode テストの実行は、このセッションでは確認できていない
   (smoke 641 件は全緑。§7)。

---

## 1. 何が問題だったか

- ツールの説明文は MCP `tools/list` で全エージェントに届く。足りないのは**ツールをまたぐ手順**
  (どの順に呼ぶか、分岐、失敗時の対処)の知識だった。
- それは今、次の 2 か所にあった。
  1. 常時読み込まれるステアリング文(`AgentHub.UapOpsModuleFamilies` と
     `ComposeUapOpsSteeringSection`)。全モジュール有効で約 4.8k 字(測定: Core のみ 4955 字)。
     新しいツールを足すたびに伸びる。
  2. 長いツール説明文(`uap_record_video` は録画を Play 後に始める注意まで抱えている)。
     説明文は 1 ツールの記述であって手順の置き場所ではなく、手順を書くと同じ文が複数の
     ツールに重複する。
- 手順は、使う場面でだけ読めれば足りる。毎ターン運ぶ必要はない。

## 2. 決めたこと、選ばなかった案

### 2.1 案

| 案 | 内容 | 判断 |
|---|---|---|
| A | スキルファイルをプロジェクトに配置(uLoop 方式) | 採用しない |
| B | MCP `prompts/list` で手順を公開 | 採用しない(§2.3) |
| **C** | `uap_help` に `topic` を足し、手順を返す | **採用** |

### 2.2 A を採らない理由

- uLoop がスキルを必要としたのは、その CLI ツールが `tools/list` に載らず、エージェントが
  存在を知る手段がなかったから。UapOps のツールは MCP なので、説明文はもう届いている。
  足りないのは手順だけで、そのためにスキルの仕組みを持ち込むのは過剰になる。
- スキルファイルは**利用者のプロジェクト内に置く同期層**になる(版の更新、利用者が編集した
  ファイルとの衝突、`.gitignore`、削除)。パネルがプロジェクトに書くファイルを増やすことの
  保守コストが大きい。
- スキルは**Claude Code 固有**の仕組み。パネルは ACP 経由で Codex / Gemini なども扱い、
  コンパクト一覧もそのためにある。ファイルを置いても、他のエージェントは読まない。
- `uap_help` なら、どのエージェントでも(stdio 中継越しでも)同じツール呼び出しで取れ、
  プロジェクトにファイルを作らず、頼まれたときだけ読み込まれる。

### 2.3 B(MCP prompts)を見送った理由

仕様上は手順の公開に向く。ただしクライアントごとの対応が揃っているとは言えず(`prompts/list`
を呼ばない、呼んでも利用者向けのスラッシュコマンドとしてしか出さない、などがあり得る。
このセッションでは各クライアントの挙動を測っていない)、手順を**エージェントが自分で
取りに行ける**保証がない。ツールとして返せば、対応状況に依らず確実に届く。

## 3. 設計

### 3.1 型(いずれも Unity 非依存。`ci/SmokeTests` が実コードをそのままコンパイルする)

- `UapPlaybook`(`Editor/Ops/UapPlaybook.cs`): `Id`(kebab-case)、`Title`、`Summary`
  (1 文)、`RequiredTools`(`string[]`)、`Body`(英語、番号付きの手順)。
- `UapPlaybookCatalog`(`UapPlaybookCatalog.cs`):
  - `CreateBuiltIn()` / `Create(providers, log)`: 組み込み → プロバイダの順に並べる。
    組み込みの ID 重複・不正は例外(自分たちのリストの誤りなので)。
  - `AddFromProvider`: 不正な形、または ID が既出(Core・先のプロバイダ)のものは
    **ログを 1 行出して捨てる**。Core が常に勝つ。プロバイダが投げたら、その
    プロバイダだけ飛ばす(ツールのプロバイダと同じ姿勢)。
  - `Find(id)`、`Filter(isToolAvailable)`、`FormatIndex(...)`、`Validate(...)`。
  - `AvailabilityFor(enabledModules, moduleOfTool, compactToolList)`: 標準の判定。
    「登録済み**かつ**そのモジュールが有効」。`uap_tools` / `uap_call` は
    登録されないツールなので、コンパクト接続のときだけ「ある」とする。
- `IUapPlaybookProvider`: `IUapToolProvider` と同じ作り(public・非 abstract・引数なし
  コンストラクタ)。`UapPlaybookProviderDiscovery` が `TypeCache` で探す(public のみ、
  型名順、失敗はログしてスキップ)。`UapOpsServer.Playbooks` が遅延生成する
  (`Registry` と同じ遅延パターン。`[InitializeOnLoadMethod]` は使わない)。
- `UapBuiltInPlaybooks`: Core の 6 本。

### 3.2 `uap_help` の `topic`

- 入力に `topic`(文字列)を追加。`ReadOnly` のまま。説明文の**先頭文**に「ツール名の
  メタデータ」と「プレイブック」の両方を入れた(コンパクト一覧では先頭文が一行要約になるため)。
- 利用可能なトピック: プレーンテキストで `Playbook "<id>": <title>` / 要約 /
  `Required tools: ...` / 本文を返す(JSON にすると本文の改行と引用符がエスケープされ、
  かえって読みにくい)。
- 未知の ID、または必要なツールのモジュールが無効で使えない ID: **例外にせず**、
  使えるトピックの一覧(`- <id>: <summary>`)を返す。使えるものが 1 本も無ければ、そう言う。
- `names` / `name` の応答は**バイト互換**(`topic` 無しなら従来と同一。テストで
  `UapToolGateway.ResolveToolDetail` から組んだ期待値と比較)。両方渡したら両方返す。
- **引数が何も無い呼び出しは、従来どおり `ArgumentException`**(既存テスト
  `UapHelp_NoNamesAndNoName_ThrowsArgumentException` が固定している)。ただしメッセージに
  `topic` と、使えるプレイブックの索引を足した。「何も無いとき応答にも索引を含める」要件は、
  この形(エラーメッセージに索引)で満たした。エラーを返さない形に変えるなら、既存テストを
  変える判断が要る。
- コンパクト接続かどうかは `uap_help` から見えない(エンドポイントのパスは
  `UapOpsRequestHandler` だけが知る)。そこで `AgentHub.StartClient` が、スポーン時に解決した
  値を `UapOpsServer.SetCompactToolList` で `SessionState` に書き、`uap_help` はそれを読む。
  ドメインリロードを越えて残る。`compact-mode` はこの値が true のときだけ提供される。

### 3.3 ステアリング文

- `ComposeUapOpsSteeringSection` の末尾に 1 行足す:
  `Playbooks (uap_help topic:"<id>"): verify-gameplay, ui-click, ...`。
  カタログの `Filter` を通すので、**有効なモジュールのツールで足りるものだけ**が載る。
  1 本も無ければ行ごと出さない。
- カタログとレジストリは省略可能な引数(既定は `UapOpsServer` のもの)にし、テストが
  偽プロバイダや部分的なレジストリを渡せるようにした。
- 他のステアリング文と同じく、スポーン時に焼き込まれる。モジュールの切り替えが索引に
  反映されるのは次の再接続から(家族テキストと同じ)。

### 3.4 手順の外出し、残したもの

外へ出した: Play Mode の手順(開始 → `uap_ping` を待つ → 操作 → 読む → 停止)、
入力ツールの使い分け、patching 系の限界とフォールバック。
**残した(ステアリングに必要な安全上の文)**: `uap_execute_code` は型付きツールで足りない
ときだけ・常に許可カードを出し自動承認されない、型付きの `uap_*` を優先する、Play Mode を
開いたまま終えない、`start`/`stop` はエディタが切り替わる前に返るので `uap_ping` を待つ、
メインスレッドのタイムアウトの扱い、保存・シーンを開くのにメニューを使わない、など。
常にどのツールにも当てはまる規則は、プレイブックにせず常時読み込みに置く。

## 4. プレイブックの一覧(Core)

本文は英語・ASCII のみ。ツール名と引数名は、実際の説明文と入力スキーマに合わせた
(§6 のテストで機械的にも確認する)。長さは 1.0k〜1.6k 字。

| ID | 要るツール | 内容 |
|---|---|---|
| `verify-gameplay` | `uap_play_mode` / `uap_console_logs` / `uap_editor_screenshot` / `uap_game_pointer` | `uap_project_info` で入力方式を確認 → `uap_play_mode start` → `uap_ping` を待つ → 入力(uGUI は `uap_game_pointer`、ゲームコードは `uap_game_key` / `uap_game_mouse`)→ `uap_game_watch` / `uap_console_logs` / `uap_editor_screenshot` で観測 → 録画は Play 開始**後** → 必ず停止。入力が効かないときは `uap_editor_focus`。 |
| `ui-click` | `uap_editor_screenshot` / `uap_game_pointer` / `uap_play_mode` | `annotate_ui` / `ui_only` で一覧 → 返った階層パスを `target` に。座標は代替。 |
| `script-change` | `uap_compile` / `uap_scripts_commit` / `uap_ping` | ステージング → `uap_scripts_commit`、外部編集は `uap_compile` の `request` / `status`。リロード後は `uap_ping`。同じエラーで回さない。 |
| `reproduce-input` | `uap_input_record` / `uap_input_replay` / `uap_play_mode` / `uap_ping` | Play 中に記録 → `stop` でパスを得る → `start` / `status` / `stop` で再生。コード変更後の回帰確認。 |
| `execute-code` | `uap_execute_code` | 使ってよい条件、スニペットの形(`object Run()` の本体)、`confirm:true` のプレビュー手順、Undo の報告、ロード済み件数と `action:"reset"`。 |
| `compact-mode` | `uap_tools` / `uap_call` | `uap_tools` で探す → `names` で完全なスキーマ → `uap_call`。コンパクト接続のときだけ。 |

追加パッケージのプレイブック(索引にどう載るか)は、そのパッケージのノートに書く。

## 5. ステアリング文の長さ(測定)

測定方法: 実際の `ComposeUapOpsSteeringSection`(`AgentHub.cs` から該当部分をそのまま
切り出し、Unity 型だけスタブにして net8 でコンパイル)の返り値の文字数。変更前は
変更前のコード(HEAD)で同じ方法で測った。モジュールは (a) 既定の 6 つ(core / prefab / editor /
markers / web / code)、(b) 家族テキストを持つ 8 つ全部(+ anim / patching)。
「ライトマップ段落あり」は追加パッケージ提供のツールが登録されているとき(その場合は
プレイブックの索引にも追加パッケージ分が載る)。

| 条件 | 変更前 | 変更後 | 差 |
|---|---:|---:|---:|
| (a) 既定モジュール、Core のみ | 4632 | 4276 | -356 (-7.7%) |
| (a) 既定モジュール、ライトマップ段落あり | 5918 | 5562 | -356 (-6.0%) |
| (b) 全モジュール、Core のみ | 4955 | 4483 | -472 (-9.5%) |
| (b) 全モジュール、ライトマップ段落あり | 6241 | 5804 | -437 (-7.0%) |

- 手順の外出しで 463 字(Play Mode 段落 770 → 307 字)、patching の家族テキストで
  116 字減り、索引 1 行(既定の Core のみで 107 字)が増えた。
- **正直に言うと、削減は小さい。** 常時読み込みの大きな塊(core の段落 約 1.8k 字、
  ライトマップ段落 約 1.3k 字、マーカー段落)は、手順ではなく規則・個別ツールの注意なので
  動かしていない。この変更の主な価値は、今後の手順を常時読み込みに足さずに済む置き場が
  できたこと。プレイブック 1 本を取り寄せる費用は約 1.0k〜1.6k 字(Core の全 6 本で約 8.5k 字)で、
  頼まれたときだけ。
- コンパクト接続では `compact-mode` の索引分(約 14 字)と、既存の
  `UapToolListModePolicy.SteeringSentence` が加わる。

## 6. テスト

- smoke(`ci/SmokeTests` の `PlaybookSmoke`、Unity 不要): 組み込み 6 本の ID・形式・長さ・
  ASCII、必須ツールが本文に出ること、`Filter` / `AvailabilityFor`(コンパクトのときだけ
  ゲートウェイ名)、索引の書式、ID の検証、プロバイダのマージ(重複拒否 + ログ、
  不正な ID、投げるプロバイダ、Core 優先)。
- EditMode(`Tests/Editor/UapHelpPlaybookTests.cs`): トピックが見つかる、必要なツールの
  モジュールが無効だと使えない、未知の ID は使えるトピックを返す(例外にならない)、
  `names` 応答のバイト互換、引数なしは従来どおり例外(メッセージに索引)、ステアリングの
  索引、偽プロバイダの統合、重複 ID の拒否、非 public プロバイダが発見されない、
  組み込み本文が**実在するツール名だけ**を使い、ツール名の直後の `arg:` が
  そのツールのスキーマにあること。
- 既存テストの更新: `SteeringSection_StaysWithinTenLines` を 11 行に(索引 1 行)。
  `SteeringSection_PlayModeLoop_OnlyWhenTheEditorModuleIsOn` に索引と誘導の確認を追加。

## 7. 未確認のこと

- **Unity でのコンパイルと EditMode テストの実行。** このセッションでは、smoke(641 件)、
  `UapHelpTool` 一式と `AgentHub` のステアリング部分を Unity 型のスタブでコンパイルして
  走らせた確認、構文チェックまで。残りは CI の EditMode 実行。
- **エージェントが実際に索引から `uap_help topic` を引くか。** 索引 1 行と
  `verify-gameplay` への誘導で足りるかは、実セッションでの測定が要る(手順を外したことで、
  引かないエージェントが以前より雑に Play Mode を扱うようになる可能性がある)。
- **手順そのものが実機で通るか。** 本文は説明文・スキーマと突き合わせたが、通しで実行して
  いない(例: 録画を Play 後に始める流れ、`ui-click` のドラッグ)。
- **`SessionState` に置くコンパクト接続の値。** 接続ごとではなくスポーンごと。同時に
  複数の接続が違うモードで繋がる使い方は想定していない。
- **Codex / Gemini などが長いプレーンテキストの結果を途中で切らないか**(本文は最大
  約 1.6k 字)。
- **MCP prompts** は実装していない。クライアントの対応が揃ったら、同じ `UapPlaybookCatalog`
  を `prompts/list` にも出せる(カタログは接続方式に依存しない)。
