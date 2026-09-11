# Phase 5 設計書: Unity操作特化機能

日付: 2026-08-01 / ユーザー要望「コード生成→コンパイルでエージェントが止まる問題を解消し、
Unity操作に特化した機能群を設計せよ」
根拠リサーチ: [R08 MCP接続実測](../research/08-mcp-transport.md) /
[R09 エディタAPI棚卸し](../research/09-editor-api-surface.md) /
[R10 uLoop・エコシステム](../research/10-uloop-and-ecosystem.md) /
[R11 コンパイル抑制](../research/11-compile-batching.md)

## 0. アーキテクチャ全景 — 3層の操作手段

エージェントの Unity 操作を「目的に応じた3層」に分離し、**既定経路をコンパイルゼロにする**。

```
┌ L1: 型付き操作ツール(パネル内蔵MCPサーバ「UapOps」) ← 既定経路。コンパイルゼロ・Undo統合・許可カード統合
├ L2: uloop CLI 連携(Bashツール経由)                  ← L1で表現できない任意操作の脱出ハッチ
└ L3: 実コード生成(.cs)+コンパイルバッチング          ← 成果物がコードの場合のみ。回数最小化+跨ぎ生存
```

選定原理: エージェントには「一番安全で速い道」をツールとして見せれば、モデルは自然にそれを選ぶ。
カスタム指示に層の使い分け規範を1段落注入して補強する。

## 1. L1: UapOps — パネル内蔵 MCP サーバ

### 1.1 トランスポートとライフサイクル(R08 実測に基づく)

- **Streamable HTTP / loopback**(`http://127.0.0.1:<動的ポート>/mcp`)+ **Bearerトークン**
  (起動毎生成、`--mcp-config` の `headers` で受け渡し — R08 §1 実測受理形)。
  loopback限定+トークンで他プロセスからの叩き込みを遮断。
- サーバはエディタ内常駐(HttpListener + 専用スレッド受信 → メインスレッドキュー →
  EditorApplication.update ディスパッチ。既存 LineChannel/EditorUpdatePump イディオム踏襲)。
- **ドメインリロード対策(R08 §3 の非対称性に対応した二段構え)**:
  1. ReloadLifecycle の復帰シーケンスを「サーバ listen 開始 **→** CLI spawn」に順序付け
  2. `system/init.mcp_servers[].status == "failed"` を監視し、サーバ起動完了時に
     **`mcp_reconnect` 制御リクエスト**(R08 §3.4 で発見・実測済み)を送って復旧
- MCP設定はセッションに保存されないため **spawn毎に `--mcp-config` を渡し直す**(R08 §0-3)。

### 1.2 ツールカタログ v1(全て R09 で実在確認済みのエディタAPIのみ使用)

命名は `<域>_<動詞>`。**読み取り系(query)と書き込み系を明確に分離**(許可既定が異なる)。

| ツール | 主API(R09) | Undo |
|---|---|---|
| `scene_create_object` | ObjectFactory.CreateGameObject/CreatePrimitive(親子付け・Transform初期値) | RegisterCreatedObjectUndo(自動) |
| `scene_place_asset` | PrefabUtility.InstantiatePrefab(プレハブ接続維持)/ 非プレハブはInstantiate | RegisterCreatedObjectUndo |
| `scene_reparent/rename/destroy` | Undo.SetTransformParent / Undo.DestroyObjectImmediate | 各専用Undo API |
| `component_add/remove` | Undo.AddComponent / DestroyObjectImmediate | 自動 |
| `component_set_property` | **SerializedObject/SerializedProperty**(任意コンポーネント・配列・ObjectReference対応。propertyPath指定) | RecordObject→ApplyModifiedProperties |
| `asset_create` | AssetDatabase.CreateAsset(Material/ScriptableObject/AnimationClip/AnimatorController...)+GenerateUniqueAssetPath | 作成はUndo対象外(削除ツールで対称性確保) |
| `asset_set_property` | 同SerializedProperty経路+AssetImporter/ModelImporter(SaveAndReimport) | RecordObject |
| `material_set` | Material.Set*/shaderプロパティ列挙(ShaderUtil) — liltoon等の実務対応 | RecordObject |
| `prefab_create/apply_overrides` | PrefabUtility.SaveAsPrefabAsset(AndConnect)/ApplyPrefabInstance | 専用API |
| `anim_create_clip` | AnimationClip+AnimationCurve+AnimationUtility.SetEditorCurve(ObjectReferenceカーブ含む) | — |
| `animator_edit` | AnimatorControllerのstate/transition/parameter/layer/BlendTree操作 | — |
| `rig_setup`(IK) | Animation Riggingのコンポーネント群はcomponent_add+set_propertyの合成で表現(専用ツールはヘルパー) | 自動 |
| `avatar_configure` | ModelImporterのHumanoidマッピング | Importer経由 |
| `query_hierarchy/components/properties/assets` | 階層ダンプ・コンポーネント一覧・SerializedProperty列挙・AssetDatabase検索 | 読み取り専用 |
| `editor_screenshot` | 既存スモークで実証済みのキャプチャ経路(SceneView/GameView) | 読み取り専用 |
| `editor_undo` | Undo.PerformUndo / RevertAllInCurrentGroup | — |

**明示的な境界(R09)**: 新しいコンポーネント**型**の定義だけは実コード(.cs)が必要 = L3 の領分。
ツール説明文にこの境界を明記し、モデルが L1 で不可能なことを試行錯誤しないようにする。

### 1.3 安全設計

- **許可統合**: MCPツールは `mcp__uap__<tool>` として既存の can_use_tool → 許可カードに乗る
  (R08 §2.4 実測)。既定ポリシー: query系=autoAllow(allowedToolsプリセット)、
  書き込み系=カード確認(Always許可可)、destroy/playmode系=常時確認。
- **ターン=Undoグループ**: ターン開始で Undo.IncrementCurrentGroup、終了で CollapseUndoOperations。
  → チャットの1ターン分の操作を **Ctrl+Z 一発 / パネルの「このターンを巻き戻す」ボタン**で戻せる。
  コード実行方式では原理的に得られない安全性であり、本設計の核心的差別化。
- ツール実行は全てメインスレッド(ディスパッチキュー経由)・try/catch でエラーを MCP エラーとして返す
  (エディタを巻き込まない)。

### 1.4 ToolSearch 前提(R08 §2.3 新知見)

現行CLIはMCPツールも遅延ロード(ToolSearch経由)する。ツール数を増やしてもコンテキスト圧迫は
限定的だが、**description の1行目に発見性の高い動詞・名詞を置く**(ToolSearchの検索対象)。

## 2. L2: uloop CLI 連携(R10 実測: uLoopMCPは現在「CLIバイナリ+Named Pipe」でMIT)

MCPクライアント接続は**不要**(R10 §3.1)。連携は3点で完結:

1. **検出と状態表示**: パッケージ(io.github.hatayama.uloopmcp)の導入有無を検出し、
   設定画面「連携」セクションに状態表示+未導入なら導入ガイド(openUPMレジストリ追加は
   manifest.json 編集を要するため、ワンクリック追加は**ユーザー確認カードを挟んで** Client.Add /
   manifest 編集で実施)。
2. **allowedTools プリセット**: `Bash(uloop *)` の許可プリセット(危険系 `uloop update/sync/launch`
   は除外パターンで明示ブロック — 本プロジェクトの実インシデント教訓)。
3. **カスタム指示スニペット**: 「L1ツールで表現できない操作は `uloop execute-dynamic-code` を使う。
   コードを書いてコンパイルするのは最終手段」という規範の定型文を1クリック挿入。

