# 2026-09-08 -- FontFix 経由のフォントが NewScene で壊れ、修復後も表示が崩れる

発端: ユーザー報告「NewScene を開くと `NullReferenceException` が
`UIRStylePainter.DrawTextInfo` から出て表示が崩れる」。同時に

```
[AgentPanel] The CJK UI font's atlas or material was destroyed
(source: fontfix:osasset:Yu Gothic UI); rebuilt it as fontfix:osasset:Yu Gothic UI.
```

が出ていた。ソースが `fontfix:` なので UITK Font Fix が導入された環境で、
パネルは 2026-09-06 のブリッジ経由で FontFix の FontAsset を使っている。

## 1. 原因(2 段)

### 1.1 FontFix 所有アセットの子オブジェクトを誰もスタンプしていなかった

2026-08-03 の測定どおり、`FontAsset.CreateFontAsset` の子(マテリアルと
アトラステクスチャ)は hideFlags なしで生まれ、TextCore はグリフを描くたびに
アトラスページを **遅延追加** する。NewScene はフラグなしのオブジェクトを
破棄する。

- FontFix(`FontAssetLifecycle`)は自分のアセットの子に `DontSave` を打つが、
  打ち直すのは **FontFix 自身の getter が読まれた時** と Play Mode 遷移だけ。
  FontFix の API ドキュメントも「キャッシュせず毎回読め」と書いている。
- パネルは `FontLoader.JapaneseUiFontAsset` で一度読んでキャッシュし、以後
  FontFix を読まない。
- パネルのアトラス監視(`AtlasGuardTickOnce`)は「FontFix がライフサイクルを
  持つ」という理由で FontFix 所有アセットを **スタンプ対象外** にしていた
  (2026-09-06 の設計)。

結果、パネルの描画で増えたアトラスページはすべてフラグなしのまま NewScene で
破棄され、アセットは `IsHealthy` false になる。`DrawTextInfo` の NRE は
2026-08-03 の表 #10 と同じ「子が壊れたアセット」の一症状。

### 1.2 修復が同一インスタンスの in-place 修復になり、UITK が再生成しない

guard は壊れたアセットを検知すると `ResetJapaneseUiCache` → 全ウィンドウに
再適用、で FontFix の getter を読み直す。FontFix はこの読み取りで
**同じインスタンスをその場で修復**(`TryRepair`: マテリアル再生成、
`ClearFontAssetData`)して返す。UITK は各 `TextElement` のテキスト生成結果を
`TextGenerationSettings` のハッシュ(FontAsset のインスタンスを含む)で
キャッシュしているので、同じインスタンスを再適用してもスタイル変化として
扱われず、画面上のラベルは破棄済みアトラスを参照したままになる。ログは
「rebuilt it as fontfix:...」と出るのに表示が崩れたまま、という報告と一致する。

## 2. 対策

- `FontLoader.ResolveJapaneseUi` の FontFix 分岐でも `AdoptSubObjects` を呼び、
  `AtlasGuardTickOnce` の FontFix 除外を撤去。FontFix 所有アセットもパネル
  自身のアセットと同じく、アトラス数の変化と使用中の子のフラグ欠落を毎フレーム
  監視してスタンプする。
- スタンプ判定は `IsProtected(flags)` = `DontSave` ビット集合を含むか、に緩和。
  パネルは `HideAndDontSave`、FontFix は `DontSave` を書くが、どちらも
  「新しいシーンをロードしても破棄されない」フラグなので両方を保護済みと
  みなす。`== HideAndDontSave` のままだと FontFix が読み直すたびに guard が
  毎フレーム書き戻す往復になる。
- `TryHealBrokenAsset` は FontFix 所有アセットなら `HealThroughFontFix` で
  安い順に試す:
  1. FontFix を読み直す(FontFix は読み取り時にその場で修復する)。別インスタンスが
     返ったら FontFix 側が再構築済み(`CachesInvalidated` → 既存ハンドラが再適用)。
  2. 同じインスタンスで健全、かつ **マテリアルが差し替わっている** 場合だけ
     in-place 修復を受け入れる。UITK の生成ハッシュはアセットとマテリアルを含むので
     差し替えで全ラベルが再生成される(FontFix 0.4.1 はこの理由で常に差し替える)。
     描画で例外を出したラベルは dirty でなくなっているので、パネルの全
     `TextElement` に `MarkDirtyRepaint` も掛ける(`AgentPanelWindow.MarkAllTextDirty`)。
  3. それ以外(0.4.0 がマテリアルを残したまま返した/まだ壊れている)は
     `FontFixBridge.RequestRebuild()`(反射で `FontFix.ResetCaches()`)。FontFix は
     所有アセットを破棄して `CachesInvalidated` を同期発火し、`OnFontFixInvalidated`
     が **新しいインスタンス** を全ウィンドウに再適用する。`ResetCaches` を持たない
     FontFix では従来どおりの読み直しにフォールバックする。
