# UITK Font Fix ブリッジ(2026-09-06)

## 症状

ユーザー報告: 「フォントが UITKFontFix の設定を読み込んでいないように見える」。

## 原因

パネルのフォント解決 `Editor/UI/FontLoader.cs` は UITK Font Fix
(`jp.colloid.uitk-font-fix`)の前身で、候補フォント名のチェーン
(`OsJapaneseUiFontNames`)をパッケージ内にハードコードしている。パッケージ
依存ゼロの方針から UITKFontFix を一切参照しておらず、プロジェクトが
`ProjectSettings/Packages/jp.colloid.uitk-font-fix/settings.json` で
フォント名リスト・スタイル名・Bold 面の配線を設定していても、パネルには
届かない。両者が同じ既定チェーンを持つ環境では違いが見えず、設定を変えた
途端に「読み込んでいない」と見える。

## 設計

依存ゼロは維持し、**反射による任意ブリッジ**にする。

- `Editor/UI/FontFixBridge.cs`: `Colloid.UitkFontFix.FontFix,
  Colloid.UitkFontFix.Editor` を `Type.GetType` で探し、
  `CjkUiFontAsset` / `CjkUiFontSource` / `CachesInvalidated` を反射で読む。
  形が違う(将来の API 変更)場合は「未導入」として扱う。
- `FontLoader.ResolveJapaneseUi` の第 0 段としてブリッジの FontAsset を
  優先する。ソース文字列は `fontfix:osasset:<family>`。この資産のライフ
  サイクル(Play Mode 修復・リロード時の破棄)は FontFix 側が持つので、
  パネル独自のスタンプ/アトラス監視は掛けない。
- FontFix の `CachesInvalidated`(設定 UI やコードでの変更、ResetCaches、
  修復不能時の再構築)を受けてパネルのキャッシュを捨て、
  `AgentPanelWindow.ReapplyContentRootStyling` で開いている全ウィンドウに
  再適用する。FontFix が「CJK なし」を返すようになった場合は、ルートの
  インライン定義を外して破棄済み資産を参照し続けないようにする
  (`ApplyJapaneseUi` の解除規則。パッケージ未導入時は従来どおり no-op)。
- **モノスペースはブリッジしない**。FontFix は OS 由来のモノフォントを
  無効化時に破棄し、購読者が全リーフを再適用する前提だが、パネルのモノ
  指定はコードブロックやツールプレビューなど多数のリーフにインラインで
  載っており再訪の仕組みがない。既定の解決結果(エディタ同梱 RobotoMono)
  は両者で同じなので、実害もない。
- 設定画面の外観診断は `Detected font: <family> (via UITK Font Fix)` /
  `検出されたフォント: <family>(UITK Font Fix 経由)` で経路を明示する。
- パネル側の「CJK フォントを優先」トグルは引き続きゲートとして働く
  (OFF なら FontFix があっても適用しない)。

## 検証

- `Tests/Editor/FontFixBridgeTests.cs`: 同じメンバー形状のスタンドイン型で
  未導入/形違い/導入の 3 状態、FontLoader の優先・接頭辞・キャッシュ・
  無効化通知での再解決、導入時未解決での解除規則を固定。
  `FontLoaderTests` は各テストでブリッジを切り、パネル独自経路を検証する。
- 実機(Unity 2022.3.22f1 / Xvfb): 検証用ホストに UITKFontFix 0.4.0 を
  埋め込み(コミットには含めない)、`settings.json` に
  `cjkUiFontNames: ["Noto Serif CJK JP"]` だけを置いて、パネル全体が
  明朝体で描画され、診断行が「via UITK Font Fix」になることを撮影で確認
  (`docs/images/verify-2026-09-06-fontfix-bridge.png`)。

## 検証で見つけた不具合(修正済み)

最初の実機撮影では、FontFix 側が「Noto Serif CJK JP」を解決しているのに
パネルは自前チェーン(Noto Sans CJK JP)で描画していた。ブリッジのプロパティ
ゲッターが `Read(_cjkAsset)` の形で、**引数 `_cjkAsset` を型の遅延バインド
前に評価**していたため、プロセス内で最初の 1 回だけ null を読んでいた
(2 回目以降は成功する)。テストは `UseProviderForTests` で即時バインドして
いたので通り、バッチモードの探針も `IsAvailable` を先に読んでいたため再現
しなかった。ゲッターで先にバインドを済ませてから読むよう直し、遅延ロケータ
経由で「最初の呼び出しが資産読み取り」となる回帰テスト
(`LazyFirstAccess_ReadsTheAssetProperty_NotNull`)を追加した。

検証用ホストへの UITKFontFix 埋め込みと `settings.json` はコミットに含めない
(`ci/HostProject/Packages/` へ手でコピーすれば再現できる)。

