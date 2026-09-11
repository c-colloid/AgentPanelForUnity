# Thinking本文が表示されない問題(改訂版)

> **改訂 2026-08-01**: 当初「確定時の置換で蓄積本文が消える」と診断したが、ユーザー指摘
> (ストリーミング中も見えない)を受けた追加調査で**デルタ側も本文空**と判明し、下記に全面改訂。
> 旧診断の§1は「モデルにThinkingブロックが残らない」説明としては正しいが、主因ではない。

# (旧題)Thinking本文が確定時に消える問題

日付: 2026-08-01 / ユーザー報告「Thinkingの内容が表示されないバグがある」

## 1. 根本原因(実データで実証)

- 実機セッションのモデル状態: `showThinking=True` なのに **Thinkingブロック0件**
  (メッセージ2件・Textブロックは正常)。
- CLIトランスクリプト(全6セッションを確認): thinking ブロックは計32件、
  **全件 `"thinking":""(本文空)+ signature のみ`**。
- つまり thinking の本文は `stream_event` の thinking_delta でしか届かず、
  **確定 assistant メッセージ上は常に空**(モデル/CLI仕様。旧セッションも同様であり
  今に始まった挙動ではない — ストリーミング中だけ見えて確定で消えるため
  気づかれにくかった)。
- パネルの確定処理(AgentHub.OnAssistantMessageCompleted)はストリーミング尾部を
  確定ブロックで**置換**し、空の thinking はブロック化しない → 蓄積済みの本文が破棄される。

## 2. 修正方針

確定時、確定メッセージ側の thinking が空で、かつストリーミング尾部に蓄積された
thinking 本文がある場合は**蓄積本文を保持**する(置換ではなく合流)。
- 確定側に本文がある場合(将来の仕様変化)はそちらを優先(現行の置換原則を維持)。
- 複数 thinking ブロック(実測: 1ターン内に複数回)は出現順に対応付け、
  数が合わない場合は「蓄積本文を最後の空 thinking に割当てる」フォールバック。
- 履歴復元(TranscriptLoader)はディスク上に本文が無いため復元不能 —
  空 thinking をスキップする現行挙動を維持(既知の制約として本ノートに明記)。
  SessionCacheFile には蓄積本文が保存されるため、**リロード復帰では保持される**。

## 3. 回帰ガード

- SubagentGroupingTests方式のハブ経路テスト: thinking_delta 蓄積 → 空 thinking 付き
  確定メッセージ → モデルに本文入り Thinking ブロックが残ること。
- 確定側に本文がある合成ケース → 確定側優先で置換されること。

## 4. 改訂: 真の根本原因(実測)

- **thinking_delta 自体が本文空**: 実キャプチャの全 thinking_delta は
  `{"type":"thinking_delta","thinking":"","estimated_tokens":N}`。本文の代わりに
  推定トークン数のみが届く。加えて `system/thinking_tokens` イベントが並走。
- 旧フィクスチャ(7/30 R02/R02b)には thinking が一切含まれず(当時のセッションは
  thinking 非発生)、**本文入り thinking がパネルに届いた実績はゼロ**。
- 全実セッションのトランスクリプトも同形状(空+signature)。
- 結論: **現行 CLI の headless stream-json は thinking 本文を送出しない**(署名と
  トークン数のみ)。パネル側のどの修正でも本文は表示できない。
  CLI 側にこれを変えるフラグ/設定が無いか claude-code-guide で確認中
  (--forward-subagent-text はサブエージェント転送用で別件)。

## 4b. 追加実測: display:"summarized" 注入実験(2026-08-01)

公式ドキュメント上、API には `thinking: {display: "summarized"}` があり(Claude 5 系の
既定は "omitted" = 本文非送出・仕様どおり)、Claude Code の settings 経由で流せる
可能性が示唆されたため実験:

- `--settings <file>`(`{"thinking":{"type":"adaptive","display":"summarized"}}`)を付けて
  ultrathink プロンプトを実行(capture5/think_sum4) → **効果なし**。
  thinking_delta は依然 `"thinking":""` + estimated_tokens、確定ブロックも空+signature。
