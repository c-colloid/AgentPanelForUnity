# Agent Panel Pro 同梱プロファイル一覧

**対象版: pro-v0.13.0**(2026-09-17 時点)。
公開 URL: <https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-PROFILES.md>
Pro のツール一覧は別文書 [PRO-FEATURES.md](PRO-FEATURES.md) にあります。

この文書は **販売ページ(BOOTH など)の「対応アセット / 同梱プロファイル」欄の
元原稿** です。プロファイルが増えたときはここだけを更新し、販売ページには上の
URL を載せるか、下の「短い版」をコピーして貼り直します。

## 拡張プロファイルとは

Agent Panel for Unity(Core)は、プロジェクトに入っているサードパーティ SDK を
検出すると、その SDK の要点(コンポーネントの正式名・「シーンに見えない」挙動・
やってはいけない操作・確認の手順)をエージェントへの指示に自動で追加します。
この「検出条件 + 指示文」のセットが **拡張プロファイル(Extension Profile)** です。

- **同梱プロファイル**(この文書の 17 件)は Pro を入れるだけで有効になり、
  承認操作は要りません。設定 > 拡張プロファイル に検出結果が並びます。
- Core だけでも `<project>/.uap-profiles/*.json` に自分でプロファイルを置けます
  (内容ハッシュの承認が必要)。Pro の `uap_profile_scaffold` /
  `uap_profile_validate` は、その自作プロファイルの下書きと検証を行うツールです。
- 指示文は英語です(エージェントが読むものなので、利用者の表示言語には
  依存しません)。

---

## 短い版(販売ページ貼り付け用)

そのままコピーして貼れるプレーンテキストです(Markdown 記法は使っていません)。

```text
■ 同梱プロファイル(pro-v0.13.0 時点、17 件)
対応 SDK がプロジェクトに入っていると自動で検出され、その SDK の要点(コンポーネントの正式名、ビルド時にしか反映されない仕組み、やってはいけない操作、確認の手順)がエージェントへの指示に追加されます。承認操作は不要です。

【VRChat】
・VRChat SDK3(共通 / com.vrchat.base): PhysBone・Contact・VRC Constraint の所在、3 つある VRCStation の区別、アップロード時のコンポーネント除去
・VRChat SDK3(アバター / com.vrchat.avatars): VRCAvatarDescriptor とエキスプレッションアセット、ParameterDriver などの StateMachineBehaviour、FX レイヤーの組み方
・VRChat SDK3(ワールド / com.vrchat.worlds): VRCSceneDescriptor と標準ワールドコンポーネント、レイヤー予約、Build & Test / Publish の扱い
・Udon: UdonBehaviour とプログラムアセットの関係、同期(UdonSynced / 所有権 / ネットワークイベント)、VRCUrl、Play モードでしか動かない前提
・UdonSharp(U#): 使えない C# 機能、プロキシと UdonBehaviour の関係、フィールドをどちらに書くか、GetComponent の落とし穴
・NDMF: ビルド時にクローンを変換する仕組み、手動ベイクでの確認、フェーズ順、NDMF Console
・Modular Avatar: Merge Armature / Bone Proxy / Menu Installer / Parameters、リアクティブコンポーネント、別コントローラを Merge Animator で合流させる手順
・AAO: Avatar Optimizer: Trace And Optimize を起点にする、各コンポーネントの置き場所、触ってはいけない設定
・VRCFury: 1 コンポーネントに全機能が入る構造と自動適用のタイミング、パネルから設定できる範囲とできない範囲
・lilycalInventory: ビルド時にメニューとアニメーションを生成する仕組み、パラメータ型ごとのコンポーネント選択
・lilToon: _Use* ゲートを先に立てる規則、レンダリングモードはシェーダー差し替えである点、lilToonSetting による機能削除

【アバター / 物理 / IK】
・UniVRM(VRM 0.x / 1.0): VRMMeta / Vrm10Instance / VRMSpringBone、ウィザードは手動
・MagicaCloth2: MagicaCloth / MagicaBoneCloth / MagicaBoneSpring、プリコンピュートは手動
・Final IK: FullBodyBipedIK / CCDIK / AimIK、ボーンチェーンの確認

【ライティング / メッシュ】
・Bakery GPU Lightmapper: Bakery のライトコンポーネント、uap_bakery_bake での非同期ベイクと設定、オブジェクト単位の制御、Contribute GI の確認
・ProBuilder: ProBuilderMesh が正、メニューでは形状を作れない理由、既存メッシュへの操作と Editor スクリプトでの生成手順

【ゲーム制作】
・RPG Maker Unite: データは Storage の JSON とマップ prefab、CoreSystem サービスでの生成手順、イベントコマンド・自律移動・タイマーの実測済みの落とし穴、Unity 6 での移行点

一覧の詳細: https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-PROFILES.md
Pro のツール一覧: https://github.com/c-colloid/AgentPanelForUnity/blob/main/docs/PRO-FEATURES.md
```

