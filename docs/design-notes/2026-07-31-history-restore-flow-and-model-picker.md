# 設計ノート: History ビューの復元/再接続フローとヘッダーのモデルピッカー

- 日付: 2026-07-31 / ステータス: 採用済み
- 関連: ARCHITECTURE.md Phase 3、`docs/design-notes/2026-07-31-session-history-restore.md`(Model 層: SessionIndex/TranscriptLoader 自体の設計)、`docs/design-notes/2026-07-31-settings-propagation.md`(model フィールドの反映方式表 -- 本ノートが更新する)
- 対象: `Editor/UI/HistoryView.cs`(新規)、`Editor/UI/HeaderView.cs`、`Editor/UI/StatusBarView.cs`、`Editor/Integration/AgentHub.cs`(`SwitchToSession` 追加)

前提となる SessionIndex/TranscriptLoader 自体のマッピング仕様は上記の姉妹ノートに
既にある。本ノートはその上に乗る **UI/Integration 層**(セッション切替のライフ
サイクル、ctx メータの実データ選択、モデルピッカーの二重反映)を扱う。

## 1. History 選択 → 復元 → 再接続のフロー

### 1.1 なぜ「セッション切替」を新しい `AgentHub` API にしたか

既存の `AgentHub` には `StartFresh`(新規セッションを**破棄して**作る)と
`EnsureStarted`/`RestoreAfterReload`(**同じ** `Session.sessionId` で再接続する)
の2系統しかない。History から別セッションを選ぶ操作はどちらとも異なる第三の
ケース: 「今のセッションを破棄はしないが、**別の既存セッション**に丸ごと
差し替えて `--resume <その別のid>` で繋ぐ」。既存2メソッドを流用/オーバーロード
すると呼び出し側の意図が読み取れなくなる(`StartFresh` は「新規」を強く含意し、
`EnsureStarted` は「今の `Session` のまま」を前提にしている)ため、
`AgentHub.SwitchToSession(string sessionId, List<ChatMessage> restoredMessages,
string titleHint)` を新設した。

シグネチャの決定: **ファイルパス解決や `TranscriptLoader.Load` の呼び出しは
`HistoryView` 側が行い、`AgentHub` には結果の `List<ChatMessage>` を渡す**(逆に
`AgentHub` が `SessionIndex`/ファイルパスを引数に取ってロードする案は不採用)。
理由: `SessionIndexEntry` は既に `HistoryView` の一覧描画のために `FilePath` /
`FirstUserTextPreview` を保持しており、二重にパス解決ロジックを持たせたくない。
`AgentHub` はプロセスのライフサイクル(kill+spawn)だけに専念させる方が既存の
`StartClient`/`TearDownClient` の役割分担(D9 が求める層分離)と一致する。

### 1.2 `SwitchToSession` の中身(既存パターンの踏襲)

```
TearDownClient(true)                    // 今のプロセスを完全に畳む(kill+ゾンビ記録クリア)
_session = 新しい ChatSession {
    sessionId = 対象の id,
    messages  = 呼び出し側が渡した復元済みメッセージ,
    title     = titleHint(空なら最初の user メッセージの PlainText から派生)
}
SessionCache.Save(_session)
SessionStateBridge.CurrentSessionId = 対象の id
_resumedMidTurn = false
StartClient(対象の id)                  // --resume <id> で新プロセスを起動
```

`StartClient` は既存コード(`EnsureStarted`/`StartFresh` と共有)をそのまま
呼ぶだけなので、CLI 起動引数の組み立てや `_lastSpawnedSettingsSnapshot` の更新、
`ZombieReaper` の再登録など既存の安全策は無条件に効く。

### 1.3 進行中のターンはどうなるか

`TearDownClient(true)` → `client.Stop(graceMillis)` は既存の `Stop()` 実装が
そのまま使う経路で、ターンが動いていれば **`interrupt` を送ってから stdin を
閉じる**(`AgentClient.Stop`)。つまり History 切替は「今の会話を中断してから
捨てる」動作になり、ARCHITECTURE.md D2 の中断パスと同一で新規コードを要しない。
生成中だった応答はそのまま失われる(ドメインリロードの mid-turn ナッジのような
「続きを実行」導線は無い -- 別セッションに移る操作自体が「この会話は一旦
終わり」という明示的なユーザー意図なので、Continue ボタンは意味を持たない)。