- 判明した副次事実: (a) `--settings` の相対パスは CLI の cwd 基準
  (見つからないと即 exit 1)。(b) 自明なプロンプトでは thinking 自体が発生しない
  (thinking_tokens:0)。(c) `system/thinking_tokens` は `estimated_tokens` 累計+
  `estimated_tokens_delta` を運ぶ(進捗表示の一次材料として最適)。
- 結論: **現行 CLI(v2.1.218+)の headless で thinking 本文を得る検証済みの手段は無い**。
  「要約本文を表示」設定は出荷しない(検証不能な機能を売らない)。CLI 側が対応した時点で
  §5-2 の前方互換によりフォルドアウト表示が自動復活する。

## 5. 改訂後の修正方針

1. **誤解を生む空フォルドアウトを廃止**: thinking 本文が空の場合はフォルドアウトを
   出さず、コンパクトな1行インジケータ「思考中... (~{0}トークン)」を表示
   (thinking_delta.estimated_tokens / system/thinking_tokens を材料に —
   これまで黙殺していたイベントの初活用)。確定後は
   「思考済み (~{0}トークン)」の控えめな行(または非表示オプションに従う)。
2. **本文が来た場合は従来のフォルドアウト表示**(前方互換 — CLI仕様が変われば
   そのまま本文表示に戻る)。§2 の「確定時に蓄積を保持する」修正も併せて実装
   (本文が来る世界での正しさを先に確保)。
3. L10n フィールド追加(en/ja)。estimated_tokens のプロトコル写像を
   StreamEventMessage に追加。
4. ドキュメント: R02c に「thinking は本文非送出(署名+トークン数のみ)」を追記。

## 4c. 訂正: `--thinking-display summarized` は本文を送出する(2026-08-01再測定)

> **見出し級の訂正**: §4bの結論「検証済みの手段は無い」は誤りだったと判明した。
> `--settings` 経由の注入ではなく、CLI引数として直接 `--thinking-display summarized`
> を渡すと thinking 本文が届く。以下、実測手順と証拠。

- バイナリ調査: `claude.exe` を `grep -aoE '.{0,90}thinkingDisplay.{0,90}'` で走査すると、
  `--thinking-display <display>` が `--thinking <mode>` とは別の**独立した公開(hideHelp)
  フラグ**として実装されていることが判明(`choices(["summarized","omitted"])`)。
  §4bで試した `--settings` 注入は同じ内部フィールド名 `thinkingDisplay` を狙っていたが、
  実際の読み出し元はCLI引数パーサの出力オブジェクトであり、settingsファイル経由では
  そこに値が渡らない(未検証の別経路だった)。
- 実験1: `capture_bidi2.js` に `--thinking adaptive --thinking-display summarized` を追加し
  ultrathink プロンプト("17×23は?")を実行
  (`scratchpad/capture8/thinkdisplay1_inbound.jsonl`) →
  **`thinking_delta` に実文が届いた**(例: `"I"`, `"'m working through the multiplication
  17 × 23 using the distribut"`, ...)。確定後の assistant メッセージの thinking ブロックも
  `"thinking":"I'm working through the multiplication 17 × 23 using the distributive
  property, then verifying it with a different approach and the difference of squares
  formula—all three methods confirm the answer is 391."` + signature という完全な形で
  届いた。
- 実験2: `--thinking-display summarized` **単独**(`--thinking` 未指定)でも同じ結果
  (`scratchpad/capture8/thinkdisplay2_inbound.jsonl`、"19×24は?") →
  デフォルトの thinking type が "adaptive"(モデルがthinking非対応/無効化されていない限り)
  であるため、`--thinking-display` 単独指定で十分。
- 結論: **`--thinking-display summarized` をCLI引数として渡すだけで、ストリーミング中も
  確定後も thinking 本文が得られる**。§5のパネル側フォールバック実装
  (空Thinkingブロック→コンパクトインジケータ、非空→フォルドアウト)は
  この訂正を待たずに前方互換設計だったため、**パネル側コードの変更は一切不要**で
  そのままフォルドアウト表示に切り替わる(実機で未検証、キャプチャ上は空文字列が
  来ないケースのみ確認)。
