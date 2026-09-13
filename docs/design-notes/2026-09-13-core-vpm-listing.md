# Core を既存の VPM リスティングに載せる + README の導入手順を初学者向けに書き直す

日付: 2026-09-13

## 背景

README の「インストール」は 3 つの問題を指摘された。

1. 方法 1(Git URL)の URL が Markdown リンクになっていて、コピーすべき文字列が
   リンク先(タグ付き)と表示(タグ無し)で食い違い、どちらを貼るのか分かりにくい。
2. Unity Package Manager(UPM)の操作は初学者には難しく、手順を番号付きで
   スクリーンショット付きにしたい。
3. Core(MIT)は colloid が既に運用している統合 VPM リスティング
   [`c-colloid/vpm`](https://github.com/c-colloid/vpm)(`https://c-colloid.github.io/vpm/index.json`)
   に素直に載せられるはず。VRChat 制作者にとっては VCC / ALCOM の「Add」が
   最も簡単な導入経路になる。

## 調査: 既存リスティングの取り込み条件

`c-colloid/vpm` の `scripts/build_listing.py` は `source.json` の
`githubRepos` に列挙したリポジトリの **GitHub Releases** を走査し、
アセットに次の 2 つを持つ Release だけを登録する(該当なしは安全にスキップ)。

- `package.json`(パッケージのマニフェストそのもの)
- `<name>-<version>.zip`(`package.json` の `name` / `version` から決まる
  ファイル名。パッケージルートが zip のルート)

`url` は zip の `browser_download_url` に書き換えられ、`zipSHA256` は
使わない。`mergeListings` 型(既存の listing JSON を取り込む)もあるが、
Core は独自の listing を持たないので Release 走査型を選ぶ。

Core の実体は公開ミラー `c-colloid/AgentPanelForUnity` にあり、
`.github/workflows/mirror-core.yml` がリリースごとに `Release <tag>` の
スカッシュコミットと同名タグを push している。Release はまだ作っていない。

## 採用した設計

### 1. ミラーワークフローが Release を作る

`mirror-core.yml` のタグ push の後段に 2 ステップを足した。

- **Publish a GitHub Release with the VPM zip**: ミラー済みの
  `public/jp.colloid.unity-agent-panel` から `zip -r -X` で
  `jp.colloid.unity-agent-panel-<version>.zip` を作り(パッケージルートが
  zip のルート)、`package.json` と一緒に `gh release create` で公開側に
  Release を作る。リリースノートは CHANGELOG の該当版セクション。
  `package.json` の `version` とタグが食い違えば失敗させる。
  Release が既にあれば足りないアセットだけ `gh release upload` する
  (`workflow_run` と `push` の二重起動に対して冪等)。認証は既存の
  `PUBLIC_MIRROR_TOKEN`(公開側へ push できる PAT なので Contents: write
  を持ち、Release 作成にも足りる)。
- **Notify the VPM listing repository**: NDMFDeform の `release.yml` と同じ
  `repository_dispatch`(`package-released`)を `c-colloid/vpm` に送る。
  `VPM_DISPATCH_TOKEN` が無ければ警告だけで終了(cron が 6 時間以内に拾う)。
  `continue-on-error: true` でミラー自体は失敗しない。

Pro(`jp.colloid.agent-panel-pro`)は別売で、製品キー認証付きの専用
レジストリから配信する。公開リスティングには載せない。

### 2. リスティング側の設定(手動・1 回だけ)

`c-colloid/vpm` の `source.json` に追記する。このセッションは同リポジトリへの
push 権限を持たないので、差分を提示して colloid が入れる。

```json
"githubRepos": [
  "...",
  "c-colloid/AgentPanelForUnity"
],
"packagesMeta": {
  "...": {},
  "jp.colloid.unity-agent-panel": {
    "docs": "https://github.com/c-colloid/AgentPanelForUnity#readme",
    "repo": "https://github.com/c-colloid/AgentPanelForUnity"
  }
}
```

### 3. README の導入手順

日英とも「インストール」を書き直した。

- 冒頭に「方法 / 向いている人 / 以後の更新」の表を置き、VCC 利用者は方法 1、
  それ以外は方法 2 と迷わず選べるようにした。
- **方法 1: VCC / ALCOM**(新設): リポジトリ URL を Add Repository に貼る →
  Manage Project で + を押す、の 3 ステップ。
- **方法 2: Package Manager(Git URL)**: URL をリンクではなくコードブロックに
  し、コピーする文字列を 1 つ(タグ付き)にした。Window > Package Manager →
  左上の + → Add package from git URL(Unity 6 では Install package from git
  URL)→ 貼り付けて Add、の番号付き手順。Git が入っているかの確認方法も添えた。
- **方法 3: zip**: リポジトリ zip ではなく **Release の
  `jp.colloid.unity-agent-panel-<version>.zip`** を指すようにし、
  `Packages/jp.colloid.unity-agent-panel/` を作ってそこに展開する、と
  展開後の形を明示した。
- 旧「方法 2: `Packages` へ配置(junction)」は「開発者向け」に格下げした。

スクリーンショットは同日に実機で撮った(下記「撮影方法」)。

| ファイル | 内容 |
|---|---|
| `docs/images/install/alcom-add-repository.png` | ALCOM の Resources > Repositories > Add Repository に URL を貼った状態 |
| `docs/images/install/alcom-repositories.png` | リポジトリ一覧に「ｺﾛｲﾄﾞのVPMパッケージ」が追加された状態 |
| `docs/images/install/alcom-manage-project.png` | Manage Packages を「Agent」で絞り込み、Agent Panel for Unity 0.45.0 の行と + ボタン |
| `docs/images/install/upm-add-menu.png` | Package Manager 左上の + メニューを開いた状態(Unity 6000.0) |
| `docs/images/install/upm-git-url.png` | Git URL の入力欄に URL を貼った状態 |
| `docs/images/install/upm-in-project.png` | Git から導入後、In Project に Agent Panel for Unity 0.45.0 が並んだ状態 |
| `docs/images/install/upm-window-menu.png` | Window メニューに Agent Panel が追加された状態 |

VCC は Windows 専用なので、VCC 側の画面は同じ VPM を扱う ALCOM(Linux 版)
で代用した。README では VCC / ALCOM それぞれのメニュー位置(VCC: Settings >
Packages > Add Repository、ALCOM: Resources > Repositories > Add Repository)
を併記している。`docs/images/` は公開ミラーの許可リストに含まれているので、
置くだけで公開側の README からも参照できる。

### 撮影方法(再現手順)

CI と同じ GameCI のエディタイメージ(`unityci/editor:ubuntu-6000.0.83f1-base-3`)
を docker で起動し、`Unity.Licensing.Client --activate-all --include-personal`
で Personal シートを取った上で、コンテナ内に `xvfb` / `xdotool` /
`imagemagick` / `libgl1-mesa-dri` を入れて `Xvfb :99 -screen 0 1600x1000x24`
上で **GUI モードの Unity**(`-force-glcore`、llvmpipe の OpenGL 4.5)を
起動した。root 実行の警告は「Continue anyway」を xdotool でクリック。
Package Manager は `Assets/Editor/ShotHelper.cs`(`InitializeOnLoad` で
8 秒後に `UnityEditor.PackageManager.UI.Window.Open("")`)で開き、
`xdotool windowmove/windowsize` で 1100x700 に整え、+ メニュー →
「Install package from git URL...」→ `xdotool type` で URL → Install、と
実際に操作して各状態を `import -window root` で撮り、Pillow で
ウィンドウ範囲を切り出した。Git 取得はコンテナに `--network host` を付けて
セッションのプロキシを通した。

ALCOM は 1.1.8 の AppImage を `--appimage-extract` で展開し、
`libwebkit2gtk-4.0-37` などを入れて `WEBKIT_DISABLE_COMPOSITING_MODE=1`
で同じ Xvfb 上で起動した。GTK のフォルダ選択ダイアログは Xvfb 上で
Open が有効にならなかったので、`settings.json` の `userProjects` に
プロジェクトパスを書いて再起動する方法で既存プロジェクトを登録した。
WebKit のテキスト入力は `xdotool windowfocus`(`--onlyvisible` で可視
ウィンドウを指定)してからでないと届かない。

撮影中に確認できたこと: ALCOM の Add Repository ダイアログには
**Headers** 欄があり、Pro の VPM リポジトリ(Authorization ヘッダー付き)も
手動登録できる。また ALCOM は追加前に listing を取得してパッケージ名を
列挙するので、利用者が URL の正しさをその場で確認できる。

### 4. リリース手順への影響

`CLAUDE.md` / `docs/RELEASING.md` の「README の導入 URL のタグを更新する」
は、リンク href ではなく方法 2 のコードブロックとその下の文を書き換える
ルールに改めた。`docs/RELEASING.md` の公開ミラー節に Release 作成・
dispatch・`VPM_DISPATCH_TOKEN` / `VPM_LISTING_REPO`・`source.json` の
追記を記載した。

### 5. README の縮約とタグ固定の廃止(同日の追補)

スクリーンショットを入れた後、さらに 3 点の指摘を受けた。

- **タグ固定は要らない**: 公開ミラーの `main` は常に最新リリースのスカッシュ
  コミットなので、Git URL にタグを付ける意味が薄く、リリースのたびに README
  の URL を書き換える手間だけが残っていた。README の URL からタグを外し、
  「更新は Package Manager の Update」とした。`CLAUDE.md` の release 手順から
  「README の導入 URL のタグを更新する」を削除し、`docs/RELEASING.md` では
  タグ固定を「固定したいプロジェクトだけ任意で」に改めた。
- **必要要件が古い**: CLI の下限「v2.1.218 以降」はプロトコルを実測した版で
  あって、パネルは最低版を強制していない(現行の CLI は 2.1.270)。版番号を
  README に書くと必ず古くなるので「最新版を推奨、未導入ならパネルから導入」
  とした。Unity の版列挙(6.3 / 6.5 と CI の 4 版)は CI 設定の写しなので
  README からは外し、「2022.3 LTS 以降、Unity 6 系を含む」だけにした。
  認証は「サブスクリプション推奨、API キーも可」に短縮し、他エージェントの
  認証手順は操作ガイド 1 章へのリンクにした。
- **README が長い・要らない情報がある**: 253 行あった README の大半は操作
  ガイド(`docs/USER-GUIDE.md`)と重複していた(使い方・スラッシュコマンド・
  権限カード・履歴・ドメインリロード・トラブルシューティング)。README は
  「何ができるか(9 行)→ 必要要件 → インストール → はじめかた → Core と
  Pro → ドキュメント」の 1 画面分に絞り、細部は操作ガイドへのリンクにした。
  Pro の説明も 1 段落にし、モジュール一覧の箇条書きは省いた。UapOps の
  ツール名(`uap_search` など)や設定画面の文言の引用は README から消した。
  開発者向けの junction 手順は `CONTRIBUTING.md` に既にあるので README から
  外した。日英とも同じ構成にした。

さらに 2 文の指摘: 冒頭の「Claude Code を使うためのパネル」は Codex /
Gemini CLI / Grok Build にも切り替えられるので「コーディングエージェントを
使うためのパネル(Claude Code が既定)」に、「外部パッケージへの依存はない」は
UITK Font Fix を任意で検出して連携するので「必須の依存はない(UITK Font Fix
があれば自動連携)」に改めた。操作ガイドも同じ観点で点検し、冒頭に「他の
エージェントでも同じように動く範囲 / Claude Code 専用の機能(履歴ブラウザ・
パネル内ログイン・サブエージェントのモデル設定・思考ブロック)」を明記、
本文の一般的な箇所の「Claude が…」を「エージェントが…」に統一、Pro の導入
方法が古かった箇所(「`Packages/` 直下に展開」)を設定カード経由に直し、
1 段落に詰め込まれていた画像プレビューと拡張プロファイルの説明を箇条書きに
分けた。機能の網羅性は 0.45.0 時点の CHANGELOG と突き合わせて確認済み
(画像プレビュー 0.43、Pro 更新カード 0.44、VCC ボタン 0.45 は記載あり)。

### 6. 公開側のドキュメントをリリース無しで更新する

`mirror-core.yml` は「Tag release」の完了・タグ push・手動実行でしか動かず、
しかも対象コミットはタグそのものなので、docs だけを直したタスク(規則 4 で
リリースを切らない)は公開ミラー `c-colloid/AgentPanelForUnity` に届かず、
公開側の README は次の Core リリースまで古いままだった。

同じワークフローに **docs モード** を足した。

- 起動: `main` への push で `README.md` / `README.en.md` / `CONTRIBUTING.md` /
  `LICENSE` / `docs/**` / `ci/public-mirror/allowlist.txt` のいずれかが
  変わったとき。手動実行では `docs_only` 入力。
- 動作: 許可リストのうち `jp.colloid.unity-agent-panel/` 以外の項目だけを
  公開側で削除 → コピーし直し(消えたファイルも消える)、`!` 除外を適用して
  「`Docs: sync from <短い sha>`」でコミット・push する。タグ・GitHub
  Release・VPM への dispatch は行わない。
- パッケージディレクトリに触らない理由: `main` は次のリリースに向けた
  パッケージ変更を含みうるので、docs 同期で公開側のパッケージ内容を
  動かすと「公開 main = 最新リリース」が崩れる。docs モードはパッケージを
  直前のリリースのまま残す。
- 既存のリリース経路(全消去 → 許可リストで再構成 → `Release <tag>` +
  タグ + Release + dispatch)はそのまま。`mode` 出力で分岐している。

検証: YAML の読み込みと各 `run` の `bash -n`、docs モードの組み立てを
ローカルで模擬(`public/` にパッケージと古い README を置き、README と
docs/ だけが差し替わり、パッケージが残ることを確認)。

## 却下した案

- **Core の zip を専用レジストリ(Cloudflare Worker)から配る**: Core は
  MIT で公開されており認証が要らない。既存リスティングに載せる方が VCC
  利用者の導線が短く、運用も GitHub Releases だけで済む。
- **README にスクリーンショットを先に貼る**: 撮れないので、壊れた画像
  リンクを出すよりコメントで置き場所を予約する方が良い。
- **`mergeListings` 用に Core 独自の listing JSON を生成する**: Release
  走査型で足りるので、二重管理になる listing は作らない。

## 検証

- `mirror-core.yml` は `actionlint` 相当の構文確認(YAML として読める)と、
  Release ステップのシェルを `bash -n` で確認。
- 実配信の確認は main へのマージ後: `Mirror Core to public repo` を
  `tag=v0.45.0` で手動実行 → 公開側に Release v0.45.0 と 2 アセット →
  `c-colloid/vpm` の `Build listing` 実行 → `index.json` に
  `jp.colloid.unity-agent-panel` の `0.45.0` が現れる → VCC で Add。

## 版

ドキュメントと CI のみの変更でパッケージ利用者に見える変更はないため、
Core のリリースは切らない(CLAUDE.md の規則 4)。