---

## 詳細

「検出」列はプロファイルが有効になる条件です。UPM / VPM パッケージとして入る
SDK は package id で、Asset Store の `.unitypackage` として `Assets/` に入る
SDK はコンパイル済みの型名で検出します(どちらか一方が当たれば有効)。
「追加」列はそのプロファイルが最初に入った Pro の版です。

### VRChat

| プロファイル(id) | 検出 | エージェントに教えること | 追加 |
|---|---|---|---|
| **VRChat SDK3 (Base)**(`vrchat-sdk-base`) | `com.vrchat.base` / `VRCPhysBone` など | SDK の共有部分であり、これだけではアバター用かワールド用か分からないこと。PhysBone / Contact / VRC Constraint は base 側にあってワールドでも動くこと。`VRCStation` は 3 つの SDK に同名の型があるので完全修飾で指すこと。アップロード時にホワイトリスト外のコンポーネントが**黙って削除**されること | 0.11.0 |
| **VRChat SDK3 (Avatars)**(`vrchat-sdk3`) | `com.vrchat.avatars` / `VRCAvatarDescriptor` | `VRCAvatarDescriptor` とエキスプレッションメニュー / パラメータのアセット。`VRCAvatarParameterDriver` などは StateMachineBehaviour なので `uap_animator_behaviour` で付けること。FX / ジェスチャーレイヤーは `uap_animator_edit` と Direct BlendTree で組むこと。アップロード窓は手動 | 0.1.0 |
| **VRChat SDK3 (Worlds)**(`vrchat-sdk3-worlds`) | `com.vrchat.worlds` / `VRCSceneDescriptor` など | ワールド用プロジェクトであること。`VRCSceneDescriptor` と `VRCPickup` / `VRCObjectSync` / `VRCObjectPool` / `VRCStation` などの標準コンポーネント。素の MonoBehaviour はアップロードで除去されるのでロジックは Udon のみ。VRChat が予約するレイヤー番号。Build & Test / Build & Publish は SDK コントロールパネルで利用者が行うこと | 0.11.0 |
| **VRChat Udon**(`udon`) | `com.vrchat.worlds` / `VRC.Udon.UdonBehaviour` | `UdonBehaviour` はプログラムアセットがあって初めて動くこと。公開変数は `uap_property_set` では書けないこと(U# ならプロキシ側に書く)。同期の宣言(`UdonBehaviourSyncMode` / `UdonSynced` / 所有権 / `SendCustomNetworkEvent`)。`VRCUrl` はエディタ時にしか作れないこと。Play モードでしか動かず、U# のコンパイルエラーは Unity のコンパイルとは別に出ること | 0.11.0 |
| **UdonSharp (U#)**(`udonsharp`) | `com.vrchat.udonsharp` / `UdonSharp.UdonSharpBehaviour` | U# で使えない C# 機能(ジェネリック・インターフェース・オーバーロード・`List<T>`)。`UdonSharpBehaviour` プロキシと空の `UdonBehaviour` の対が正常な状態であること。フィールドはプロキシ側に書くこと。`GetComponent<T>()` が SDK3 コンポーネントに使えず実行時に落ちること | 0.11.0 |
| **NDMF**(`ndmf`) | `nadena.dev.ndmf` / `GeneratedAssets` など | NDMF プラグインはシーンを編集せず、アップロード / Play / 手動ベイク時にクローンを変換すること。ビルド結果を見るには選択 → `Tools/NDM Framework/Manual bake avatar`。クローンを直さず元を直すこと。`INDMFEditorOnly` の除去とフェーズ順(Resolving → Generating → Transforming → Optimizing)。エラーは NDMF Console に出ること | 0.7.0 |
| **Modular Avatar**(`modular-avatar`) | `nadena.dev.modular-avatar` / `ModularAvatarMergeArmature` など | 非破壊なのでシーンが「合体して見えない」のを手で直さないこと。Merge Armature / Bone Proxy / Merge Animator / Menu Installer / Parameters の使い分け。ON/OFF・ブレンドシェイプ・マテリアルはリアクティブコンポーネントで済ませること。アニメーターが要るときは別コントローラを作って Merge Animator で合流させること。`ModularAvatarMMDLayerControl` は StateMachineBehaviour であること。手動ベイクでの確認手順 | 0.3.0(0.7.0 で全面改訂) |
| **AAO: Avatar Optimizer**(`avatar-optimizer`) | `com.anatawa12.avatar-optimizer` / `TraceAndOptimize` など | 最適化はシーンに見えず、シーンの数字は最適化前であること。型名は `Anatawa12.AvatarOptimizer.` で完全修飾すること。まずアバタールートに Trace And Optimize を置くこと。手動コンポーネントの置き場所(レンダラ必須のものと対象に付けるもの)。見た目が変わるのは AAO のバグとして扱うこと、危険なトグルと Debug Options には手を出さないこと | 0.7.0 |
| **VRCFury**(`vrcfury`) | `com.vrcfury.vrcfury` / `VF.Model.VRCFury` など | アップロード前と Play 開始時に自動適用され、手動ベイクもクローンも無いこと。ほぼ全機能が 1 コンポーネント `VF.Model.VRCFury` の `[SerializeReference]` に入るため、パネルからは追加はできても設定はできないこと。SPS / Global Collider などの単体コンポーネントは普通に扱えること。確認は利用者の Play / アップロードで | 0.9.0 |
| **lilycalInventory**(`lilycalinventory`) | `jp.lilxyzw.lilycalinventory` / `ItemToggler` など | メニュー・パラメータ・アニメーションをビルド時に生成するので手書きしないこと。型名は `jp.lilxyzw.lilycalinventory.runtime.` で完全修飾すること。生成するパラメータ型でコンポーネントを選ぶこと(ItemToggler / CostumeChanger / SmoothChanger / AutoDresser / Prop / MenuFolder / Preset)。メニュー項目でないコンポーネントの役割。NDMF の手動ベイクでの確認 | 0.9.0 |
| **lilToon**(`liltoon`) | `jp.lilxyzw.liltoon` / `lilToonSetting` | マテリアルは普通のマテリアルなので `uap_material_set` で扱えること。まず `op:"list"` で実際のシェーダーとプロパティを確認すること。**規則 1**: 機能は `_Use*` ゲートを 1 にするまで効かないこと。**規則 2**: レンダリングモードはプロパティではなくシェーダーアセットの差し替えなので利用者に頼むこと。`lilToonSetting` で機能が削られている可能性 | 0.9.0 |

### アバター / 物理 / IK

| プロファイル(id) | 検出 | エージェントに教えること | 追加 |
|---|---|---|---|
| **UniVRM (VRM 0.x / 1.0)**(`univrm`) | `com.vrmc.vrm` / `com.vrmc.univrm` / `VRMMeta` など | `VRMMeta`(0.x)/ `Vrm10Instance`(1.0)/ `VRMSpringBone` の役割。型名は `uap_query_component_types` で確認すること。インポート / エクスポートのウィザードは操作できないので利用者に頼むこと | 0.1.0 |
| **MagicaCloth2**(`magicacloth2`) | `jp.magicasoft.magicacloth2` / `MagicaCloth` など | `MagicaCloth` / `MagicaBoneCloth` / `MagicaBoneSpring` の使い分け。プリコンピュートなどのエディタユーティリティは操作できないので、プロパティ編集後に利用者に頼むこと | 0.1.0 |
| **Final IK**(`finalik`) | `FullBodyBipedIK` / `CCDIK` / `AimIK`(型検出) | Asset Store 製で package id が無いこと。主要コンポーネントの役割。IK はボーンチェーンの順序に敏感なので、追加・編集の前に階層を確認すること | 0.1.0 |

### ライティング / メッシュ

| プロファイル(id) | 検出 | エージェントに教えること | 追加 |
|---|---|---|---|
| **Bakery GPU Lightmapper**(`bakery`) | `BakeryPointLight` / `ftLightmapsStorage` など(型検出) | Bakery のライトは別コンポーネント(`BakeryDirectLight` など)であること。ベイクは `uap_bakery_bake` で行い、設定名(bounces / samples / texelsPerUnit / renderMode / renderDirMode ...)とプリセット。`start` は即時に返るので `status` で待つこと、Lighting 設定の自動修復。オブジェクト単位の制御コンポーネント。Contribute GI の確認 | 0.1.0 |
| **ProBuilder**(`probuilder`) | `com.unity.probuilder` / `ProBuilderMesh` など | `ProBuilderMesh` が正であり、`MeshFilter` に直接書いても上書きされること。New Shape などのメニューは対話ツールなので形状を作れないこと。既存メッシュへの操作(Object / Geometry / Export)は選択してからメニュー実行すること。パラメータから作るなら `ShapeGenerator` などを使う Editor スクリプトを `uap_scripts_commit` すること(コンパイル無しなら Pro の `uap_mesh_create`)。ProBuilder メッシュはアセットではなくシーンデータであること | 0.10.0 |

### ゲーム制作

| プロファイル(id) | 検出 | エージェントに教えること | 追加 |
|---|---|---|---|
| **RPG Maker Unite**(`rpgmaker-unite`) | `jp.ggg.rpgmaker.unite` / `RpgMakerEditorParam` など | ゲームデータは `Assets/RPGMaker/Storage` の JSON とマップ prefab にあり、シーンの GameObject を触っても変わらないこと。RMU 自身のエディタウィンドウ(UI Toolkit)は `uap_editor_ui_*` で操作できるが、大量生成は CoreSystem サービスで行うこと。コードでマップを作るときの必須手順(レイヤーマネージャの追加・タイルの座標系・保存と Hierarchy 更新)。イベントコマンドのコード表と分岐・通行・画像の落とし穴。自律移動・接触・タイマーの実測済みの挙動。Unity 6 で必要な移行点 | 0.2.0 |

---

## 版ごとの追加分

| 版 | 追加されたプロファイル |
|---|---|
| 0.11.0 | VRChat SDK3 (Base) / VRChat SDK3 (Worlds) / VRChat Udon / UdonSharp |
| 0.10.0 | ProBuilder |
| 0.9.0 | VRCFury / lilycalInventory / lilToon |
| 0.7.0 | NDMF / AAO: Avatar Optimizer(Modular Avatar を全面改訂) |
| 0.3.0 | Modular Avatar |
| 0.2.0 | RPG Maker Unite |
| 0.1.0 | VRChat SDK3 (Avatars) / Bakery GPU Lightmapper / Final IK / MagicaCloth2 / UniVRM |

---

## メンテナ向け(この文書の更新ルール)

- `jp.colloid.agent-panel-pro/Editor/Profiles/*.json` を足したら、同じブランチで
  **該当カテゴリの表**、**短い版**、**「版ごとの追加分」**を更新し、冒頭の
  「対象版」と件数を合わせる。既存プロファイルの指示文を書き換えたときも、
  「教えること」の要約が古くなっていないか読み直す。
- `ci/check-pro-docs.sh`(`.github/workflows/pro-docs-check.yml`)が、すべての
  プロファイル `id` がこの文書に載っていることを確認する。
- この文書は公開ミラー(`ci/public-mirror/allowlist.txt` に明示的に含めている)
  に載る。**載せてよいのは「何を教えるか」の要約まで**で、指示文そのもの
  (JSON の `instructionLines`)は Pro の中身なので転記しない。