- 出荷判断: 「思考の要約を表示」設定トグルの追加は本ラウンドでは**実施しない**
  (担当ストリームのファイル所有範囲がAssistantMessage.cs/AgentHub.cs(thinking-tail部分)
  /ChatMessage.cs/SessionCacheFile.cs/MessageBlockFactory.cs/L10nカタログ/テストに限定されており、
  SettingsView.cs・PanelSettings.cs・ClaudeCliProcess.cs=CLI起動引数の組み立て箇所への
  変更はスコープ外かつ他ストリームとの同時編集衝突を避ける必要があったため)。
  実装時のフック先: `AgentHub.StartClient`が`AgentClientOptions`を組み立てて
  `client.Start(...)`を呼ぶ箇所(本ファイル1005行目付近)に
  `ThinkingDisplay`相当のオプションを足し、`ClaudeCliProcess`(または
  そのCLI引数ビルダー)で`--thinking-display summarized`を追加する。設定変更は
  再接続が必要(reconnect-needed)なので`SettingsChangeDetector`+
  設定自動適用(`RequestAutoApplyReconnect`)の既存経路にそのまま乗せられる。

## 5b. 監査: interleaved thinking / signature_delta / thinking_tokens指標への影響(2026-08-01)

Stream Bの追加監査タスクとして、v0.6.0のtail-merge実装(`AgentHub`の
`ExtractTrailingThinkingTails`/`AppendFinalizedThinking`/`MergeLeftoverThinkingTails`)が
仕様上ありうる複雑なケースを安全に処理できるか確認した。

- **1つのassistantイベントのcontent[]内に複数thinkingブロックが混在するケース**
  (API仕様上は `[thinking, tool_use, thinking, text]` のような形もありうる):
  実キャプチャ全件で `assistant` タイプの行は**常に content 配列が長さ1**
  (1イベント=1確定コンテンツブロック)であることを確認した
  (`scratchpad/capture8/thinkdisplay1_inbound.jsonl` の2件の assistant 行がそれぞれ
  thinking 単体・text 単体)。したがってこの多重ブロックのケースはCLIの実挙動としては
  発生しない。コード側 (`AppendFinalizedThinking`の`pendingThinkingIndex`によるインデックス
  対応)は複数ブロックが来ても安全に動作する設計(該当tailが無ければ
  `tokens=0`・`text=finalizedText`にフォールバック)だが、実測不能な仮説ケースであることを
  ここに明記する。
- **複数の独立したassistantイベントにまたがるinterleaved thinking**
  (thinking→tool_use→thinkingのように、別々の`assistant`/`tool_use`イベントとして届く
  実際の形): `target.blocks`に確定済み非ストリーミングブロック(前段のThinking確定/
  ToolCallカード追加)が挟まるたびに、`ExtractTrailingThinkingTails`は「末尾から
  streaming=trueが続く区間」だけを取り出すため、直前のツールコールカードで区切られた
  **新しいthinkingセグメントだけ**を正しく抽出・確定できる。各`OnAssistantMessageCompleted`
  呼び出しがその時点の末尾ストリーミング区間だけを対象にするため、複数回にまたがる
  interleaved thinkingは**正しく処理される**(バグなし、追加対応不要)。回帰テスト:
  `ThinkingDisplayHubTests.RedactedThinkingBlock_DoesNotDisturbAnUnrelatedPendingThinkingTail`
  が、末尾の非redacted thinkingタイルとその後に届く別種のブロック(本ラウンドでは
  redacted_thinking)が干渉しないことを確認している(同じ抽出ロジックを使うため
  interleaved thinking一般の安全性の傍証にもなる)。
- **`signature_delta`**: `StreamEventMessage.TryGetTextDelta`/`TryGetThinkingDelta`は
  `delta.type`が`"text_delta"`/`"thinking_delta"`と一致する場合のみ値を返し、それ以外
  (`signature_delta`含む)は`false`を返すだけで例外もフィールド書き込みも起きない
  (`JsonNode`のインデクサは存在しないキーに対して安全なnullノードを返す実装のため)。
  **無視は安全**(コード変更不要、確認のみ)。
