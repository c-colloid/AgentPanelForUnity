# Agent Panel Pro 収録機能一覧

**対象版: pro-v0.13.0**(2026-09-17 時点)。
公開 URL: <https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-FEATURES.md>
同梱プロファイルは別文書 [PRO-PROFILES.md](PRO-PROFILES.md) にあります。

この文書は **販売ページ(BOOTH など)の「収録機能」欄の元原稿** です。
Pro の機能が増えたときはここだけを更新し、販売ページには上の URL を載せるか、
下の「短い版」をコピーして貼り直します。文書の更新ルールは末尾の
「メンテナ向け」を参照してください。

---

## 短い版(販売ページ貼り付け用)

そのままコピーして貼れるプレーンテキストです(Markdown 記法は使っていません)。

```text
■ Agent Panel Pro 収録機能(pro-v0.13.0 時点)
Agent Panel for Unity(無料・Core)に追加する Unity 操作ツール 37 本(テスト実行を含む)と、主要アセット向けの同梱プロファイル 17 件。
すべて Core のチャット画面からエージェント(Claude Code)が呼び出すツールで、スクリプトのコンパイルなしに動きます。

・プレハブ: プレハブ作成(Variant 判定つき)/オーバーライドの一覧・適用・差し戻し/Prefab Mode の開閉(中身をそのまま編集)
・アニメーション: AnimationClip 作成(float・ON/OFF・スプライト差し替え、キー補間)/AnimatorController 編集(レイヤー・サブステートマシン・遷移の詳細・削除)/BlendTree(1D/2D/Direct)/StateMachineBehaviour(VRChat の ParameterDriver 等)/AvatarMask/AnimatorOverrideController/マテリアル・シェーダープロパティ/アセットとインポーターのプロパティ/Humanoid 設定
・ライトマップ: 非同期ベイク・メモリ見積りプリフライト・自動調整/Bakery GPU Lightmapper の設定と非同期ベイク
・UI 操作: UI Toolkit 製エディタウィンドウの一覧・ツリーダンプ・クリック・値設定(SDK の独自ウィンドウを自動操作)
・プロファイル作成: このプロジェクト用の拡張プロファイル(.uap-profiles)の下書きと検証/Claude Code スキルの書き出し
・アバター: 三角形数・マテリアル・ボーン・PhysBone 等の計測とベイク前後の差分(VRChat SDK があれば PC/Quest ランクも)/NDMF の手動ベイク実行/VRChat エキスプレッションメニュー・パラメータの読み書き(SDK の制限を事前検証)
・パーティクル: Particle System の 23 モジュールをスクリプト API の名前で読み書き(バースト含む)
・メッシュ: 数値からメッシュ生成(プリミティブ・押し出し・回転体・SDF・生データ)/頂点の調査/変形編集(移動・膨張・スムーズ・細分化・ノイズ)/ブーリアン/不整合の検証と修復/シーンの Z ファイティング検出/Scene ビューに描いたスケッチ線を取り込んでの溝・盛り上げ・チューブ・穴あけ
・一括実行: 複数ツール呼び出しを 1 回・許可カード 1 枚で実行
・テスト実行: EditMode テストの実行と失敗の報告(Test Framework 導入時)
・同梱プロファイル 17 件: VRChat SDK3(共通/アバター/ワールド)、Udon、UdonSharp、NDMF、Modular Avatar、AAO: Avatar Optimizer、VRCFury、lilycalInventory、lilToon、UniVRM、MagicaCloth2、Final IK、Bakery、ProBuilder、RPG Maker Unite

動作要件: Unity 2022.3 LTS 以降(Unity 6 系を含む)、Agent Panel for Unity(Core)。
一覧の詳細: https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-FEATURES.md
同梱プロファイルの詳細: https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-PROFILES.md
```

---

## 詳細

以下はモジュール(設定 > Unity 操作(UapOps) のトグル)ごとの一覧です。
「追加」列はそのツールが最初に入った Pro の版で、販売ページの「追加分だけ
追記したい」ときの目印です。ツール名は `uap_*` の MCP ツール名で、エージェントが
呼ぶ名前です(利用者が打ち込む必要はありません)。

