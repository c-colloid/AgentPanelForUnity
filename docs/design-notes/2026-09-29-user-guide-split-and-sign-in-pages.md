# 操作ガイドのページ分割と、エージェント別サインイン手順(と、その撮影で見つけたログイン不具合)

日付: 2026-09-29 / 対象: v0.60.1-beta.6

## 1. 依頼

> ほぼすべての説明が 1 つのドキュメントページに集約していて縦に長くなってしまっているので、
> 読んでいて大変に感じてしまう。ユーザーが必要な情報にすぐに飛べて、必要十分な情報が得られる
> ようにページを分けて管理したほうがいい。
> また、ドキュメントに各エージェントのログイン手順の説明が欲しい。仮想環境に Unity を用意し、
> スクリーンショットとともに解説を作って。

## 2. ページ構成

`docs/USER-GUIDE.md`(17 章・約 440 行)を、テーマごとのページに分けて `docs/guide/` に置いた。

| ページ | 旧章 |
|---|---|
| `guide/getting-started.md` | 1(Claude 固有の手順はエージェント別ページへ) |
| `guide/agents/README.md` | 1.1 |
| `guide/agents/{claude-code,codex,grok-build,gemini-cli,custom-acp}.md` | 新規 |
| `guide/chat.md` | 2, 3, 8, 11 |
| `guide/context.md` | 4 |
| `guide/permissions.md` | 5, 6, 7 |
| `guide/sessions-and-models.md` | 9, 10 |
| `guide/unity-ops.md` | 12 |
| `guide/scene-tools.md` | 13 |
| `guide/reload-and-play-mode.md` | 14 |
| `guide/settings.md` | 15 |
| `guide/shortcuts.md` | 16 |
| `guide/troubleshooting.md` | 17 |

- `USER-GUIDE.md` はパスを変えずに**目次**にした。README・公開ミラー・外部からのリンクは
  すべてこのパスを指しているため。「目的から探す」表(やりたいこと → ページ)と、旧章番号の
  移動先の表を置いた。旧アンカー(`#11-claude-以外のエージェントを使う` など)は失われるので、
  リポジトリ内のリンクは新しいページに張り替えた。
- 章の分け方は「一度に読む単位」を基準にした。短い章(2, 3, 8, 11 はどれも画面上の同じ場所の
  話)はまとめ、長い章(12, 13, 15)は 1 ページにした。各ページの先頭にパンくず、末尾に
  前後のページへのリンクを置いた。
- 本文は旧ガイドから移し、次だけ直した: 章番号での相互参照をページへのリンクに、設定の場所を
  2026-09-17 の設定画面の再設計後のタブ構成(「設定 > 会話」→「設定 > エージェント > 会話」
  など)に合わせた。
- 公開ミラーの allowlist に `docs/guide/` を足した。内容は旧 `USER-GUIDE.md` と同じで、
  公開範囲も変わらない。

## 3. サインイン手順の撮影

### 環境

`ci/shop-images/login-capture.sh`(新規)。GameCI の `unityci/editor:ubuntu-2022.3.62f3-base-3`
を Xvfb で GUI 起動し、Core のみのホストプロジェクト(`ci/HostProjectCoreOnly`)を開く。
既存のキット(2026-09-18 の Agent Tools など)と同じ理由で 2022.3.22f1 ではなく 62f3
(22f1 は llvmpipe で UI Toolkit を描かない)。Core のみのホストにしたのは、公開版の利用者が
入れる構成と画面を揃えるため。

ドライバ `ci/HostProject/Assets/Editor/UapShotLogin.cs` は、既存の「決め打ちの手順を流す」
ドライバと違い**コマンド駆動**にした。本物の CLI の応答速度はまちまちで、インストールの完了や
サインイン URL の表示を見てから次へ進む必要があるため。ホスト側が `$UAP_SHOT_DIR/cmd` に
1 行ずつ動詞(`backend N` / `settings TAB CARD` / `invoke METHOD` / `text FIELD VALUE` /
`login` ...)を書き、ドライバがメインスレッドで実行して `out` に結果を返す。画面は `shot` で
X ディスプレイから切り抜く。

### 本物の CLI、ホストのアカウントは使わない

- 各 CLI は**コンテナの中に、パネル自身の「インストール」ボタンで**入れた(Claude Code 2.1.284、
  codex-acp 2.0.0 + Codex 0.159.0、Grok Build 1.0.44、Gemini CLI 0.61.0、Qwen Code 0.24.6)。
  インストール確認ダイアログとインストール中の表示もそのまま撮れる。
- コンテナにはホストのホームを一切渡していないので、どの CLI も「未サインイン」から始まる。
  アカウントでのサインイン完了までは行っていない(撮影用アカウントを持たせないため)。
  「サインイン済み」の画面が要る Claude Code だけ、既存の疑似 CLI(`fake-claude.py`、
  `you@example.com`)で撮り、ページにその旨を書いた。
- コンテナにはブラウザが無い。`xdg-open` が無いと Codex などは「spawn xdg-open ENOENT」で
  サインインに失敗するので、URL を記録して成功を返すだけのダミーを置いて「ブラウザが開いた」
  状態を再現した(この失敗自体は Codex のページの「うまくいかないとき」に Linux の症状として
  書いた)。
