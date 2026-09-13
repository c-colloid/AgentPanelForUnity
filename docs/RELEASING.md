# リリース手順

本リポジトリのバージョン管理規約と、新バージョンを切る手順。

## 規約

- **Semantic Versioning**(`X.Y.Z`)。0.x 期は **minor = 機能フェーズ(新機能のまとまり)**、
  **patch = 修正のみ** のリリース。1.0.0 は「他プロジェクトへの配布に耐える」と判断した時点。
- git タグは **`vX.Y.Z`**(annotated)。`jp.colloid.unity-agent-panel/package.json` の
  `version` とタグは常に一致させる(v0.2.0 以前はタグのみの遡及マイルストーンで、
  当時の package.json は 0.1.0 のまま — 歴史的例外)。
- CHANGELOG は **`jp.colloid.unity-agent-panel/CHANGELOG.md`**(Package Manager が表示する場所)。
  [Keep a Changelog](https://keepachangelog.com/) 形式・英語。日々の変更は `[Unreleased]` に
  積み、リリース時に切り出す。

## 手順

1. `[Unreleased]` の内容を確認し、`## [X.Y.Z] - YYYY-MM-DD` として切り出す
   (`[Unreleased]` は `(nothing yet)` で残す)。
2. `jp.colloid.unity-agent-panel/package.json` の `version` を同じ値に上げる。
3. 検証用サンドボックスプロジェクト(CONTRIBUTING.md 参照)でコンパイル+EditModeテストが全緑であることを確認する。
4. コミット(例: `Release v0.3.0`)。
5. タグ付け: **`.github/workflows/tag-release.yml` が自動で行う。**
   `jp.colloid.unity-agent-panel/package.json` の変更が main に入ると、
   そのバージョンを**導入したコミット**(マージコミットでも push の先頭でもなく、
   `Release vX.Y.Z` のコミット自体)に annotated タグを打って push する。
   同名タグが既にあれば何もしない(冪等)ので、再実行やバージョンを変えない
   package.json の編集は無害。手で打つ必要はない。

   **タグ付けトークン**: このワークフローはリポジトリシークレット
   `RELEASE_TAG_TOKEN` があればそれで、なければ自前の `GITHUB_TOKEN` でタグを
   push する。`GITHUB_TOKEN` は通常のリリースには十分だが、リリースコミットが
   `.github/workflows/` 配下を追加・変更している場合、GitHub はそのコミットへの
   タグ push を拒否する(`refusing to allow a GitHub App to create or update
   workflow ... without `workflows` permission`。REST の refs API でも同じく
   403 で、トークンなしの回避策はない)。v0.42.0 は `mirror-core.yml` を追加した
   ため実際にこれで失敗した。CI を同時に触るリリースを通すには、このリポジトリ
   に対して Contents: Read and write と Workflows: Read and write を持つ
   fine-grained PAT(classic なら `repo` + `workflow` スコープ)を
   `RELEASE_TAG_TOKEN` として登録し、失敗した「Tag release」を
   workflow_dispatch で再実行する(同名タグが無ければ打ち直す)。

   自動化以前のリリース(v0.25.0 / v0.26.0 / v0.27.0)は付け忘れているため、
   遡って手で打つ場合のみ:

```bash
git tag -a vX.Y.Z -m "vX.Y.Z" <リリースコミット>
git push origin vX.Y.Z
```

6. 他プロジェクトからは Git URL でタグを固定して導入できる:

```
https://github.com/c-colloid/AgentPanelForUnity.git?path=jp.colloid.unity-agent-panel#vX.Y.Z
```

## Agent Panel Pro(`jp.colloid.agent-panel-pro`)のバージョニング

Core とは別系統。CHANGELOG は `jp.colloid.agent-panel-pro/CHANGELOG.md`、
バージョンは同パッケージの `package.json`。**Pro に変更があったときだけ**
上げる(Core だけの変更ではバンプしない)。リリースコミットのメッセージは
`Release pro-vX.Y.Z: <概要>` とし(`Release vX.Y.Z` と紛れないよう接頭辞
`pro-` を付ける)、Pro 単独では git タグを打たない(`tag-release.yml` は
Core の package.json しか見ていない)。Core と Pro を同じ作業ブランチで
同時に変更した場合は、この手順のとおり Core のリリースコミットを切った
あと、続けて Pro のリリースコミットを別コミットとして積む
(1 コミットに両方を混在させない)。

### Pro の配信(更新レジストリ)

Pro は zip の手渡しではなく、トークン認証付きの npm 互換 scoped registry
(Cloudflare Worker、`registry/`。設計は
`docs/design-notes/2026-09-12-pro-update-delivery.md`)から配信する。
`Release pro-vX.Y.Z` のコミットが main に載ると
`.github/workflows/publish-pro.yml` が `npm pack` で tgz を作り、R2 へ上げ、
Worker の admin API に版を登録する(登録済みの版は no-op。取りこぼしたら
workflow_dispatch で再実行)。必要なリポジトリ設定(`CLOUDFLARE_API_TOKEN`
/ `CLOUDFLARE_ACCOUNT_ID` / `REGISTRY_ADMIN_TOKEN` のシークレットと
`REGISTRY_URL` の変数)、Worker の初期構築、製品キーの発行コマンドは
`registry/README.md` を参照。購入者側は パネルの 設定 > Agent Panel Pro の
更新 にレジストリ URL と製品キーを入れるだけで、以後は Package Manager から
更新できる。Unity 6.3+ の「Unsigned」警告を避けるには、Unity Cloud の
サービスアカウント(Package Manager Package Signer)を
`UPM_SERVICE_ACCOUNT_KEY_ID` / `UPM_SERVICE_ACCOUNT_KEY_SECRET` /
`UPM_ORGANIZATION_ID` として登録すると、同ワークフローが `upm pack` で
署名付き tarball を作る(`registry/README.md` "Signing")。

## 公開ミラー(`.github/workflows/mirror-core.yml`)

`tag-release.yml` の完了(`workflow_run`)、手で push した `v*` タグ、または
ワークフローの手動実行を起点に、Core だけを
`ci/public-mirror/allowlist.txt` の許可リストに従って抽出し、公開リポジトリ
`${{ vars.PUBLIC_MIRROR_REPO }}`(例: `c-colloid/AgentPanelForUnity`)へ
1スカッシュコミット「`Release <tag>`」として `main` と同じタグを push する。
セットアップ:

- リポジトリ変数 `PUBLIC_MIRROR_REPO`: `owner/repo` 形式で公開リポジトリを指定。
- リポジトリシークレット `PUBLIC_MIRROR_TOKEN`: 公開リポジトリへの push 権限を
  持つ Personal Access Token(このプライベートリポジトリの `GITHUB_TOKEN` では
  別リポジトリへ push できないため)。
- `tag-release.yml` が `GITHUB_TOKEN` で打つタグは `push` イベントを発生させない
  (GitHub の仕様)ため、ミラーは「Tag release」ワークフローの完了を受けて起動し、
  そのコミットの Core `package.json` からタグ名を求める。`RELEASE_TAG_TOKEN`
  (PAT)で打たれたタグは `push` イベントも発生させるので、ミラーが
  `workflow_run` と `push` の 2 回起動しうるが、公開側に差分・タグが既に
  あれば何もしない(冪等)ので無害。
- 初回のミラー push が公開側の履歴を v1.0.0 相当からゼロ地点として作る
  (それ以前のタグをまとめて後追いで流す運用ではない)。
- Pro に関するドキュメントを Core 側の許可対象ファイル(`docs/*.md`・
  design-notes 等)に追記するときは、必ず `allowlist.txt` も見直すこと
  (Pro 固有の内容が公開ミラーに漏れないよう、design-notes は個別の
  `!`除外行が必要)。

## 既存タグ

| タグ | 内容 |
|---|---|
| v0.1.0 | Phase 1 MVP スケルトン+Core(2026-07-29) |
| v0.2.0 | Phase 2〜3(Markdown/許可カード/履歴/設定/モデルピッカー)+安定化(2026-07-31) |
| v0.3.0 | Phase 4 サブエージェント表示+README/スクリーンショット+圧壊・リーク修正(2026-08-01) |
| v0.4.0 | 設定画面の充実(カスタム指示/表示/クイックアクション/通知/About)(2026-08-01) |
| v0.5.0 | 多言語化(L10n機構+全UI文字列の日英スイープ)(2026-08-01) |
| v0.6.0 | UI修正ラウンド(スイッチ右揃え/フォントスライダー安定化/About CLI Version)(2026-08-01) |
| v0.7.0 | Thinking本文の確定時消失の修正+設定の自動適用(アイドル時自動再接続)(2026-08-01) |
| v0.8.0 | モデル設定(Defaultモデル+サブエージェントモデル詳細設定)(2026-08-01) |
| v0.9.0 | モデル設定リワーク(デフォルト/使用中の分離+サブエージェント一括モデル)(2026-08-01) |
| v0.10.0 | パネル内ログイン/ログアウト(claude auth 統合)(2026-08-02) |
| v0.11.0 | サブエージェントモデル優先順位リワーク+Default表記の正直化(2026-08-02) |
| v0.12.0 | Phase 5a: UapOps MCPサーバ基盤+coreツール+スクリプト検証ゲート(2026-08-02) |
| v0.12.1 | Phase 5a 後の修正(2026-08-02) |
| v0.12.2 | 復元セッションでサブエージェントカードが開けない問題の修正(2026-08-02) |
| v0.13.0 | Phase 5b: prefab override/anim/material/importer ツール+SDK別プロファイル(2026-08-02) |
| v0.13.1 | 絵文字がフォント警告でコンソールを埋める問題の修正(2026-08-02) |
| v0.13.2 | Shift+Enter で改行が入らない問題への最初の対処(2026-08-02) |
| v0.13.3 | Shift+Enter の改行を実際に修正(v0.13.2 は原因を外していた)(2026-08-02) |
| v0.14.0 | 実作業セッション由来の3件(ノイズ/権限/uloop優先)(2026-08-03) |
| v0.14.1 | NewScene がフォントアトラスを破棄する不具合の修正(2026-08-03) |
| v0.15.0 | 履歴リワーク(使用状況の復元+一覧の実用化)(2026-08-03) |
| v0.15.1 | リスト行のコントロール整列(標準ルール化+全体監査+ガード)(2026-08-03) |
| v0.15.2 | 履歴フィルタバーの横溢れと同種の罠の修正(2026-08-03) |
| v0.15.3 | uap_material_set が何も設定せずに成功を返す問題の修正(2026-08-03) |
| v0.16.0 | 自動承認レベル(CLIの各モードを実測して不採用と判断した上で)(2026-08-03) |
| v0.17.0 | サブエージェントカードの状態保持・進捗表示・ターン中の入力(2026-08-03) |
| v0.18.0 | Phase 5c: UI Toolkit 自動操作/uLoop 連携/コンパイル後の自動継続(2026-08-04) |
| v0.18.1 | Phase 5c の敵対的レビュー + 初回導入実行で出た欠陥の修正(2026-08-04) |
| v0.18.2 | レビュー指摘の残り4件(includeInternal 不一致/窓の識別/改行コード)(2026-08-04) |
| v0.19.0 | 設定UIの注釈過多を実測して解消(長文はツールチップへ、警告は残す)(2026-08-04) |
| v0.20.0 | AskUserQuestion のステッパー表示(タブ+1問ずつ)+ウィンドウの高さ鎖修正(2026-08-05) |
| v0.20.1 | 起動直後にモデル別使用状況が空になる問題の修正(キャッシュに永続化)(2026-08-05) |
| v0.20.2 | 使用状況ポップオーバーの空表示文言を状態に応じて正直に(2026-08-05) |
| v0.20.3 | 起動時にトランスクリプトから内訳をバックフィル(ユーザー指摘による却下撤回)(2026-08-05) |
| v0.21.0 | uLoop導入のライブ進捗表示+確認カードの整理(222px→99px)(2026-08-12) |
| v0.21.1 | 常に許可が ruleContent を捨てていた問題の修正(スコープ拡大と蒸発の両方)(2026-08-12) |
| v0.22.0 | scripts_commit の CS0012 修正+エラーチップの恒久無視+AskUserQuestion 自由入力(2026-08-14) |
| v0.23.0 | UI監査ラウンド: AAコントラスト/警告の音量配分/カード磨き/履歴・設定整理(2026-08-14) |
| v0.24.0 | 履歴メニューのパネル様式化(GenericMenu廃止)+トークン衛生(束E)(2026-08-14) |
| v0.24.1 | 履歴メニューの過大サイズと、ページ切替で左上へ飛ぶバグの修正(実測値化+開き直し方式)(2026-08-14) |
| v0.25.0 | パッケージ全体レビューのフェーズ1是正: セキュリティ/データ整合/ハブ生存期間/許可UX 16件+CI安全網の新設(2026-08-23) |
| v0.26.0 | フェーズ2是正: 許可面・情報設計・UI正確性/性能・クライアント堅牢性・コミット経路の安全性 18件(2026-08-23) |
| v0.27.0 | フェーズ3是正: UapOpsの偽成功一掃/CLIトランスポート競合/ハブの正直さ/トランスクリプト上限/UX操作性 9束(2026-08-26) |
| v0.28.0 | UI再設計フェーズ1(統一コントロール語彙/状態/狭幅対応)+設定画面の再設計とレビュー是正(ヘルプマーク)+UITK Font Fix 連携(2026-09-06) |
| v0.29.0 | ドメインリロード耐性の再点検: 復帰順序の冪等化/ゾンビ記録の保持/実リロード E2E テスト/中断ターンの自動継続(opt-in)/Play モード非フォーカス時の uap_* タイムアウト文面/InitializeOnLoadMethod 化(2026-09-06) |
| v0.30.0 | uap_lightmap_bake(非同期ライトベイク。同期 Bake による Hold on 固まりの回避)+リロード前の終了猶予 500→150 ms(2026-09-06) |
| v0.31.0 | Scene ビュー 3D マーカー(uap_marker_* + ユーザーピン + メッシュ判定)/ 画像添付(ファイル・ドロップ・画面・クリップボード・履歴復元)/ uap_editor_screenshot の window 撮影と return_image / エラーチップ送信の一致修正 / Console Clear 同期(2026-09-07) |
| v0.32.0 | スラッシュコマンド入力(候補ポップアップ・補完・/clear のパネル内処理)/ コンテキスト圧縮の可視化(compact_boundary ノート・履歴復元・メーターの「圧縮済み」表示)(2026-09-07) |
| v0.33.0 | Bakery GPU Lightmapper 連携(uap_bakery_bake: 設定の取得/変更/シーン保存・プリセット・full/selected/probes スコープ・Lighting 設定の preflight)/ bakery 拡張プロファイル / フォントアトラス保護の自己修復(2026-09-08) |
| v0.34.0 | uap_transform_set / uap_editor_execute_menu の status / uap_property_set のサブアセット参照と LayerMask / 遅延 uap_* ツールへの誘導 / ライトマップベイクのメモリ preflight・自動最適化・Scale In Lightmap・最適化ガイド / ステータスバーのコンテキスト計とトークン量をターン途中でも更新 / メインスレッドタイムアウトの文面 / EditMode 全体実行時の順序依存修正(2026-09-09) |
| v0.35.0 | uap_job_status(待ちを超えた呼び出しのジョブ化・Editor ブロック中も応答)/ 破壊的ツールの confirm・dry_run ゲート(uap_asset_delete / uap_prefab_apply_overrides)(2026-09-09) |
| v0.36.0 | 権限面(全ツール自動承認レベル / ツール単位の常に許可 / キュー深さ表示)+リロード(簡潔な自動継続 / 破棄された許可要求のノート / Play モード読み出し)+会話更新で折りたたみが閉じる問題の修正(2026-09-10) |
| v0.37.0 | Claude 以外のサブスク型 AI: ACP(Agent Client Protocol)ブリッジで Gemini CLI / Codex(codex-acp)/ 任意の ACP エージェントをバックエンドに選択可能 + 公開向けドキュメント再編(2026-09-10) |
| v0.37.1 | 会話圧縮中の進行表示(system/status の compacting を配線。ステータスバー「コンテキスト圧縮中...」+会話欄のスピナー行。/compact 送信直後から表示)(2026-09-10) |
| v0.38.0 | 導入のパネル内完結(CLI の「インストール」ボタン + ACP エージェントのブラウザサインイン)+ Unity 公式プラグイン連携(検出 / ワンクリック導入 / 誘導行)+ uap_search(2026-09-10) |
| v0.39.0 | Grok Build バックエンド(`grok agent stdio` + 公式インストーラ)+ ACP サインインの修正: 認証メソッドの順位付け(API キーより ChatGPT/Google ログインを優先)・失敗時の次候補フォールバック・サインイン/ハンドシェイク失敗後の再接続ループ停止(2026-09-10) |
| v0.39.1 | ACP バックエンド(Codex / Gemini CLI / Grok Build)でトークン量とコンテキストメーターが表示されない問題の修正(usage_update / PromptResponse.usage / _meta.quota / Grok の _meta をパネルの result.usage・modelUsage に変換)+ UI 文言のエージェント名・スパーク記号・アクセント色を選択中のバックエンドに追従(`{agent}` プレースホルダ / `uap-agent--*`)+ 会話中のエージェント切替で再接続が無限に続く問題の修正(セッション id の所有バックエンドを記録し他バックエンドでは resume しない / クラッシュカウンタのリセット猶予)(2026-09-10) |
| v0.39.2 | 製品名を「Agent Panel for Unity」に変更(表示名・空状態タイトル・ACP clientInfo.title・スクリプトゲート拒否文・README。package 名 / 名前空間 / メニュー / リポジトリ URL は据え置き)(2026-09-10) |
| v0.40.0 | ANTHROPIC_API_KEY のブロックを廃止(Claude Code 利用条件対応): 既定を Auto(環境変数に一切触れず CLI 自身の認証選択に委ねる)に反転し、従来の無条件除去は設定 > アカウントの「APIキー認証」を Subscription only にしたときだけの明示オプトインに。API キー認証中はアカウントカードの常時ノート+会話ログの一度きりの system note で可視化(2026-09-10) |
| v0.41.0 | ACP バックエンドの認証案内の是正と可視化: Gemini CLI の個人向け Google ログイン終了(2026-06-18)に合わせて説明を「Gemini API キー(`GEMINI_API_KEY` / `~/.gemini/.env`)または Code Assist Standard/Enterprise の Google ログイン」に訂正し、サインイン全滅時の会話ノートに環境変数名と設定場所を明記 + Codex(`CODEX_API_KEY` / `OPENAI_API_KEY`)/ Grok Build(`XAI_API_KEY`)の API キー経路を併記 + ACP で実際に認証に通った方式をアカウントカードに常時表示(API キー系なら課金先の注記と会話ログへの一度きりの注記)。パネルは API キーを保存しない方針を明文化(2026-09-10) |
| v0.42.0 | Core/Pro 分割: prefab・アニメーション・UI 自動操作の全モジュールとライトマップ/Bakery ベイクツール、同梱 Extension Profiles 5件を新パッケージ `jp.colloid.agent-panel-pro`(プロプライエタリ)へ切り出し。Core に `IUapToolProvider`/`IExtensionProfileProvider` の登録シームを新設し、Pro 導入時は挙動無変化。Pro 不在時は該当モジュールのトグルを無効化してヒント表示(2026-09-11) |
| v0.42.1 | Unity 6(6000.x)対応: 非推奨の `FindObjectOfType`/`FindObjectsOfType` を `FindFirstObjectByType`/`FindObjectsByType` に置換し、Editor 内部 API への reflection 4 箇所を 6000.0 のソースで確認。EditMode CI を 2022.3.22f1 と 6000.0.83f1 のマトリクスに(2026-09-11) |
| v0.42.2 | Unity 6.3〜6.5 対応: `GetInstanceID`(6.4 で非推奨・6.5 でエラー)と `FindObjectsSortMode` 付き `FindObjectsByType`(6.4 で非推奨)を `UnityObjectCompat` / `UnityObjectId` に集約(`EntityId` 対応)。CI マトリクスに 6000.3.24f1 / 6000.5.11f1 を追加し、runner のディスク確保と常駐コンテナ方式に変更、6.5 で削除された `com.unity.modules.vr` をホスト manifest から除去(2026-09-11) |
| v0.42.3 | コンパイルのたびに会話履歴が消える不具合の修正: `AtomicFile.ReadAllText` を共有可能オープン(`FileShare.ReadWrite | FileShare.Delete`)+短いリトライに変更(MODEL-3。置き換え直後のファイルを掴むアンチウイルス/インデクサで Sharing violation になっていた)。読めなかったキャッシュには絶対に書き戻さない不変条件を `SessionCacheFile.Load` の `unreadable` 通知と `AgentHub.SaveSessionCache` の単一経路化で担保。同一ドメイン内で再読込して復元し、ロック中に届いたメッセージは連結して保持。復帰できない場合は会話欄に理由を明示(2026-09-12) |
| v0.42.4 | `uap_scripts_commit` のコンパイルゲートが Unity 本体では通るファイルを CS0012 で拒否する不具合の修正: `AssemblyBuilder.defaultReferences` はプラグイン DLL の Runtime 側だけを含み Editor 専用側(`LibForUniteForEditor.dll` など)を落とすため、`CompilationPipeline.GetPrecompiledAssemblyNames()` を走査して既定集合に無いプリコンパイル済みアセンブリだけを追加参照に加える(重複追加なし、CS0433 は再発しない)(2026-09-12) |
| v0.42.5 | AskUserQuestion カードの質問文を選択肢のスクロール領域から出し、要約行/タブ帯の直下に太字で固定表示(`uap-perm-qprompt`。要約行の折り返し以降、40% キャップの中で 1 段スクロールすると質問文が消え、選択肢ラベルとも見分けがつかなかった)。Core 単体利用者向けの文言調整: Pro 専用モジュールのトグルは本来のヒントを残して「別売の Agent Panel Pro 拡張パッケージが必要です(未導入)」を付記しツールチップで入手方法を案内、拡張プロファイル欄は同梱プロファイル無しの理由を表示、UI 操作モジュールのトグルを新設、Pro 不在で無効化したトグルが `RefreshUapOpsStatus` で再有効化されていた不具合を修正。README / USER-GUIDE の機能一覧に (Pro) 印、README・UPM ドキュメント・設定 > About の GitHub リンクを公開ミラー(`c-colloid/AgentPanelForUnity`)に統一、docs 索引 / CONTRIBUTING / ARCHITECTURE にモノレポ限定の範囲を注記。実機計測で追加: 質問カードは `uap-perm--question`(床 220px・`flex-shrink: 0`)で会話が長くてもキャップまで使う、会話圧縮中の行が押し潰されて重なる不具合を `flex-shrink: 0` で修正(2026-09-12) |
| v0.43.0 | ツール結果の画像プレビュー: `uap_editor_screenshot` / PNG の Read / MCP 画像生成ツールが返した画像をツールカード見出し直下のサムネイル帯に常時表示(埋め込み画像は Attachments に保存、本文が名指しした既存 PNG/JPEG も採用、キャッシュ・履歴復元・ACP 経路に対応)+ 配列形式の結果でも Result 要約が出るよう修正(2026-09-12) |
| v0.44.0 | Pro の更新配信 Phase 1: トークン認証付き npm 互換 scoped registry(Cloudflare Worker、`registry/`)と `publish-pro.yml`、設定画面の「Agent Panel Pro の更新」カード(レジストリ URL と製品キーを入力すると `~/.upmconfig.toml` と `Packages/manifest.json` を書き、以後 Package Manager から Pro を更新できる)。パネルはキーを保存しない(2026-09-12) |
| v0.45.0 | Pro の更新配信 Phase 2: レジストリが VPM リポジトリ(`/vpm/index.json` と版ごとの zip、同じ製品キーで認証)も配信し、設定カードに「VCC / ALCOM に追加」ボタン(`vcc://vpm/addRepo` ディープリンクにキーを Authorization ヘッダーとして同梱)を追加。`publish-pro.yml` は tgz と一緒に VPM 用 zip を生成・登録し、UPM CLI による署名パス(任意)も備える(2026-09-13) |