### プレハブ(prefab)

| ツール | できること | 追加 |
|---|---|---|
| `uap_prefab_create` | シーンの GameObject からプレハブアセットを作成し、インスタンスとして接続。ソースが既にプレハブインスタンスなら Unity の仕様どおり **Variant** を作り、どちらを作ったか(Variant なら base も)を返す。`expect` で期待と違えば**書く前に**拒否 | 0.1.0 |
| `uap_prefab_get_overrides` | プレハブインスタンスのオーバーライド(プロパティ変更・追加コンポーネント・追加子オブジェクト)を一覧(読み取り専用) | 0.1.0 |
| `uap_prefab_apply_overrides` | インスタンスのオーバーライドをプレハブアセットへ適用 | 0.1.0 |
| `uap_prefab_revert_overrides` | インスタンスの全オーバーライドをアセットの既定値に差し戻す(インスタンスのみ変更) | 0.1.0 |
| `uap_prefab_revert_override` | オーバーライドを 1 件だけ種類指定(property / component / object)で差し戻す | 0.1.0 |
| `uap_prefab_stage` | Prefab Mode の開閉と状態確認。開いている間は通常のシーン系ツールでプレハブの**中身**を編集でき、閉じると保留中の編集がコミットされる | 0.9.0 |

### アニメーション(anim)

| ツール | できること | 追加 |
|---|---|---|
| `uap_anim_create_clip` | AnimationClip アセットの作成。float カーブ、GameObject の ON/OFF、スプライト・マテリアル差し替え(オブジェクト参照カーブ)。キー補間(Smooth / Linear / Constant)をカーブ単位・キー単位で指定 | 0.1.0(ON/OFF・参照カーブ 0.4.0、補間 0.6.0) |
| `uap_animator_edit` | AnimatorController の編集。パラメータ・ステート・既定ステート・遷移(Any State / Entry / Exit / サブステートマシン、Exit Time や割り込み設定などの遷移詳細)・レイヤー(追加・設定・名前変更)・サブステートマシン・`remove_*` 系の削除・一覧。初回書き込み時にアセットを自動作成 | 0.1.0(レイヤー 0.3.0、サブステートマシン・Entry/Exit 0.4.0、遷移詳細・削除 0.5.0) |
| `uap_animator_blendtree` | BlendTree(1D / 2D / Direct)の作成・編集・子の追加・一覧。入れ子は `childPath` で指定 | 0.3.0 |
| `uap_animator_behaviour` | StateMachineBehaviour の付与・設定・一覧・削除(VRChat の VRCAvatarParameterDriver / VRCAnimatorTrackingControl / VRCAnimatorLayerControl、Modular Avatar の MMD レイヤー制御、プロジェクト独自のものも) | 0.3.0 |
| `uap_avatar_mask` | AvatarMask アセット(.mask)の作成・編集。Humanoid の部位トグルと Transform パス一覧(シーンの階層から生成可) | 0.4.0 |
| `uap_animator_override` | AnimatorOverrideController(.overrideController)の作成・編集。ベースコントローラとクリップ差し替え表 | 0.6.0 |
| `uap_material_set` | マテリアルアセットまたはシーンのレンダラのシェーダープロパティ(float / color / vector / texture)とキーワードの設定。`op:"list"` でシェーダーのプロパティ表を取得 | 0.1.0 |
| `uap_asset_set_property` | アセット、またはそのインポーターのプロパティを SerializedProperty パスで設定(インポーターは同期リインポート) | 0.1.0 |
| `uap_avatar_configure` | モデルの Humanoid インポート設定(animationType / avatarSetup)の取得と変更 | 0.1.0 |

### ライトマップ(editor)

Core の `editor` モジュールのうち、ベイク系の 2 本が Pro です(スクリーンショットとメニュー実行は Core)。

