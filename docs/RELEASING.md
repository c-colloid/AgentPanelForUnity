# リリース手順

本リポジトリのバージョン管理規約と、新バージョンを切る手順。

## 規約

- **Semantic Versioning**(`X.Y.Z`)。0.x 期は **minor = 機能フェーズ(新機能のまとまり)**、
  **patch = 修正のみ** のリリース。1.0.0 は「他プロジェクトへの配布に耐える」と判断した時点。
- **ベータ版と安定版**(2026-09-18、`docs/design-notes/2026-09-18-prerelease-channel.md`)。
  タスクごとに切るのは **ベータ版** `X.Y.Z-beta.N`(`X.Y.Z` は次の安定版、`N` はタスクごとに
  +1)。**安定版** `X.Y.Z` は「リリースして」と指示があったとき、または利用者を止める不具合の
  ホットフィックス(ベータを経ない)のときだけ切る。ベータ版は公開ミラーの `beta` ブランチと
  GitHub の pre-release、VPM リスティングでは「プレリリース版を表示」をオンにした人にだけ
  届き、`main` / `releases/latest` / 既定の VCC 表示は最後の安定版のまま。
  - `X.Y.Z` の決め方: 直前が安定版なら、`[Unreleased]` に `### Added` があれば minor、
    無ければ patch を上げて `-beta.1`。直前がベータなら同じ `X.Y.Z` で `N + 1`。系列の
    途中で最初の `### Added` が入り、それまで patch 上げだったなら minor へ昇格して
    `-beta.1` から数え直す。降格(minor → patch)はしない(SemVer の順序が崩れる)。
  - **`N + 1` は「直前のベータが `main` に乗っている」場合だけ**。まだ `main` に
    流していないブランチで作業を続けるときは、**同じ番号を切り直す**(番号を増やすと、
    実体が 1 つしか無いのにベータが 2 つあったように見える)。乗ったかどうかは
    `vX.Y.Z-beta.N` タグの有無で判断できる(タグは `main` への push でしか作られない)。
    切り直しは package.json を戻して普通にコミットすればよい:
    `ci/version-introducing-commit.sh` は「その版を持つ連続コミットのうち最も古いもの」
    を選ぶので、タグは切り直したコミット(= 全部入りの木)に付く。
  - **番号を据え置いている間、ベータのコミットは「package.json を最後に触った
    コミット」であり続ける必要がある**。あとから同じブランチでパッケージを
    さわったら、その作業はベータのコミットに畳み込む(amend か前倒しの rebase)。
    タグは版を変えたコミットにしか付かず、`mirror-core.yml` は**タグをチェック
    アウトして**プレリリースを作るので、ベータのコミットより後ろに積んだ作業は
    main には入るがベータ利用者には届かない。
  - ベータのコミットは `Beta vX.Y.Z-beta.N: <概要>`、安定版は `Release vX.Y.Z: <概要>`。
  - CHANGELOG はベータでは節を切らず `[Unreleased]` に積んだまま。安定版で切り出す。
  - 下の表にはベータの行を作らない。タスクの日本語の要約は「次の安定版に含める作業」の
    箇条書きに 1 行足し、安定版を切るときに 1 行にまとめて表へ移す。
- git タグは **`vX.Y.Z`**(annotated)。`jp.colloid.unity-agent-panel/package.json` の
  `version` とタグは常に一致させる(v0.2.0 以前はタグのみの遡及マイルストーンで、
  当時の package.json は 0.1.0 のまま — 歴史的例外)。