ベータ依存リスク(R10 §3.1 Cons)は「検出+プリセット」という疎結合に留めることで吸収する
(uLoopのAPIが変わってもパネルのコア機能は無傷)。

### 2.5 簡易導入経路(ユーザー要望 2026-08-01: 手動OpenUPM追加は面倒)

未導入検出時、連携セクションに**「uLoopを導入」ボタン**を出し、ワンクリック導入を提供する:

1. **第一候補: Git URL 経由の `Client.Add`**(公式API・manifest手編集不要)。
   リポジトリ(hatayama/unity-cli-loop)のUPM対応レイアウト(`?path=`要否・タグ形式)を
   実装時に実測確認し、確立したURLを定数化。
2. **フォールバック: manifest.json への scoped registry + 依存追加**(OpenUPM形)。
   パネルのJsonParser/Writerで読み書きし、**適用前に確認カードで正確なdiffを提示**
   (manifest.jsonはユーザー資産。ARCHITECTURE リスク#12「パネル自身のフットプリントは
   manifest非編集」の原則は維持 — これはユーザーが明示ボタンで指示した導入操作であり別枠。
   適用前バックアップ `manifest.json.uap-backup` を残す)。
3. 導入後は Client.Resolve → コンパイル発生を CompileGate が通常どおり吸収(kill+resume)。
   導入完了をパネルが検出したら状態表示を「導入済み・接続可」に更新。

## 3. L3: コンパイルバッチング+跨ぎ生存(R11)

1. **ターン内 Refresh 抑制(設定・既定ON)**: ターン開始で AssetDatabase.DisallowAutoRefresh、
   TurnCompleted で AllowAutoRefresh+Refresh 1回。カウンタ式でリエントラント安全・
   明示Refreshをブロックしない(R11 公式Docs確認済み)。try/finally+リロード時の対越境保護。
   LockReloadAssemblies は**不採用**(ペア崩れ事故が重大 — R11)。
2. **ステージング(設定・既定OFF)**: `Assets/../<pkg>Staging~/` (末尾チルダ=インポート完全除外、
   R11 公式規則)にエージェントが下書き → 「コミット」操作で本配置+1回コンパイル。
   複数ファイルの拡張開発が**コンパイル1回**で済む。
3. **自動継続(設定・既定OFF)**: R11 §4 のガードレール全採用 — ターン最大1回・
   エージェント自身の .cs/.asmdef 書き込み起因と判定できた場合のみ・システムノート必須・
   **resume直後にコンパイル結果ログをコンテキスト注入**(古い前提で継続する事故の対策)。

## 3b. サードパーティ拡張の操作(ユーザー要望 2026-08-01: VRCSDK / MagicaCloth / VRM / FinalIK / RPGMaker Unite 等)

### 前提となる設計上の好条件

L1 の中核である **SerializedObject/SerializedProperty 経路は、任意のサードパーティ
コンポーネント・ScriptableObject にそのまま機能する**(Unityのシリアライズ機構自体が
型非依存のため)。つまり VRCPhysBone のパラメータ調整も MagicaCloth のクロス設定も、
`component_add` + `component_set_property` + `asset_set_property` の既存3ツールで
機構的にはすでに操作可能。不足しているのは次の2つであり、それを補う設計を追加する。

### (1) 型の発見 — `query_component_types` ツール

- TypeCache/アセンブリ走査で「名前(部分一致)→ 利用可能なComponent/ScriptableObject型」を
  検索するツールを L1 に追加(例: "PhysBone" → `VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone`)。
  `component_add` は短い型名・完全修飾名の両方を解決する。
- `query_properties` は既存設計どおり SerializedProperty 列挙でサードパーティ型の
  プロパティ構造をエージェントが自己発見できる(ドキュメント不要で操作可能になる基盤)。

### (2) ドメイン知識 — 拡張プロファイル(Extension Profiles)

SDKごとの「作法」(セットアップ手順・命名規約・典型ワークフロー・危険操作)は
コードではなく**データ(プロファイル)**として提供する:

```
Editor/Resources/Profiles/<sdk>.json(パネル同梱)+ ユーザー定義(UserSettings配下)
{ id, 検出条件(パッケージ名 or 型名の存在), instructionsSnippet(カスタム指示へ自動注入),
  quickActions[](定型プロンプト), assetTemplates[](asset_create用の型・既定値),
  safeInvocations[](→v2、下記) }
```

- **検出は自動**: プロジェクトに VRCSDK/VRM 等を検知したら、該当プロファイルの
  instructionsSnippet を(ユーザー可視のトグル付きで)システムプロンプトに注入。
  エージェントは「このプロジェクトはVRChatアバター。PhysBoneの追加はArmature配下に…」
  といった作法を最初から知っている状態になる。
- 同梱プロファイル v1 候補: **VRCSDK(Avatars)/ VRM(UniVRM)/ MagicaCloth2 / FinalIK**。
  RPGMaker Unite 等は器(ユーザー定義プロファイル+共有可能なJSON)で受ける。
  プロファイルは QuickActionStore と同じ JSON サイドカー機構で編集・追加可能にする。
- **SDK固有のエディタユーティリティ呼び出し**(VRCSDKのビルド/検証、MagicaClothの事前計算等)は
  v1 では L2(uloop execute-dynamic-code)+プロファイル同梱スニペットで賄う。
  任意staticメソッドを叩く汎用ツール(`editor_invoke_static`)は**コード実行と等価の権限**に
  なるため v1 では見送り、v2 でプロファイル宣言型のホワイトリスト方式
  (safeInvocations: 完全修飾メソッド名+引数スキーマ+説明)として再検討する。

### フェーズへの反映

- 5a に `query_component_types` と型名解決を追加(小さい)。
- **5b' として拡張プロファイル基盤+同梱4プロファイル**を追加(5bのアニメーション系と並行可)。
- safeInvocations(宣言型ホワイトリスト)は v2 バックログ。

## 3c. 独自UIを持つ拡張への対応戦略(ユーザー要望 2026-08-01: RPGMaker Unite / PlayMaker / ProBuilder / UModeler / UMotion / Meta XR SDK 等)

「独自のエディタUIが主戦場」の拡張は、**到達手段の階層**で整理する。上の層ほど堅牢で、
プロファイル(§3b)が「このSDKはどの層で攻めるか」のレシピを宣言する。

### 到達手段の4層(堅牢な順)

| 層 | 手段 | 対応ツール(新設) | 効く例(推定は実装時にプロファイル単位で実測) |
|---|---|---|---|
| T1 | シリアライズデータ直接操作(既存L1) | component/asset_set_property | Meta XR(コンポーネント設定)、RPGMaker Unite(SO/データベース資産)、ProBuilderのメッシュデータ(部分) |
| T2 | **メニューコマンド実行** | `editor_execute_menu`(MenuItemパス実行+実行後の状態確認はquery系で) | ほぼ全拡張が何らかのMenuItemを公開(ProBuilderの各種操作、SDKのセットアップウィザード起動等)。UIフレームワーク非依存の最大レバー |
| T3 | 公開C# API呼び出し | v1=L2(uloop+プロファイルスニペット)、v2=safeInvocations | ProBuilder(公開メッシュAPI)、Meta XR(Building Blocks追加API)、PlayMaker(限定的) |
| T4 | **エディタウィンドウUI自動操作** | `editor_ui_*` ファミリ(ウィンドウ列挙/UITKツリーダンプ/要素クリック・値設定/ウィンドウスクリーンショット) | **UI ToolkitベースのウィンドウのみV1対象**(Meta XR Building Blocks=UITK確認済み、RPGMaker UniteもUITK系と推定)。IMGUIウィンドウ(PlayMaker/UModeler/UMotion等の旧来UI)は要素ツリーが存在せず**原理的に選択的操作が困難** — スクリーンショット+座標クリック(SendEvent)は極めて脆く、v1では非対応と明記 |