| ツール | できること | 追加 |
|---|---|---|
| `uap_lightmap_bake` | Progressive Lightmapper の非同期ベイク(start / status / cancel)。`preflight` でシステム RAM に収まるかを見積もり、警告と推奨を返す。`start` は同じプリフライトを先に走らせ、`autoOptimize` で Auto Generate を切り、Max Lightmap Size / Lightmap Resolution を収まる値に丸め、未使用アセットをアンロードして「out of system memory」で黙ってスキップされる事故を防ぐ | 0.1.0 |
| `uap_bakery_bake` | Bakery GPU Lightmapper(導入時)の設定一覧・設定変更(シーンに保存)・非同期ベイク(全体 / 選択 / ライトプローブ / リフレクションプローブ、preview / balanced / final プリセット)・進捗・中断 | 0.1.0 |

### UI 操作(ui)

| ツール | できること | 追加 |
|---|---|---|
| `uap_editor_ui_list_windows` | 開いているエディタウィンドウの一覧(型・タイトル・フォーカス・位置・UI Toolkit で自動操作できるか) | 0.1.0 |
| `uap_editor_ui_dump` | UI Toolkit ウィンドウの要素ツリー(パス・型・名前・クラス・テキスト・値)をダンプ | 0.1.0 |
| `uap_editor_ui_click` | ダンプで得たパスの要素を実際にクリック(pointer down/up を送るので Button のコールバックが動く) | 0.1.0 |
| `uap_editor_ui_set_value` | TextField / Toggle / ドロップダウンなどの値を設定(ユーザー編集と同じ ChangeEvent を発火) | 0.1.0 |

### プロファイル作成(authoring)

| ツール | できること | 追加 |
|---|---|---|
| `uap_profile_scaffold` | 導入済み SDK 向けの拡張プロファイル(`.uap-profiles/<id>.json`)の下書き。コンパイル済みの型を実際に走査して検出条件を組み立て(短い型名が衝突すれば完全修飾)、文章だけを TODO として残す | 0.9.0 |
| `uap_profile_validate` | 拡張プロファイルの検証: このプロジェクトで実際に検出されるか、id が既存と衝突しないか、型と package id が実在するか、指示行が実在する `uap_*` ツールだけを名指ししているか | 0.9.0 |
| `uap_skill_scaffold` | Claude Code のスキル(`.claude/skills/<name>/SKILL.md`)を書き出し、今のセッションで使えるか新しいチャットが要るかを返す。既存スキルは上書きしない | 0.9.0 |

### アバター(avatar)

| ツール | できること | 追加 |
|---|---|---|
| `uap_avatar_stats` | アバターの計測: 三角形数・マテリアルスロット・レンダラ・ボーン・ブレンドシェイプ・PhysBone / Contact / Constraint・欠損スクリプト。VRChat SDK があれば PC / Quest のパフォーマンスランクも。`comparePath` で 2 つ目の階層(通常はベイク後のクローン)との差分と 1 行要約 | 0.9.0 |
| `uap_ndmf_bake` | NDMF の手動ベイクを実行してクローンを特定し、元との差分まで一度に返す(生成物を書くため confirm / dry_run ゲートつき) | 0.9.0 |
| `uap_vrc_menu` | VRChat エキスプレッションメニューとパラメータの読み取り・書き換え。書く前に SDK が実際に強制する規則を全件検証: 1 メニューのコントロール上限、256 bit の同期パラメータ予算、パペットの subParameters 個数、存在しないパラメータを指すコントロール | 0.9.0 |

### パーティクル(fx)

| ツール | できること | 追加 |
|---|---|---|
| `uap_particle_set` | Particle System のモジュールを**スクリプト API の名前**(main / emission / shape / colorOverLifetime ...)で読み書き。23 モジュールの有効/無効と全プロパティ。カーブ値は数値 / `{min,max}` / カーブのいずれでも渡せ、モードを同時に決める。OFF のモジュールと `[Obsolete]` プロパティへの書き込みは拒否。バーストもここから設定 | 0.9.0 |

### メッシュ(mesh)