- CHANGELOG は **`jp.colloid.unity-agent-panel/CHANGELOG.md`**(Package Manager が表示する場所)。
  [Keep a Changelog](https://keepachangelog.com/) 形式・英語。日々の変更は `[Unreleased]` に
  積み、リリース時に切り出す。

## 手順

### ベータ版(タスクごと)

1. `jp.colloid.unity-agent-panel/package.json` の `version` を規約どおり `X.Y.Z-beta.N` に上げる
   (CHANGELOG は `[Unreleased]` のまま)。
2. 「次の安定版に含める作業」の箇条書きに 1 行足す。
3. コミット(例: `Beta v0.59.0-beta.2: settings escape sequences`)。タグ・ミラー・Release
   は安定版と同じ自動化が動き、ミラー側は `beta` ブランチと pre-release に載る。

### 安定版(指示があったとき / ホットフィックス)

1. `[Unreleased]` の内容を確認し、`## [X.Y.Z] - YYYY-MM-DD` として切り出す
   (`[Unreleased]` は `(nothing yet)` で残す)。`X.Y.Z` はベータ系列のラベルを外した値。
2. `jp.colloid.unity-agent-panel/package.json` の `version` を同じ値に上げる。
3. 検証用サンドボックスプロジェクト(CONTRIBUTING.md 参照)でコンパイル+EditModeテストが全緑であることを確認する。
4. コミット(例: `Release v0.3.0`)。「次の安定版に含める作業」を表の 1 行にまとめて空にする。
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

6. README の導入 URL はタグ無し(公開ミラーの `main` は常に最新リリースと
   一致するので、Package Manager の Update で追従できる)。特定の版に固定
   したいプロジェクトだけ、末尾にタグを付ける:

```
https://github.com/c-colloid/AgentPanelForUnity.git?path=jp.colloid.unity-agent-panel#vX.Y.Z
```

## Agent Panel Pro(`jp.colloid.agent-panel-pro`)のバージョニング

Core とは別系統。CHANGELOG は `jp.colloid.agent-panel-pro/CHANGELOG.md`、
バージョンは同パッケージの `package.json`。**Pro に変更があったときだけ**
上げる(Core だけの変更ではバンプしない)。ベータ / 安定版の規約は Core と
同じで、ベータのコミットは `Beta pro-vX.Y.Z-beta.N: <概要>`、安定版は
`Release pro-vX.Y.Z: <概要>` とし(`Release vX.Y.Z` と紛れないよう接頭辞
`pro-` を付ける)、タグ `pro-vX.Y.Z[-beta.N]` は `build-pro-bundle.yml` が自動で打つ
(手で打つ必要はない。`tag-release.yml` は Core の package.json しか見て
いないので、Core のタグとは別経路)。Core と Pro を同じ作業ブランチで
同時に変更した場合は、この手順のとおり Core のリリースコミットを切った
あと、続けて Pro のリリースコミットを別コミットとして積む
(1 コミットに両方を混在させない)。

### Pro の配信(更新レジストリ)

ベータ版(`pro-vX.Y.Z-beta.N`)もレジストリに登録されるが、既定の URL
(`https://<host>/npm`)からは返さない(Unity 2022.3 はスコープドレジストリの
最大版を pre-release 設定に関係なく latest として出すため。
`docs/design-notes/2026-09-18-prerelease-channel.md`)。ベータを受け取る
プロジェクトはレジストリ URL を `https://<host>/beta/npm` にする(VPM
リスティングも `/beta/vpm/index.json` に付いてくる)。

Pro は zip の手渡しではなく、トークン認証付きの npm 互換 scoped registry
(Cloudflare Worker、`registry/`。設計は
`docs/design-notes/2026-09-12-pro-update-delivery.md`)から配信する。
`Release pro-vX.Y.Z` のコミットが main に載ると
`.github/workflows/publish-pro.yml` が `npm pack` で tgz と zip を作り、
R2 へ上げ、Worker の admin API に版を登録する(登録済みの版は no-op。
取りこぼしたら workflow_dispatch で再実行)。タグも Release も作らない ——
それは下の `build-pro-bundle.yml` の仕事。必要なリポジトリ設定
(`CLOUDFLARE_API_TOKEN`
/ `CLOUDFLARE_ACCOUNT_ID` / `REGISTRY_ADMIN_TOKEN` のシークレットと
`REGISTRY_URL` の変数)、Worker の初期構築、製品キーの発行コマンドは
`registry/README.md` を参照。購入者側は パネルの 設定 > Agent Panel Pro の
更新 にレジストリ URL と製品キーを入れるだけで、以後は Package Manager から
更新できる。Unity 6.3+ の「Unsigned」警告を避けるには、Unity Cloud の
サービスアカウント(Package Manager Package Signer)を
`UPM_SERVICE_ACCOUNT_KEY_ID` / `UPM_SERVICE_ACCOUNT_KEY_SECRET` /
`UPM_ORGANIZATION_ID` として登録すると、同ワークフローが `upm pack` で
署名付き tarball を作る(`registry/README.md` "Signing")。

### Pro の GitHub Release(このプライベートリポジトリ)

各版に `pro-vX.Y.Z` タグと GitHub Release を作る(設計は
`docs/design-notes/2026-09-15-pro-private-github-release.md`)。作るのは
**`build-pro-bundle.yml`** —— Release を読む唯一のワークフローであり、
そこに載せる中身を作る当人。`publish-pro.yml` は関与しない。

**Release に載るのは BOOTH 用の束 `AgentPanelPro-<ver>-<lot>.zip` 1 つだけ。**
パッケージ本体(zip / tgz)は載せない。本体は R2 に置かれ、購入者にはレジストリ
から配られる —— GitHub に 2 つ目の置き場を作っても読む者がどこにも居ない
(`build-pro-bundle.yml` は本体 zip を R2 から取り、Worker は GitHub を一切
読まない)。束の中には本体 zip がそのまま入っているので、素の本体が要るときは
そこから取り出せばよい。

リリースノートは Pro の CHANGELOG の当該節。タグを打つコミットは Core と
同じ規則(そのバージョンを導入したコミット。`ci/version-introducing-commit.sh`
を Core の `tag-release.yml` と共有)で、push に使うトークンも同じ
(`RELEASE_TAG_TOKEN` があればそれ、無ければ `GITHUB_TOKEN`。上の
「タグ付けトークン」を参照)。Pro は公開ミラーには出さない: ミラーのタグ
トリガは `v*` で `pro-v*` に一致せず、ミラーが push するのは allowlist の
ツリーとその 1 タグだけ。

ロットの束がまだ 1 つも無い版には、タグも Release も作られない(載せるものが
無いため)。通常は *Publish Pro* の直後に束が自動で組まれるので、すべての版に
付く。

### BOOTH の配布物(`.github/workflows/build-pro-bundle.yml`)

BOOTH は購入者ごとにファイルを変えられないので、配布物は「Pro 本体の zip
+ 販売ロット共有の製品キー」を 1 つの zip にしたものにする。中身は
`jp.colloid.agent-panel-pro-<ver>.zip`、`KEY.txt`、
`ci/pro-bundle/README.{ja,en}.md` のプレースホルダを埋めた案内、
`LICENSE.md` / `LICENSE-ADDITIONAL.md` で、名前は
`AgentPanelPro-<ver>-<lot>.zip`。

**Pro のリリースごとに自動で組まれる。** *Publish Pro* の完了を
`workflow_run` で受けて走り、その版の GitHub Release から本体 zip を取り
(Release がまだ無い古い版は R2 にフォールバック)、束を組んで**同じ
`pro-vX.Y.Z` の Release に添付**する(Actions のアーティファクトにも 14 日
残す)。BOOTH に上げるときは Release ページからその zip を落とすだけ。
アップロード自体は BOOTH 側に手段が無いので手作業のまま。

**ロットの設定は無い。前の束がそのまま引き継がれる。** 実行時は、このリポジトリ
が最後に出した `AgentPanelPro-<ver>-<lot>.zip` を見て、**同じロットを同じキーで**
続ける。

- **ロット名はファイル名そのもの。** 束は必ず「その版の Release」に添付される
  ので、`pro-vX.Y.Z` が `<ver>` を確定させ、その後ろが丸ごとロット名になる
  (プレリリース版のハイフンでも曖昧にならない)。したがってロットの綴りは
  ファイル名に入る形の 1 種類だけで、手で入れた `lot` はロットを始める実行の
  ときに一度だけその形に正規化される(`BOOTH 2026-10` → `booth-2026-10`)。
- **キーと更新権のメジャーは束の `KEY.txt` から。** ファイル名には入れられない
  が、購入者に渡すファイルである以上どのみち中に入っている。レジストリは
  キーのハッシュしか持たず平文を返せないので、**この Release 群がそのロットの
  唯一の控え**になる。

変数もシークレットも要らない。

実行のたびに発行してはいけない理由: `POST /admin/tokens` には重複排除が
無く、ロット名はただのラベルなので、「同じロットなのに購入者ごとに別キー」
になり、流出時に `DELETE /admin/tokens/<id>` で止めてもその回の zip を
落とした人しか止まらない。

組む前に `GET <registry>/npm/jp.colloid.agent-panel-pro` にそのキーで当てて、
生きていること・その版が入手できることを確認する(失効したキーを配る事故を
配る前に落とす)。束がまだ 1 つも無いときは、自動実行は警告だけ出して何も
作らず(リリースを赤くしない)、手動実行はエラーで止まる。

**前提条件は無い。** 本体 zip は R2 から取り、タグと Release はこの
ワークフロー自身が作る。したがってロットを始めるのに人がする操作は
「`new_key` にチェックして実行」の 1 回だけ。束はアーティファクトに出してから
Release に添付する —— 添付に失敗しても、発行したてのキーが道連れにならない
ように。

**ロットの開始と切り替え**: *Build the BOOTH bundle for Pro* を、新しい
`lot`(例 `BOOTH 2026-10`)を入れて `new_key` にチェックを入れて手動実行
する(Actions の入力欄は説明文しか表示しないので、各説明は入力名で始めて
ある。`new_key` のチェックボックスは `new_key -- tick ONLY to start or
rotate a lot...` と読める行)。それだけでキーが 1 本発行され、それを含む束が
Release に載るので、以後の実行はロット名もキーもそこから読み直す(設定する
場所も、手で控える作業も無い)。**そのロットが現役の間は、束を載せた Release とそのアセットを
消さないこと** —— 消すとキーの控えが無くなり、切り替えるしかなくなる(緊急の
逃げ道として、シークレット `BOOTH_LOT_KEY` を置けばそちらを使う)。過去の
ロットの束を組み直したいときは、`lot` にその名前を入れて手動実行すれば、
その名前の束を持つ一番新しい Release からキーを読む。前のロットのキーは
**流出したときだけ** `DELETE /admin/tokens/<id>` で止める。止めても Pro
本体は購入者の手元に残るので、失うのは更新権だけ。止めたら BOOTH の
メッセージで新キーを送る。

## 公開ミラー(`.github/workflows/mirror-core.yml`)

`tag-release.yml` の完了(`workflow_run`)、手で push した `v*` タグ、または
ワークフローの手動実行を起点に、Core だけを
`ci/public-mirror/allowlist.txt` の許可リストに従って抽出し、公開リポジトリ
`${{ vars.PUBLIC_MIRROR_REPO }}`(例: `c-colloid/AgentPanelForUnity`)へ
1スカッシュコミット「`Release <tag>`」として `main` と同じタグを push する。
**ベータ版**(タグに `-` を含む: `v0.59.0-beta.2`)は `main` に触れず、
「`main` + その 1 コミット」を `beta` ブランチに force-push し(常に
「現在の main + 最新ベータ 1 つ」に書き直す。安定版のときも `beta` を新しい
`main` に揃える)、GitHub Release は pre-release として作る(ノートは
CHANGELOG の `[Unreleased]` 節)。`releases/latest` は安定版を指し続け、
VPM リスティングには両方が載って VCC / ALCOM 側の表示設定で選ばれる。
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
- タグ push の後、公開リポジトリに **GitHub Release** を作り、
  `jp.colloid.unity-agent-panel-<version>.zip`(パッケージルートが zip の
  ルート)と `package.json` を添付する。これは統合 VPM リスティング
  `c-colloid/vpm`(`scripts/build_listing.py`)が Release を走査して拾う
  形式で、VCC / ALCOM から Core を導入・更新できるようにするためのもの
  (`docs/design-notes/2026-09-13-core-vpm-listing.md`)。Release が既に
  あれば足りないアセットだけ追加する(冪等)。`PUBLIC_MIRROR_TOKEN` は
  push 権限(Contents: write)を持っているので Release 作成にも使える。
- 最後に `c-colloid/vpm` へ `repository_dispatch`(`package-released`)を
  送って即時再生成を促す。リポジトリシークレット `VPM_DISPATCH_TOKEN`
  (`c-colloid/vpm` の Contents: read/write だけに絞った fine-grained PAT。
  NDMFDeform 等と同じもの)が無ければ警告だけ出して終わり、リスティング側の
  6 時間ごとの cron が拾う。リスティング先を変えるときは変数
  `VPM_LISTING_REPO`(既定 `c-colloid/vpm`)。
- リスティング側の設定は `c-colloid/vpm` の `source.json` に
  `githubRepos` へ `c-colloid/AgentPanelForUnity`、`packagesMeta` へ
  `jp.colloid.unity-agent-panel` を足す(1 回だけ)。
- **ドキュメントだけの同期**: `main` への push が `README.md` /
  `README.en.md` / `CONTRIBUTING.md` / Core の `LICENSE.md` / `docs/**` /
  `allowlist.txt` に触れると、同じワークフローが docs モードで動き、
  許可リストのうちパッケージディレクトリ以外を公開側に上書きして
  「`Docs: sync from <sha>`」としてコミットする(タグ・Release・dispatch
  は無し。パッケージは直前のリリースのまま)。リリースを切らない docs
  タスクでも公開側の README が古くならない。手動で流すときは
  *Mirror Core to public repo* を `docs_only` にチェックして実行する。
- **ドキュメントサイト**(`https://agentpanel.futeikei.com/`): 同じワークフローが、
  許可リストを通した公開側のツリーから `ci/docs-site/`(Astro Starlight)で
  サイトを組み、Cloudflare Workers の静的アセットとして `wrangler deploy` する
  (`ci/docs-site/wrangler.toml`、Pro レジストリと同じ `CLOUDFLARE_API_TOKEN` /
  `CLOUDFLARE_ACCOUNT_ID`)。docs モードでも安定版リリースでも走り、ベータ
  (pre-release)では走らない(サイトは公開 `main` の docs と一致させる)。
  サイトのビルド失敗はミラーを止めない(`continue-on-error`、Step Summary に
  出る)。PR 段階では `.github/workflows/docs-site-check.yml` が同じ組み立てと
  ビルド(リンク検証付き)を回す。カスタムドメインの作成には API トークンに
  `futeikei.com` ゾーンの DNS: Edit / Workers Routes: Edit が要る(初回のみ)。
  設計: `docs/design-notes/2026-10-01-documentation-site.md`。
- Pro に関するドキュメントを Core 側の許可対象ファイル(`docs/*.md`・
  design-notes 等)に追記するときは、必ず `allowlist.txt` も見直すこと
  (Pro 固有の内容が公開ミラーに漏れないよう、design-notes は個別の
  `!`除外行が必要)。

## 既存タグ

Core(`vX.Y.Z`)の一覧。Pro のタグは `pro-vX.Y.Z` で、この表には積まない
(一覧は GitHub の Releases と `jp.colloid.agent-panel-pro/CHANGELOG.md`)。
ベータ版(`vX.Y.Z-beta.N`)も表には積まず、次の箇条書きに 1 行ずつ足して、
安定版を切るときに表の 1 行へまとめる。

### 次の安定版に含める作業

- Pro: Model のライブプレビューを描画シンク(`UapMeshSink`、方式 A)経由にし、同じ形なら頂点ストリームとインデックスをその場で書き直し(フラグで検証と境界計算を省く)、接線は確定時のみ計算。起動時マイクロベンチ `UapPerfProbe`(機種ごとに EditorPrefs にキャッシュ)と対話ガード `UapInteractiveGuard` を追加。設計ノート `docs/design-notes/2026-10-12-modeling-design-revision.md` の M0。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: 編集コア `UapEditMesh`(共有位置 + n-gon のループ、辺の放射 / ディスクサイクル、世代付きスロット、オイラー演算子 4 種、`Compact`、CSR、Mesh との往復で元の頂点順を再現)と、`int revision` だけを Unity の Undo に載せるコマンドスタック `UapEditSession` を追加。`uap_cage_edit` の Mesh 読み込みと Undo をそれに載せ替え(`Undo.RecordObject(mesh)` を廃止)、押し出し・インセット・ループカット・ブリッジ・三角形→四角形をオイラー演算子で組んだその場編集(`UapEditOps`)に移植、`UapEditMesh` 上の描画シンク `UapEditMeshSink` を追加。同ノートの M1。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: MeshFilter 向けの Sculpt ツール(Scene ビューのツールバー「Agent Panel Pro: Sculpt」。Displace / Inflate / Smooth、Ctrl で反転、`[` `]` で半径、ダブはブラシ間隔で補間、`MouseDown` で hotControl を取り `MouseDrag` ごとに 1 回シンクへ流し、`MouseUp` で動いた頂点だけの疎な差分を `UapEditSession` に 1 ステップとして記録)。組み込み / インポート済みメッシュは detach と同じシーン埋め込みコピーに。初回に `UapPerfProbe` を走らせ 1 ステップが入力周期を超える機種では警告。Xvfb 上の Unity GUI で実マウスのストロークと Undo / Redo を目視確認し、ストロークを detach と別の Undo ステップにし、Undo 後の Scene ビュー再描画、パネル位置、既定の強さ 0.1 を直した(`docs/verify/2026-10-12-sculpt-*.png`)。同ノートの M2 前半と 13.1 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: Sculpt ツールの M2 後半(1): GPU ID パスのピッキング(`UapIdPicker`、カーソル周辺 16×16 の RT に三角形 ID を描いて 1 枚だけレイ交差、デバイス無しは CPU レイキャスト)、Burst のブラシスキャン(`UapBurstBrush`、Editor 側のデリゲートに登録、無ければマネージド)、シンク自前の角度重み法線をダーティ 1 リングだけ更新、ドラッグ 1 回の p50 / p95 と法線をマウスアップへ下げる梯子。GUI 確認で見つけたシンクのサブメッシュ記述子の頂点範囲が空で `DrawMeshNow` が何も描かない不具合を修正。同ノートの 14 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: Edit Mesh ツール(M2 後半(2)): Scene ビューのツールバー「Agent Panel Pro: Edit Mesh」で頂点 / 辺 / 面をクリック選択(Shift 追加、Ctrl トグル、1 / 2 / 3 で種別)、GL のオーバーレイ、選択中心の PositionHandle で移動(ドラッグ中は動いた頂点だけライブフラッシュ、1 ドラッグ = 1 Undo ステップ)。三角形 ID から面、面の最近傍で頂点・辺を決めるので頂点・辺の ID パスは不要。Sculpt と共通の基底 `UapMeshToolBase` に detach / シンク / ピック / 記録をまとめた。Xvfb の GUI で面のハンドル実ドラッグと頂点移動を確認。同ノートの 15 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: Sculpt / Edit Mesh を SkinnedMeshRenderer で(M3 前半): 対象を Renderer にし、バインドポーズで編集して SMR 自身に描かせ、ピックは BakeMesh のコピーを ID パスで描いて重心座標で元頂点へ戻す。シンクに in-place 経路(SetVertices 等、Clear もレイアウト変更も無し)を足してボーンウェイト・バインドポーズ・ブレンドシェイプを保ち、全書き込み時は `UapAvatarAttachment` で復元。ID パスの解決はシンクのキャッシュから(Mesh の配列コピー無し)。編集メッシュとシンクを `UapEditSession` が持ちツールを跨いで Undo が効くように修正。Xvfb の GUI でスキン付き球のストロークとツール切替後の Undo / Redo を確認。同ノートの 16 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: `uap_mesh_*` の SkinnedMeshRenderer 解禁(M3 後半(1)): `UapMeshTarget` が SMR の sharedMesh を対象にし detach もコピーを SMR に付ける。位置だけの編集(displace / inflate / smooth / noise / colors)はスキニングとシェイプを保ち、トポロジを作り直す subdivide / lightmapuv / uap_cage_edit / uap_mesh_repair は再マップが入るまで文面付きで拒否。uap_mesh_inspect がスキニングを報告。対話ツールはマウスアップで MeshCollider を 1 回再クック。同ノートの 17 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: `uap_mesh_edit` / `uap_mesh_repair` の Undo を `Undo.RecordObject(mesh)` から `UapEditSession` のスナップショットコマンド(呼び出し前後の Mesh 状態、同じ頂点数ならセッターで戻しスキニングを保つ)へ(M3 後半(2))。同ノートの 18 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: スキン付きメッシュの `subdivide` / `lightmapuv` を解禁(M3 後半(3)): `UapAvatarAttachment.Remap` がボーンウェイトとシェイプのデルタを新しい頂点へ再マップ(一致点はコピー、他は最近傍 4 頂点の逆距離ブレンド、グリッド探索)。スナップショットもスキニングを持ち Undo で戻る。同ノートの 19 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: 非破壊編集の記録 `UapAvatarEdit`(Runtime、IEditorOnly: Source とハッシュ、元頂点番号で疎な位置差分)と `uap_avatar_edit`(status / reapply / revert / bake、51 本目)(M4 前半)。Sculpt / Edit Mesh が組み込み・インポート済み Mesh を開くとコンポーネントを付け、マウスアップごとに差分を書き直す。ビルドは `UapModelBuildStrip` が剥がす。同ノートの 20 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: NDMF の Transforming パス `UapAvatarEditNdmfPlugin`(NDMF ≥ 1.3 のときだけコンパイル、`[RunsOnAllPlatforms]`)とプレイヤービルドでの適用(`UapModelBuildStrip` が剥がす前に `ApplyAll`)。検証用ホスト `ci/HostProjectNdmf` を `ci/make-ndmf-host.sh` で作り(NDMF 1.14.8 埋め込み、`Dependencies~` のリンク)、NDMF の実ビルドでパスが走るテストを通した(M4 後半)。同ノートの 21 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: `UapEditMesh.FromMesh` を `MeshUtility.AcquireReadOnlyMeshData` 経由に(マネージドコピー無し、Read/Write OFF でも開く)。設計ノート 9 節に進捗を追記(M0〜M4 済、M5 未着手)。同ノートの 22 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: M3 の残り 3 点。`uap_mesh_export` を `IUapPollableTool` に(ワーカースレッドで glb をエンコード、`UnityEditor.Progress` の進捗とキャンセル)。Sculpt / Edit Mesh にシェイプ編集(パネルの「Target」ポップアップ、終端を編集してマウスアップでデルタを書き換え全シェイプを再追加、切り替えもセッションの 1 ステップ)。Edit Mesh に矩形選択(Shift 追加 / Ctrl トグル、「Visible only」で見えている面の要素だけ)と辺の ID パス(Lines の Pass 1、SMR はベイク後の位置)。SMR のピックが `BakeMesh(useScale:true)` の読み違いでスケール分ずれていたのを修正。同ノートの 23 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: 残り 3 点。トポロジ変更時の再マップ(シンクの全量書きで頂点数が変わればスキニングを `Remap` → `Restore`、`uap_cage_edit` / `uap_mesh_repair` のスキン付き拒否を解除、スキン付きのケージ編集は UV を保って三角形で書く)。NDMF の `IRenderFilter` プレビュー `UapAvatarEditPreviewFilter`(元アセットを表示中で記録が Ready のレンダラに、プロキシへ差分入りコピーを載せる)。M5: `UapMeshBvh`(デバイス無しのピックと Edit Mesh の Slide on surface)、`UapChunkSink`(ストローク中は Morton 順 4,096 三角形のチャンクを代役表示し触ったチャンクだけ再書き込み)、`UapGpuBrush`(Raw 頂点バッファにコンピュートでダブ、ダブごとに GPU で法線再計算、マウスアップで読み戻し)、SMR のチャンク表示(`UapCpuSkinner` で姿勢後の位置)、Sculpt パネルの「Sink」(Auto / Mesh / Chunks / GPU)。同ノートの 24 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: 同梱スキル `agent-panel-modeling` と `modeling` / `ndmf` プロファイルに既存メッシュ編集(SMR・ウェイト・シェイプ、`uap_mesh_*` の経路、Avatar Edit の非破壊記録と `uap_avatar_edit`、NDMF のパスとプレビュー、Scene ビューの Sculpt / Edit Mesh の案内)を追記。`docs/PRO-PROFILES.md` の modeling 行も。同ノートの 25 節。pro-v0.14.0-beta.4 を再カット(2026-10-12)
- Pro: 検証結果(レポート第 4 版・検証ノート 13 / 14・計測計画 16)との照合で直した点: シェイプ再追加をターゲット解除時まで遅延(`FlushShapeToMesh` / `UapEditSession.FlushPending`、リロード・保存・Play・ツール読み取りで必ず書く)、頂点 ID パス(`DrawProceduralNow` の 3 px 四角、`hit.Vertex`)、チャンク書き換え順のハッシュ化、プローブの GPU 同期、SMR ホストのプローブ 3 倍係数。実装で分かった事実を研究ノート 13 の 7 節に追記。同ノートの 26 節。main に別ブランチの pro-v0.14.0-beta.4(PlayMode テスト実行ほか)が入ったのでマージし、このブランチは pro-v0.14.0-beta.5 として再カット(2026-10-12)
- Pro: 検証結果をエージェント側にも反映(設計ノート 27 節): `uap_mesh_inspect` がプローブの結果から手編集の見込み(1 ステップ p50、Auto が選ぶシンク、Burst / managed 走査)を返し、skinned の説明を「トポロジ編集はスキン再マップ + シェイプ全再追加(100 本あたり約 0.5 秒)」に修正。`uap_mesh_edit` の subdivide はメモリの 1/4 超か 1,600 万三角形超を実行前に拒否(`SubdivideRefusal`)。同梱スキルと `modeling` / `ndmf` プロファイルに作業順(トポロジ → 位置 → エクスポート)、単一 Mesh の境目(MeshRenderer 約 1M / SMR 約 300k)、シェイプ書き戻しの遅延(スキルは旧動作のままだった)、NDMF プレビューは記録の再生であることを追記(2026-10-12)
- Pro: モデリング UI のブラッシュアップ(設計ノート 28 節): Sculpt / Edit Mesh のパネルを共通の `UapToolPanel`(スキン追従の背景、内容に合わせた高さ、見出し、ツールチップ、必要なときだけの警告バー、ジェスチャのヒント行)に載せ替え、Sculpt はモード 3 ボタンと半径スライダー、Edit Mesh はキー併記の要素ボタンと Deselect。Model インスペクターを Field / Preview / Script / State に分けて状態チップと古いベイクの警告、Evaluate now / Bake again。オーバーレイはメッシュを持つ Renderer でも出て Sculpt / Edit Mesh ボタンと Avatar Edit の要約、Model の状態行を追従。Avatar Edit に専用インスペクター(状態チップ、記録数、Reapply / Revert / Bake)(2026-10-12)
- Pro: `uap_model_apply` が同じ呼び出しで先に作った(または `set` で名前を付けた)ノードを名前で参照できるように(設計ノート 29 節。スキルの例 `parent: "body"` が拒否されていた)。曖昧・未知の名前は従来どおり拒否(2026-10-12)
- Pro: ツールパネルの文字をアイコンとチップに置き換え(設計ノート 30 節): EditorTool のツールバーに自作アイコン(空白だった)、ブラシのモードと要素はアイコンボタン、状態は色チップと色の点 + 数字、ヒントと注記は「?」トグルの中、警告は警告アイコン + 1 文。Model / Avatar Edit インスペクターの説明文はツールチップへ。Model Node インスペクターもラベルを短くして意味はツールチップ、Scene ビューの操作説明は「?」トグル、オーバーレイのツールボタンにアイコン(2026-10-12)
- Pro: オーバーレイを「Model」に(設計ノート 31 節、案 A): 名前 + 状態チップ 1 個 + 主操作 1 つ(塗り)+ 副操作 1 つ。Sculpt / Edit Mesh の入口はツール列だけにし、ツール名から「Agent Panel Pro:」を外す。素のメッシュでは出さない(2026-10-12)
- Pro: ベイク再設計の F0(設計ノート `docs/design-notes/2026-10-12-bake-topology-uv-redesign.md`): `uap_model_bake` の結果に `quality` ブロック(三角形の最小角・細長い三角形の割合・アスペクト比、不規則頂点の割合、UV のチャート数・ストレッチ・テクセル密度のばらつき、法線マップが補正する角度の分布と 20° 超の割合、硬い辺の数)を追加。純関数 `UapBakeQuality` と、KernelSmoke の 5 例題(角丸の箱・20° の斜め接合・丸穴パネル・smooth union の取っ手・折れ線フレーム)で今のベイク経路の基準値を記録(細長い三角形 7〜48%、20° の接合の折れ目再現率 0)。法線マップも AO も無いベイクでも計測のためにワーカーを回す。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F1(同ノート 7 章・15 章): `uap_model_bake` に `uvLayout: "tiled"` と `tileSize` を追加。UV0 を三角形ごとの所有ノードの座標系での投影(box / planar / cylindrical、メートル / タイル)にし、固有のアトラス(ライトマップ UV を兼ねる)を UV1 に移して法線マップと AO をそこへ焼く。ノードに `uv {mode, tile, axis}`(インスペクターにも)、所有命令を返す `UapFieldProgram.EvalOwnerInstruction`、純関数 `UapTiledUv`、シェーダーテンプレート `tiledBaked`(`uap_material_create` のプリセットにも。Built-in / URP: UV0 のタイル albedo・法線 + UV1 の焼き込み法線・AO、UV0 の接線空間)を追加。結果に `uvLayout` と `quality.uv0`。KernelSmoke で箱(回転含む)・円柱・タイルの違う 2 ノードを検査。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F2(同ノート 3 章・16 章): 折れ目を木から読む `UapFeaturePatches`(所有する葉の命令と、丸めの無い箱 / 円柱の面。減算の刃は切った面を持つ)。2 つのパッチが硬く接する辺(同じ形の別の面、smooth 無しで折りたたんだ別の形)は角度によらず折れ目として、デシメーションで拘束し、法線を分割する。パッチ境界の単一扇の頂点は場の法線が面から 30° 超ずれていれば面の法線に、平らな面(箱の面・円柱の上下)上の頂点は面の解析的な法線に。`uap_model_bake` に `featureLines`(既定 true)。5 例題で補正角 20° 超の割合が 0.0〜0.3%(Unity で取っ手 37.7%→0.0%、斜め接合 3.4%→0.1%、丸穴 0.7%→0.2%)、20° の接合の折れ目再現率 0→1.00。`EvalOwnerInstruction` は減算の刃を所有者にする(tiled UV の穴の壁も円柱投影に)。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F3(同ノート 4 章・17 章): `uap_model_bake` に `topology`(既定 `even`)。デシメーション後のメッシュを g3 の Remesher で等方リメッシュ(分割・つぶし・入れ替え・接線方向のスムージング、各頂点を細かい抽出メッシュへ投影、素材の縫い目・木の硬い辺を拘束、exact parts は凍結、予算の辺の長さを数え直して最大 3 回、なお超えれば拘束付きの縮約)。四角形リメッシュ(`remesh: true`)も硬い辺を縫い目として追う。Unity の 5 例題で細長い三角形 7〜48%→0.2〜9%、不規則頂点 71〜82%→23〜29%、フレームの UV 1 は 79 チャート・退化 183→16・0。平らな領域を粗くする予算配分は境界固定のリメッシュが退化三角形を作るので見送り。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F3b(同ノート 18 章、試験的・既定オフ): `uap_model_bake` に `flatScale`(1〜4、既定 1)。平らな領域(箱の面、円柱の上下、角丸の箱の面の平らな中央)を、境界を残したまま内側で段階的に粗くする。境界から 1 周内側の芯に 2-1 / 3-1 / 1-1 セルの遷移行(`UapFlatTransition`、角はセルの境目、内側は耳切り + 2 次元の Delaunay 辺入れ替え + 粗いリメッシュ)、組めない領域は 1.5 倍ずつのリングのリメッシュ。パッチは角丸の箱の面の平らな中央も平面として扱う(硬い辺にはしない)。Unity で角丸の箱の補正角 p95 12.8°→9.5°、斜め接合の細長い三角形 4.8%→12.9% なので既定オフ。F3 の硬い辺沿いの汚れ(折れ返った細い三角形)が遷移行を阻む主因。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F3c(同ノート 19 章): `topology: "even"` は縮約を挟まず細かい抽出を直接リメッシュ(縮約が残す裏返り・潰れた三角形はリメッシュで消えない)。硬い辺の拘束チェーンは、木の両面が分かる所では 50° の角度判定を使わず、短いひげを刈り、2 面の交線(`UapFeaturePatches.ProjectToCrease`、平面・形の表面への交互投影)へ頂点を移して分割点もそこへ投影(`CreaseTarget`)。最後に長さ 0 の辺をつぶす。品質に `triangles.degenerate` / `folded`。Unity の 5 例題で細長い三角形 0.0〜0.1%、最小角 p5 40〜47°、潰れ・裏返り 0〜2、UV1 のチャート 6〜20(角丸の箱 206→6)、丸穴の縁の半径誤差 0.00%。F3b は縁から直接遷移行を組み、丸穴パネルの上面で初めて組めた。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F4(同ノート 20 章): ベイクのアトラスを自前で展開する `UapChartUnwrap`(硬い辺・素材の縫い目・tiled UV0 の継ぎ目で分かれた部分を初期チャートに、LSCM〔g3 の疎 CG〕で平らにし、閉じている・裏返る・自分と重なる・伸びるチャートは法線の向きで 2 つに〔2 つの種から隣接を伸ばして〕割り直し、最小外接矩形に回して同じ密度でラスタ詰め〔不規則な形を噛み合わせる、余白 4 テクセル〕)。`uap_model_bake` に `unwrap`(既定 `auto` = 重なるか Unity の展開が 0.08 以上多く詰めるときだけ Unity、`charts` / `unity`)。品質に `uv.overlap`。Unity の 5 例題で詰め込み率 0.56〜0.77(Unity 0.20〜0.67、フレーム 0.20→0.44)、重なり 0、auto は角丸の箱だけ Unity を選ぶ。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F5(同ノート 21 章): 折れ線の `sweep` 投影(u = 経路の弧長、v = 管の周り、曲がり角でねじれない運んだフレーム、折れ線の auto もこれ)、トリムシートの定義アセット `UapTrimSheet`(行 name / v0 / v1 / tags、任意の albedo / normal)とモデルのルートの `trimSheet`(`uap_model_apply` でパス指定か `{asset, rows}` で作成・書き換え)、ノードの `uv.trim` で管(周の半分を鏡映)と円柱の壁(見えている高さ、丸穴の壁はパネルの厚み)を行に写す。シートのテクスチャを tiled 材質へ、知らない行名は警告、行の変更は再ベイク。結果に `sweep` / `trimmed` / `trimIgnored`。KernelSmoke でフレームの v が行の中・u の縮尺が経路長どおり、丸穴の壁が行いっぱい。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイク再設計の F6(同ノート 22 章): ベイク結果に `review`(数値を固定の上限と比べ、verdict ok / consider / fix と、数値・理由・回すつまみ〔再ベイクの設定かノードのヒント〕を持つ advice、fix をまとめた `retry`)。ノードのヒント `detail`(0.25〜4、even トポロジーのリメッシュで持ち主の面の三角形密度を予算内で変える。g3 の `Remesher.ProcessEdge` を上書きして辺ごとに長さを変える)と `uv.seam`(cylindrical / sweep の折り返しを隠す側)。同梱スキルと modeling プロファイル(`docs/PRO-PROFILES.md` の行も)に検証ループを追記。Unity で、斜めの接合を古い設定でベイク → retry で再ベイクすると fix が消え細長い三角形と補正が減ることを確認。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ベイクのガイド曲線(同ノート 23 章): モデルのルートの `guides`(点列を表面へ射影、または平面の断面ループ、`cut` でアトラスも切る、最大 32 本)。抽出に曲線沿いの辺の列を回廊内の最短路で埋め込んで曲線へ移し、even リメッシュ・削減の各段で曲線への射影付きの拘束にする(法線は分けない)。cut は低ポリの頂点を曲線の両側で分ける。`remesh: true` では細かい辺を向き場の拘束に。結果に各ガイドの `recall`、review に `guide-missed` / `guide-recall`。Model インスペクターで編集、Scene ビューに描画。KernelSmoke で円柱の輪の recall 0→1.000、交差する 2 本も 1.000 / 0.956。pro-v0.14.0-beta.5 を再カット(2026-10-12)
- Pro: ガイド曲線を Unity の実ベイク 32 回(4 形状 × even / decimated / 四角形 / tiled × ガイド有無)で検証して直した点(同ノート 23.5): 閉じた断面の最後の錨が始点の隣に来ると列が潰れる、細い輪で弦のたわみが判定を外れる(弧長での判定に変更)、穴をまたぐ線を場所付きで拒否、まっすぐなガイド上の近道の辺(拘束せず、リメッシュ後に反転)、交点の頂点を両方の曲線の間に置く、四角形リメッシュでの recall 不足には `remesh: false` を勧める。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: 四角形リメッシュ(`remesh: true`)でもガイドに辺を乗せる(同ノート 23.6): 出来上がったケージの辺のグラフで曲線沿いの最短路を求めて頂点を曲線へ移し(列の乗り換えの段は曲線に近い端だけ)、まわり 2 リングを均す。向き場への拘束はやめた(特異点が増えるだけで、ガイドが折れ目扱いにもなっていた)。Unity の 4 形状で recall 0〜0.7 → 0.94〜1.0、退化・裏返りはガイドなしと同じ。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: UV1(アトラス)のシームヒント(同ノート 24 章): `uap_model_bake` の `seamSide` とノードの `uv.seam` で、展開が自分で入れる切れ目の向きを指定。割る軸をヒントの方向にし、2 つの領域の芯の間で「見える所ほど高い」最小カット(Dinic)で切れ目を選び直す。割っても直らないチャートが出たらヒントなしに戻す(`hintDropped`)。結果に `unwrap.charts.cuts`、review に `seam-hint-dropped`。Unity で球・角丸の箱・枠の切れ目のヒント側の割合 78〜87%、重なり 0。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: `flatScale`(F3b)のベイク結果が実行ごとに変わっていたのを修正(同ノート 18.5)。平らな領域のリメッシュが g3 の並列スムージングのままで、動いた頂点の印を複数スレッドから共有の BitArray に書くため取りこぼしがあった。ほかのリメッシュと同じく直列にした。KernelSmoke の時々落ちる `clean grid x2` が直り、10 回続けて全件成功・計測値も一致。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: エディタ起動後の最初のベイクだけ結果が違ったのを修正(同ノート 18.6)。Burst の非同期コンパイルが終わるまでカーネルが管理コードで走り、丸めが違っていた。ベイクはワーカースレッドで Burst のコードが走るまで最大 30 秒待ってから抽出する(プレビューは待たない)。Unity で新しいエディタの 1 回目からすべて同じメッシュ。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: ベイクで薄い部品・小さい部品が消える不具合の修正(同ノート 25 章): `topology: "even"` の辺を局所の厚み(抽出への光線で測る)の半分以下にし、予算の長さを二分法で解き直す。リメッシュ後に抽出からの距離の p99 が許容(抽出の辺 2 本か辺の長さの 0.6 倍)を超えたらデシメーションに戻す。結果に `topology.fidelity`、review に `shape-fallback`。インスペクター / オーバーレイの「Bake again」は unchanged の木も焼き直す。KernelSmoke の椅子で予算 3,000 の p99 6.7→0.2 セル(戻し)、8,000 で 2.2→1.6 セル(肘掛け 3.8→1.7)。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: ボックスの面取り(`2026-10-13-box-chamfer.md`): 箱のノードに `chamfer`(12 本の辺を size の内側で 45° に切る平らなベベル)。フィールド(通常評価・Burst・参照木)、木の硬い辺(面取り 12 面を平らな面として知る)、exact parts(44 三角形の面取り箱)。`uap_model_apply` の説明に `round` / `chamfer` の意味、スキルとプロファイルに「辺に沿ってカプセルやポリラインを並べない」。KernelSmoke で予算 2,000 の even ベイクが 12 本の面取りを硬い辺の平らな帯として保つ。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: exact パーツのベイクで未使用頂点が残る不具合の修正(`2026-10-14-exact-unused-vertices.md`): exact の coplanar マージの後に `uap_mesh_boolean` と同じ片付け(退化・重複三角形と未使用頂点の除去)、`Decimate` の出力は三角形が使う頂点だけ、結果の `vertices` は UV 展開後のアセットの数。exact パーツの椅子で頂点 20,204 のうち 10,912 が未使用だった。pro-v0.14.0-beta.5 を再カット(2026-10-14)
- Pro: exact の mesh leaf と `uap_model_apply` の原子性の修正(同ノート 8 章): mesh leaf の配列を木の読み取り(メインスレッド)で取り、ワーカーで `Mesh` を読まない(`get_isReadable` の警告でパーツが抜けていた)。`uap_model_apply` は失敗した op があれば呼び出し全体を Undo で戻し(createModel のモデルも)、ルートの set も Undo に記録。`AssignIds` を検証の前に回し、同じ呼び出しで作ったノードの名前が別ノードや存在しない id に解決されないように。pro-v0.14.0-beta.5 を再カット(2026-10-14)
- Pro: exact の残りの挙動の修正(同ノート 9 章): CSG の入力で約 3 mm 未満の三角形が `Vector3.normalized` で捨てられていた(平たい ellipsoid・ボルトに穴、ベイクでトゲ)のを倍精度の平面で救う。分割数をベイクの cell ではなく島の大きさ × 0.002 の弦の誤差で決め(解像度で三角形が増えない)、decimated でも exact を凍結。exact の `round` を box(角丸ボックス)・cylinder・sphere・capsule・torus で反映。大きな union は誤差以下の round を角のまま、CSG の雑音を溶接、T 字の継ぎ目を割って穴を開けない(椅子の背もたれ 6 万 → 2,600 三角形、穴 0)。`UapModelExact` を Reading に依存しない本体と入口に分け KernelSmoke `exact parts` を追加。pro-v0.14.0-beta.5 を再カット(2026-10-14)
- Core: Agent Tools ツールバーの末尾に拡張スロット `SceneAgentModelSlot`(`SceneAgentToolsExtensions.RegisterModelElement` で別パッケージが中身を登録、未登録なら薄い「Pro」の語とツールチップ)(設計ノート 2026-10-13-agent-tools-extension-slot、2026-10-13)。v0.61.0-beta.9 として再カット(2026-10-23、main の beta.8 が先にタグ付けされたため)
- Pro: 「Model」オーバーレイを廃止し、同じ状態チップと主操作を Core の Agent Tools のスロットに `UapModelToolbarElement` として登録(設計ノート 32 節、案 B)。Scene ビューの帯は 1 本に(2026-10-13)
- Pro: exact パーツのモデリング改善(`2026-10-14-exact-modeling-improvements.md`): cylinder の `a` / `b`(両端の中心で置く、inspect も出す)、torus の `arc` / `arcStart`(場・Burst・exact の弧、端面付き)、`repeat.pivot`(モデル空間の回転中心)。ベイクで exact パーツ間の突き合わせ面を両側から切り取り(`exactContactFaces`)、`quality.mesh`(退化・非多様体・同一平面の重なり・開いた辺)と `quality.exactTriangles`、exact だけで予算超過の警告、review の `mesh-defects` / `exact-over-budget`。even の予算から凍結した exact の三角形を先に引き、面積にも数えない(場の辺が数 mm に縮んで縮約・展開 48 秒 → 縮約 0・3.8 秒)。スキルに exact の組み方の節、プロファイルに要点。pro-v0.14.0-beta.5 を再カット(2026-10-14)
- Pro: exact の知見ノートの取り込みと追記された不具合の修正(`2026-10-14-exact-hard-surface-modeling-lessons.md`、改善案ノート 12〜15 章): Prefab Mode で開いたモデルへの `create` が `node ids moved` で全部失敗していた(`UapModelNode.Reset` が先に id を取る。原子化の退行)のを、仮の id をそのまま付けて解消。三角形のケージの bevel は同一平面の三角形の対を四角形に戻してから(上面の縁の段)、丸めの円弧は辺に向けて曲げ、角は球面に(帯のねじれと角の裏返り)。tiled ベイクのトリムシートはトリム行を使うスロットだけに入れ、cylinder / sweep のふたも行を横切らせ、`stretchL2` は三角形ごとの寄与に上限、レンダラーの不足スロットを足す(`renderersExtended`)。pro-v0.14.0-beta.5 を再カット(2026-10-14)
- Pro: メッシュ葉の第一級化とステージ内の bake 後表示(設計ノート `docs/design-notes/2026-10-13-own-mesh-leaf-and-baked-in-stage.md`): Model Node 自身の MeshFilter を葉にできる(`uap_model_apply` の `ownMesh`、インスペクターの Edit here / Reference instead、inspect の `mesh.own`)。Sculpt / Edit Mesh をその葉にそのまま当てると、ツールが開いている間は部品ビュー(葉は自分のレンダラー、ゴーストは重ねない)、ストローク中はスケジューラが読まず、マウスアップで再メッシュ → settle → 再 bake。融合プレビューの裏では葉のレンダラーを隠し、葉でなくなれば戻す。Prefab Mode のソースでは Agent Tools ツールバーの Model 要素の「Baked」トグルで最後の bake をステージ内に描く(Scene ビューのカメラへ `Graphics.DrawMesh`、ステージのシーンには何も置かない、保存の再 bake でそのまま追従)。pro-v0.14.0-beta.5 を再カット(2026-10-13)
- Pro: ブリック格子の細分化に 2 つの判定を opt-in で追加(`UapBrickStore.RefineThinNearSurface` / `RefineCurvatureDegrees`、既定オフ、コードからのみ)。細い葉は自分の表面の近くだけを割り、曲率は交差セルの勾配の曲がりが閾値を超え、かつ木が硬い辺と言わないときに割る。低解像度 DC 生出力の計測(`docs/research/17`、6〜7 章)の結論は、細い回転体の特徴は exact / 通過経路に任せ、場の細分化は数 cm 級の曲面向け、というもの。bake と ライブプレビューの挙動は変えていない。
- Pro: exact の通過経路(`docs/design-notes/2026-10-15-exact-passthrough.md`): メッシュ葉 1 つだけの exact 島(畳む子・複製・ケージ修飾子なし、Mesh に UV と法線あり)は CSG・縮約・投影・折れ目分割を通さず、Mesh の位置・法線・UV0・全三角形をそのまま低ポリに追加する(アトラスと焼いたマップは UV1、法線マップは平坦、AO のオクルーダに参加、`quality.passthroughTriangles` / `passthroughParts`)。通過部品だけのモデルも bake できる。`uap_mesh_create` の lathe は v を弧長に、`hard`(硬い点のリングを 2 重化して法線を分ける)、`seam`(u = 0 の角度)、`uvCuts`(UV を島に切り、各島をメートル単位で展開して同じ密度で棚詰め)を追加。 sweep の UV も同じ方式(帯 + 蓋をメートル単位で 0..1 に棚詰め、シームの列と閉路の行を繰り返す)にした。`uvScale` / `uvOffset`(どの kind でも、UV を縮めて隅に置く。殻のスロットを共有するリベット 4 個が各自 0..1 全面を取って殻の島に重なっていた)を追加。スキルに経路表の行と手順を追記。
- Pro: `uap_model_bake` に `topology: "grid"`(設計ノート `2026-10-12-bake-topology-uv-redesign.md` 27 章)。抽出した DC の三角形をそのまま(折れ目分割とスロット分けだけ)低ポリにする。`docs/research/17` の計測(大きな特徴では縮約+even と同等以上で 50〜100 倍速い)の露出で、`resolution` が出力のセルに戻る。`resolution` 省略時は低解像度の抽出を数回回して三角形数が `targetTriangles` に最も近い解像度を選ぶ(`topology.tried`)。ガイド曲線は追わない、remesh とは併用しない。保存時の再ベイクは 128 の下限なしで Model の Resolution に従う。`refineLevels`(0..3、ブリック格子の局所細分化の段数)を露出し、grid の既定を 0 にした(`bench-2026-10-15-grid/` 第 2 回: 予算 2,000 では段数 0 が被覆で同等以上、細長い三角形 0、チャート最少、基底解像度 16〜37)。`resolution` も `refineLevels` も省略した grid は段数を自動で選ぶ(段数 0 / 1 / 2 で基底を探し、最細セルが最も細い葉の厚み以下になる最小の段数、無ければ 0。第 4 回で探針を突出量 + 帯の法線に直して測り直し、存在の境界は厚みの 3/4 ではなく 1.0 と分かったので `GridKeepCellShare` を 1.0 にした: ビードは 2,000 で落ち、8,000 で段数 1、20,000 で基底だけで届く。陰影は法線マップ頼み)。届く段数が複数あるときは `refinePreference`(`base` = 最小の段数 = 既定、`feature` = 最大の段数)で選ぶ(第 5 回: feature は板のビードの被覆を 2.6 → 1.4 mm に良くするが、曲面の胴体の被覆を 2〜4 倍悪くし、ベイク時間も伸びる)。細分化が働かない太い葉では段数を数えない。第 6 回で `auto` を既定にした: 胴体の最小曲率半径(箱の round、球・管・円筒の半径、smooth の k)の 0.4 倍以内に基底セルが収まる最大の届く段数、無ければ最小。平らな板は feature、角丸の箱と丸み 8 mm の板は base、リブ付き缶は 20,000 で段数 2。charts 展開を速くした(`bench-2026-10-16-unwrap/`: ラスタ詰めの走査、円板でない断片は LSCM を解かずに割る、2,000 チャート超は棚詰め直行、auto は 2,000 ピース超で Unity 直行。缶 20,000 で段数 0 は 14 → 4 秒、段数 2 は 84 → 5 秒、枠 48 → 20 秒。結果の `unwrap.charts` に内訳)。管の分割の向きを位相で振り分け(境界の無い断片と切れ目のあるループは主軸の中央で横に、曲がった円板の管は長軸の中央で横に、開いた管だけ縦のシーム): 枠の charts の被覆 0.04 → 0.39(8,000 では 0.60)、缶 L2 は 0.16 → 0.31。LSCM を逆 Cuthill–McKee 順の帯 Cholesky で直接解く(第 3 回: 枠 20,000 の LSCM 19.2 → 0.4 秒、缶 L2 2.8 → 0.27 秒。帯・ピボットが拒む系だけ CG)。解けると枠の一辺の半管が 1 チャートになって被覆が 0.04 に落ちたので、UV 主軸方向の広がりがメッシュ面積の平方根の 0.6 倍を超えるチャートは半分に割る(`MaxChartSideShare`): 枠 20,000 は被覆 0.47・展開 2.7 秒(21 秒から)、8,000 は 0.62、取っ手 0.62。`unwrap.charts.lscm` に `direct` / `bandMax` / `capped`、`splitsLong` / `maxChartSide`。fallback の 1 三角形化を減らした(第 4 回: 重なるだけの断片は UV 主軸で半分に `splitsOverlap`、分割が取り残す 4 三角形以下を戻す `absorbed`、深さ上限 16、結果に `unwrap.charts.fallback` の理由別の数。缶 L2 は 747 → 369 チャート、被覆 0.31 → 0.38、fallback 384 → 0 三角形で auto が charts を選ぶ側に。鋭い辺で先に切る案は DC の折れ目をまたぐ三角形を孤立させて却下)。折れ目分割の途切れを直した(第 5 回: 折れ目をまたぐ三角形が重心のパッチでラベル付けされて「パッチ境界だが 5° 未満」の辺を残していたのを、面法線が合う側のパッチに付け替える `RelabelStraddling`。結果に `quality.creases {relabelled, flatPatchEdges}`。缶 L2 は胴体が壁と 3 本のリブ帯に分かれ、217 チャート・被覆 0.50・展開 1.2 秒、法線補正は不変。枠・取っ手は変化なし)。charts 段の LSCM 以外の時間を縮めた(第 6 回: 結果に `unwrap.charts.phases` の内訳。辺キーの `long` 既定ハッシュの衝突 → `UapPackedKeyComparer` + union-find、境界・円板・ループを 1 パスに、自己重なりのラスタを三角形数に合わせる、`Flatten` のレイアウトを持ち越して再解法をやめる、切り口の計測は小さい側だけ。缶 L2 の展開 1.4 → 0.3 秒、枠 2.8 → 0.5 秒(固定ラスタの誤検出が消えて 124 → 48 チャート、被覆 0.47 → 0.50)、取っ手 1.1 → 0.3 秒)。LSCM 自体も縮めた(第 7 回: 結果に `lscm.phases` / `wasted` / `burst` / `longSkipped`。帯 Cholesky を Burst の関数ポインタに(`UapBurstBandCholesky` → `UapChartUnwrap.BandSolver`、無ければ管理コード)、3D の主軸方向の広がりが上限を超える円板の断片は解かずに主軸で割る、索引を世代印つき配列に。LSCM は缶 136 → 47 ms、枠 274 → 52、取っ手 152 → 37、展開フェーズは 4 例題とも 0.1〜0.3 秒)。特異な系の CG 落ちを直した(第 8 回: 原因は抽出が残す 2 つの角が同一座標の零面積三角形。grid 経路で抽出直後に同一座標の頂点を束ねて落とす `CollapseCoincident`、`quality.creases.degenerateDropped`。展開側は対角 0 の未知数に 1 を足し `lscm.regularised`、面積の無い三角形を折れ判定から外す。枠の CG 反復 451 → 0)。パッカーの縮尺を入らなかった縦と入った縦の二分探索にした(最初の失敗時の割合で一度に決めていたのが被覆の揺れの正体。缶 0.50 → 0.54、枠 0.50 → 0.53、取っ手 0.62 → 0.63、pack 25〜50 → 50〜100 ms)。同一座標の頂点の由来を直した(第 9 回: `UapBrickStore` / `UapBurstKernel` の頂点配置が、折れ目の上で面上のままセルの外に出た QEF の解をセルの面に硬く押し付け、隣り合う 2 セルが共有面の同じ点に押し付けられていた。面上の解は `ProjectSlack` の範囲でセルの外に残す(残す量は |d| に連続: 二択では管理コードと Burst が許容の境目で 0.3 セル離れた)。枠の零面積三角形 4 → 0、34 チャート、被覆 0.525 → 0.604)。細長三角形 460・境界辺 10 の理由を直した(第 10 回: 格子面が対称面 y = 0 と接平面 z = −350 mm に一致し、面上の格子線の標本が d = 0 で符号が無作為に反転していた。標本が面上ならセルの 1/1000 だけ外側と読む `UapBrickStore.SampleEpsilon`、両カーネル。枠は細長 490 → 6、境界辺 0、被覆 0.62。原点のずらしと近接頂点の束ねは却下)。角の近接頂点対 6 を直した(第 11 回: 折れ目が 2 セルの共有面を横切る所で両セルの解が同じ点に落ちる 25〜53 µm の対。長さがセルの 0.5% 以下の辺を多様体を保つときだけ潰す `CollapseCoincident`、`CollapseShare` 0.005。枠の細長 0、非多様体 0。被覆 0.617 → 0.573 は同じチャート構成での詰め方の揺れ)。ラスタ詰めは 3 つの並び順(面積・長辺・外接矩形)で詰めて最も密な結果を採る(第 12 回: 枠の予算掃引の平均 0.583 → 0.597、缶 0.51 → 0.53、pack 155〜285 ms。向き 4 通りの最下位置は却下)。小片の吸収で片側が空になるときは分割を立てる(吸収が分割を打ち消して深さ上限まで同じ分割を繰り返し、缶 8,000 で 22 三角形を退避させていた。退避 0)。重なりラスタの下限 64 → 128 セル。unchanged 判定に `targetTriangles` / `textureSize` を足した(予算だけ変えた再ベイクが unchanged になっていた)。さらに `normalMap` / `ao` / `aoSamples` / `aoDistance` / `remesh` / `remeshMirror` / `remeshAdaptivity` / `output` / `region` も比べる(AO を付けただけ・サンプル数を変えただけの再ベイクが unchanged になり、マップが更新されないように見えていた。木を編集した再ベイクは PNG のバイト列とインポートハッシュの両方が変わることを BakeExp で確認)。判定の鍵は入力スキーマから導くようにした(`RequestKey`: 免除 8 項目以外の全プロパティ、スキーマの `default` を省略と同一視、enum は大文字小文字を無視。個別比較のヘルパー 5 本は削除)。
- Pro: 実機報告(Linear 色空間の VRChat プロジェクト)の修正(同ノート 7 節): ID パスの RT を `RenderTextureReadWrite.Linear` に(sRGB の既定では ID のバイトがガンマ変換され、面・頂点が選べない / カーソルとずれた所が選ばれる / ストロークが始まらない)。Sculpt / Edit Mesh のパネル上のマウスイベントはパネルのボタンに渡す(既定コントロールが先に取ってボタンが効かず、裏のメッシュにストロークが始まっていた)。ホストプロジェクトを Linear にした Xvfb の GUI 実行で再現と修正を確認。pro-v0.14.0-beta.5 を再カット(2026-10-17)
- Pro: Own part のリメッシュが低品質になる報告の修正(`bench-2026-10-18-ownpart/`、設計ノート 2026-10-12 の 28 章と 2026-10-01 parts の 3.6 節): even が縮約へ退避していた理由(落とした隠れ面の頂点の残り、円柱の縁の面取り = 側面の面を円柱全体の距離で射影・到達距離が抽出の辺)を直し、隠れ面は主側だけ落として部品は接触面を保ち、落とした境界は折れ線に拘束、部品は bake のセルと段数で作り、接触面の勾配はメッシュの向きで決める(`UapFieldSet.Gradient(hint)`、`ComputeOriented`)。蓋 0.767 → 0.007、法線補正 p95 180° → 0.4°。`topology.fidelity.beyond`、`quality.hiddenFaces`。報告のデッキチェア(Exact の木枠 + Own part の角丸の板)は even の射影先が抽出全体で板の頂点が Exact の横木へ吸い付いていたので距離場へ射影(`RemeshOptions.Surface`、0.24 → 0.027)、主場が空のモデルで grid の解像度探索が走らなかったのも修正。テスト 3 本。続けて `topology` の既定を `auto` に(設計ノート 2026-10-12 の 29 章): 予算の grid 候補を抽出して法線補正の p95 を測り、10° 以内なら grid、超えれば even(ガイド・remesh・cage・flatScale・detail は even、予算を 1.25 倍より超える grid は even、与えた resolution は even 側)。瓶と蓋・箱を貫くピン → grid、角丸の板・球のそばの角丸の箱 → even。grid の段数選択にも予算の上限。`topology.auto`(`gridP95Degrees`)。テスト 1 本、even 検証の既存テストは明示指定に。pro-v0.14.0-beta.5 を再カット(2026-10-18)
- Pro: Edit Mesh の移動ハンドルが Local で選択の法線を向く(設計ノート `2026-10-13-own-mesh-leaf-and-baked-in-stage.md` 7.1 節): `UapElementSelection.Normal`(面 / 辺 / 頂点の周りの面の面積法線の和)を足し、Local では Z を法線・X / Y を面内にした `LookRotation` の向き(選択変更時に読み、移動中は保持。法線の無い選択はホストの回転のまま)。テスト 1 本追加。pro-v0.14.0-beta.5 を再カット(2026-10-18)
- Pro: ベイクのマテリアル(`docs/design-notes/2026-10-18-bake-materials.md`): `uap_model_bake` に `materials`(既定 `model`: ルートの `materials` のマテリアルをそのまま共有で使い、焼いたマップは割り当てず警告で示す / `generate`: 従来どおり生成して割り当て / `none`)。スロットが増えたベイクがシーンのレンダラーに生成マテリアルを後ろから足していた(`renderersExtended`、プレファブのインスタンスでは上書きになり見た目が変わった)のをやめ、数えて名前を警告に出すだけ(`renderersShort`)、`(baked)` がプレファブのインスタンスなら触らない。テスト 1 本。pro-v0.14.0-beta.5 を再カット(2026-10-18)
- Pro: KernelSmoke の赤を消す(`docs/design-notes/2026-10-12-bake-topology-uv-redesign.md` 30 章): 三角形のパッチを法線方向に半セル押した点で引き直す(穴の脇の上面が「円柱の側面」と読まれ縁のループが切れた)、線へ寄せる reach を最初の移動(鎖の辺 2 本)と投影先(目標辺の 0.5)に分ける、鎖上の扇の弦を入れ替える `FlipChainFans`、入れ替え判定 `FlipImproves` を三角形の向きから組んで一本化(`GetEdgeV` はソート済み)、線上の短い辺(目標辺の 0.25 未満、両端が同じ線)を最後につぶす `CollapseShortLineEdges`、縫い目ヒントは切れ目の 75% 以上がヒント側のときだけ採る(`HintKeepShare`)。診断 `ChainLog` / `AfterRound` / `KERNELSMOKE_DUMP`。薄い板の端が削れる(腕のパッドの端 6 cm)のを、頂点自身の法線方向に場の零点を探す投影(`RemeshOptions.Distance` / `DistanceReach`)で止め(椅子 8,000 で p99 1.78 → 0.70 セル)、葉ごとのサンプルの 1 割が許容超なら(`PartLostShare`)小さい部品の喪失としてデシメーションへフォールバック。pro-v0.14.0-beta.5 を再カット(2026-10-23)
- 実環境テスト(アクアリウム)の修正で Core に足したもの: `uap_scene_duplicate`(count / offset / parentPath、プレハブのリンク維持)、`uap_property_set` / `uap_object_inspect` の `componentType:"GameObject"`(active / layer / tag / static)、`uap_editor_screenshot` の `camera` と `sceneView`、階層パスの `Name[i]` と `#id`(同名の兄弟を指せる)。`### Added` が入ったので v0.60.1 の次は 0.61.0-beta.1(2026-09-30)
- 同梱スキル導入の Core 側: 設定「同梱スキルをこのプロジェクトに配置する」(`bundledSkillsEnabled`、既定オン、再接続不要)を拡張プロファイルのカードに追加、`uap_scene_create_object` の `primitiveType` 説明をブロックアウト・コライダー用途に限定。0.61.0-beta.2(2026-09-30)
- 長い会話でタブ再表示時に数秒〜十数秒固まる件の対策: トランスクリプトを仮想化し、ビューポート近傍の行だけ実体化してスクロールに応じて生成・解放する(上下は計測/推定高さのスペーサー、読んでいる行は動かない、300 件上限と非表示注記は廃止、スクロール位置はメッセージ単位でリロード後も復元)。0.61.0-beta.3(2026-09-30)
- 会話ログのテキスト選択・コピー・「選択部分について質問」: 応答・自分の発言・思考・ノート・エラー・ツール結果の Label をマウス選択可能にし(Ctrl/Cmd+C でコピー)、メッセージの右クリックメニューに「選択をコピー」「メッセージをコピー(Markdown 原文)」「選択部分について質問(引用を入力欄へ挿入)」を追加。0.61.0-beta.4(2026-10-01)
- Scene ビューの表示物を消さずに隠す「表示」トグル: Agent Tools ツールバーの 4 つ目のトグルとコンテキストバー「マーカー N 個」チップの目のボタンで、マーカー・ピン・スケッチ(Pro の Model 状態ラベルも)を Gizmos ボタンのように一時非表示にする。ストアは保持、チップに「(非表示)」、`uap_marker_add` / `uap_marker_list` はエージェントに非表示中と伝え、スクリーンショットにも載らない。EditorPrefs で再起動後も維持。設計ノート `docs/design-notes/2026-10-01-scene-overlay-visibility.md`。 0.61.0-beta.6(2026-10-02、main の beta.5 が先にタグ付けされたため再カット)、v0.61.0-beta.9 として再カット(2026-10-23、main の beta.8 が先にタグ付けされたため)
- その他の ACP エージェント実機テスト(`ci/AcpLive`、13 本)で見えた 2 点: 端末専用の認証メソッド(`_meta.terminal-auth` / `type:"terminal"`、Qwen Code・Copilot CLI・Kimi Code)しか無いときは `authenticate` を送らず「`copilot login` を実行して再接続」とコマンド名を出す。エージェントが stdout に書いたサインイン URL(Gemini CLI のブラウザ無し経路)も stderr と同じ経路でチャットのリンク注記と CLI 出力ログに出す。その他 ACP ガイド用の実機スクリーンショット(OpenCode の接続〜応答、Qwen / Copilot / Auggie / Cline のサインイン画面)を撮る過程で、ドメインリロード後に ACP のサインイン注記が出ない件と、端末専用認証で再接続を 4 回繰り返す件を修正。0.61.0-beta.5(2026-10-07〜08)
- スケッチの高機能化: 形(フリーハンド / 直線 / 折れ線 / 矩形 / 楕円。ツールバーのドロップダウン・「形」サブメニュー・1〜5 キー、表面モードはドレープ)、描画中の視点操作(ホイールはズームに戻し、深度は Ctrl+ホイール / `[` `]`。折れ線はクリックの合間に視点を動かせる)、エリア分け(閉じた線を塗って名前を付け、`uap_stroke_list` に `name` / `shape` / `area` / `centroid` / `contains`)。0.61.0-beta.6(2026-10-09)
- v0.61.0-beta.7: Play 中のゲームへの入力ツールを追加。`uap_game_pointer`
  (uGUI の EventSystem にクリック / 長押し / ドラッグを送る。階層パスまたは
  左上原点のスクリーンショット座標で指定)と `uap_game_key`(Input System に
  キーの押下・保持・解放を注入。旧 Input Manager は非対応と明示)。どちらも
  リフレクション実装で、パッケージが無いプロジェクトでもコンパイルが通る。
  これで uLoop 常駐コスト調査 §9.2 のギャップ表が埋まり、uLoop は hot-reload /
  pause-point / dynamic-code のためだけの任意パッケージになる
  (`docs/design-notes/2026-09-22-game-input-tools.md`)。
  あわせて uLoop 自前化ロードマップのフェーズ 1 を実装
  (`docs/design-notes/2026-09-22-uloop-phase1.md`): スクリーンショットの UI 要素一覧
  (`annotate_ui`)、`uap_game_mouse`、`uap_compile`、`uap_project_info`、
  `uap_editor_focus`、`uap_help`、Claude Code 以外向けのコンパクトなツール一覧
  (`uap_tools` / `uap_call`。既定は自動)、HTTP MCP 非対応の ACP エージェント向けの
  stdio 中継(Unity 同梱の .NET でビルド)、CI の Core-only ホストに uGUI / Input System を追加。
  続けてフェーズ 2(記録と観測、`docs/design-notes/2026-09-23-uloop-phase2.md`)も
  同じベータに入れた: 値の時系列を 1 回で返す `uap_game_watch`、Game / Scene ビューを
  裏で録画する `uap_record_video`(mp4 / Linux は webm)、Play 中の入力を記録・再生する
  `uap_input_record` / `uap_input_replay`。
  フェーズ 3(`docs/design-notes/2026-09-28-uloop-phase3.md`)として、型付きツールで
  できない操作用の C# 実行 `uap_execute_code` を新モジュール「code」(既定で有効)に追加:
  AssemblyBuilder で裏コンパイル、権限カードに全文を必ず表示し自動承認しない、危険パターン
  検出時は `confirm:true` が必要、Undo グループ、ロード済み件数の報告と `action:"reset"`。
  フェーズ 4(`docs/design-notes/2026-09-28-uloop-phase4.md`)として、Agent Panel Pro に
  Harmony + Cecil 基盤の新「patching」モジュールを追加し、Core 側にもトグル行を配線:
  `uap_hot_reload`(ドメインリロードなしで static メソッド本体を書き換え)と
  `uap_pause_point`(file:line にローカル変数付きの一時停止ポイントを挿入)。
  Mono スクリプティングランタイム限定(Unity 6.8+ の CoreCLR エディタは非対応)で、
  既定OFF・fx/mesh と同じく Agent Panel Pro 提供。ステアリング文にも家族テキストを追加。
  さらに、複数ツールにまたがる手順(Play で動作確認、uGUI クリック、スクリプト変更、
  入力の記録と再生、execute_code、コンパクト一覧)を `uap_help topic:"<id>"` の
  プレイブックとして必要なときだけ返す仕組みを追加し、ステアリング文は手順を外して
  利用可能な ID の 1 行だけにした(`docs/design-notes/2026-10-02-uap-help-playbooks.md`。
  追加プロバイダ `IUapPlaybookProvider` で Pro も patching / test-run のプレイブックを提供)。
  最後に uLoop 自前化ロードマップ §6 の最終行(`docs/design-notes/2026-10-02-uloop-card-final.md`):
  設定の uLoop カードは uLoop を検出したプロジェクトでだけ表示し、中身は注意書き 1 行と
  「エージェントに uloop を使わせる」スイッチと削除ボタンだけにした(導入ボタン・進捗行・許可プリセット・
  指示スニペット・長いコスト説明と、それ専用のコードを撤去)。スイッチの既定は新規設定でオフ
  (保存済みの値は移行しない)。`UloopCapabilityMatrix` と `ToolRegistry.CreateDefault(bool)` の uLoop
  フラグも削除し、全ての `uap_*` ツールを常に登録する。
  最後にセッション常駐コストの計測で見つかった 3 件を直した
  (`docs/design-notes/2026-10-02-session-context-cost.md`): Claude Code が 2,048 文字で
  切る MCP ツール説明を超えていた `uap_execute_code` / `uap_hot_reload` / `uap_model_apply` を
  1,700 字前後に縮めて安全文を残し(ガードテストつき)、モデリング プロファイルを新フィールド
  `requiresUapModules` で mesh モジュールが有効なときだけ注入し、Core 単体のステアリング文が
  Pro 専用ツール名(prefab の上書き等)を出さないようにした。
  コンパクトなツール一覧(Claude Code 以外が毎セッション読む分)の `inputSchema` を、
  型・enum・required・範囲などはそのままに各項目の説明を 1 文(100 字まで)に縮めた軽量版にして
  35,681 → 29,738 字(-16.7%)にした。フル一覧と `uap_help` / `uap_tools` の `names` は完全なスキーマのまま
  (`UapLeanSchema`、`docs/design-notes/2026-10-02-session-context-cost.md` §6)。
- v0.61.0-beta.8: `uap_execute_code` の行アイコンを Console の警告マークから C# スクリプトのアイコンへ変更(実行前から「失敗した」ように見えていた)。

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
| v0.46.0 | ツールカードの詳細が大きな入力で空白になる不具合の修正(UI Toolkit の 1 要素 65535 頂点上限。約 16,000 文字超のテキストを 1 つの Label に入れると `A VisualElement must not allocate more than 65535 vertices` で描画されなかった): ツールカードの入力/結果、Markdown のコードブロック、添付ペイロードを `LongTextChunker` で 8,000 文字ごとに分割して要素を積み、200,000 文字超は「… あと N 文字」の注記とコピーボタンに退避。Write/Edit/MultiEdit のツールカードに「変更内容」ビューを追加(対象パス + `+`/`-` 行。Edit 系は権限カードと同じ diff、Write は全行追加として表示、40 行超は「全 N 行を表示」、Write はコピーボタンで本文全文を取得)(2026-09-13) |
| v0.47.0 | Claude Code 専用だった 4 機能を ACP エージェントにも拡張: 履歴ブラウザ(パネル側の `PanelSessionStore` で ACP セッションを保存・一覧・再開、再開できないエージェントには会話ログを引き継ぎ)、パネル内ログイン(`codex login` / `grok login` をパネル内で実行し、リンク・出力・初回カードを表示)、サブエージェントのモデル設定(新セッション冒頭の指示文として送信)、思考ブロック(本文→思考の順序崩れを修正、文言訂正)(2026-09-13) |
| v0.47.1 | 「中断後に自動継続する」のツールチップ(英/日)と操作ガイドに残っていた「連続 3 回まで」の記述を訂正(上限は v0.36.0 で撤廃済み。止まるのは CLI 連続終了によるクラッシュループ停止と手動停止のみ。Core と Pro で挙動の差はない)。操作ガイドに章ごとのスクリーンショット(`docs/images/guide/`、撮影キットで再現可能)を追加し、設定画面リファレンスを現在の並びに更新(2026-09-14) |
| v0.48.0 | `uap_query_component_types` の各ヒットに `kind`(`component` / `stateMachineBehaviour` / `scriptableObject`)を追加。StateMachineBehaviour は ScriptableObject 派生のため以前から検索には出ていたが Component と区別できず、`uap_component_add` を試して `Unknown component type` で詰まる経路になっていた。あわせて StateMachineBehaviour 専用の型解決を `UapComponentTypeResolver` に追加(Pro の新しいアニメータツールが使用)(2026-09-14) |
| v0.49.0 | `uap_editor_select`(エディタの選択を設定する。`uap_editor_execute_menu` で駆動したいサードパーティのメニュー項目の多くは引数を取らず `Selection` を読むが、パネルは選択を読むことしかできず設定できなかった: NDMF / Modular Avatar の 「Manual bake avatar」、VRChat SDK のビルドパネル、UniVRM のエクスポータ、Bakery の selected スコープ、RPG Maker Unite の各エディタ。`path` / `paths` / `assetPath` / `clear` のいずれか 1 つを取り、全パスを解決してから Selection に触るので途中失敗で中途半端な選択が残らない。単一指定は `Selection.activeGameObject` に確実に入る。ReadOnly ではない = 自動承認しない) + 拡張プロファイルの検出が VPM / 埋め込みパッケージを見るように修正(VCC / ALCOM は VPM パッケージを `Packages/<id>/` に実体コピーし `Packages/vpm-manifest.json` で管理するため、`Packages/manifest.json` の `dependencies` にも `Library/PackageCache` にも現れず、`packageIds` 側の判定が VRChat エコシステム全体に対して一度も当たっていなかった)。設定 > 拡張プロファイルの説明文(英/日)に NDMF / Modular Avatar を追記。Pro 側に NDMF / AAO の同梱プロファイルを追加し Modular Avatar プロファイルを書き直し(pro-v0.7.0)(2026-09-15) |
| v0.49.1 | 実使用フィードバック 3 件(`docs/design-notes/2026-09-15-panel-ux-followups.md`)。(1) MCP 経由のコンパイルのたびに「このエラーを修正」チップが一瞬出て自分で消える問題を修正。コンパイラエラーを `assemblyCompilationFinished`(実行の途中で 1 アセンブリごとに発火)で publish していたため、次の実行の世代交代かドメインリロードで消える途中経過を表示していた。実行中はバッファに溜めて `compilationFinished` で一括 publish(HUB-8 のリロード前スナップショットが見られるよう同期 flush)、途中で世代交代した実行は何も残さない、コンパイル中は `Settling` でチップと空状態の提案を出さない。捕捉内容自体は不変なのでコンパイル後判定と無視リストは従来どおり。`uap_scripts_commit` の検証ビルド(プロジェクト外でステージ済みスクリプトをコンパイル)が吐く診断も窓で捨てる —— エージェントにはツール結果で逐語で返っており、パスは `UapStaging/` でユーザーが開けない。(2) 設定の「Agent Panel Pro の更新」カードを「Unity 連携」から「接続とアカウント」へ移動(アカウントの直後)。Unity 連携は Unity 側の機構の置き場で、購入時のレジストリ URL と製品キーを資格情報に書くカードは主題が違ううえ探しづらかった。セクション ID 据え置きで折りたたみ状態は引き継ぐ。(3) 設定のツールチップが行全体に載っていて読み進めるたびに段落が浮く問題を修正。「?」マークが付いた行はツールチップをマーク側へ移し、短いキャプションは従来どおり行ホバー。マーク付与の閾値を文字数から表示幅(CJK=2)に変更し、日本語で最も長い説明がマークをもらえていなかった逆転を解消(2026-09-15) |
| v0.50.0 | Pro の新モジュール 3 つ(`authoring` / `avatar` / `batch`)のための設定行と受け口を追加。設定 > Unity操作(UapOps) に「プロファイル作成」「アバター計測」「一括実行」の行(いずれも既定 OFF、Core 単体ではトグルが無効化され「別売の Agent Panel Pro 拡張パッケージが必要です(未導入)」のヒントが出る)。あわせて `ToolRegistry.AllNames()`(モジュール一覧を要求せずに登録済みツール名を返す。`ListEnabled` はモジュール指定が必須で「その名前のツールが存在するか」を聞けない)と `UapOpsServer.EnabledModules()`(モジュールのフィルタは `tools/list` 構築時にしか効かず、名前で他のツールから呼ぶ経路は素通りするため、呼ぶ側が確認できる必要がある)を新設。設定 > 拡張プロファイルのツールチップと README / 操作ガイドの同梱プロファイル一覧を 9 件から 12 件へ更新。設定 > 拡張プロファイルに「プロファイルが無い導入済みパッケージ」の一覧と「下書きを頼む文をコピー」ボタンを追加(Pro の authoring モジュールがある時だけ表示。依頼文は composer ではなくクリップボードへ — 打ちかけの本文を壊さないため)。あわせて Pro の EditMode テスト実行ツール(`uap_test_run`)のための「テスト実行」行(既定 OFF)。この行だけはグレーアウトの理由が 2 通りある —— Pro 未導入と、「Pro はあるが `com.unity.test-framework` が無い」 —— ので、後者には Test Framework の導入を案内する別の文言を出す(買った人に「別売の Pro を買え」と言わないため)。「アバター計測」行の説明文を、モジュールの現在の中身(計測・NDMF ベイク・エキスプレッションメニュー編集)に合わせて更新。プレハブステージのガードが返す拒否文言に、それを実行できるツール名(Pro の `uap_prefab_stage`)を明記 —— 従来は「prefab mode を閉じてください」と言いながら、閉じる手段がどちらのパッケージにも無かった。Core 自体の挙動は変わらない(2026-09-15) |
| v0.51.0 | `uap_rect_transform_set`(uGUI の RectTransform のアンカー相対のレイアウト値—— `anchorMin` / `anchorMax` / `pivot` / `anchoredPosition` / `sizeDelta` / `offsetMin` / `offsetMax`—— を 1 回で読み書きする。`uap_transform_set` が書く `localPosition` は RectTransform ではアンカーから毎レイアウトパス再計算される従属値で、アンカーされた要素を動かしても次のパスで戻るため、正しい経路は `uap_property_set` をプロパティごとに最大 5 回 + アンカー計算を手でやる、しか無かった。各フィールドは軸ごとの部分マージ。アンカーとピボットは Unity の setter どおりraw に書き、Inspector のような補正はしない代わりに `keepRect: true`(矩形を保ったままアンカーし直す)を用意。`preset` は Inspector のアンカーグリッド 16 セルを `<垂直>-<水平>` で名前指定しピボットも設定する(`keepPivot` で回避)。冗長な組み合わせ 3 組は優先順位を決めず拒否し、検証は全て書き込み前に走る。戻り値に解決後の `rect`、`parentRect`、上書きしてくる `LayoutGroup` / `ContentSizeFitter` / `AspectRatioFitter`、`warnings` を含む。`core` モジュール、Undo 可、プレハブステージガード付き、ReadOnly ではない = 自動承認しない。設計ノート `docs/design-notes/2026-09-15-rect-transform-layout-tool.md`)(2026-09-15) |
| v0.52.0 | `uap_rect_transform_set` に World Space Canvas(VR / ワールド内 UI)の経路を追加。Screen Space の Canvas はルートの rect を Unity が毎フレーム画面から計算し直すため書き込みが残らないが、World Space の Canvas は作者が rect を決める実在オブジェクトで書き込みは残る。v0.51.0 はこの区別をせず、**ルート Canvas なら render mode を見ずに**「書いても残らない」と警告していた —— VR の最も普通の操作に対して正しい書き込みを「効かない」と誤報していたことになる。最も近い Canvas を自分から親へ遡って探し (`UapCanvasProbe`、UI モジュールを剥がしたプロジェクトでも壊れないよう型名 + リフレクションで判定)、render mode が Screen Space と**分かっている**ときだけ警告するようにした(読めない場合は「不明」であって Screen Space ではない)。戻り値に `canvas`(`path` / `type` / `renderMode` / `worldSpace` / `scope`)と `worldSize`(`rect` × `lossyScale`。World Space ならメートル)を追加。`worldSize` は書き込みも可能で、「このパネルを 1.2m 幅に」をそのまま渡せる —— `lossyScale` で割り、アンカーが張る幅を引くので、伸びた子では `sizeDelta.x` が 500 ではなく -500 になるという計算を呼ぶ側にさせない。`sizeDelta` / offsets とは排他、`lossyScale` が 0 の軸があれば書き込み前に拒否。`keepRect` とアンカー計算は親 rect 空間で完結し render mode に依存しないため変更なし(2026-09-15) |
| v0.53.0 | スクリプト検証ゲートの「ステージング無視」報告への対応(`docs/design-notes/2026-09-15-script-gate-steering-and-powershell.md`)。(1) ゲート有効時、システムプロンプトに「`*.cs` / `*.asmdef` は Assets に直接書かず `UapStaging/`(Assets 配下のサブパスを鏡写し)に置いて `uap_scripts_commit` で入れる。自分でコンパイル/リフレッシュを起こさない」の 2 行を注入。従来は拒否文でしか伝えておらず、セッション開始・compact・resume のたびに Assets 直書き → 拒否の往復が起きていた。(2) Windows の CLI シェルツール名は `PowerShell` だが can_use_tool 側の事前フィルタは `Bash` 厳密比較だったため `Set-Content Assets/Foo.cs` が素通りしていた問題を修正。`Bash` / `PowerShell` / `Shell` を同一扱いにし、`Set-Content` / `Add-Content` / `Out-File` / `New-Item` / `Copy-Item` / `Move-Item` / `[IO.File]::WriteAll*` を書き込み標的として抽出。生成する PreToolUse フックの matcher も `Bash|PowerShell` を含め、同じ走査をフック内で再実装(「常に許可」ルールで can_use_tool が来ない場合でも止まる)。(3) 拒否のシステムノートをストリーミング中のアシスタント発話のブロックとして挿入し、拒否した呼び出しの直後に表示(従来は別メッセージとしてターン全体の後ろに出ていた)(2026-09-15) |
| v0.53.1 | 設定 > 拡張プロファイルのツールチップ(英/日)と README / 操作ガイドの同梱プロファイル一覧に ProBuilder を追加(pro-v0.10.0 で同梱 13 件目として追加。設計ノート `docs/design-notes/2026-09-15-probuilder-profile-and-mesh-route.md` に、コンパイル不要のメッシュ生成経路として ProBuilder 経由ではなく `uap_mesh_create` を推す検討も記録)。Core の挙動は不変(2026-09-15) |
| v0.54.0 | Pro の新モジュール `mesh`(`uap_mesh_create` / `uap_mesh_inspect` / `uap_mesh_edit` / `uap_mesh_boolean` / `uap_mesh_validate` / `uap_mesh_repair` / `uap_scene_zfight_scan`: コンパイル不要のメッシュ生成・調査・範囲編集・ブーリアン・不整合の検出と修復・Z ファイティング走査。`docs/design-notes/2026-09-15-mesh-create.md`、`2026-09-15-mesh-edit-sdf-boolean.md`、`2026-09-15-mesh-validate-repair-zfight.md`)のための設定行「メッシュ生成」を 設定 > Unity操作(UapOps) に追加(既定 OFF、Core 単体ではトグルが無効化され「別売の Agent Panel Pro 拡張パッケージが必要です(未導入)」のヒントが出る)。Core 自体の挙動は不変(2026-09-15) |
| v0.54.1 | main で落ちていた EditMode テスト 5 件の修正(`docs/design-notes/2026-09-15-mesh-ci-fixes.md`)。Core 側は 設定 > Unity操作(UapOps) の「メッシュ生成」行の説明文が 110 文字上限を超えていた件: インラインを 1 行に縮め、ツールごとの説明はホバーのツールチップ(`SettingsUapOpsModuleMeshTooltip`)へ移動。あわせて Pro 側 pro-v0.12.1(プリミティブのシーム列と球の底の極を双子と厳密一致させる。SDF・ブーリアン・winding のテスト 3 本は主張の側が誤りだったので期待値を幾何に合わせた)(2026-09-15) |
| v0.54.2 | 「コンパイル後に自動継続する」の「1 ターンにつき 1 回」ガードレールを撤廃(`docs/design-notes/2026-09-15-chained-auto-continue-after-compile.md`)。自動継続ターン自身が `uap_scripts_commit` でさらにコミットした場合も、そのリロード後に再び継続する(従来は「このターン自体が自動継続だったため 2 回目は行いません」で止まっていた)。書く→コミット→コンパイル→直す のループを人が毎回つなぎ直す必要をなくす。止まる条件はエージェントがコミットをやめる・クラッシュループ停止・停止ボタンの 3 つで、2026-09-10 に上限を撤廃した「中断後に自動継続する」と揃えた。起因判定(エージェント自身の .cs/.asmdef コミットのみ)、チケットの 5 分期限、システムノートは不変(2026-09-15) |
| v0.54.3 | AskUserQuestion カードを「ウィンドウで開く」で分離すると、パネル側に約 220px の空カードが残って会話履歴を隠していた件の修正(`docs/design-notes/2026-09-16-askuserquestion-detached-ghost-card.md`)。質問カード用の高さ下限 `.uap-perm--question` が variant だけで付いていたため待機バー状態でも効いていた。`.uap-perm--expanded` と同じ可視性ゲート(インライン・展開中・ウィンドウ非表示)に揃え、折りたたみ時も外れるようにした。回帰テスト 3 本追加(2026-09-16) |
| v0.54.4 | ウィンドウリサイズ中に会話(最大 300 行)・設定・履歴の折り返しラベルが毎フレーム再計測されて Repaint が重かった件の修正(`docs/design-notes/2026-09-16-resize-reflow-throttle.md`)。縦 ScrollView の内容幅をドラッグ中はピン留めし、幅が 120ms 静止した時点で一度だけ再折り返しする `ScrollReflowThrottle` を追加。純関数 `ShouldFreeze` のテスト 6 本追加(2026-09-16) |
| v0.54.5 | パネルが大きいときにインラインで表示された権限/質問カードが、その後パネルをインライン最小(高さ 380px / 幅 300px)より小さくしても展開されたまま(質問は下限 220px)で残り、コンポーザーとステータスバーを画面外へ押し出していた件の修正(`docs/design-notes/2026-09-16-inline-card-refit-on-shrink.md`)。インライン/ウィンドウの自動判定はリクエストごとに 1 回だけ(UICODE-8)なので、`ChatView` がチャットルートのサイズ遷移を見て「小さすぎる」に入った瞬間に展開中のカードを要約行へ畳み、出た瞬間に自分が畳んだカードだけを開き直す `PermissionCardLayout.ResolveInlineFit`(純関数)を追加。ユーザーがシェブロンで開閉したカードには触らない。テスト 9 本追加(2026-09-16) |
| v0.54.6 | 「+」で新規会話を作った直後(再接続直後も同様)にヘッダー/折りたたみチップからモデルを切り替えても、そのセッションに反映されなかった件の修正(`docs/design-notes/2026-09-17-model-switch-before-init.md`)。initialize 応答が返るまでの数秒はメニューがキャッシュ入口になり、選択は「次回セッションの既定」を書くだけだった。クライアントが生きている間の選択はセッション向けに保留し、initialize が解決した瞬間に `set_model` を送る。保留中はチップに選んだモデルを表示。切替の結果(待機中 / 適用 / 失敗・タイムアウト)を会話ログのシステムノートで確認できるようにし、`AgentClient` はタイムアウトした control_request も `ControlRequestResolved` で通知する。テスト 6 本追加・1 本更新(2026-09-17) |
| v0.54.7 | ACP(Grok / Codex)のツール名写像の修正(`docs/design-notes/2026-09-17-acp-tool-name-mapping.md`)。Grok は MCP ツールを `use_tool` 経由・`unity-ops__uap_x` 名で呼ぶため `mcp__unity-ops__uap_*` に写らず、自動承認レベル「全 Unity 操作」でも `uap_*` ごとに許可カードが出ていた。実機(Grok Build 1.0.34 / codex-acp 1.12.0)のフレームを取り直し、ツール id は「id が入る場所」だけから構造で読むように変更(自由文 title からの検索は、Codex のシェル title = コマンド行を `uap_ping` として自動承認しうる穴だったので廃止)。kind が other / 無しのツールはエージェントの title を表示、Grok のシェルは `_meta` の kind で最初から Bash、Codex の他 MCP サーバ呼び出しは MCP カードに。実フレームのテスト 5 本 + 純関数テスト 5 本追加・2 本更新(2026-09-17) |
| v0.54.8 | Codex(codex-acp)で MCP ツールが返した画像がツールカードに埋め込み表示されず、base64 入りの約 500 KB の JSON 文字列が結果テキストとして流れていた件の修正(`docs/design-notes/2026-09-17-acp-mcp-rawoutput-images.md`)。codex-acp は MCP の結果を `content` ではなく `rawOutput` = `{"result":{"content":[…]},"error":null}` で渡し、ACP ブリッジはそれを丸ごと文字列化していた。`result.content` が配列なら `content` 経路と同じく text は結果テキスト、image は埋め込み画像(1 結果 4 枚・base64 8 MiB まで、超過分は `[image]`)に訳す。他の形の `rawOutput` は従来どおり。テスト 9 本追加(2026-09-17) |
| v0.55.0 | `uap_editor_execute_menu "File/Save"` で Editor が固まった件の修正と `uap_scene_save` 追加(`docs/design-notes/2026-09-17-modal-menu-and-base64-scan.md`)。原因は独立した 2 つ: (A) 未保存シーンの `File/Save` がネイティブモーダル「Save Scene」を開き、人が閉じるまで main thread が Win32 モーダルループに留まる(実測 110 s で tick 0 回、実機 288 s)。(B) 直後のスクリーンショット結果(498 KB の base64)を Codex が 1 本の JSON 文字列で渡し、`ToolResultImages` のパス検出正規表現が O(n²) で `EditorUpdatePump.OnEditorUpdate` を占有(再現 431 s、実機 256 s)。対策: モーダルを開くと実測したメニュー 7 項目を実行前に拒否(`UapMenuDialogPolicy`)、ダイアログを出さない `uap_scene_save` を新設、拒否した `File/Open Scene` の代替として `uap_scene_open`(single は未保存シーンがあると拒否、`discardUnsaved` / `additive` で回避)も追加、サードパーティのモーダルはタイムアウト文でダイアログ名を告げる(`UapNativeModalProbe`、Windows)、パス検出を拡張子起点の窓走査で線形化(同入力 1 ms)。テスト 4 ファイル追加・2 ファイル更新(2026-09-17) |
| v0.55.1 | エージェント自身のツール呼び出しが出した Console エラーで「修正を依頼」チップが出ていた件の修正(`docs/design-notes/2026-09-17-tool-caused-console-errors.md`)。実機の Codex が `uap_editor_execute_menu` に存在しないメニュー(`GameObject/Duplicate`)を渡すと、ツールは `found:false` を返すのに Unity 自身が Error を出し、チップがセッション・バックエンドをまたいで残っていた。UapOps ツールの実行中にメインスレッドで出た Error はその呼び出しに帰属させ、チップには積まずツール結果の末尾(`Unity Console errors logged during this call:`、最大 5 行)でエージェントに返す。別スレッドのエラーと呼び出し後に再発したエラーは従来どおり。`uap_editor_execute_menu` は実行前に `Menu.MenuItemExists` で存在を確認し、この Error 自体を出さない。テスト 7 本追加・2 本更新(2026-09-17) |
| v0.55.2 | 設定の CLI カードとアカウントカードを 1 枚の「エージェント」カードに統合(`docs/design-notes/2026-09-17-account-card-agent-picker-and-acp-init.md` で先にエージェント選択を折りたたみの CLI カードから出し、`2026-09-17-agent-card-merge.md` で統合)。上から エージェント選択 → 解決結果とインストール → サインイン状態 → サインイン方式 → サインイン操作 → 詳細設定(実行ファイル / コマンド。未検出のとき自動で開く)→ 再接続 の順。「再検出」「今すぐ再接続」「再接続(ACP)」を 1 個の「再接続」に統合し、「診断」カードを「CLI の出力(stderr)」に改名。Claude 専用だった「APIキー認証」と ACP 専用だった「認証メソッド ID」を、エージェントで形が変わる 1 行「サインイン方式」にまとめた。折りたたみキー `cli` を廃止(テスト更新)。あわせて、ACP エージェントから Claude Code に戻した直後にアカウントカードが「APIキー(acp)で接続中」と誤表示していた件を修正: ACP ブリッジが合成する system/init の `apiKeySource: "acp"` を Claude 側の判定(`IsApiKeyAuth`)が読んでいた。`acp_backend` 付きの init は判定から除外し、ブリッジ側も `"none"` を送る。テスト 2 本追加。main が別内容の v0.54.7 を先に出したため、このブランチの v0.54.7 + v0.54.8 を v0.55.2 に再カット(2026-09-17) |
| v0.56.0 | Scene ビューへの描き込み(スケッチストローク)を追加(`docs/design-notes/2026-09-17-scene-sketch-strokes.md`)。コンテキストバーの「スケッチ」メニューと Scene ビューの「Agent Sketch」ツールバーで平面モード / 表面モードに入り、左ドラッグで線(S1, S2, ...)を描くとチップになる。平面モードは深度を先に決めて描き、平面と周囲メッシュの交線(断面の輪郭)・カーソル下の深度テスト付き格子・深度と「カーソル下の面が平面の何 m 前 / 後ろか」の読み出しを Scene ビューに表示(ホイール / `[` `]` で深度、F で面に合わせる、X/Y/Z で軸平面、C でカメラ正対、Shift で直線)。表面モードはカーソル下のメッシュ表面に描き、各点の法線とヒットしたオブジェクトを記録。保存前に Douglas-Peucker で間引き(長さの 0.5%)。新ツール `uap_stroke_list`(ReadOnly、module `markers`)が全ストロークを JSON で返す。ドメインリロードは SessionState で越え、シーン切替 / Play で消える。テスト 2 ファイル(19 本)追加・1 本更新(2026-09-17) |
| v0.56.1 | スケッチ機能を Prefab Mode で試した際の不具合 3 件の修正(`docs/design-notes/2026-09-17-scene-sketch-strokes.md` §9): 隠れているメインシーンのオブジェクトの断面が描かれ、ピン / 表面ストロークがそこに落ちていた(現在のステージに含まれるものだけを拾う)、軸平面をホイールで動かせない視点があった(軸平面は常に自分の法線方向へ)、読み出しラベルが Y 平面でしか見えなかった(Scene ビュー左上に固定)。テスト 1 本追加。あわせて Pro 側 pro-v0.13.0(段階 2: `uap_mesh_edit` の `stroke` 範囲、`uap_mesh_create` の `sdf` に `stroke` / `stroke_prism`、`extrude` の `stroke`。純関数 `UapStrokeGeometry` とテスト 11 本追加)(2026-09-17) |
| v0.56.2 | ACP エージェント(Grok Build など)で接続済みなのに設定の「エージェント」カードに強調表示の「サインイン」と案内が出続けていた件の修正(`docs/design-notes/2026-09-17-acp-account-card-phases.md`)。カードを 未サインイン(「サインイン」+案内)/ サインイン中(状態行・キャンセル・ブラウザ用リンク)/ 接続中(「{0} に接続しています...」、ボタン無し)/ サインイン済み(サインイン方式の行と強調なしの「アカウントを切り替え」)の 4 形に整理し、純関数 `ResolveAcpAccountPhase` で選ぶ。テスト 1 本(7 ケース)追加(2026-09-17) |
| v0.57.0 | `uap_web_fetch` を追加(`docs/design-notes/2026-09-17-web-fetch-tool.md`、段階 1 と段階 2 を 1 版にまとめて切り出し)。**段階 1**: エージェント自身の Web 取得はテキストしか返せないため、URL の中身をモデルが使える形で返す Core ツール(新モジュール `web`、既定 ON、世代 3)。PNG / JPEG は添付と同じ縮小をして image ブロックで同梱、GIF / WebP は 5 MB 以下なら素通し、PDF とその他のファイルは `Library/AgentPanel/Downloads/` に保存してパスと読み方(Read の pages 範囲)を返し、HTML は本文テキスト + 画像 / リンク URL 一覧、その他テキストは `max_chars` / `offset` の窓で返す。種類判定はマジックナンバー → Content-Type → 拡張子 → スニフ。ダウンロードは UapOps のワーカースレッド(`IUapOffThreadTool`)で行いメインスレッドを止めず、画像の縮小だけディスパッチャ経由でメインスレッドへ。公開ホストのみ(ループバック / プライベート / リンクローカル / マルチキャストをリダイレクトごとに拒否)、20 MB / 20 秒、Cookie・認証ヘッダなし。外部送信の経路になるため ReadOnly は false(毎回ホストとパスを示す許可カード)。7 日 / 200 MB で古いダウンロードを削除。設定に「Web 取得」行。テスト 5 ファイル(約 60 本)追加・3 ファイル更新(2026-09-17) **段階 2(a)**: `save_to`(+ `overwrite`)で取得したファイルを `Assets/` 配下に書いて同期インポートし、アセットパスと GUID を返す(メインスレッドへのホップ `uap_web_fetch.import_asset`、既存ファイルは `overwrite:true` が無ければ拒否、保存するのは元のバイト列)。設定に「許可するホスト」「拒否するホスト」(1 行 1 ホスト、サブドメイン含む、拒否優先、許可が空なら公開ホストすべて)を追加し、純クラス `UapWebHostRules` のスナップショットを最初の URL とリダイレクトの各ホップで検査。PDF テキスト抽出(PdfPig 同梱)は、作業環境から見える NuGet の版一覧が upstream と一致せず出自を確認できなかったため保留(§12)。テスト 1 ファイル追加・1 ファイル更新(2026-09-17) **段階 2(b)**: `as:"text"` または `pages`(`"3"` / `"1-5"` / `"7-"`)で取得した PDF の本文をページ区切りで返す。PdfPig は NuGet 経由で出自を確認できなかったため同梱せず、依存なしの自作抽出器 `UapPdfText`(全 obj 走査、ObjStm 展開、Flate + 予測子 / LZW / AHx / A85 / RL、ToUnicode CMap、WinAnsi / Standard / MacRoman + Differences、字幅とペン位置から語間・改行を判定)にした。Unity の無い環境のため .NET 8 の dotnet ハーネスで Unity 非依存部分をコンパイル・テスト(152 本)し、実 PDF 3 件で本文が段落単位に出ることを確認。テスト 1 ファイル(17 本)追加・1 ファイル更新。**段階 3**: `uap_web_search`(§8): 設定の「検索プロバイダ」(Brave Search API / Tavily)と「検索 API キー」で動く検索。タイトル・URL・スニペットの番号付きリストを返し、キー無しなら設定場所を伝えるだけ。常に登録(キー連動の登録は再読込まで反映されないため)。PDF の画像化は見送り。テスト 1 ファイル(16 本)追加(2026-09-17) |
| v0.58.0 | 設定画面のリデザイン フェーズ 1〜4 を 1 版に統合(main が v0.57.0 のまま未マージのため、ブランチ内の 3 回の切り出しを再カット)。**フェーズ 1**: 16 枚 1 列のスクロールを 5 タブ(概要 / エージェント / パネル / Unity 連携 / 接続)に分け、最後に見たタブをエディタセッション内で記憶。新設の概要タブはコントロールを持たず、セットアップ 3 行(エージェント検出 / サインイン / Unity 操作)と「いま効いている設定」4 行(+ 権限確認スキップ中の赤行)を「変更」リンク付きで並べ、About カードの中身をフッターに吸収。`AgentPanelWindow.ShowSettings(tab, cardId)` を追加し FirstRun の「ログイン」・概要の各行・UapOps の自動承認クロス参照(文 → リンク)がこれを使う。「危険な設定」は会話カードからエージェントタブ末尾の独立カードへ(ON のときだけ警告色)。300px ではタブを短縮ラベルに。テスト 1 ファイル(8 本)追加・4 ファイル更新。main が先に v0.57.0 を出したためマージして v0.58.0 として切り出し(2026-09-17) **フェーズ 3**: タイトル行右端の検索欄に入力するとタブ列が隠れ、全タブの行のうちラベル・ヒント・ツールチップ・ボタン文言・現在値に部分一致(大小無視)するものだけを「タブ > カード」の見出し付きで表示。折りたたみカード / Foldout 内の一致は開く。クリア(または Esc)で見ていたタブに戻り、行の inline display は検索前の値に正確に復元(`SettingsSearchFilter`)。概要タブは検索対象外。純粋部品 `SettingsSearch` / `SettingsSearchFilter` を新規ファイルに分離。この版から作業環境に Unity 2022.3.62f3 を用意して EditMode テストを実機実行(4352 本、v0.58.0 分も含めて合格)。テスト 1 ファイル(8 本)追加(2026-09-17) **フェーズ 2**: 全カードの見出し右端に 1 語の状態ピル(`SetSectionStatus`: Unity 操作 = ポート N / オフ / 停止中、エージェント = サインイン済み / 未 / 見つかりません、外観 = 検出フォント、モデル = 実際に動くモデル、カスタム指示 = 未設定 / N 行、クイックアクション・コンソールエラー = 件数、拡張プロファイル = 検出数、uLoop・公式プラグイン = 導入状態、危険な設定 = すべてオフ / スキップ中)。本文末尾の状態行 3 本(UapOps・uLoop・外観)を廃止。常時表示ヒント 22 行をツールチップ / ? マークへ移動(モジュール 12 行、UapOps と拡張プロファイルの冒頭説明、通知の注記、Web 取得 / 検索の注記 3 行、ステージングフォルダ、禁止ツールの「1 行に 1 つ」、サブエージェントカードの注記、エージェント選択の「次回再接続で反映」)。テスト 1 ファイル(6 本)追加。EditMode 4358 本合格(2026-09-17) **フェーズ 4**(§11): 会話カードのツール許可・禁止リストと UapOps のモジュール 13 個を件数ピル付きの開閉行(`AddDisclosureFoldout`、`SessionState` 記憶、検索の一致で開く)に畳み、概要のサインイン行を「メール (プラン)」に短縮。**実機確認**(§10): Xvfb 上の Unity で 9 フレームを撮り、タブ文字が見えない(USS `inherit`)・狭幅で補足が消えない(inline display)・狭幅の横スクロール(ピル行)の 3 件を修正、ユーザーガイド 15 章の図を差し替え。テスト 1 ファイル(6 本)追加 **仕上げ**(§12): ヒント 5 行を追加で tooltip へ(常時表示は約 24 行)、概要の「いま効いている設定」先頭に再接続待ちの行(バナーと同じ「今すぐ再接続」) **仕上げ 2**(§13): カードと概要行の骨組みを UXML(`SettingsCard.uxml` / `SettingsOverviewRow.uxml`、`InstantiateTemplate` で `TemplateContainer` を外して使う)に切り出し、インラインの `whiteSpace` 21 か所を `uap-wrap` クラスに、直値 4 つをトークンに、死んだ USS 2 ルールを削除、見出しとピルの文法を各 1 系統に統合。設計とのずれ 8 件を修正(リンク色 + 「変更」のシェブロン、検索欄の虫眼鏡とプレースホルダ、モジュールの二重インデント、オフ状態のアイコン、状態行の斜体、ピルの大きさ、`--ok` の輪郭型、中央寄せの解消)。テスト 1 ファイル(4 本)追加 |
| v0.58.1 | v0.58.0 のタグと公開ミラー(Release zip 含む)がリデザイン後半の 5 コミット(フェーズ 4、ヒント削減 2 巡目、UXML 切り出しと USS 修正、ガイドの図)を含まないツリーで出ていたため、完全なツリーを再公開。再カット後に同じ版のまま積んだので、タグが「版を導入したコミット」に付いた。コード変更なし(2026-09-18) |
| v0.58.2 | Scene ビューのツールバーを「Agent Tools」1 本に統合し、実機撮影で見つけた不具合とエスケープシーケンスの表示崩れを修正。**(1) テキストのエスケープシーケンス**(`docs/design-notes/2026-09-18-text-escape-sequences.md`): 設定 > 概要のエージェント行に出る実行ファイルのパス(`...\Roaming\npm\node_modules\...`)が `\n` の位置で改行され `n` が消える、`\t` がタブに化けて文字が消える件の修正。Unity 2022.3 の C# 生成 `TextElement` は `parseEscapeSequences` が既定 true で `\n` / `\t` を制御文字に書き換えるため、`TextEscapes.Disable` で window root と後から生える部分木(メッセージ、Markdown、コードブロック、ツール / サブエージェント / 許可カード、履歴、チップ、設定の動的行)のすべての `TextElement` を false にした。テスト 1 ファイル(4 本)追加。**(2) Agent Tools ツールバー**(`docs/design-notes/2026-09-18-agent-tools-overlay.md`): ピン(Agent Pin)と平面 / 表面スケッチ(Agent Sketch)が別オーバーレイで 2 本のストリップに分かれていたのを、`SceneAgentToolsOverlay`(id `uap-agent-tools`)の 3 トグルにまとめ、ピンとスケッチのアームを排他にした(同じ左クリックを取り合うため)。テスト 1 本追加。**(3) 実機撮影と操作ガイド §13**: GameCI の 2022.3.62f3 + Xvfb で Agent Tools ツールバーを実機撮影し(`ci/shop-images/agent-tools-capture.sh` / `drive-x11.py` / `UapShotAgentTools.cs`、xdotool の実ポインタでクリック・ドラッグ)、操作ガイド §13 をピン・平面 / 表面スケッチ・チップの 7 枚の画像付きに書き直した(`docs/images/guide/13-agent-tools-*.png`)。撮影で見つけた 2 件を修正: 平面モードの深度読み出しが Tools オーバーレイに隠れる(上端中央へ)、平面トグルのアイコンが矢印カーソル(`d_Grid.Default`)だった(ピン `d_ToolHandlePivot` / 平面 `d_Mesh Icon` / 表面 `d_TerrainInspector.TerrainToolRaise`)。2022.3.22f1 の GameCI イメージは llvmpipe で UI Toolkit を描かないことを記録(設計ノート §4)。実機で採った「エージェントが受け取るもの」(チップの payload / 送信本文 / `uap_marker_list` / `uap_stroke_list`)を `docs/examples/agent-tools-request.md` と §13.6 の使用例に追加(2026-09-20) |
| v0.59.0 | Play Mode 操作(`uap_play_mode`)、Console ログの読み取りとクリア(`uap_console_logs` / `uap_console_clear`)、Game View サイズ(`uap_game_view_size`)の4 ツールを追加。uLoop 常駐コストの調査(`docs/design-notes/2026-09-21-uloop-always-loaded-cost.md`)で洗い出した「安い 4 本」がこれで揃い、一般的な用途で uLoop を入れる理由がほぼ無くなる(`docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md` / `docs/design-notes/2026-09-21-console-clear-and-game-view-size.md`)。あわせて uLoop 連携カードに「導入すると何が常駐するか」と「弱めるには uLoop 側のどのウィンドウか」を表示(同ノート §5 の案 0)、「エージェントに uloop コマンドを使わせる」トグル(既定オン。オフでステアリング文・起動引数の拒否パターン・`can_use_tool` 拒否の3 層が効く。uLoop 自体は止まらないと明記。同 §5 の案 A)、導入ボタンの対になる「uLoop を削除」(同 §5 の案 B。差分提示とバックアップは導入と同じ作法。依存の削除はUnity、scopedRegistries の後始末はパネルという 2 段構成で、自分が書いた完全一致のスコープしか消さない。`docs/design-notes/2026-09-22-uloop-remove-from-panel.md`)を追加。**設定の自動適用まわりの修正 3 件**(実機報告 2 件を起点に全スイッチを監査。`docs/design-notes/2026-08-01-settings-auto-apply.md` §6-§7): (1) 新トグルを`SettingsChangeDetector.RequiresReconnect` には足したが `AgentHub.CloneNextSpawnOnlyFields`(spawn スナップショット)に足し忘れ、「次回再接続後に適用されます」の保留表示が再接続しても消えなかった件。PanelSettings の全フィールドをリフレクションで走査して「比較対象なのにスナップショットが落とすフィールド」を名指しするテストを追加(`CloneNextSpawnOnlyFieldsTests`)。(2) 1 回目の自動再接続が spawn 中(`Starting`)に2 回目の変更が来ると「未接続」として要求を破棄し、`Ready` での再評価経路も無いため同じ保留表示が固まる件。「一時的に使えない(保持)」と「恒久的に使えない(破棄)」を分離(`AutoApplySettingsPolicy.ShouldArm`)。(3) UapOps の「Web」モジュールのトグルだけが`RequestAutoApplyReconnect()` を呼んでいなかった件(v0.57.0 で 13 個目として足した際のコピペ漏れ。ピルは出るが誰も適用しない)。同じ落とし方が再びできないよう、SettingsView の設定変更ハンドラ(~100 個)を 1 つの出口 `CommitSettingsChange()` に集約し、13 個のモジュールトグルを共通の `ToggleUapOpsModule(moduleId, enabled)` に畳んだ(再接続要否の判断は元々 `SettingsChangeDetector` が持っており、各ハンドラの再導出は冗長だった)。監査テストは「チョークポイントの排他性」を検査する形に作り直し(`SettingsHandlerAuditTests`)。ユーザーから見た挙動は不変(2026-09-22) |
| v0.60.0 | 設定 > 概要のフッターに **Claude Code CLI の更新確認と更新**を追加: 「CLI の更新を確認」で npm レジストリの dist-tags(`latest`)とインストール済みのバージョンを比較し、新しい版があれば「CLI を更新」で解決済みバイナリ自身の `claude update` を実行(確認ダイアログでコマンドを提示。通信はボタン押下時のみ)。更新後は設定の自動適用と同じ経路で同じセッションのまま自動再接続(待機中は即時・応答中は完了後)し、案内の横に「今すぐ再接続」ボタンも置く。再開した接続が `system/init` を出すまで旧バージョンが表記に残る問題と、再接続案内が消えない問題を beta.2 で修正。あわせて Agent Panel Pro がある場合は **Pro のバージョンピル**をパッケージバージョンの横に表示(`docs/design-notes/2026-09-23-cli-update-and-pro-version.md`)(2026-09-23) |
| v0.60.1 | 修正のみの安定版(v0.60.1-beta.1〜6): バックグラウンド起動のサブエージェントをカードで追跡(終端 task イベントで閉じ、入れ子カード・進捗・サマリを受け取る)。`/mcp` をパネル側で処理しカード化(サーバーごとの状態・ツール一覧・再接続)、設定に「MCP サーバー」カード(`--mcp-config` 同梱、Claude Code の設定から取り込み、「ターミナルを開く」)。`uap_property_set` が JSON 文字列の構造体値(Color / Vector / Rect / LayerMask)と位置指定の数値配列を受け付ける。長い思考ブロックが 65535 頂点の上限で描かれない件をチャンク分割で解消。パネル内の Claude ログインで「認証コード」欄が有効にならない件(Mono の StreamReader がバッファを埋めるまで返らなかった)を `AuthLoginSession.PumpChunks` で修正。ACP の「サインイン方式」をエージェントごとに保存(旧共通値は 1 回だけ移行)、インストール完了行の残留とサインイン通知のエージェント名を修正。あわせて操作ガイドを `docs/guide/` に分割し、エージェント別のサインイン手順を実機スクリーンショット付きで追加(2026-09-30) |