### 設計上の含意

1. `editor_execute_menu`: EditorApplication.ExecuteMenuItem のラッパー。**常時確認カード**
   (メニューは削除・ビルド等の重い操作も含むため)+プロファイルの quickActions が
   既知の安全なメニューパスを提示する形を推奨経路とする。メニューパスのローカライズ・
   バージョン差はプロファイル側で吸収(データ更新で追随、コード変更不要)。
2. `editor_ui_*`(UITK限定): 本プロジェクトの実機プローブで実証済みの技術
   (rootVisualElementツリー走査・要素操作)の製品化。ダンプ→操作→`editor_screenshot`で
   結果確認、という**視覚フィードバックループ**を標準手順としてツールdescriptionに明記。
   IMGUIウィンドウはツリーダンプ不能を検知して「このウィンドウはUI自動操作非対応
   (T2/T3経路を検討)」と構造的に返す(むやみに試行させない)。
3. **正直な限界の宣言**: UModeler/UMotionのようなIMGUI中心・非公開APIのツールは、
   T2(メニュー)+T3(あれば)+ユーザーへの操作依頼(エージェントが手順を提示して
   ユーザーが実行)が現実解。プロファイルに `uiFramework: imgui` を宣言し、
   エージェントが最初から適切な経路を選ぶようにする。
4. 各同梱プロファイルは「T1で可能な操作一覧/T2の主要メニューパス/T4可否」を
   実測してから出荷する(推測でレシピを書かない — 本プロジェクトの実測第一原則)。

### フェーズへの反映(改)

- `editor_execute_menu` は **5b** に前倒し(実装が小さくレバーが大きい)。
- `editor_ui_*`(UITK自動操作)は **5c** に配置(5cは「uloop連携+L3+UI自動操作」に再定義)。
- 同梱プロファイルの対象に Meta XR SDK を追加検討(T1+T2+T4すべて有効な好例)。

## 4. 追加提案(要件+α、v1採用)

- **クエリ系ツール群**(§1.2): 書き込みの前提となる「見る」手段。ツール数の1/3をここに割く
- **Prefab操作**: Unity実務の中心でありながら要件リストから漏れていた領域
- **マテリアル/シェーダ操作**: アバターワークフロー(liltoon等)の頻出操作
- **ターン単位Undo**(§1.3): エージェント操作の安心感の核
- **editor_screenshot**: エージェントが自分の操作結果を視覚確認して反復する自己修正ループ
- **Playモード制御**(v2候補): enter/exit+状態取得。誤爆リスクが高いため常時確認カード前提で後段

## 5. フェーズ分割と受け入れ基準

| フェーズ | 内容 | リリース |
|---|---|---|
| **5a** | UapOpsサーバ基盤(HTTP+token+mcp_reconnect復旧)+ scene/component/asset/query ツール+許可プリセット+ターンUndo | v0.9.0 |
| **5b** | anim/animator/rig/avatar + material/prefab + editor_screenshot | v0.10.0 |
| **5c** | uloop連携セクション + L3バッチング(Refresh抑制/ステージング/自動継続) | v0.11.0 |

**5a受け入れ基準(抜粋)**: ①「Cubeを3つ横に並べて赤いマテリアルを付けて」が**コンパイルゼロ**で
完了 ②操作全体がCtrl+Z一発で戻る ③ドメインリロード(手動コンパイル)後の再開ターンでツールが
復旧している(mcp_reconnect経路の実機確認) ④query系は許可カードなし・書き込み系はカード表示
⑤全582+テスト維持+新規回帰テスト。

## 6. リスク

| リスク | 対策 |
|---|---|
| ポート衝突 | 動的ポート+失敗時リトライ。Named Pipe移行はv2検討(R10のuLoop方式が先行事例) |
| リロード直後のツール呼び出しレース | R08二段構え(§1.1)+failed検知でツール呼び出し前にreconnect |
| ツール数増によるToolSearchミス | description先頭行の検索性設計+E2Eでツール発見率を確認 |
| VRChat SDK等の重いプロジェクトでのUndo負荷 | RecordObjectは対象Objectに限定、階層一括はRegisterFullObjectHierarchyUndoを慎重適用 |
| セキュリティ(ローカル他プロセス) | loopback限定+Bearerトークン+書き込み系は許可カード必須 |

## 7. 追補(2026-08-02 ユーザーフィードバック4点)

### 7.1 MCPトークン経済 — モジュール分割と遅延読み込み

前提の実測(R08 §2.3): 現行CLIはMCPツールも ToolSearch 遅延読み込みの対象にする。
つまり**スキーマ本体は使用時まで課金されないが、ツール名+一行説明は常に一覧に載る**。
コストはツール本数に比例して残るため、「全部入り単一サーバ」は不可。

決定:

- UapOps は**ツールモジュール**単位で構成する: `core`(シーン/コンポーネント/
  アセット基本+query_component_types)、`prefab`、`anim`(5b)、`ui`(editor_ui_*、5c)、
  `profiles`(拡張プロファイル注入)。
- `tools/list` は**有効なモジュールのツールだけを返す**(MCPサーバ側でフィルタ)。
  設定画面にモジュールのトグルを置き、既定は `core`+`prefab` のみON。
- モジュール構成の変更は `tools/list_changed` 通知+(必要なら)`mcp_reconnect`
  制御リクエスト(R08 §3.4)で反映する。切替の実挙動(CLIが list_changed を
  拾うか)は 5a 冒頭のプローブ項目に追加する。
- ツール名は短く(`uap_scene_create` 等の接頭辞1個)、説明は一行に抑える。
- 完全なプラグイン式(外部アセンブリがツールを登録する公開API)は v2 バックログ。
  現段階では Extension Profiles(JSON知識パック)が実質のプラグイン面を担う。

### 7.2 uLoop 重複排除 — 「uLoopで賄えるものはuLoopに」

現状設計は uLoop 導入前提で書かれていたが、方針を**能力マトリクス駆動**に改める:

- 起動時+パッケージ変更時に uLoop を検出(既存 L2 設計の検出器)し、
  **uLoop が提供する能力(コンパイル、テスト実行、ログ取得、メニュー実行等)と
  重複する UapOps ツールはそもそも登録しない**。uLoop 側は Bash 経由の CLI
  利用なので MCP ツール一覧に載らず、トークンコストはゼロ(指示スニペットの
  数行のみ)。
- uLoop 不在時は、(a) 該当能力が UapOps のスコープ内なら UapOps 版を登録、
  (b) スコープ外(コンパイル/テスト等の重量級)は登録せず、ワンクリック導入
  (§2.5)への誘導を指示スニペットに含める。
- 「どの能力をどちらが担うか」の対応表は 5a 実装時に
  `docs/research/10-uloop-and-ecosystem.md` に追記して固定する。

### 7.3 Prefab Revert — 差分管理の両方向対応

Prefab はインスタンス差分(オーバーライド)の保存が本質であり、Apply だけでは
片方向になる。5b の prefab モジュールに以下を追加:

- `uap_prefab_get_overrides` — インスタンスのオーバーライド一覧
  (プロパティ/追加オブジェクト/追加コンポーネント別、R09 §9.4(2026-08-02追記)で実在確認済みの
  `PrefabUtility.GetPropertyModifications` / `GetAddedComponents` /
  `GetAddedGameObjects` 系)。
- `uap_prefab_apply_overrides` — 全体 or 個別 Apply(既設計)。
- `uap_prefab_revert_overrides` — **全体 Revert**(`PrefabUtility.RevertPrefabInstance`)。
- `uap_prefab_revert_override` — **個別 Revert**(`RevertPropertyOverride` /
  `RevertObjectOverride` / `RevertAddedComponent`)。