| ツール | できること | 追加 |
|---|---|---|
| `uap_mesh_create` | 数値からメッシュ生成: プリミティブ(box / plane / cylinder / cone / sphere / torus / stairs)、`extrude`(XZ 外形の押し出し)、`lathe`(XY プロファイルの回転体)、`sdf`(球・箱・カプセル・円柱・トーラス・楕円体を union / intersect / subtract で合成、`smooth` で継ぎ目を丸める)、`raw`(頂点・三角形・任意で uv / 法線 / サブメッシュ)。Scene ビューに描いたスケッチ線(Core のスケッチ機能)も入力にでき、`sdf` の `stroke`(線に沿ったチューブ)/ `stroke_prism`(閉じた線の外形を厚み分押し出し)と `extrude` の `stroke`(外形の押し出し。ブーリアンの相手に)。Mesh アセットに保存、または MeshFilter + MeshRenderer(任意で MeshCollider)を持つ GameObject としてシーンに配置。ProBuilder があれば ProBuilderize | 0.12.0 / 0.13.0 |
| `uap_mesh_inspect` | 既存メッシュの頂点数・bounds・保存場所(書き込めるか)・閉じているか、範囲内または点に最も近い頂点の一覧(法線つき) | 0.12.0 |
| `uap_mesh_edit` | 既存メッシュをその場で編集: `displace`(範囲を移動。球 + 減衰 = プロポーショナル編集)、`inflate`、`smooth`(ラプラシアン)、`subdivide`、`noise`。範囲には Scene ビューに描いたスケッチ線も指定でき(`stroke` + 半径 + 減衰)、線に沿って溝を掘る・盛り上げる・寄せるが 1 回で済む。複数操作を順に適用、法線はシームと折り目を保って再計算、Ctrl+Z 可。組み込み・インポート済みメッシュは `detach:true` でコピーしてから | 0.12.0 / 0.13.0 |
| `uap_mesh_boolean` | 閉じた 2 メッシュの union / intersect / subtract を新しいメッシュに(入力は不変)。CSG が残すスライバー・重複・同一平面の重なりを自動除去、`place.checkZFight` で配置直後の重なりを警告 | 0.12.0 |
| `uap_mesh_validate` | メッシュ内部の不整合を重大度と代表位置つきで報告: 退化・重複三角形、非多様体エッジ、巻き方向の食い違い、開いた辺、未参照 / NaN 頂点、面と逆向きの法線、内向きの面、同一平面の重なり | 0.12.0 |
| `uap_mesh_repair` | 検証結果を `fix` で選んで修復: 既定は退化・重複・未参照頂点の除去、巻き方向の統一、法線の再計算。溶接・同一平面の重なり除去・穴埋めはオプトイン。修復後の検証結果も返す。Ctrl+Z 可 | 0.12.0 |
| `uap_scene_zfight_scan` | シーン(または範囲)の別オブジェクト同士で同一平面に重なる面を検出し、組ごとに重なり面積・位置・向き(同方向 = ちらつく / 背中合わせ = カリングで隠れる)と対処案を返す | 0.12.0 |

### 一括実行(batch)

| ツール | できること | 追加 |
|---|---|---|
| `uap_batch` | 複数の UapOps 呼び出しを 1 回・許可カード 1 枚で実行。実行前に全件を検証するので 1 件でも不正なら何も実行されない。破壊的ツール・無効化中のモジュール・入れ子のバッチは拒否 | 0.9.0 |

### テスト実行(tests)

| ツール | できること | 追加 |
|---|---|---|
| `uap_test_run` | EditMode テストを Unity Test Runner で実行し、失敗の名前・メッセージ・スタックを返す。`assemblyNames` / `groupNames`(正規表現)/ `testNames`(完全一致)/ `categoryNames` で絞り込み、`listOnly` で存在するテストの一覧だけを取得。0 件ヒットは 0 件として報告。PlayMode は非対応。**`com.unity.test-framework` が入っているプロジェクトでのみ現れる** | 0.9.0 |

### 同梱プロファイル