- 画面に写るサインイン URL の `state` / Grok のデバイスコードは撮影後にキャンセルしたもので、
  再利用できない。

### 撮ったもの(`docs/images/guide/agents/`)

| エージェント | 画面 |
|---|---|
| Claude Code | 未導入のカード、インストール確認、インストール中、導入直後、サインインカード、URL と認証コード欄、サインイン済み(疑似 CLI)、準備完了 |
| Codex | 未導入(チャット / 設定)、インストール確認、インストール中、未サインイン、`codex login` の URL |
| Grok Build | 未導入、インストール確認、インストール中、ブラウザ待ち、未サインイン、`grok login` のデバイス認証 URL |
| Gemini CLI | 未導入、インストール確認、導入直後(Google ログイン待ち)、サインイン方式 `gemini-api-key`、キーが無いときの会話欄 |
| その他の ACP | 空のカード、コマンドと引数を入れた状態、解決結果 |

設定画面の切り抜きは、下の空白を自動で詰めた(背景色と違う最後の行 + 14px)。

## 4. 撮影で見つけた不具合: Claude のパネル内ログインでコード欄が有効にならない

### 現象

「ログイン」を押すとサインイン URL までは出るが、「認証コード」欄と「送信」が無効のまま、
案内も「サインインを開始しています... まもなくブラウザが開きます。」から進まない。
Claude Code 2.1.284 の認可 URL の `redirect_uri` は `platform.claude.com/oauth/code/callback`
で、ブラウザは認証コードを表示するだけなので、コードを貼れないとパネル内ではログインを
完了できない(ターミナルでの `/login` は動く)。

### 原因

`AuthLoginSession.ReadLoop` は `StreamReader.Read(char[512])` で stdout を読んでいた。
Unity の Mono の `StreamReader.Read` は、下のストリームが少ししか返さなくても**バッファが
埋まるまで読み続ける**(.NET Framework 参照実装にある「ブロックしたら返す」判定が無い)。
CLI の出力は

```
Opening browser to sign in…
If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?...(約 470 字)
Paste code here if prompted > 
```

の計 559 字で、最後の行は改行されないまま CLI が stdin を待つ。最初の 512 字を超えた残りは
StreamReader の中に溜まったまま呼び出し元に返らず、`EndsWithCodePrompt` が一度も
成り立たなかった(撮影中にバッファを覗くと 540 字、末尾が `\nPaste code ` で止まっていた)。
URL が短かった以前の CLI では全体が最初の 512 字に収まっていたので表に出なかった。

同じコンテナで Python の `subprocess`(パイプ、`os.read`)から同じコマンドを起動すると、
0.6 秒で末尾のプロンプトまで届く。CLI 側の変化ではなく読み方の問題と確認した。

### 修正

`AuthLoginSession.PumpChunks`(新規、internal static): `Stream.Read(byte[])` が返した分を
そのたびに `Decoder`(UTF-8、状態付き)で文字にして渡す。`Stream.Read` はパイプに届いている
分だけで返るので、短い最後のチャンクも待たずに届く。複数バイト文字(出力先頭の `…`)が
読み取りの境目で割れても、`Decoder` が残りのバイトを持ち越す。ACP エージェントのログイン
コマンド(`codex login` / `grok login`)の出力も同じクラスで読んでいるので、同じ修正が効く。

修正後、同じ環境で `waiting=True`(559 字、末尾 `Paste code here if prompted > `)になり、
「ブラウザに表示された認証コードを下に貼り付けてください。」と入力欄が有効になった
(`claude-07-login-url.png` はこの状態)。

### テスト

`AuthCliTests`:

- `PumpChunks_DeliversEachReadBeforeReadingAgain`: 1 回の Read ごとに 1 チャンクを返す
  ストリームで、次の Read が呼ばれる前に前のチャンクが呼び出し元に渡っていること
  (本番では次の Read は子プロセスが終わるまでブロックする)。
- `PumpChunks_MultiByteCharacterSplitAcrossReads_DecodesWhole`: `…` の 3 バイトが 2 回の
  Read に割れても 1 文字として復元されること。

GameCI 2022.3.62f3 の EditMode で `AuthCliTests` 30 件中 29 件成功、1 件は既定でスキップの
`UAP_LIVE_CLI` 付きテスト。

## 5. 別タスクに回したもの

撮影中に気づいたが、この依頼の範囲外なので別タスクにした(3 件とも同日に修正:
`2026-09-29-per-agent-auth-method-and-ui-fixes.md`)。

- 「サインイン方式」(`PanelSettings.acpAuthMethod`)が ACP エージェント共通で、Gemini 用の
  `gemini-api-key` がカスタム ACP に切り替えても残る。ガイドには「切り替えたら空欄に戻す」と
  書いた。
- インストール完了後の「… をインストールしました。接続しています...」が、接続・サインインの
  状態が変わってもドメインリロードまで残る。
- 同じチャット欄で Codex → Grok Build と切り替えると、Codex のサインイン要求ノートが
  「Grok Build へのサインインが必要です(ChatGPT)」と、今のエージェント名で表示された。

## 6. 版

パネル内ログインの修正はパッケージ利用者に見えるので Core のベータ v0.60.1-beta.6
(`[Unreleased]` に `### Added` が無いので patch 系列のまま N + 1)。