**確認バー(confirm bar)はこの破壊的操作をガードする UI 側の責務**として
`HistoryView` に実装する(`AgentHub` 側にガードは持たせない -- 単体で
`SwitchToSession` を呼べば常に即座に切り替わる、というシンプルな契約を保つ):

- 選択行が **今開いているセッションと同じ id** → 確認なしで即 Chat ビューへ戻る
  だけ(`AgentPanelWindow.ShowChat()`)。再接続もメッセージ差し替えも行わない
  (下記 1.4 の二重再開ガードと表裏)。
- 現在のセッションに **1件以上メッセージがある、またはターンが進行中** →
  行内に確認バーを展開し「この会話を離れて履歴のセッションに切り替えますか?」
  + [切り替える]/[キャンセル] を出す。
- 現在のセッションが空(メッセージ0件、ターンなし)→ 確認なしで即切り替え
  (新規パネルを開いた直後に履歴を覗いただけのケースを配慮)。

### 1.4 二重再開(double-resume)ガード

`SwitchToSession` 自身は「同じ id へもう一度切り替える」呼び出しを弾かない
(呼べば毎回 kill+spawn が起きてしまう)。ガードは **呼び出し側**
(`HistoryView` の行クリックハンドラ)に置く: `entry.SessionId ==
AgentHub.Session.sessionId` なら `SwitchToSession` を呼ばずに `ShowChat()`
だけ行う。これは 1.3 の確認バー分岐と同じ条件式を使うので、実装上は
「同一セッション判定」を一箇所の private ヘルパーに切り出し、確認バー要否と
二重再開スキップの両方から参照する(条件がズレると「同じセッションなのに
確認バーが出る」/「別セッションなのに黙って何もしない」という不整合が起きうる
ため)。

### 1.5 ファイルが消えている競合(missing-file race)

`SessionIndex.Refresh` が一覧を作ってから実際にクリックされるまでの間に、
対象の `.jsonl` が削除される(他プロセスでの `claude project purge` 実行、
ディスク整理など)可能性がある。`TranscriptLoader.Load` は既に「ファイルが
存在しない/読めない」場合に**例外を投げず空リストを返す**契約なので
(姉妹ノート §5)、`HistoryView` はこのケースでも普通に `SwitchToSession` を
呼び、結果は「空の会話で `--resume <消えたid>` を試す」になる。

CLI 側の挙動は未検証だが、想定される結末は次のいずれかで、**どちらも
既存のエラーパスがそのまま拾う**:
- CLI が resume 失敗を `assistant.error`/exit code で返す →
  `AgentClient` の initialize タイムアウトまたは通常の `HandleProcessDeath`
  経路に乗り、`AgentHub.OnProcessDied` の**既存の**バウンデッド自動再接続
  (最大3回、`_consecutiveDeaths`)とエラーバナーがそのまま機能する。
- CLI が黙って新規セッション(同じ id で空の会話)を作る → 復元メッセージが
  空のまま新しい会話が始まるだけで、パネルがクラッシュすることはない。

どちらの結末でも新規コードは不要と判断し、**既知のリスクとして受容**する
(このノートに明記する以上の対策は今回のスコープ外)。

## 2. ステータスバーの ctx メータ: `result.modelUsage` の実データ確認

R02/ARCHITECTURE.md は `modelUsage` を「モデル別内訳、実測では空 `{}`」としか
書いていなかった(認証エラー時のキャプチャしか無かったため)。今回
`Tests/Editor/Fixtures/success_bidi_inbound.jsonl` 等の**認証成功後**の実
キャプチャ(02b 由来)を確認したところ、**1回のターンで複数モデルのエントリが
同時に載る**ことが分かった(実データ、`success_bidi_inbound.jsonl` の result 行):

```json
"modelUsage": {
  "claude-haiku-4-5-20251001": { "inputTokens": 558, "outputTokens": 14, "...": "...",
    "costUSD": 0.000628, "contextWindow": 200000, "maxOutputTokens": 32000 },
  "claude-opus-5[1m]": { "inputTokens": 4, "outputTokens": 102, "...": "...",
    "cacheReadInputTokens": 49893, "cacheCreationInputTokens": 5899,
    "costUSD": 0.0865065, "contextWindow": 1000000, "maxOutputTokens": 64000 }
}
```