17 件。一覧と各プロファイルがエージェントに教える内容は
[PRO-PROFILES.md](PRO-PROFILES.md) を参照してください。

---

## 版ごとの追加分

販売ページを「前回載せた版」から更新するときは、その版より下の行を足します。
修正だけの版(0.2.1 〜 0.2.3、0.12.1 など)は載せていません。

| 版 | 追加されたもの |
|---|---|
| 0.13.0 | スケッチ線の取り込み: `uap_mesh_edit` の `stroke` 範囲、`uap_mesh_create` の `sdf` に `stroke` / `stroke_prism`、`extrude` に `stroke` |
| 0.12.0 | メッシュモジュール一式(`uap_mesh_create` / `uap_mesh_inspect` / `uap_mesh_edit` / `uap_mesh_boolean` / `uap_mesh_validate` / `uap_mesh_repair` / `uap_scene_zfight_scan`) |
| 0.11.0 | プロファイル: VRChat SDK3(共通)/ VRChat SDK3(ワールド)/ Udon / UdonSharp |
| 0.10.0 | プロファイル: ProBuilder |
| 0.9.0 | プロファイル作成(`uap_profile_scaffold` / `uap_profile_validate` / `uap_skill_scaffold`)、アバター(`uap_avatar_stats` / `uap_ndmf_bake` / `uap_vrc_menu`)、一括実行(`uap_batch`)、テスト実行(`uap_test_run`)、パーティクル(`uap_particle_set`)、`uap_prefab_stage`。プロファイル: VRCFury / lilycalInventory / lilToon |
| 0.8.0 | ライセンスの追加許諾(共同制作者との共有)。機能追加なし |
| 0.7.0 | プロファイル: NDMF / AAO: Avatar Optimizer(Modular Avatar は全面改訂) |
| 0.6.0 | `uap_animator_override`、`uap_anim_create_clip` のキー補間 |
| 0.5.0 | `uap_animator_edit`: 遷移の詳細設定・`set_transition`・ステートのパラメータ / タグ / フラグ・レイヤー名変更・`remove_*` 系 |
| 0.4.0 | `uap_avatar_mask`、`uap_animator_edit` のサブステートマシンと Entry / Exit 遷移、`uap_anim_create_clip` の ON/OFF・参照カーブ |
| 0.3.0 | `uap_animator_blendtree`、`uap_animator_behaviour`、`uap_animator_edit` のレイヤー操作。プロファイル: Modular Avatar |
| 0.2.0 | プロファイル: RPG Maker Unite |
| 0.1.0 | 初版: プレハブ 5 本、ライトマップ / Bakery 2 本、アニメーション 5 本、UI 操作 4 本。プロファイル: VRChat SDK3(アバター)/ Bakery / Final IK / MagicaCloth2 / UniVRM |

---

## メンテナ向け(この文書の更新ルール)

- Pro にツールを足したら、同じブランチで **この文書の該当モジュールの表**、
  **短い版**、**「版ごとの追加分」**の 3 か所を更新する。プロファイルを足したら
  [PRO-PROFILES.md](PRO-PROFILES.md) も同様。冒頭の「対象版」もそのリリースの版に
  合わせる。
- `ci/check-pro-docs.sh` が、`ProToolProvider` / `ProTestRunnerToolProvider` が
  登録するすべてのツール名がこの文書に、`Editor/Profiles/*.json` のすべての
  `id` が PRO-PROFILES.md に載っていることを確認する
  (`.github/workflows/pro-docs-check.yml`)。名前の漏れは CI で落ちるが、説明文の
  古さまでは見ないので、変更した機能の説明は手で読み直す。
- この文書は公開ミラー(`ci/public-mirror/allowlist.txt` に明示的に含めている)
  に載る。`main` に入ると `mirror-core.yml` の docs-only 同期で上の公開 URL が
  更新される(Core のリリースを待たない)。**ここに書いてよいのは販売ページに
  載せる粒度の説明まで**で、実装の詳細(設計ノートの内容)は書かない。
