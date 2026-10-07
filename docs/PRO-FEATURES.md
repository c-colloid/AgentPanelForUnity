# Agent Panel Pro 収録機能一覧

<!--
メンテナ向け(読者には表示されません):
- この文書は BOOTH などの販売ページの元原稿です。公開ミラーとドキュメントサイト
  (https://agentpanel.futeikei.com/pro-features/)に載るので、書いてよいのは
  販売ページに載せる粒度の説明まで。実装の詳細(設計ノートの内容)は書かない。
- Pro にツールを足したら、同じブランチで「各モジュールのツール」の表、末尾の
  「販売ページ用テキスト」、「版ごとの追加分」を更新し、「対象版」をそのリリースの
  版に合わせる。プロファイルを足したら PRO-PROFILES.md も同様。
- ci/check-pro-docs.sh(.github/workflows/pro-docs-check.yml)が、登録されている
  すべての uap_* ツール名がこの文書にあること、「対象版: pro-vX.Y.Z」「pro-vX.Y.Z 時点」
  「Unity 操作ツール N 本」「N 件」が package.json と実数に一致することを確認する。
  説明文の古さまでは見ないので、変更した機能の説明は手で読み直す。
- 設計: docs/design-notes/2026-09-16-pro-docs-for-booth.md、
  docs/design-notes/2026-10-07-pro-docs-brushup.md
-->

**対象版: pro-v0.14.0**(2026-09-28 時点)。同梱プロファイルは [別文書](PRO-PROFILES.md) にあります。

Agent Panel Pro は、無料の [Agent Panel for Unity](../README.md)(Core)に **Unity 操作ツール 50 本** と **同梱プロファイル 18 件** を追加する有料パッケージです。ツールはすべて Core のチャット画面からエージェント(Claude Code など)が呼び出すもので、スクリプトを書かせることなく、プレハブ・アニメーション・ライトマップ・メッシュなどを直接編集させられます。

| | |
|---|---|
| 動作要件 | Unity 2022.3 LTS 以降(Unity 6 系を含む)、Agent Panel for Unity(Core) |
| 導入 | 購入時に受け取るレジストリ URL と製品キーを 設定 > Agent Panel Pro の更新 に入力すると、Package Manager または VCC / ALCOM から導入・更新できます([操作ガイド](guide/settings.md)) |
| ライセンス | PolyForm Internal Use License 1.0.0(内部利用と改変は可、再配布は不可)。共同制作者との共有についての追加許諾を含みます |

## モジュール一覧

ツールはモジュール単位で 設定 > Unity 連携 > Unity 操作(UapOps) からオン / オフできます。

| モジュール | できること | ツール数 |
|---|---|---|
| [プレハブ](#プレハブ) | プレハブの作成・変換、オーバーライドの適用と差し戻し、Prefab Mode の操作 | 7 |
| [アニメーション](#アニメーション) | AnimationClip と AnimatorController の作成・編集、BlendTree、AvatarMask、Humanoid 設定 | 8 |
| [ライトマップ](#ライトマップ) | Progressive Lightmapper と Bakery の非同期ベイク | 2 |
| [UI 操作](#ui-操作) | UI Toolkit 製のエディタウィンドウ(SDK 独自のウィンドウなど)の自動操作 | 4 |
| [プロファイル作成](#プロファイル作成) | 自作の拡張プロファイルと Claude Code スキルの下書き・検証 | 3 |
| [アバター](#アバター) | アバターの計測、NDMF の手動ベイク、VRChat メニューの編集 | 3 |
| [パーティクル](#パーティクル) | Particle System の全モジュールの読み書きとプリセット | 1 |
| [メッシュ・マテリアル](#メッシュマテリアル) | メッシュの生成・編集・ブーリアン・検証と修復、マテリアル・テクスチャ・シェーダーの生成、glTF の入出力 | 16 |
| [非破壊モデリング](#非破壊モデリング) | 形状ノードを組み合わせて作る編集可能なモデルと、ゲーム用アセットへの書き出し | 4 |
| [一括実行](#一括実行) | 複数のツール呼び出しを許可カード 1 枚で実行 | 1 |
| [テスト実行](#テスト実行) | EditMode テストの実行と失敗の報告 | 1 |

## 各モジュールのツール

ツール名はエージェントが呼ぶ名前で、利用者が打ち込む必要はありません。

### プレハブ

| ツール | できること |
|---|---|
| `uap_prefab_create` | シーンの GameObject からプレハブを作成し、インスタンスに置き換えます。元がプレハブインスタンスなら Variant になります |
| `uap_prefab_convert` | シーンに直接置いたオブジェクトを、既存プレハブのインスタンスに変換します。違いはオーバーライドとして残ります |
| `uap_prefab_get_overrides` | インスタンスのオーバーライド(プロパティの変更、追加したコンポーネントや子)を一覧します |
| `uap_prefab_apply_overrides` | オーバーライドをプレハブ本体に適用します(全部、または 1 件だけ) |
| `uap_prefab_revert_overrides` | すべてのオーバーライドをプレハブの値に戻します |
| `uap_prefab_revert_override` | オーバーライドを 1 件だけ戻します |
| `uap_prefab_stage` | Prefab Mode を開閉します。開いている間は通常のシーン操作ツールでプレハブの中身を編集できます |

### アニメーション

| ツール | できること |
|---|---|
| `uap_anim_create_clip` | AnimationClip を作成します。数値カーブ、オブジェクトの ON / OFF、スプライトやマテリアルの差し替え、キーの補間方法を指定できます |
| `uap_animator_edit` | AnimatorController を編集します。パラメータ・ステート・遷移(条件や Exit Time などの詳細も)・レイヤー・サブステートマシンの追加、変更、削除 |
| `uap_animator_blendtree` | BlendTree(1D / 2D / Direct)を作成・編集します |
| `uap_animator_behaviour` | ステートに StateMachineBehaviour(VRChat の Parameter Driver など)を付けて設定します |
| `uap_avatar_mask` | AvatarMask を作成・編集します |
| `uap_animator_override` | AnimatorOverrideController を作成し、クリップの差し替えを設定します |
| `uap_asset_set_property` | アセットやインポーターのプロパティを設定します(必要なリインポートまで行います) |
| `uap_avatar_configure` | モデルの Humanoid インポート設定を確認・変更します |

### ライトマップ

| ツール | できること |
|---|---|
| `uap_lightmap_bake` | Progressive Lightmapper で非同期にベイクします。事前にメモリの見積りを行い、収まらない設定は自動で調整して、メモリ不足で黙って失敗するのを防ぎます |
| `uap_bakery_bake` | Bakery GPU Lightmapper(導入時)の設定変更と非同期ベイク。全体 / 選択 / ライトプローブ / リフレクションプローブを対象に、品質プリセットを選べます |

### UI 操作

SDK が独自に持つエディタウィンドウ(UI Toolkit 製)を、エージェントに操作させるためのツールです。

| ツール | できること |
|---|---|
| `uap_editor_ui_list_windows` | 開いているエディタウィンドウを一覧します |
| `uap_editor_ui_dump` | ウィンドウの要素ツリー(ボタン、入力欄、値など)を取得します |
| `uap_editor_ui_click` | ウィンドウ内のボタンなどをクリックします |
| `uap_editor_ui_set_value` | 入力欄、トグル、ドロップダウンの値を設定します |

### プロファイル作成

| ツール | できること |
|---|---|
| `uap_profile_scaffold` | プロジェクトに入っている SDK 向けに、拡張プロファイルの下書きを作ります |
| `uap_profile_validate` | 自作の拡張プロファイルを検証します(検出条件が実際に当たるか、id の重複、参照しているツール名) |
| `uap_skill_scaffold` | Claude Code のスキルファイルを書き出します |

### アバター

| ツール | できること |
|---|---|
| `uap_avatar_stats` | 三角形数、マテリアル、ボーン、ブレンドシェイプ、PhysBone などを計測します。VRChat SDK があれば PC / Quest のパフォーマンスランクも。ベイク前後の差分比較もできます |
| `uap_ndmf_bake` | NDMF の手動ベイクを実行し、元との差分を返します |
| `uap_vrc_menu` | VRChat のエキスプレッションメニューとパラメータを読み書きします。コントロール数や同期パラメータの容量など、SDK の制限を書く前に検証します |

### パーティクル

| ツール | できること |
|---|---|
| `uap_particle_set` | Particle System の全モジュール(バーストを含む)を読み書きします。煙・炎・火花・雨・塵のプリセットで効果一式を一度に適用できます |

### メッシュ・マテリアル

**生成と編集**

| ツール | できること |
|---|---|
| `uap_mesh_create` | 数値からメッシュを生成します。プリミティブ、外形の押し出し、回転体、らせん、経路に沿った掃引、距離場(SDF)の合成、頂点と三角形の直接指定。Scene ビューに描いたスケッチ線も入力にできます |
| `uap_mesh_edit` | 既存メッシュをその場で変形します。範囲の移動、膨張、スムーズ、細分化、ノイズ、頂点カラー、ライトマップ UV。スケッチ線に沿って溝を掘る・盛り上げることもできます |
| `uap_mesh_boolean` | 2 つのメッシュの合成・交差・差分を新しいメッシュにします。連続して使っても三角形が増えにくい作りです |
| `uap_mesh_remesh` | 既存メッシュを、曲率や稜線に沿った四角形主体のメッシュに作り直します |
| `uap_cage_edit` | 四角形トポロジーのメッシュをポリゴン編集します(押し出し、インセット、ループカット、ブリッジ、三角形から四角形へ) |

**検査と修復**

| ツール | できること |
|---|---|
| `uap_mesh_inspect` | 頂点数、大きさ、閉じているか、指定位置に近い頂点などを調べます |
| `uap_mesh_validate` | 退化した三角形、非多様体エッジ、裏返った面、開いた辺などの不整合を重大度つきで報告します |
| `uap_mesh_repair` | 検証で見つかった不整合を修復します(不要な頂点や三角形の除去、巻き方向の統一、法線の再計算、溶接、穴埋め) |
| `uap_scene_zfight_scan` | シーン内で同一平面に重なっている面(Z ファイティングの原因)を検出し、対処案を返します |

**マテリアル・テクスチャ・シェーダー**

| ツール | できること |
|---|---|
| `uap_material_create` | マテリアルを作成します。Lit / Unlit と、Pro 同梱シェーダー(頂点カラー、窓の室内表現、デカール、コマ送り、トリプラナー)のプリセット。Built-in / URP / HDRP を自動判別します |
| `uap_material_set` | マテリアルのシェーダープロパティとキーワードを一括設定します。シェーダーのプロパティ一覧も取得できます |
| `uap_texture_create` | 手続きテクスチャを PNG で生成し、インポート設定まで行います。単色、グラデーション、チェッカー、ノイズ、パレットアトラス、室内表現用のキューブマップ |
| `uap_shader_create` | 同梱のシェーダー雛形を `.shader` アセットとして書き出します(パイプラインに合わせた版を選びます) |

**入出力・移行**

| ツール | できること |
|---|---|
| `uap_mesh_export` | メッシュを glTF(.glb)に書き出します。Blender や外部ツールとの受け渡しに使えます |
| `uap_mesh_import` | glTF(.glb / .gltf)を Mesh として取り込みます。取り込んだメッシュはそのまま編集やブーリアンに使えます |
| `uap_forge_migrate` | 旧モデリング方式(Forge)のシーンを非破壊モデルに移行します |

### 非破壊モデリング

`Model` コンポーネントの下に形状ノードを階層で置き、合成・交差・差分とミラー・反復などの修飾で形を作る、編集可能なモデルです。編集はエディタ上でライブプレビューされ、最後にゲーム用のメッシュに焼きます。

| ツール | できること |
|---|---|
| `uap_model_inspect` | モデルの構造(ノード、形状、パラメータ)と、前回からの変更点を読み取ります |
| `uap_model_apply` | ノードの作成・変更・削除、マテリアルスロットと頂点カラー、変数と式によるパラメトリックな定義を 1 回の Undo で適用します |
| `uap_model_evaluate` | モデルをメッシュ化してプレビューに反映します |
| `uap_model_bake` | ゲーム用アセットに焼きます。三角形予算までの削減、UV 展開、法線 / AO のベイク、Mesh・テクスチャ・マテリアルの書き出し |

### 一括実行

| ツール | できること |
|---|---|
| `uap_batch` | 複数のツール呼び出し(200 件まで)を 1 回・許可カード 1 枚で実行します。実行前に全件を検証するので、1 件でも不正なら何も実行されません |

### テスト実行

| ツール | できること |
|---|---|
| `uap_test_run` | EditMode テストを実行し、失敗したテストの名前・メッセージ・スタックを返します。Unity Test Framework が入っているプロジェクトでのみ使えます |

## 同梱プロファイル

18 件。対応 SDK がプロジェクトに入っていると自動で有効になり、その SDK の要点をエージェントに教えます。一覧は [PRO-PROFILES.md](PRO-PROFILES.md) を参照してください。

## 版ごとの追加分

修正だけの版は載せていません。

| 版 | 追加されたもの |
|---|---|
| 0.14.0 | 非破壊モデリング(`uap_model_*` 4 本)、glTF の入出力、マテリアル・テクスチャ・シェーダーの生成、四角形主体リメッシュとケージ編集、Forge シーンの移行、パーティクルのプリセット、`uap_prefab_convert`。プロファイル: モデリング作法 |
| 0.13.0 | Scene ビューのスケッチ線をメッシュの生成・編集の入力に |
| 0.12.0 | メッシュモジュール(生成、編集、ブーリアン、検証と修復、Z ファイティング検出) |
| 0.11.0 | プロファイル: VRChat SDK3(共通 / ワールド)、Udon、UdonSharp |
| 0.10.0 | プロファイル: ProBuilder |
| 0.9.0 | プロファイル作成、アバター、一括実行、テスト実行、パーティクル、`uap_prefab_stage`。プロファイル: VRCFury、lilycalInventory、lilToon |
| 0.8.0 | ライセンスの追加許諾(共同制作者との共有) |
| 0.7.0 | プロファイル: NDMF、AAO: Avatar Optimizer(Modular Avatar を全面改訂) |
| 0.6.0 | `uap_animator_override`、AnimationClip のキー補間 |
| 0.5.0 | AnimatorController の遷移詳細、削除系の操作 |
| 0.4.0 | `uap_avatar_mask`、サブステートマシンと Entry / Exit 遷移、AnimationClip の ON / OFF と参照カーブ |
| 0.3.0 | BlendTree、StateMachineBehaviour、レイヤー操作。プロファイル: Modular Avatar |
| 0.2.0 | プロファイル: RPG Maker Unite |
| 0.1.0 | 初版: プレハブ、ライトマップ / Bakery、アニメーション、UI 操作。プロファイル: VRChat SDK3(アバター)、Bakery、Final IK、MagicaCloth2、UniVRM |

## 販売ページ用テキスト

<details>
<summary>BOOTH などに貼るプレーンテキスト(Markdown 記法なし)</summary>

```text
■ Agent Panel Pro 収録機能(pro-v0.14.0 時点)
Agent Panel for Unity(無料・Core)に追加する Unity 操作ツール 50 本と、主要アセット向けの同梱プロファイル 18 件。
すべて Core のチャット画面からエージェントが呼び出すツールで、スクリプトを書かせずに Unity を直接操作させられます。

・プレハブ: 作成(Variant 対応)/既存プレハブのインスタンスへの変換/オーバーライドの一覧・適用・差し戻し/Prefab Mode の操作
・アニメーション: AnimationClip・AnimatorController・BlendTree・StateMachineBehaviour・AvatarMask・OverrideController の作成と編集/Humanoid 設定
・ライトマップ: Progressive Lightmapper と Bakery の非同期ベイク(メモリ見積りと自動調整つき)
・UI 操作: UI Toolkit 製エディタウィンドウ(SDK 独自のウィンドウなど)の自動操作
・プロファイル作成: 自作の拡張プロファイルと Claude Code スキルの下書き・検証
・アバター: 三角形数・マテリアル・ボーンなどの計測(VRChat の PC / Quest ランク対応)/NDMF の手動ベイク/VRChat メニュー・パラメータの編集
・パーティクル: Particle System の全モジュール編集と煙・炎・火花・雨・塵のプリセット
・メッシュ・マテリアル: メッシュの生成(プリミティブ・押し出し・回転体・掃引・SDF・スケッチ線から)・変形・ブーリアン・リメッシュ・検証と修復/Z ファイティング検出/マテリアル・手続きテクスチャ・シェーダー雛形の生成/glTF の入出力
・非破壊モデリング: 形状ノードを組み合わせて作る編集可能なモデルと、ゲーム用メッシュへの書き出し(三角形予算・UV・法線 / AO ベイク)
・一括実行: 複数のツール呼び出しを許可カード 1 枚で実行
・テスト実行: EditMode テストの実行と失敗の報告
・同梱プロファイル 18 件: VRChat SDK3(共通 / アバター / ワールド)、Udon、UdonSharp、NDMF、Modular Avatar、AAO: Avatar Optimizer、VRCFury、lilycalInventory、lilToon、UniVRM、MagicaCloth2、Final IK、Bakery、ProBuilder、RPG Maker Unite、モデリング作法

動作要件: Unity 2022.3 LTS 以降(Unity 6 系を含む)、Agent Panel for Unity(Core)
一覧の詳細: https://agentpanel.futeikei.com/pro-features/
同梱プロファイルの詳細: https://agentpanel.futeikei.com/pro-profiles/
```

</details>
