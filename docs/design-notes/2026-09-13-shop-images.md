# ショップ用スクリーンショット・サムネイルの撮影キット

日付: 2026-09-13

## 背景

販売ページ(BOOTH など)に載せるスクリーンショットとサムネイルが必要になった。
条件は「本物のパネル画面であること」「日本語 UI」「ロゴ・アイコンはブランド
パック(`BRAND.md` の規定)を使うこと」。実 Claude アカウントで会話を撮ると
文言・所要時間・トークン数が毎回変わり、撮り直しも効かないので、決定的に
再現できる方法にした。

## 方法

### 台本付きの疑似 CLI

パネルは `claude` CLI と stream-json(双方向)で話す。CI の
`ci/FakeCli/claude` は「1 ツール呼び出しで止まる」だけの最小実装なので、
販売画像向けに **`ci/shop-images/fake-claude.py`** を書いた。

- `--version` → `2.1.270 (Claude Code)`、`auth status --json` → ログイン済み
  (メールはダミー)を返す。パネルの CLI 探索は `/usr/local/bin/claude` を
  最初に見るのでそこに置く。
- `system/init`(モデル・ツール一覧・`unity-ops` MCP 接続済み)、`initialize`
  control_request への応答(コマンド一覧・モデル一覧・アカウント)、
  `stream_event` によるタイプライター表示、`assistant` の tool_use、
  `can_use_tool` control_request(許可カード)、`user` の tool_result、
  Markdown 本文、`result` を、`$SHOT_SCENARIO` の JSON の台本どおりに流す。
- 台本は「ターン → ステップ」の配列。ステップは `text`(本文)/ `tool`
  (ツール呼び出し。`permission: true` で許可カードを出して応答を待つ)/
  `ask`(AskUserQuestion)。`ask` は `requires_user_interaction: true` を
  付けないとパネルが通常の許可カードとして描く(`PermissionCard.cs` の
  `_request.RequiresUserInteraction` 分岐)。
- メッセージの形は `Tests/Editor/Fixtures/*.jsonl`(実 CLI の記録)に
  合わせた。

台本は 2 本: `scenario-light.json`(Player の子に PointLight を作る 2 ターン。
許可カード → ツールカード 2 つ → Markdown 表 → 追加の依頼)と
`scenario-question.json`(雰囲気を 3 択で聞く AskUserQuestion)。

### 撮影環境

導入スクリーンショットのとき(`2026-09-13-core-vpm-listing.md`)と同じく、
GameCI の `unityci/editor:ubuntu-6000.0.83f1-base-3` を docker で起動し、
Personal シートを取り、Xvfb(1920x1080、llvmpipe)上で GUI の Unity を
`-force-glcore` で動かした。追加で必要だったもの:

- `fonts-noto-cjk` と `locales`(`ja_JP.UTF-8` を生成し `LANG` / `LC_ALL` に
  設定すると、パネルの言語 Auto が日本語になる)。
- `xclip`: 日本語は `xdotool type` では入らないので、クリップボードに入れて
  Ctrl+V で貼る。`docker exec` の中で `xclip -i` を起動すると exec の終了と
  一緒に消えるので、`docker exec -d ... xclip -l 3` で別に生かしておく。
- `ShotHelper.cs`(`Assets/Editor/`): `SHOTS_SCENE=1` で Ground / Player /
  Crate / PointLight のデモシーンを作って Player を選択、`SHOTS_PANEL=1` で
  `AgentPanelWindow` をリフレクションで開く(Inspector 横へのドックは
  `GetWindow<T>(desiredDockNextTo)` でも効かず、フローティングになったので
  `xdotool windowmove/windowsize` で右端 580x1040 に置いた)。
- root 実行の警告、Package Manager の位置合わせなどは `xdotool` のクリックで
  処理。各状態は `import -window root` で撮り、Pillow でパネル領域
  (1340,40)-(1920,1080) を切り出した。

撮った状態: 許可カード待ち / 1 ターン完了(ツールカード + Markdown 表)/
2 ターン目 / ツールカード展開(入力と結果)/ 設定画面 / 質問カード(選択前・
選択後)。

### 合成(`compose.py`)

ブランドパックの SVG(横組・縦組ロックアップ、マーク、暗背景版)を
`rsvg-convert` で PNG にし、`BRAND.md` の色(暗背景 `#161B22`、文字 `#E6EDF3`
/ `#8B9AA8`、青 `#2E86D9`)と Noto Sans CJK JP で次を生成した。

| ファイル | サイズ | 内容 |
|---|---|---|
| `booth-main-dark-1200.png` / `booth-main-1200.png` | 1200x1200 | ロックアップ + キャッチ + 会話部分の切り抜き + 特徴バッジ 4 つ |
| `ogp-1200x630.png` | 1200x630 | ロックアップ + キャッチ + 会話の切り抜き |
| `shot-01-editor.png` / `shot-01b-editor-permission.png` | 1920x1080 | エディタ全体(上部にキャプション帯) |
| `shot-02-permission.png` 〜 `shot-07-question.png` | 1600x1000 | 左にパネルの切り抜き(角丸 + 影)、右に見出しと 3 点の箇条書き |

商標注記(`BRAND.md` 6 章)は画像には入れず、商品ページ本文に載せる。

## 却下した案

- **実 Claude で撮る**: 文言が固定できず、料金も発生する。疑似 CLI なら
  画面の文言まで設計できる。
- **`GetWindow` でのドック**: 効かなかった。フローティング窓を右端に
  重ねても、切り出せば同じ絵になる。
- **英語 UI**: 販売先が国内向けなので日本語で撮った。英語版が要るときは
  `LANG` を外して同じ手順で撮り直せばよい。

## 版

ドキュメントと CI 補助スクリプトのみの追加で、パッケージ利用者に見える変更は
ないため、Core のリリースは切らない(CLAUDE.md の規則 4)。