- いずれもターンスコープ Undo グループ+権限カード対象(破壊的操作)。
  Apply は「アセット側を書き換える」、Revert は「インスタンス側を巻き戻す」で
  影響先が逆になることを権限カードの説明文に明示する。

### 7.4 スクリプト検証ゲート — 「検証なしでいきなり実装」の禁止

要求: エージェントが書いた C# が未検証のまま Assets に入り、コンパイル(=
ドメインリロード)を汚すことを避けたい。L3 と一体で次のゲートを設ける:

- **ステージング必須**: ターン中に書かれる新規/変更スクリプトはチルダ
  ステージング(R11: `~`サフィックスフォルダはインポート完全除外)に置く。
- **コミット時検証**: ターン終端(または明示の `uap_scripts_commit`)で、
  ステージ内容+既存ソースを `UnityEditor.Compilation.AssemblyBuilder` で
  一時アセンブリにコンパイルして検証する。**失敗したらステージのまま**
  エラー全文をエージェントに返し、Assets へは移さない(=壊れたコードは
  ドメインリロードを一度も発生させない。「コンパイル回数最小化」目的に直結)。
- 成功時のみ Assets へ移動し、リフレッシュはバッチ窓(L3)で1回。
- AssemblyBuilder の参照解決が最終的な asmdef 配置と完全一致しない可能性
  (Editor 専用参照等)は既知のリスクとして 5a のプローブ項目に追加し、
  乖離があれば「参照セットを対象 asmdef から引く」実装に倒す。
- 併せて指示スニペットに「スクリプトはステージへ書き、commit で検証してから
  反映する」旨の運用規範を含める(プロセス面の検証も明文化)。

### フェーズへの反映(2026-08-02 改)

- 5a: モジュール分割基盤(トグル+tools/list フィルタ)、uLoop 能力マトリクス
  検出、`core` モジュール、スクリプト検証ゲートのプローブ
  (AssemblyBuilder 忠実度、tools/list_changed)を追加。
- 5b: `prefab` モジュールに Revert 系4ツールを追加。
- 5c: 変更なし(`ui` モジュールとして遅延登録)。

## 8. 設計ゲート結果と改訂(2026-08-02、実装移行前の最終精査)

4系統(実現可能性レビュー/スコープ・安全性レビュー/外部リサーチ/実機プローブ)による
敵対的ゲートを実施。プローブの生データはscratchpad `phase5probe/`、レビュー全文は
ワークフローログ参照。**ブロッカー3件を含む所見を以下の通り設計に反映して確定する。**

### 8.1 実証済みになった前提(プローブ2本、いずれもconfirmed)

**P1: AssemblyBuilder検証ゲート(§7.4の礎石)** — Phase5Probeプロジェクトで実測:
- 壊れた.csに対し CS番号+ファイル/行/桁 の正確なCompilerMessagesを返す(エージェントへの
  フィードバックにそのまま使える)。dll不生成。
- **AssemblyBuilder実行中にアセットインポート/ドメインリロードは一切発生しない**(ログの
  Domain Reload Profiling / Asset Pipeline Refreshマーカーで確認)。§7.4の中心主張は成立。
- 参照解決は非対称: プロジェクトasmdef/Assembly-CSharpの型は**自動解決**されるが、
  **UnityEditor.dll(CoreModule)は既定で含まれず** additionalReferences必須。実装は
  UnityEditor/UnityEngine系モジュールDLLを常に明示追加する方針とする。
- Windows既知の罠(R11 §5): 同名再コミットは File.Copy(overwrite)+Delete 等の明示処理。

**P2: エディタ内Streamable HTTP MCPサーバ(L1の礎石)** — HttpListener(127.0.0.1、
非管理者でURL-ACL不要)+実CLI v2.1.220で**フルラウンドトリップ成功**(initialize →
notifications/initialized → GET SSE拒否405をCLIが正しく許容 → tools/list → tools/call)。
- CLIは2025-11-25を要求し、サーバ側の2025-06-18ネゴシエートに正しく追従(以後の全リクエストに
  MCP-Protocol-Version/Mcp-Session-Idを付与)。SSEストリーム未実装(405)で問題なし。
- **MCPツールのToolSearch遅延読み込みを本ツールで直接確認**(ツール名のみ掲載→ tool_reference
  をselectで取得→呼び出し)。ハンドシェイク完了までToolSearch解決が保留される点も確認。
- 実装上の罠(実際に踏んで根本原因まで特定): BeginGetContextの雑なpoll/timeout再発行は
  受理済み接続を放置し「クライアント無限ハング+サーバログ空白+`claude mcp get`は
  Unexpected content type: null」という症状を生む。**コールバック型受理ループ
  (EndGetContext→即再アーム)必須**。実装ノートとして固定。

### 8.2 ブロッカー3件への改訂

**(B1) スクリプト検証ゲートに技術的強制力がない(指示スニペット頼み)** — 改訂:
- パネルは can_use_tool を既に掌握している。ゲート有効時(既定ON、設定でOFF可)、
  **CLIネイティブWrite/Edit/MultiEditの対象が `Assets/**/*.cs|*.asmdef` の場合は自動deny**し、
  拒否メッセージでステージングパス(`Assets/UapStaging~/`)への書き込みと
  `uap_scripts_commit` の使用を案内する(モデルはメッセージに従って自己修正できる)。
  ステージング内への書き込みは通常の権限フローのまま。
- これにより「未検証コードはAssetsに入らない」が**指示ではなく機構**になる。
- §3.2のL3バッチング既定OFFとの矛盾は「ゲート(deny+staging+commit検証)は既定ON、
  ターン単位Refresh抑制(DisallowAutoRefresh)は既定ON、ガード付き自動継続のみ既定OFF」
  と整理して解消する。

**(B2) ターン=Undo一発ロールバックの過大主張** — 改訂:
- ツールカタログに undoable: yes/no メタデータを持たせる。Undo非対応
  (asset_create、anim_create_clip、animator_edit、avatar_configure、Importer系)は
  (a) §1.3に明示的な例外として列挙、(b) 権限カードに「Undo対象外」バッジ表示、
  (c) パネルの「このターンを巻き戻す」ボタンは非対応ツールを含むターンで警告付き動作
  (巻き戻せない操作の一覧を提示)に変更。
- asset_create の対称性主張に対応する **uap_asset_delete(Trash送り=復元可能)** を5aに追加。
- Prefabステージ(編集モード)ガード: 全書き込み系ツールは実行前に
  PrefabStageUtility.GetCurrentPrefabStage() を確認し、対象シーンの不一致時は明示エラー。

**(B3) Extension Profilesがプロンプトインジェクション面** — 改訂:
- v1は**同梱プロファイルのみ自動注入**(リポジトリ=レビュー済み)。
- ユーザー/サードパーティ追加プロファイルは、**初回検出時に注入内容全文を確認カードで提示し、
  承認後は内容ハッシュをピン留め**(変更されたら再承認要求)。無承認プロファイルは注入しない。

### 8.3 主要(major)所見への改訂

- **L2/L3のコンパイルトリガー所有権**: ターン中(バッチ窓アクティブ)は、Bashコマンドが
  `uloop compile`系・AssetDatabase.Refresh相当を含む場合、can_use_toolで常時確認カード
  (自動許可させない)。uloop の execute-dynamic-code はallowedToolsプリセットに含めず
  常時確認。指示スニペットにも「ターン中のコンパイル起動はcommit経由のみ」と明記。