これは haiku がタイトル生成などの補助タスク(`ai-title` 行が姉妹ノート §2 で
既に観測されている)に使われ、opus/sonnet が本会話のモデルという構成である。
**ctx メータは「今使っている会話モデル」の contextWindow に対する使用率を
見せたいので、キーが複数あるとき単純に最初のエントリや合計を使うと誤った
数字になる**(例: haiku の 200,000 を分母にすると、実際は opus の 1,000,000
枠で 3% しか使っていないのに 34% のように見えてしまう)。

**決定**: `StatusBarView.SelectPrimaryModelUsage(modelUsage, currentResolvedModel)`
という純粋関数を追加し、
1. `currentResolvedModel`(`AgentClient.InitMessage.Model`、例
   `"claude-opus-4-8[1m]"`)と**完全一致**するキーがあればそれを使う。
2. 一致しなければ、`ComputeUsedTokens`(input+output+cache 全4フィールドの
   合計)が最大のエントリを使う。
3. `modelUsage` が null/空なら null を返し、呼び出し側はメータ自体を
   非表示にする(0% ではなく「データなし」の区別。ARCHITECTURE.md リスク16
   の「サブスク認証でコストが無意味なら…」と同じ考え方をトークンにも適用)。

**実装時の訂正(実データで踏んだ地雷)**: 当初は 2. の判定基準を
`inputTokens+outputTokens` の合計だけにしていた(「補助モデルはトークン数が
小さいはず」という直感)。ところが実フィクスチャ
(`success_bidi_inbound.jsonl`)の実数値でテストを書いたところ
**この直感は逆だった**: 本会話モデル(opus)はキャッシュがよく効いた状態
(`inputTokens:4, outputTokens:102` だが `cacheReadInputTokens:49893,
cacheCreationInputTokens:5899`)で、生の input+output だけ見ると **106**。
一方タイトル生成の補助モデル(haiku)は要約対象の会話全文を毎回渡すため
`inputTokens:558, outputTokens:14` で input+output は **572** と、
本会話モデルより大きい。`StatusBarViewLogicTests`(このラウンドで新規作成)を
実際にこの実データで実行したところ判定が逆転する回帰が即座に検出され、
判定基準を「input+output のみ」から「`ComputeUsedTokens`(4フィールド合計)」
に修正した。修正後は opus の合計 55,898 が haiku の 572 を上回り、正しく
本会話モデル側を選ぶ。**ctx メータの分母選定ロジックは、実キャプチャ値を
使ったテストなしには直感だけで正しく書けない**という教訓をここに残す。

「使用量」の定義も同様に確定させる必要があった: `ModelUsage` は
`inputTokens`/`outputTokens`/`cacheReadInputTokens`/`cacheCreationInputTokens`
の4本を持つ。ctx window は次のターンに送り直される総コンテキスト(過去の
input・output・キャッシュ全部)を表すので、**4本の合計**を「使用トークン数」
として `contextWindow` に対する百分率を出す
(`StatusBarView.ComputeContextPercent`)。`contextWindow <= 0` なら 0 を返し
呼び出し側は非表示にする(0除算ガード、かつ「未知のモデルで contextWindow が
省略された」ケースを黙って 0% と誤表示しないための区別)。

80% 以上で警告色(`--uap-status-warn`、既存トークン。R05 §2.6 の「まもなく
自動 /compact されます」tooltip も踏襲)。

## 3. モデルピッカー: ライブ反映と永続化の両方

`docs/design-notes/2026-07-31-settings-propagation.md` の反映方式表は
「`model` は次回接続時のみ、モデルピッカーは別エンジニアの担当範囲」と
書いていたが、実装してみると `AgentClient.SetModel`(`set_model`
control_request)が**既に**存在し、D2/R02 §5.4 が「実行中セッションのモデル
切替」を仕様として明記していたため、**ライブ反映を採用しない理由が無かった**。
よって本ノートで反映方式表を更新する:

| 設定 | 反映方式(更新後) |
|---|---|
| `model` | **即時**(接続中なら `AgentClient.SetModel` で `set_model` control_request を送信)**かつ** `PanelSettings.model` に永続化(次回起動時の `--model` にも反映)。両方を毎回同時に行う -- 「ライブは変わったが次回起動時は元に戻る」という不整合を避けるため |