- `EditorSceneManager.newSceneCreated` / `sceneOpened` でも guard の tick を
  1 回走らせる。NewScene の破棄は同期で、次の `EditorApplication.update` より
  先にパネルの再描画が来るため、これまでは 1 回だけ例外が出てから修復して
  いた。2026-08-03 で「シーンコールバックだけに頼る」案は却下したが、これは
  update フックに **加える** 早期実行なので、取りこぼしは従来どおり update
  側が拾う。

## 2.1 UITK Font Fix 0.4.1 との整合

同日に FontFix 側も同じ症状を 0.4.1 で修正した
(`fa237b6` "Protect lazily added atlas pages across scene loads; repair
regenerates text")。内容は (a) `EditorApplication.update` でページ数を比較して
新規ページに `DontSave` を打つ、(b) `newSceneCreated` / `sceneOpened` と 0.25 秒
周期の生存確認で `VerifyOwned`、(c) in-place 修復で常にマテリアルを差し替え、全
EditorWindow の該当 `TextElement` を `MarkDirtyRepaint`。

パネル側との関係:

- **スタンプは二重だが衝突しない。** FontFix は `DontSave`、パネルは
  `HideAndDontSave` を書くが、パネルの判定は `DontSave` ビット集合の有無なので
  FontFix が書いた直後にパネルが書き戻すことはない。FontFix はページ数が増えた
  時と VerifyOwned 時にしか書かないので、逆方向の往復も起きない。
- **修復は FontFix が先に走る。** FontFix のシーンフックはパネルが最初に
  FontFix を読んだ時(パネルの guard 登録より前)に登録されるので、
  `newSceneCreated` では FontFix の in-place 修復が先に完了し、パネルの tick は
  健全なアセットを見る(`ClearFontAssetData` でページ数が 1 に戻るのでパネルは
  スタンプし直すだけ)。update 側の生存確認はパネルが毎フレーム、FontFix が 0.25
  秒周期なので、シーンフック外の破壊(Bakery のベイクループなど)はパネルが先に
  見つけることがあるが、その場合も上記 1〜2 で FontFix の in-place 修復を受け入れ、
  0.4.0 のときだけ 3 で再構築に落ちる。
- 0.4.0 のまま(in-place 修復がマテリアルを残す)でも 3 で必ず再生成される。

## 3. 検証

- `Tests/Editor/FontFixBridgeTests.cs` に追加:
  - `RequestRebuild_CallsProviderResetCaches_AndForwardsItsInvalidation` /
    `RequestRebuild_FalseWhenAbsent_OrWhenTheFacadeLacksResetCaches`
  - `FontLoader_StampsAFontFixOwnedAssetsSubObjects_OnResolveAndOnGuardTicks`
  - `IsProtected_AcceptsFontFixsDontSave_AndThePanelsHideAndDontSave`
  - `GuardHeal_RebuildsABrokenFontFixAsset_ThroughFontFix_WhenTheReadDoesNotRepairIt`
    (読み直しで直らなければ偽 FontFix の `ResetCaches` が呼ばれ、壊れた
    インスタンスが再利用されない)
  - `GuardHeal_AcceptsAnInPlaceRepair_ThatSwappedTheMaterial`(0.4.1 相当:
    同一インスタンス・新マテリアルはそのまま受け入れ、`ResetCaches` を呼ばない)
  - `GuardHeal_RejectsAnInPlaceRepair_ThatKeptTheMaterial`(0.4.0 相当:
    マテリアルが同じなら `ResetCaches` に昇格)
- この環境には Unity がないため、コンパイルと EditMode 実行は CI
  (`editmode-tests.yml`)と実機に委ねる。NewScene そのものの再現テストは
  2026-08-03 と同じ理由(他の EditMode テストを乱す)で入れていない。