- **Bearer付きHTTP往復は未実測**(R08は形状確認のみ): 5a着手プローブに
  「Authorizationヘッダ付き実往復」を追加。失敗時のフォールバックはトークンをURLパスに
  埋めない(ログ流出面)。ローカルループバック限定である事実は維持。
- **`--resume`起動プロセスへのmcp_reconnect**は類推であり未実測 → 5a着手プローブに追加。
- **tools/list_changed は当てにしない**(外部リサーチ: クライアント側の取りこぼし報告が
  歴代複数 #13646/#31893/#50339、HTTPサーバの大規模ツール群でToolSearch遅延が効かない
  報告 #40314 もあり)。モジュールトグルの反映は **mcp_reconnect(実測済み)→ダメなら
  クライアント再spawn(既存機構)** の二段で行い、list_changedは使わない。
  ツール総数は各モジュール数個・全モジュール合計でも20未満に抑える(P2で少数ツールの
  遅延読み込みは実測済み)。
- **MCP仕様2026-07-28改訂でセッション/Mcp-Session-Id廃止方向**: CLI v2.1.220は
  2025-06-18/2025-11-25系で動作(P2実測)。サーバは実測済みのネゴシエーションに合わせ、
  仕様改訂追随はCLI側の要求バージョンが変わった時点で対応(リスク表に追加)。
- **ARCHITECTURE.mdリスク#13との矛盾**(「パネルはサーバを持たない」): リスク表を
  「UapOpsはloopback HTTPのみ保有。uLoopMCP(Named Pipe)とはトランスポート独立で資源競合なし」
  に更新する(本コミットで実施)。
- **uLoopワンクリック導入の失敗経路**: 事前チェック(オフライン検出/既存スコープ付き
  レジストリ検出/VCC・VPM管理プロジェクト検出=vpm-manifest.json存在)→manifest.json
  バックアップ→失敗時復元、をインストーラ設計に明記。VCC検出時は注意文言を添える。
- **自動継続とバッチ窓のUX整合**: ターン終端シーケンスを「commit検証→(成功時のみ)
  Refresh解放→コンパイル→リロード→復帰通知カード」と定義。リロードが発生する場合は
  ターン完了通知の前に「コンパイル保留中」状態カードを出し、無通知の裏リロードをしない。

### 8.4 外部リサーチからの採用事項

- Unity公式 AI Assistant(com.unity.ai.assistant, Unity 6 beta)の権限ティア
  (read / write / scene-write / full-autonomy)と同型の段階を権限カードの既定分類に採用。
- uLoopMCP は unity-cli-loop に改名し**自身のMCPクライアント経路を非推奨化**
  (CLI+Named Pipe路線へ) — 本設計のL2(Bash経由CLI)方針を裏付け。R10に追記対象。
- 参考カタログ: CoplayDev/unity-mcp(47ツール, MIT)、AnkleBreaker(268ツール)。
  ツール粒度の参考のみ(コード借用なし)。mcp_reconnect が非公開内部APIである点は
  リスク表に明記(消えた場合のフォールバック=クライアント再spawnは既存機構)。

### 8.5 5a受け入れ基準(改訂版)

1. UapOps(coreモジュール)がエディタ内HTTPサーバとして起動し、実CLIから tools/list /
   tools/call 往復(P2の実測系列を回帰テスト化。Bearer付き)。
2. scene_create_object → component_add → property_set → **uap_asset_delete まで含む**
   一連の操作がターンUndoグループで巻き戻る(Undo非対応ツールを含むターンは警告表示)。
3. スクリプトゲート: Assets直書きWrite/Editの自動deny→staging誘導→commit検証
   (AssemblyBuilder、エラー時Assets非反映・リロード0回)の一連が実機で動く。
4. モジュールトグルOFFのツールが tools/list に出ない+mcp_reconnectで反映される。
5. uLoop検出時、重複ツールが登録されない(能力マトリクスのログ出力で確認)。
6. ドメインリロード後、listen-before-spawn+mcp_reconnect(--resume起動に対する実測含む)
   で自動復旧する。

### 8.6 ADR: uLoopのMCP非推奨化を受けてもL1はMCPを維持する(2026-08-02、ユーザー質疑)

質問: uLoop(unity-cli-loop)が自身のMCP経路を非推奨化したのだから、AgentPanelも
MCPを使わない作りにすべきではないか。

決定: **L1(UapOps)はMCPを維持する。** 理由:

1. **前提条件が正反対**: uLoopの非推奨化は「不特定多数のMCPクライアントへの互換性維持
   コスト」からの撤退。本パネルのクライアントは自分でspawnするClaude Code CLIただ1つで、
   バージョン検出済み・リクエスト系列実測済み(R08 §5)・ライフサイクル完全掌握
   (listen-before-spawn / --mcp-config毎spawn / mcp_reconnect)。互換性マトリクスが存在しない。
2. **型付きツール呼び出しが権限カード安全モデルの前提**: MCPならcan_use_toolに構造化入力が
   届き、ツール別カード+Undo可否バッジ(§8.2 B2)が成立する。Bash経由は文字列パターン
   マッチの粗い許可粒度に落ちる(§8.3で自ら矛盾として指摘した方向への逆行)。
3. **CLI方式の再発明コスト**: 自前CLI+Named Pipe+配布バイナリはuLoopのインフラの複製。
   エディタ内HTTPサーバ1本(P2で実CLI往復実証済み)の方がUnityパッケージとして軽い。
4. **トークンコストは実測済みで許容内**: ツール名のみ掲載+ToolSearchスキーマ遅延読み込み
   (P2実測)。モジュール分割で総数20未満(§8.3)。

uLoopの教訓の取り込み: L2はBash+CLI(彼らの推奨路線)/list_changed不使用/SSE非依存/
UapOps内部はツールレジストリ+ディスパッチャのtransport非依存構造とし、前面差し替えの
シームを確保する。

### 8.7 スクリプトゲート強制力の再設計(2026-08-02 ライブE2E不合格→実測に基づく修正)

**発見(ライブE2E)**: §8.2 B1 の can_use_tool 自動deny 実装は、ユーザー設定
`--permission-mode acceptEdits` 下で**一度も発火せず**、壊れた .cs が Assets 直下に
着弾した(エージェント自身が「ゲートが効かなかった」と報告)。

**原因の実測確定(gateprobe)**: acceptEdits は Write/Edit 系の can_use_tool 往復を
**丸ごと省略**する(MCPツールは同一ターンでも発火=ライブ症状と完全一致)。デフォルト
モードでは Write も can_use_tool を通る。つまり can_use_tool ベースのゲートは
「権限モード次第で消える」構造的欠陥だった。

**候補機構の実測比較**:

| 機構 | 判定 | 要点 |
|---|---|---|
| A1 `--disallowedTools "Write(Assets/**)"` | ✗ | **起動エラー無しの黙殺**(見かけ上有効な設定が一切効かない=最悪の偽装安全) |
| A2 `--disallowedTools "Write"` | △ | ブロックはするがエージェントは同一ターンで PowerShell 書き込みに即迂回。誘導文なし |
| A3/C `Edit(Assets/**)` 指定子(flag/settings deny) | △ | Write まで塞がる(パスdenyエンジンが共有=非公開仕様)。deny > permission-mode の優先を確認。メッセージは汎用文のみ。Assets配下の**正当な非.cs編集まで全部塞ぐ**ため既定採用は不可 |
| **B PreToolUse フック(--settings)** | **✓** | **acceptEdits 下でも確実に発火**(フック入力に permission_mode がエコーされ確認済み)。ディスク到達前にブロック。**panel自作の誘導文が tool_result にそのまま届き、エージェントが読んで自己修正**(実測でほぼ逐語引用) |

フック契約(v2.1.220 実測): stdin に `{tool_name, tool_input, permission_mode, ...}`、
拒否は exit 0 + stdout に
`{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"<誘導文>"}}`。
`--settings` は CLI の cwd 基準で解決されるため**絶対パス必須**。

**決定**:

1. **主機構 = PreToolUse フック**: パネルが `UserSettings/AgentPanel/` 配下に
   フックスクリプト(v1 は PowerShell、-NoProfile -NonInteractive)と settings JSON
   (matcher `Write|Edit|MultiEdit`)を生成し、ゲート有効時のみ `--settings <絶対パス>`
   で注入。ゲート判定(Assets配下 + .cs/.asmdef、大小文字/スラッシュ/..正規化)は
   既存 ScriptGate と同義に保つ。
2. **ステージングを Assets の外へ移動**: `<projectRoot>/UapStaging/`(Assets外は
   そもそもインポート対象外なのでチルダ不要、deny系グロブとの衝突も消える)。
3. **縮退フォールバック(C)**: `permissions.deny` の狭いグロブ
   (`Edit(Assets/**/*.cs)` 等)が機能するかを実装前プローブで確認し、機能する場合のみ
   同じ settings JSON に併記(フック起動失敗時も黙殺ではなく汎用denyで止まる)。
   広い `Edit(Assets/**)` は正当な編集を壊すため不採用。
4. **既存 can_use_tool 層は存置**(デフォルト/planモードでは従来通り誘導文付きで機能、
   Bash リダイレクトのヒューリスティックも acceptEdits 下で有効なことを実測済み)。
5. 制約の明記: フックは Write/Edit 毎に子プロセス起動(PowerShell ~数百ms)の
   レイテンシを足す。非Windowsは v1 では can_use_tool 層のみ(フォローアップ)。

**教訓(プロセス)**: サンドボックスの EditMode テストは can_use_tool 層の正しさを
証明したが、「その層に制御が来るか」は権限モードという実行環境変数に依存していた。
ゲート系機能の受け入れ基準には「ユーザーの実設定(acceptEdits)での live E2E」を必須と
する(§8.5 基準3を「acceptEdits 下で」に強化)。

#### 8.7.1 追記(2026-08-02): deny フォールバックはフックと排他にする

§8.7 では「フック(主機構)+ 狭い deny グロブ(縮退フォールバック)を**併記**」と決めたが、
実装プローブとライブE2Eの二度の実測で**deny 規則がフックを短絡する**ことが判明した:

- 両方を `--settings` に載せると書き込みはブロックされるが、モデルに届くのは CLI の汎用文
  「denied by your permission settings」であり、**パネルの誘導文(ステージングを使え)は届かない**。
- ライブE2Eでエージェントは「拒否メッセージに誘導がない」と述べて**作業を放棄**した。
  自己修正こそがゲートの目的(§8.2 B1)なので、これは機能的失敗。

**訂正した決定**: 2つは**排他**。インストーラが PowerShell の実在を確認し、
- 実行可能 → **フックのみ**(誘導文が届く)
- 実行不可 → **deny グロブのみ**(汎用文でも硬く止める。黙殺よりまし)

`GateHookInstaller.IsHookRunnable()`(System32既定パス→PATH走査、純関数
`ResolvePowerShellPath` として単体テスト)で判定する。

**ライブE2E最終確認(2026-08-02、acceptEdits、実エディタ)**: 壊れた C# の Assets 直書きを
フックが拒否 → エージェントが誘導文に従い `UapStaging/` へ配置 → `uap_scripts_commit` で
AssemblyBuilder 検証 → **CS1525/CS1002 を行・桁付きで受領**し、ユーザーに選択肢を提示して終了。
**Assets には一切着弾せず、ドメインリロードは0回**。§8.5 基準3を acceptEdits 下で満たした。

### 8.8 5b着手時の予算改訂(2026-08-02)

§8.3の「ツール総数20未満」ガイドラインを**総数40未満・既定公開セット25前後**に改訂する。
根拠: P2実測で本サーバのツールはToolSearch遅延読み込みが効いており(名前+一行説明のみ常時掲載)、
問題報告(#40314)は250ツール規模。モジュール既定(core+prefab+editor=ON、anim=OFF、ui=5c)により
既定で公開される名前数は25前後に収まる。プロファイルはツールを追加しない(指示注入のみ)。

### 8.9 モジュール既定値の移行(2026-08-02 ライブE2Eで発見)

5bのライブE2Eで「prefabツールが無い」とエージェントが報告。原因: `PanelSettings.
uapOpsModules` のフィールド初期化子は**新規設定にしか効かない** — v0.12.x が書いた
State.asset の `["core"]` が初期化子の上にデシリアライズされ、既定ONで追加したはずの
prefab/editor モジュールが既存ユーザーに永遠に届かない。

**修正**: `uapOpsModuleDefaultsGeneration` を導入し `EnsureUapOpsModuleDefaults()` を
`PanelStateStore.OnEnable` で1回だけ実行(既存の `EnsureCjkUiFontDefault` と同じイディオム)。
**世代ゲート方式**にしたのは、単純な「既定値との和集合」だと**ユーザーが明示的にOFFにした
モジュールが毎回復活**してしまうため。既定OFFのモジュール(anim)は移行対象外。

**一般化した教訓**: 永続化された設定に**新しい既定値を追加**する変更は、フィールド初期化子
だけでは不十分。今後 default-ON の項目を追加する際は世代を上げ、移行テスト(旧資産→新既定が
入る/ユーザーOFFは維持される)を必ず添える。

---

## 9. Phase 5c 実装後の実測(2026-08-03)

### 9.1 合成イベントは Clickable を駆動しない(`uap_editor_ui_click`)

**当初の実装は「成功」を返しながら、ボタンのコールバックを一度も実行していなかった。**
設計上は正しく見えた — Unity のソース上 `Clickable` は `MouseDownEvent`/`MouseUpEvent` を
購読しているので、その対を送れば動くはずだった。実際のエディタで、レイアウト済み
(worldBound 80x20)のボタンに対して計測するまで、誰もそれが嘘だと気づけなかった。

9通りのディスパッチ経路を、それぞれ専用のカウンタ付きボタンに対して実測:

| 経路 | 発火 |
|---|---|
| MouseDown+MouseUp(座標指定) | しない |
| MouseDown+MouseUp(IMGUI Event 由来) | しない |
| 同上を `panel.visualTree` 経由で送信 | しない |
| PointerDown+PointerUp(引数なし) | しない |
| PointerDown+PointerUp(IMGUI Event 由来) | しない |
| ClickEvent 単体 | しない |
| PointerDown+PointerUp のあと ClickEvent | しない |
| **`Clickable.Invoke(ClickEvent)`(非public、リフレクション)** | **する** |

ポインタ系は、初回が position=(0,0) だったため `Clickable` の内包判定で落ちた可能性を疑い、
実際のワールド中心座標で再計測した。それでも発火しない。結論:
**2022.3 では、合成イベントの送出でパネル外部から `Clickable` を駆動することはできない。**
イベント送信が効くのはパネル自身の入力システムが発生源のときだけで、ツール側からは
必要なポインタ状態を再現しきれない。再導入しないこと。

**より重要なのは後半**で、そもそもこの欠陥が成立した理由がそこにある。ツールは
`"clicked": true` を無条件に返していた。現在は invoke の前後で manipulator の `clicked` を
購読し、**観測できた事実だけ**を返す。この検査はコストがゼロで、Unity 側の内部実装が
変わっても壊れず、将来の回帰を「嘘」ではなく「dispatched but not observed」という
見える形に変える。同日 `uap_material_set` が学んだのとまったく同じ教訓
(2026-08-03-material-set-silent-noop.md)であり、このフェーズで**2回**踏んだ。

**一般化した教訓**: ツールが返す成功は、**観測した結果**でなければならない。
「その手順を踏んだのだから成功したはず」は成功ではない。とくにツールの出力が
そのままモデルの次の判断材料になる本プロジェクトでは、静かな嘘のコストが極端に高い。

### 9.2 uloop プリセットを「広く許可して除外」から「許可リスト」へ

L2 プリセットは当初 `Bash(uloop *)` を1本書き、危険なサブコマンド
(update / sync / launch)を `disallowedTools` で削り戻す形だった。この安全性は
**CLI のパターン文法がこちらの想定どおりに一致すること**に依存している。とくに引数なしの
裸のサブコマンド — まさにこのエディタをフリーズさせた `uloop update` そのものの形 —
がどう扱われるかが未計測だったため、実測した。

**実測**(2026-08-04、`scratchpad/toolpattern/probe2.js`。同じ6コマンドを両方の形に対して
実行。2回とも同一の結果):

| コマンド | A: 広く許可+除外 | B: 許可リスト | 期待 |
|---|---|---|---|
| `uloop compile` | 自動許可 | 自動許可 | 自動許可 |
| `uloop compile --x` | 自動許可 | 自動許可 | 自動許可 |
| `uloop update`(裸) | **CLI が拒否** | CLI が拒否 | 自動許可しない |
| `uloop update --yes` | CLI が拒否 | CLI が拒否 | 自動許可しない |
| `uloop launch` | CLI が拒否 | CLI が拒否 | 自動許可しない |
| `uloop execute-dynamic-code` | **自動許可** | **確認を求めた** | 自動許可しない |

**除外は効いていた。** 引数なしの `uloop update` は両方の形で拒否される
(`Permission to use Bash with command uloop update has been denied`)。
この変更の動機だった懸念そのものは、**存在しなかった**。

**実測が見つけたのは最終行のほう**であり、こちらは実在する欠陥だった。広いワイルドカードは
`uloop execute-dynamic-code` を黙って自動許可していた。これはエディタ内で任意の C# を
コンパイルして実行するコマンドで、**設計 8.3 が「常に確認を求める」と定めているもの**である。
旧実装は update/sync/launch については何も間違っていなかった。間違っていたのは、
**誰も列挙しようと思わなかったすべて**についてだった。これは拒否リスト一般の失敗様式
(すでに誰かが挙げた危険しか排除できない)であり、書き換えの動機になった具体的な不安が
空振りだったにもかかわらず、許可リストを維持する理由になる。

- 許可(明示列挙): `compile` / `get-logs` / `list` / `run-tests` — 各2形
- 不許可(継続): `update` / `sync` / `launch` — 各2形。許可リストに載っていない以上すでに
  カード止まりだが、カードは阻止ではない。長い無人実行の末尾でユーザーが気づいて拒否する
  必要が残るため二重化する
- `execute-dynamic-code`: 両リストから除外(=常に確認)。B で実際に `can_use_tool` の
  往復が発生することを確認済み

回帰ガード: `BuildUloopAllowedPatterns_NeverContainsABareWildcard`、
`..._NeverAllowsADangerousOrArbitraryCodeSubcommand`。

**プローブ自身が2回間違えた**ので記録しておく。

1. 初版は PATH から npm/nodejs を剥がして実 uloop を隠そうとし、それが CLI 自身の
   OAuth リフレッシュを壊した。全6ケースが同一の結果を返し、プローブが assistant の
   本文も記録していなければ「発見」として報告していた。
2. 2版目は **CLI 側の拒否も `tool_result` を返す**ことを見落とし、あらゆる `tool_result` を
   「コマンドが走った」と数えていた。結果、拒否された3ケースすべてが「自動許可」と表示され、
   **実際の結論と正反対**の表を出力した。中身(`Permission to use ... has been denied`)を
   目視して初めて分かった。

どちらも「一様すぎる結果は測定系を疑う」で救われている。加えて、`fakebin/uloop.cmd` の
スタブは実際には実 uloop を遮蔽できていなかった(結果に本物の `PROJECT_NOT_FOUND` が
返っている)。今回は `update` が拒否されたので実害はないが、**安全網はかかっていなかった**。
今後この種のプローブでは、スタブが効いていることをまず1ケースで確認すること。

### 9.3 ワンクリック導入を初めて実行した(2026-08-04)

出荷から一度も実行されていなかったので、使い捨てプロジェクト
(`C:\Unity\UnityProjects\UloopInstallTest`、uLoop 未導入・`scopedRegistries` なし)に
パッケージをジャンクションで入れ、バッチモードから `Plan()` → `Apply()` → 別セッションで
検証、という順で通した。

**結果: 動く。** 実エディタで **30.5 秒**で解決し、`io.github.hatayama.uloopmcp@2.2.0` が
`packages-lock.json` と `manifest.json` の `dependencies` に入った。計画段階の差分も正しく、
既存の依存関係は一字一句そのまま、`scopedRegistries` だけが追加される。caveat 0。

**バージョンを固定しない判断が、実測で裏付けられた。** 解決されたのは **2.2.0(安定版)**で、
このマシン上の2つのプロジェクトが持っていた beta.48 / beta.71 の**どちらでもない**。
定数を書いていたら、3者ともに対して間違っていたことになる。

**副作用の記録**: uLoop は `com.unity.nuget.newtonsoft-json` と `com.unity.ugui` を
transitive に引き込む。確認カードが見せるのは manifest の差分だけなので、この2つは
カードに現れない。実害は薄いが、「差分を見せているのだから全部見せている」という
読み方を許す点は正直ではない。

#### 危うく誤報するところだった

最初の検証は「何も入っていない」と出た。`packages-lock.json` に無し、登録パッケージに無し、
`Plan()` 再実行も `alreadyInstalled: False`。**`Apply()` は `Success: true` を返していた**ので、
これは「成功を偽って返す」欠陥そのものに見えた。

実際は測定系の問題だった。`-executeMethod ... -quit` はメソッドが返った瞬間に Unity を
落とすため、非同期の `Client.Add` が完了する前にエディタが消えていた。`-quit` を外し、
`EditorApplication.update` で manifest / lock を監視して自分で `Exit` する形に変えたら
30.5 秒で解決した。**製品の欠陥ではなく、ハーネスの欠陥。**

とはいえ、`Apply()` が `Client.Add` の**投函**をもって成功を返すこと自体は事実で、
これは実装のコメントが明示的に選択して記録している(解決失敗は Unity 自身の Package
Manager に出る、レジストリ項目が残っても無害、という理由)。妥当ではあるが、
**UI 上は「成功」と出てから実際に入るまで 30 秒の空白がある**。ステータス表示の更新が
それを埋めているかは未確認。

#### 実行して初めて見つかった不具合: 自作の誤検知

`UloopDetector.IsPresentInManifest` は manifest 全文に対して `"<id>"` の部分一致を
していた。ドキュメントコメントは「引用符で囲めばコメントや文字列値への誤一致は避けられる」と
主張していたが、**`UloopInstaller` 自身が `scopedRegistries[].scopes` にまったく同じ
引用符付きの id を書き込む**。したがって導入を始めた瞬間から — そして OpenUPM の解決が
一度も成功しなくても永続的に — 検出器は「uLoop は入っている」と答える。
誤検知は自作であり、Plan() の `alreadyInstalled` とも食い違っていた
(実測: `DetectInProject=True` / `alreadyInstalled=False` が同時に成立)。

修正: 依存キーの形、すなわち引用符付き id の直後(空白を挟んでもよい)にコロンが来る
場合だけを真とする。scopes の項目は `"<id>"` の後がカンマ・改行・`]` なので一致しない。
最初の出現で打ち切らず全出現を走査する(scopes が dependencies より前に来る実際の
manifest があるため)。

**そしてテストがこの不具合を「意図した挙動」として固定していた。**
`IsPresentInManifest_QuotedAnywhereInText_Matches` は、まさに scopedRegistries の中の
id に一致することを、肯定的なコメント付きで assert していた。実装と同じ前提から書かれた
テストは、その前提を検査できない — このプロジェクトで **4例目**(先例:
UapUiClickDispatcherTests、ui ダンパー、AgentClientStateTests)。
現在は反対を assert し、実測で得た導入後の実 manifest 形(id が scopes と dependencies に
2回出る)も併せて固定してある。

### 9.4 Phase 5c 未レビュー面の敵対的レビュー(2026-08-04)

`ui` モジュール / インストーラ / 自動継続 / テスト品質の4面に、5本の探索と、
1件あたり3つの異なるレンズ(コードが本当にそうなっているか / 呼び出し側を辿って到達可能か /
欠陥か好みか)による反証を回した。42件提起、32件が多数決で生き残り、23件に集約。
以下は**そのうち実測で決着したもの**だけを記録する。

#### 9.4.1 許可リストの書き換えは、上書きしない限り無意味だった(最重要)

`MergeToolListAdditions` は純粋に追記のみ。したがって**旧バージョンの同じボタンが書いた
`Bash(uloop *)` が残っているマシンでは、狭い8パターンを隣に足しても何も変わらない**。
9.2 で実測したとおり、そのワイルドカードの下では `execute-dynamic-code` が自動許可される。
つまり 9.2 の書き換えは、**旧ボタンを押したことがある人 = まさに uLoop 利用者**に対しては
一切効いておらず、ヘルプ文言(「execute-dynamic-code を含め、それ以外は確認を求めます」)は
その人たちにとって虚偽だった。

`StripBroadUloopAllowEntries` を追加し、マージ前に「uloop 全体に対するワイルドカード」だけを
除去する(`Bash(uloop compile *)` のような狭いものは残す)。回帰ガードは
`PresetAppliedOverAPreExistingWildcard_LeavesNoWildcardBehind` —
**既存のテストが素通りしていたのは、新規リストを検査するか空リストにマージするかの
どちらかしかしていなかったから**で、実際の上書き経路を一度も通っていなかった。

#### 9.4.2 クリック観測の「修正」に、同じ欠陥が残っていた

9.1 で `clicked` を購読して観測する形にしたが、実測(2026-08-04)でこうなった:

| 対象 | 報告 | 実際 |
|---|---|---|
| Toggle | `observed=True` | **値は変わらない**(False→False) |
| コールバック無し Button | `observed=True` | **何も起きない** |
| 通常の Button | `observed=True` | コールバック1回(正しい) |

原因は2つ。(a) `Clickable` にはハンドラ系統が **2つ**ある —
`clicked`(`Action`)と `clickedWithEventInfo`(`Action<EventBase>`)。実測:
`Toggle` は `BaseBoolField.m_Clickable` に `clicked=null, withInfo=非null`、
`Button` は `clicked=非null, withInfo=null`、コールバック無し `Button` は**両方 null**。
(b) 検証プローブが `clicked` に**自分で購読を足していた**ため、元の購読者がいない場合は
自分だけが発火し、それを「観測できた」と解釈していた。

修正は2点。既存デリゲートを**足すのではなく包む**ことで、フラグを立てられるのは
元からあったコードだけにした。そして bool 値を持つコントロール
(`INotifyValueChanged<bool>`)は**クリックせず拒否**し、`uap_editor_ui_set_value` を案内する
— `ClickEvent` では `BaseBoolField.OnClickEvent` がイベント種別で弾くため、値が変わらないことが
実測で分かっているから。何も起きないと分かっている操作を投げて何かを報告するのは、
このプロジェクトが2度踏んだ罠そのものである。

#### 9.4.3 IMGUI 分類器の指摘は再現しなかった(変更しない)

レビューは「IMGUIContainer と非IMGUI要素を1つずつ持つレガシー OnGUI ウィンドウが
`usable` と誤判定される」と主張した。**実在9ウィンドウで実測したところ、そのような窓は無い。**

| ウィンドウ | ルート直下 | 分類器 |
|---|---|---|
| Animation / Console / GameView / SceneHierarchy | **空(子0)** | 拒否「ツリーが空」 |
| SceneView | `IMGUIContainer` のみ | 拒否「全面 IMGUI」 |
| Inspector | `IMGUIContainer` + `TemplateContainer` | usable |
| Profiler / ProjectBrowser / AgentPanel | UITK | usable |

レガシー窓は `rootVisualElement` を**一切構築しない**(子0)ので、既存の空ツリー判定で
正しく拒否される。Inspector は実際に UITK 部分を持つ混在窓なので `usable` で妥当。
**前提が成り立っていない指摘は直さない**ことにし、代わりにこの表を記録する
(設計が持っていた「IMGUI 窓 = IMGUIContainer が1つ」というモデル自体が誤りだった)。

#### 9.4.4 残りの確認済み4件(v0.18.2)

v0.18.1 では 23件のうち19件を直し、4件を残していた。以下で完了。

**`includeInternal` の不一致が指紋検査を素通りしていた。** 2つのモードは子を別々に数えるので、
同じパス文字列が別の要素を指しうる。具体例: 最初の内容子が Toggle である Foldout。
`includeInternal:true` では `"0/0"` は Foldout 自身のヘッダトグル(`hierarchy.Add` で足される)、
既定の false ではユーザーの Toggle。**どちらも型は Toggle、名前は空**なので、型/名前の指紋では
区別できず、別のコントロールを操作して成功を返す。

推測せず、**呼び出し側がモードを明示しなかった場合は両モードで解決し、食い違ったら拒否する**
ようにした(`TryResolveUnambiguously`)。パスを産んだ dump はモードを知っているので、
呼び出し側は答えられる。明示された場合はその問いに答えているので素通しする。
実測確認: 未指定→拒否、`false` 指定→ユーザーの Toggle に解決。

**そのときのエラーがモードを名指ししていなかった。** 範囲外エラーは「ツリーが変わった、
dump をやり直せ」と述べるだけだったので、エージェントは**元のモードのまま**やり直し、
同じパスを得て、同じ呼び出しをして、無限に回る。エラーが walked したモードと、
反対のモードで dump したなら同じ値を渡せと言うようにした。

**同一タイトルの窓が区別できなかった。** あいまい判定のエラーはタイトルと型名しか出さないので、
Inspector を2枚(片方は Prefab にロック)開いていると候補が完全に同じ文字列になる。しかも
`uap_editor_ui_list_windows` は索引を出さないので、`windowIndex` を答えようがなかった。
一覧に `windowIndex`(`FindAllWindows` の順序、他ツールの引数と同一)を出し、
あいまいエラーの各候補に索引・画面位置・フォーカス有無を添えるようにした。

**確認カードが改行コードを正規化して差分から消していた。** `PrettyPrintManifest` は常に LF を
出すので、Windows の CRLF manifest では**全行が書き換わる**。カードは両側を正規化してから
差分を取るので、それを見せられない。開示するのではなく**起こさない**方を選び、元ファイルが
CRLF 優勢なら書き出しも CRLF に合わせる。誰も改行コードの churn は望んでいない。

サンドボックス 1958 tests / 0 failed。検証に使った使い捨てプロジェクトは削除した
(パッケージへのジャンクションを先に外してから — 順序を誤るとリポジトリ本体を消す)。