両方を1箇所にまとめるため `AgentHub.SwitchModel(string model)` を新設した
(`Reconnect()` と同じ「1個の公開エントリポイントに副作用をまとめる」方針)。
ここで**もう一つ副作用がある**ことが実装中に判明した:
`SettingsChangeDetector.RequiresReconnect` は `LastSpawnedSettingsSnapshot.model`
と現在の `PanelSettings.model` を比較して Settings 画面に「次回接続時に反映」
バナーを出す(`2026-07-31-settings-propagation.md`)。モデルピッカーで
ライブ切替した瞬間、`PanelSettings.model` だけ更新して `LastSpawnedSettingsSnapshot`
を放置すると、**既にライブ反映済みなのに** 誤って「reconnect が必要」バナーが
出てしまう(スナップショットは「最後に spawn した時の model」のままなので、
ピッカーで変えた瞬間に両者が食い違う)。対策として `SwitchModel` は
`PanelSettings.model` を書き換えると同時に `LastSpawnedSettingsSnapshot.model`
も書き換える(スナップショットが null -- まだ一度も spawn していない -- の
場合は何もしない。次の `StartClient` が新しいスナップショットを作るため)。
これにより「モデルピッカーで切り替えた直後は reconnect ヒントが出ない」
「CLI パスなど本当に次回接続待ちの変更が別にあれば、それは引き続き正しく
検知される」の両方が成り立つ。

ヘッダーのモデルピッカーはボタン+`GenericMenu`(`PermissionCard` の
「常に許可 ▾」で既に使われているパターンを踏襲。ネイティブメニューなので
新規のドロップダウン用 USS/レイアウトを増やさずに済む)。選択肢は
`ControlResponseMessage`(`initialize` の応答)の `response.models[]` から
`HeaderView.ParseModels` で純粋パースする(`out_bidi.jsonl` フィクスチャで
実データ確認済み: `value`/`resolvedModel`/`displayName`/`description` の4
フィールドが常に存在し、`supportsEffort` 等は任意)。表示ラベルは
`displayName`(狭幅では省略しない -- メニュー自体は幅を気にしなくてよい
ネイティブ UI なので、R05 §2.2 の「狭幅ではサブメニューに畳む」は不要になった)。

**Streaming 中は無効化**(タスク仕様どおり)。ToolRunning/WaitingPermission
中は無効化しない: CLI 側の `set_model` はいつでも受理される制御リクエストで
あり(R02 §5.4 に排他条件の記載なし)、次のターンから有効になるだけなので
ツール実行中に送っても壊れない。Streaming だけ止める理由は「今まさに文章が
出ている最中にモデル名の下のラベルが変わる」という見た目の混乱を避ける
UX 上の配慮であり、プロトコル上の制約ではない。

現在選択中モデルのハイライトは `client.InitMessage.Model`(resolvedModel)と
各選択肢の `ResolvedModel` を比較する。クライアント未接続時は
`PanelSettings.model` の生値(alias、例 `"sonnet"`)をそのまま表示する
フォールバックとし、`StatusBarView.ResolveModelName` の既存ロジックと矛盾
しないようにする(同じ「接続中は InitMessage、それ以外は設定値」の優先順位)。

> **訂正(2026-07-31、別ノート)**: 上記は `set_model` がまだライブ反映の
> UI 経路を持たなかった時点の記述で、**実装後に defect として判明した**:
> `SwitchModel` がライブ切替に成功しても `InitMessage.Model` は
> `system/init` でしか更新されないため、ピッカーのラベル/チェックマークと
> ctx メータが次の再接続まで古いモデルを表示し続けていた。
> `docs/design-notes/2026-07-31-live-model-tracking.md` で
> `AgentClient.CurrentModel`(ライブ切替を反映する新プロパティ)を導入し、
> この節と `StatusBarView.RefreshContextMeter` の両方を `InitMessage.Model`
> 直読みから `CurrentModel` 読みに置き換えた。今後「現在のモデル」を
> 参照する箇所は `CurrentModel` を使うこと。

## 4. 検討したが採らなかった案

