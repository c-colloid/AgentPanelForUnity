# 2026-09-08 -- EditMode フルスイートの順序依存失敗 4 件

発端: AITemp サンドボックスでフルスイート(`-runTests -testPlatform EditMode`)
を回すと次の 4 件が落ちるが、`-testFilter "FontLoaderTests|SceneMarkerPinTests"`
で同じツリーを回すと通る。

- `FontLoaderTests.AtlasGuardTick_RebuildsABrokenAsset_Once_ThenThrottles`
  ("a broken asset must be rebuilt" Expected True but was False)
- `FontLoaderTests.AtlasGuardTick_ReStampsAUsedTextureThatLostItsStamp_EvenWhenCountIsUnchanged`
  (MissingReferenceException: Material destroyed)
- `FontLoaderTests.AtlasGuardTick_ReStampsOnlyWhenAtlasCountChanged`
  (Expected True but was False)
- `SceneMarkerPinTests.PinsInStore_GetReusableNumbers_IndependentOfIds`
  (Expected "P1  pin" but was the Japanese label)

## 1. 原因の実証

前回のフルラン結果 `AITemp/Logs/full3-results.xml` の文書順(= 実行順)から
確定した。

### 1a. FontLoader: ヒール抑制タイマーがフィクスチャをまたいで残る

- NUnit はフィクスチャ内のテストを **大文字小文字を無視した名前順** で回す。
  `Rebuilds...` は `ReStamps...` 2 件より **先** に走る(序数比較だと逆)。
- 直前のフィクスチャ `FontFixBridgeTests` の `GuardHeal_*` 3 件がそれぞれ
  ヒールを実行し、`FontLoader._lastHealAt`(2 秒のスロットル、static)を
  armed のまま残す。`ResetJapaneseUiCache` はこれを消さない。両フィクスチャは
  同一秒内に走る。
- `Rebuilds` は material を破棄してから `AtlasGuardTickOnce()` を呼ぶが、
  スロットル中なので false → 失敗。テスト末尾の `ResetJapaneseUiCache()`
  に届かず、**キャッシュ済みアセットが material 破棄済みのまま残る**。
- 続く `ReStampsAUsedTexture` は `asset.material.hideFlags = None` で
  MissingReferenceException、`ReStampsOnly` は unhealthy+throttled で tick
  が false。3 件目以降は 1 件目の連鎖。

### 1b. L10n: ウィンドウ生成フィクスチャが言語設定を適用したまま去る

- `PermissionWindow.CreateGUI` / `AgentPanelWindow.CreateGUI` は
  `L10n.ApplyFromSettings(PanelStateStore.instance.Settings.language)` を呼ぶ
  (2026-08-01 の起動時言語適用)。サンドボックスの永続設定は `Auto` で、
  この OS では Japanese に解決される。
- `PermissionWindowFontScaleTests` / `PermissionWindowRebuildTests`(順序 96, 97)
  以降、`SceneMarkerPinTests`(106)まで誰も L10n を戻さない。
  AgentPanelWindow 系(順序 1x)も同じ漏れを起こすが、`L10nTests`(75)の
  SetUp/TearDown がたまたま英語に戻していたため隠れていた。
- `SceneMarkerPin.CreatePinMarker` のラベルは `L10n.S.CtxPinMarkerLabel`。

## 2. 選択肢

1. 各フィクスチャで状態を戻す(TearDown で `L10n.OverrideForTests(null)` /
   `FontLoader.ResetHealThrottleForTests()`)+ 失敗側テストが前提条件を自分で
   確立する。**採用**。漏れの発生源と依存側の両方が自明になる。
2. アセンブリ全体の `ITestAction` で全テスト後に L10n / FontLoader をリセット。
   一括だが漏れを隠す。将来フィクスチャが増え続けるなら再検討。
3. `ApplyFromSettings` をテスト実行中は無効化する本体側の分岐。本体に
   テスト都合を持ち込むので不採用。

## 3. 実装(回帰ガード込み)

- `FontLoaderTests`: SetUp/TearDown で `ResetHealThrottleForTests()`、SetUp で
  unhealthy なキャッシュ済みアセットを破棄して再解決。`Rebuilds` テストは
  前提としてスロットルを明示的に解除し、`finally` でキャッシュを再解決する
  (失敗しても次のテストへ破棄済み material を渡さない)。
- `FontFixBridgeTests`: TearDown で `ResetHealThrottleForTests()`。
- `SceneMarkerPinTests`: SetUp で English を pin、TearDown で解除
  (期待値が英語文字列である以上、OS 言語に依存させない)。
- CreateGUI を呼ぶ 9 フィクスチャ(`AgentPanelWindow{,FontScale,StyleSheet,
  ViewState}Tests`, `PermissionWindow{FontScale,Rebuild}Tests`,
  `SettingsView{Layout,SectionDisclosure,SectionIcon}Tests`): TearDown で
  `L10n.OverrideForTests(null)`。`ViewStateTests` は
  `CreateGUI_AppliesThePersistedLanguageSetting` が `ApplyFromSettings` を
  必要とするので SetUp では pin しない。
- 各フィクスチャの doc comment に回帰ノートを追記。

## 4. 検証

AITemp のフルスイート(junction を作業ツリーへ付け替えて実行)で 0 failure を
確認する。結果は `AITemp/Logs/fix1-results.xml`。