- **thinking_tokens指標(`system/thinking_tokens`)**: `OnThinkingTokensReceived`は
  `EnsureStreamingBlock(ChatBlockKind.Thinking)`経由で独立に動作し、今回追加した
  `redacted_thinking`処理(直接`target.Add`で完成ブロックを追加するだけの経路)とは
  一切交差しない。影響なし。

## 6. redacted_thinking ブロックの追加(2026-08-01)

- **仕様**: モデルが安全上の理由でフラグした思考は、テキストの代わりに暗号化された
  `data`フィールドのみを持つ`redacted_thinking`ブロックとして届く
  (`thinking_delta`を経由せず、確定assistantメッセージに直接含まれる)。
- **プロトコル層**: `ContentBlockType.RedactedThinking`を追加し、
  `ContentBlock.FromJson`の`"redacted_thinking"`caseでマッピング
  (`Editor/Core/Protocol/AssistantMessage.cs`)。表示に使える本文が無いため
  `Thinking`フィールドには何も入れない(`Raw`に生ノードは既存の全ブロック共通の仕組みで
  保持される)。
- **表示モデル**: 新しい`ChatBlockKind`は追加せず、既存の`Thinking`kindを再利用し
  `ChatMessageBlock.thinkingRedacted`フラグ(bool、既定false)を追加する最小侵襲な設計
  (`Editor/Model/ChatMessage.cs`)。理由: `showThinking`設定によるゲート、
  `SessionCacheFile`のシリアライズ、`MessageListController.ComputeSignature`の
  `kind`ベースの分岐など、`ChatBlockKind`で分岐する既存コードが変更不要のまま動く。
  `MessageBlockFactory`だけがこのフラグを見て表示を分岐すればよい。
- **AgentHub**: `OnAssistantMessageCompleted`/`OnSubagentAssistantMessageCompleted`の
  ContentBlockType switchに`RedactedThinking`caseを追加し、
  `ChatMessageBlock.MakeRedactedThinking()`を直接追加する
  (thinking_deltaのストリーミング尾部とは無関係に、確定ブロックとして完成した形で届くため
  §2/§5のtail-merge機構とは交差しない)。
- **永続化**: `SessionCacheFile`に`thinkingTokens`と同じ「常に書く・0/falseは
  absentと区別しない」規約で`thinkingRedacted`を追加(後方互換: 旧キャッシュはfalseで
  読み込まれる)。
- **表示**: `MessageBlockFactory.CreateThinkingBlock`が`block.thinkingRedacted`を
  最優先でチェックし、既存のthinking-indicator書式(コンパクトな1行ラベル、
  `uap-thinking-indicator`クラス)を再利用して
  「Some thinking was redacted for safety. / 一部の思考は安全のため非表示です。」を表示する
  (フォルドアウトは使わない -- 展開できる本文が存在しないため)。`showThinking`設定に
  従って(Thinking kindなので)非表示にもできる。
- **L10n**: `UiStrings.ChatThinkingRedactedNote`(en)/
  `UiStringsJa`の`chatThinkingRedactedNote`(ja)を末尾に追加(このラウンドの
  マージ安全規約に従い、フィールド一覧・コンストラクタ引数リスト・Ja Create()呼び出しの
  いずれも末尾に追記)。
- **テスト**: `ProtocolMappingTests.Assistant_RedactedThinkingBlock_MapsToRedactedThinkingType`
  (合成行によるマッピング)、`MessageBlockFactoryThinkingTests.RedactedThinking_*`
  (表示・showThinkingゲート)、`SessionCacheFileTests.RoundTrip_RedactedThinking_Preserved`
  /`Load_BlockWithoutThinkingRedactedKey_BackwardCompatible`(永続化)、
  `ThinkingDisplayHubTests.RedactedThinkingBlock_*`(AgentHub経由のエンドツーエンド、
  および無関係なthinkingタイルとの非干渉)を追加。
- **実フィクスチャなし**: 2026-08-01時点でredacted_thinkingを含む実キャプチャは
  存在しない(安全フラグが立つ入力を意図的に再現するのは困難)。上記テストはすべて
  API仕様どおりの形を模した合成行に基づく。実フィクスチャが将来手に入り次第、
  `ProtocolMappingTests.EveryFixtureLine_DispatchesToTypedMessage_NothingDropped`が
  自動的にカバーする。