| 案 | 却下理由 |
|---|---|
| ctx メータの分母に「全モデルの contextWindow の合計」を使う | 意味を持たない数字になる(別々のモデルの枠を足しても実際の制限にならない) |
| History の確認バーを `AgentHub` 内のフラグで実装 | `AgentHub` の他の全メソッドが「呼べば即実行」の単純契約を保っているのに、この1メソッドだけ内部で保留状態を持つと、UI 側の再入力(連打)やテストの組み立てが複雑になる。確認は UI の責務として `HistoryView` に閉じ込めた |
| モデルピッカーを `ToolbarMenu`(UI Toolkit 標準)で実装 | 2022.3 の `ToolbarMenu` はメニュー項目の動的生成 API が薄く、`GenericMenu` の方が「現在選択中にチェックマーク」等の表現がしやすい。既存コード(`PermissionCard`)で実績のあるパターンに合わせた |

## 5. 検証方法(このラウンド)

このマシンに実際に Unity 2022.3.22f1 と AITemp サンドボックス(本パッケージが
symlink 済み)が存在したため、先行エンジニアの手法(Mono 版 `csc.exe` + 実
`nunit.framework.dll` によるリフレクション実行)をそのまま踏襲し、**さらに
実行結果まで確認した**:

1. AITemp の `Library/Bee/artifacts/.../Colloid.AgentPanel.Editor{,.Tests}.rsp`
   (Unity が生成した本物の csc 引数一式)を土台に、新規/未収録ファイル
   (`HistoryView.cs` および先行ラウンドで rsp 生成後に追加された
   `SessionIndex.cs`/`TranscriptLoader.cs`/`SettingsView.cs` 等6本)を追記して
   Unity 本体の `Data/DotNetSdkRoslyn/csc.dll`(`dotnet` 経由)でコンパイル
   -> **両アセンブリともエラー0件**。
2. Unity 同梱の `mono.exe` + 実 `UnityEngine/UnityEditor` DLL 一式 + 実
   `nunit.framework.dll` を使い、`[Test]`/`[TestCase]`/`[SetUp]` 属性を
   リフレクションで拾って実行する簡易ランナーを新規に書いて実行 ->
   **328 件成功 / 2 件失敗 / 22 件エラー**。失敗・エラーの全件は
   `ContextUiRegressionTests` / `FontLoaderTests` / `MarkdownConverterTests` /
   `MarkdownTableTests`(いずれも本ラウンド対象外の既存ファイル)で、原因は
   `UnityEngine.UIElements.VisualElement` / `UnityEditor.EditorGUIUtility` の
   静的初期化子や OS フォント列挙 icall が**実エディタプロセス外では解決できない**
   という環境依存の既知の制約であり、本ラウンドの変更とは無関係(History/
   Header/StatusBar/AgentHub に触れるテストは**全件成功**)。
3. この実行で `StatusBarViewLogicTests` が実際に2件の回帰を検出し、§2 に
   記載した `SelectPrimaryModelUsage` の判定基準バグ(input+output のみ
   -> `ComputeUsedTokens` 4フィールド合計)を本ラウンド中に修正できた
   (直感だけでは気づけなかった実データ起因のバグ)。

**申し送り事項**:
- `Editor/UI/HistoryView.cs` の `.meta` はこのセッションでは生成していない
  (GUID は実 Unity インポート由来である必要があるため、手で捏造しない方針
  -- 次の `uloop compile` または Editor 起動時に自動生成される)。
- このマシンには既に **稼働中の `Unity.exe` プロセス(PID 12512)** が存在した。
  どのプロジェクトを開いているか(AITemp か、触ってはいけない
  `DevelopmentProject` か)を安全に確認する手段がこのサブエージェントに無かった
  ため、バッチモード起動や `uloop` 経由の実 EditMode テスト実行(2 の
  リフレクション実行の上位互換になるはずだった)は**意図的に見送った**。
  次のラウンドで実際の Unity Editor 上から
  `uloop run-tests --test-mode EditMode` を実行し、上記2の22件のエラーが
  実エディタ内では解消することを確認するのが望ましい。
- `StatusBarView.UsagePopover`(`ShowAsDropDown` の小さな内訳ポップアップ)は
  `EditorWindow.rootVisualElement` への直接構築という他コンポーネントに前例の
  ある手法だが、実エディタでの目視確認はできていない。
