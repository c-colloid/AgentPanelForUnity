# Agent Panel Pro 同梱プロファイル一覧

<!--
メンテナ向け(読者には表示されません):
- この文書は BOOTH などの販売ページの元原稿です。公開ミラーとドキュメントサイト
  (https://agentpanel.futeikei.com/pro-profiles/)に載るので、書いてよいのは
  「何を教えるか」の要約まで。指示文そのもの(JSON の instructionLines)は Pro の
  中身なので転記しない。
- jp.colloid.agent-panel-pro/Editor/Profiles/*.json を足したら、同じブランチで
  該当カテゴリの表、末尾の「販売ページ用テキスト」、「版ごとの追加分」を更新し、
  「対象版」と件数を合わせる。指示文を書き換えたときも要約が古くなっていないか読み直す。
- ci/check-pro-docs.sh が、すべてのプロファイル id が「(`id`)」の形でこの文書に
  あること、「対象版: pro-vX.Y.Z」「pro-vX.Y.Z 時点」「N 件」が実数に一致することを確認する。
- 設計: docs/design-notes/2026-09-16-pro-docs-for-booth.md、
  docs/design-notes/2026-10-07-pro-docs-brushup.md
-->

**対象版: pro-v0.14.0**(2026-09-28 時点)。Pro のツール一覧は [別文書](PRO-FEATURES.md) にあります。

## 拡張プロファイルとは

Agent Panel for Unity は、プロジェクトに入っているサードパーティ SDK を検出すると、その SDK の要点(コンポーネントの正式名、ビルド時にしか反映されない仕組み、やってはいけない操作、確認の手順)をエージェントへの指示に自動で追加します。この「検出条件 + 指示文」のセットが **拡張プロファイル** です。

- Pro の **同梱プロファイル 18 件** は、Pro を入れるだけで有効になります。検出結果は 設定 > 拡張プロファイル に並びます。
- Core だけでも、プロジェクトに自作のプロファイルを置けます。Pro の `uap_profile_scaffold` / `uap_profile_validate` はその下書きと検証を行うツールです。
- 指示文はエージェントが読むものなので英語です。パネルの表示言語には依存しません。

## 一覧

「対応 SDK」は検出の目印です。UPM / VPM で入る SDK はパッケージ名、Asset Store から `Assets/` に入る SDK は代表的なコンポーネント名で検出します。

### VRChat

| プロファイル | 対応 SDK | エージェントが知ること |
|---|---|---|
| **VRChat SDK3 (Base)**(`vrchat-sdk-base`) | `com.vrchat.base` | PhysBone・Contact・VRC Constraint の所在、3 つの SDK に同名で存在する VRCStation の区別、アップロード時にホワイトリスト外のコンポーネントが除去されること |
| **VRChat SDK3 (Avatars)**(`vrchat-sdk3`) | `com.vrchat.avatars` | VRCAvatarDescriptor とエキスプレッションアセット、Parameter Driver などの StateMachineBehaviour、FX レイヤーの組み方 |
| **VRChat SDK3 (Worlds)**(`vrchat-sdk3-worlds`) | `com.vrchat.worlds` | VRCSceneDescriptor と標準のワールドコンポーネント、ロジックは Udon のみ、予約レイヤー、Build & Test / Publish は利用者が行うこと |
| **VRChat Udon**(`udon`) | `com.vrchat.worlds`(UdonBehaviour) | UdonBehaviour とプログラムアセットの関係、同期(UdonSynced・所有権・ネットワークイベント)、VRCUrl、Play モードでしか動かないこと |
| **UdonSharp (U#)**(`udonsharp`) | `com.vrchat.udonsharp` | U# で使えない C# 機能、プロキシと UdonBehaviour の関係、フィールドをどちらに書くか、GetComponent の落とし穴 |
| **NDMF**(`ndmf`) | `nadena.dev.ndmf` | ビルド時にクローンを変換する仕組み、手動ベイクでの確認、クローンではなく元を直すこと、フェーズ順、NDMF Console |
| **Modular Avatar**(`modular-avatar`) | `nadena.dev.modular-avatar` | Merge Armature / Bone Proxy / Menu Installer / Parameters の使い分け、リアクティブコンポーネント、別コントローラを Merge Animator で合流させる手順 |
| **AAO: Avatar Optimizer**(`avatar-optimizer`) | `com.anatawa12.avatar-optimizer` | Trace And Optimize を起点にすること、各コンポーネントの置き場所、触ってはいけない設定 |
| **VRCFury**(`vrcfury`) | `com.vrcfury.vrcfury` | 1 コンポーネントに全機能が入る構造と自動適用のタイミング、パネルから設定できる範囲とできない範囲 |
| **lilycalInventory**(`lilycalinventory`) | `jp.lilxyzw.lilycalinventory` | ビルド時にメニューとアニメーションを生成する仕組み、パラメータ型ごとのコンポーネント選択 |
| **lilToon**(`liltoon`) | `jp.lilxyzw.liltoon` | 機能を有効にするゲートの規則、レンダリングモードはシェーダー差し替えである点、lilToonSetting による機能削除 |

### アバター / 物理 / IK

| プロファイル | 対応 SDK | エージェントが知ること |
|---|---|---|
| **UniVRM (VRM 0.x / 1.0)**(`univrm`) | `com.vrmc.vrm` / `com.vrmc.univrm` | VRMMeta / Vrm10Instance / VRMSpringBone の役割、インポート・エクスポートのウィザードは利用者が行うこと |
| **MagicaCloth2**(`magicacloth2`) | `jp.magicasoft.magicacloth2` | MagicaCloth / MagicaBoneCloth / MagicaBoneSpring の使い分け、プリコンピュートは利用者が行うこと |
| **Final IK**(`finalik`) | FullBodyBipedIK などのコンポーネント | FullBodyBipedIK / CCDIK / AimIK の役割、ボーンチェーンの順序を先に確認すること |

### ライティング / メッシュ

| プロファイル | 対応 SDK | エージェントが知ること |
|---|---|---|
| **Bakery GPU Lightmapper**(`bakery`) | Bakery のライトコンポーネント | Bakery 固有のライトコンポーネント、`uap_bakery_bake` での設定とベイク、オブジェクト単位の制御、Contribute GI の確認 |
| **ProBuilder**(`probuilder`) | `com.unity.probuilder` | ProBuilderMesh が正であること、メニューでは形状を作れない理由、既存メッシュへの操作手順 |

### モデリング

| プロファイル | 対応 SDK | エージェントが知ること |
|---|---|---|
| **Modeling workflow**(`modeling`) | Agent Panel Pro 自身(常に有効。設定でオフにできます) | モデリングの進め方: まずプロップの一覧と三角形予算を決め、形状の種類に合わせて作り方(ケージ、距離場、ProBuilder、glTF 取り込み)を選び、仕上げの順序とマテリアル数の予算を守り、最後に結果を報告すること |

### ゲーム制作

| プロファイル | 対応 SDK | エージェントが知ること |
|---|---|---|
| **RPG Maker Unite**(`rpgmaker-unite`) | `jp.ggg.rpgmaker.unite` | ゲームデータの置き場所(JSON とマップ prefab)、エディタ UI の自動操作と CoreSystem 経由の生成、イベントコマンドや自律移動の落とし穴、Unity 6 への移行点 |

## 版ごとの追加分

| 版 | 追加されたプロファイル |
|---|---|
| 0.14.0 | Modeling workflow |
| 0.11.0 | VRChat SDK3 (Base)、VRChat SDK3 (Worlds)、VRChat Udon、UdonSharp |
| 0.10.0 | ProBuilder |
| 0.9.0 | VRCFury、lilycalInventory、lilToon |
| 0.7.0 | NDMF、AAO: Avatar Optimizer(Modular Avatar を全面改訂) |
| 0.3.0 | Modular Avatar |
| 0.2.0 | RPG Maker Unite |
| 0.1.0 | VRChat SDK3 (Avatars)、Bakery GPU Lightmapper、Final IK、MagicaCloth2、UniVRM |

## 販売ページ用テキスト

<details>
<summary>BOOTH などに貼るプレーンテキスト(Markdown 記法なし)</summary>

```text
■ 同梱プロファイル(pro-v0.14.0 時点、18 件)
対応 SDK がプロジェクトに入っていると自動で検出され、その SDK の要点(コンポーネントの正式名、ビルド時にしか反映されない仕組み、やってはいけない操作、確認の手順)がエージェントへの指示に追加されます。設定は不要です。

【VRChat】VRChat SDK3(共通 / アバター / ワールド)、Udon、UdonSharp、NDMF、Modular Avatar、AAO: Avatar Optimizer、VRCFury、lilycalInventory、lilToon
【アバター / 物理 / IK】UniVRM(VRM 0.x / 1.0)、MagicaCloth2、Final IK
【ライティング / メッシュ】Bakery GPU Lightmapper、ProBuilder
【モデリング】モデリング作法(Pro 自身で常に有効)
【ゲーム制作】RPG Maker Unite

一覧の詳細: https://agentpanel.futeikei.com/pro-profiles/
Pro のツール一覧: https://agentpanel.futeikei.com/pro-features/
```

</details>