## 7. 実装: 既存トグルへの結線(2026-08-01)

§4c末尾で挙げたスコープ外理由(担当ストリームのファイル所有範囲の都合)が解消
したため、実装した。

- **決定: 新規トグルは追加しない**。既存の「思考ブロックを表示」設定
  (`PanelSettings.showThinking`, Display セクション)をそのまま流用し、
  ON のときだけ起動引数に `--thinking-display summarized` を足す。OFF なら
  フラグ自体を省略する(§4cの実測どおり、`--thinking` 未指定でもデフォルトの
  thinking type が "adaptive" のため単独指定で足りる)。二重トグルにしない
  理由: ユーザーから見て「思考を表示する/しない」は一つの意思決定であり、
  「本文まで見るか」を別スイッチにする実用上の意味がない。
- **AgentClientOptions**(`Editor/Core/Client/AgentClient.cs`): 新フィールド
  `ThinkingDisplaySummarized`(bool, 既定false)を追加し、`BuildArguments`が
  true のときだけ `--append-system-prompt` の後・
  `--dangerously-skip-permissions` の前に `--thinking-display summarized`
  を追記する。既定false時は既存の引数文字列とバイト完全一致(他の
  next-spawn-onlyフィールドと同じ回帰パターン、
  `AgentClientOptionsExtensionTests`で保証)。
- **AgentHub.StartClient**(~993-1004行目): `AgentClientOptions`組み立てに
  `ThinkingDisplaySummarized = settings.showThinking` を追加。
  `CloneNextSpawnOnlyFields`にも`showThinking`をコピーする1行を追加し、
  `LastSpawnedSettingsSnapshot`(SettingsChangeDetectorの比較対象)に
  反映されるようにした。
- **SettingsChangeDetector.RequiresReconnect**(2引数版):
  `spawnedWith.showThinking != current.showThinking` を比較条件に追加。
  showThinking は本ラウンドから「再接続が必要な設定」の一員になる
  (可視性の反映自体は従来どおり即時 -- `MessageBlockFactory.SettingsGeneration`
  のインクリメントで現在のトランスクリプトに即反映されるのは変わらない。
  再接続が要るのは「新しく届く thinking ブロックが本文を持つかどうか」の方)。
- **SettingsView.OnShowThinkingChanged**: 他のnext-spawn-onlyフィールドの
  ハンドラ(`OnAllowedToolsChanged`等)と同じパターンで
  `RefreshReconnectHint()` + `AgentHub.RequestAutoApplyReconnect()` を追加。
  既存の設定自動適用の仕組み(0.6.0, docs/design-notes/2026-08-01-settings-
  auto-apply.md)にそのまま乗るため、トグルを切り替えるとデバウンス後に
  自動で再接続される(手動の「Reconnect now」クリックは不要)。
- **L10n**: `SettingsShowThinkingHint`(既存フィールドの文言を差し替え、
  新規フィールドは追加していない)。旧文言「現在のトランスクリプトにすぐに
  適用されます("Applies immediately to the current transcript.")」は
  可視性の話としては正しいが、本文が届くかどうかは再接続に依存するという
  新事実を反映していなかったため、「可視性は即時反映/本文表示には
  Claude Code CLI v2.1.218以降が必要でクイック自動再接続が発生する」旨に
  更新した(en/ja両カタログ)。
- **失敗モード**: v2.1.218より古いCLIは`--thinking-display`フラグ自体を
  知らないため、トグルONで再接続すると起動直後にCLIプロセスが引数エラーで
  即終了する可能性が高い -- 通常のCLI起動失敗経路(`HubCliStartFailedFmt`/
  エラーバナー)がそのまま拾う想定(README既に「Claude Code CLI v2.1.218
  以降」を要件として明記済みのため、新規の警告UIは追加しない)。
- **テスト**: `AgentClientOptionsExtensionTests`(フラグ有無・既定値の
  バイト完全一致・他フラグとの順序)、`SettingsChangeDetectorTests`
  (showThinking差分で`RequiresReconnect`がtrueになること)、
  `L10nTests`(構造変更なしのため既存の非空/プレースホルダ整合性チェックが
  そのまま担保)。
