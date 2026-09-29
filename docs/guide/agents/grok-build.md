# Grok Build のサインイン

[操作ガイド](../../USER-GUIDE.md) > [エージェントの導入とサインイン](README.md) > Grok Build

xAI の Grok Build を、ACP モード(`grok agent stdio`)で使います。SuperGrok / X Premium+ のアカウントでのサインインを推奨します。

**必要なもの**: SuperGrok または X Premium+ のアカウント。API キーで使う場合は環境変数 `XAI_API_KEY`。Node.js は不要です。

## 1. Grok Build に切り替える

設定(⚙)> 接続 > エージェント の **「エージェント」** で **Grok Build** を選び、上部の **「今すぐ再接続」** を押します。

## 2. CLI を入れる

`grok` が見つからないと、カードに「見つかりません」と出ます。

![設定 > 接続 > エージェント。Grok Build が見つからず、「Grok Build をインストール」と「サインイン」が並ぶ](../../images/guide/agents/grok-02-card-missing.png)

1. **「Grok Build をインストール」** を押します。
2. 確認ダイアログの **「インストール」** を押します。xAI の公式インストーラが実行されます。

   ![確認ダイアログ。curl -fsSL https://x.ai/cli/install.sh | bash](../../images/guide/agents/grok-03-install-confirm.png)

   Windows では PowerShell 版(`irm https://x.ai/cli/install.ps1 | iex`)が表示されます。
3. インストール中はカードに経過秒数が出ます。

   ![「Grok Build をインストール中...」](../../images/guide/agents/grok-04-installing.png)

## 3. サインインする

インストールが終わるとパネルが Grok Build に接続し、未サインインなら Grok 自身のブラウザ手順が始まります。カードは「サインイン中...」になり、「ブラウザでの Grok Build へのサインインを待っています...」と出ます。ブラウザでサインインを済ませれば、パネルが自動で続けます。

![サインイン中のカード。「ブラウザでの Grok Build へのサインインを待っています...」](../../images/guide/agents/grok-05-browser-pending.png)

ブラウザが開かなかったときや、途中でやめたときは「未サインイン」になります。その場合は次の手順でサインインします。

![未サインインのカード。「サインイン」ボタンと、`grok login` を実行する旨の案内](../../images/guide/agents/grok-06-signed-out.png)

1. **「サインイン」** を押します。パネルが `grok login` をパネル内で実行します。
2. カードにサインイン URL(`https://accounts.x.ai/oauth2/device?user_code=...`)が出ます。**「ブラウザで開く」** を押すか、**「コピー」** した URL をブラウザに貼り付けます。

   ![サインイン中のカード。accounts.x.ai のデバイス認証 URL と「Waiting for authorization...」](../../images/guide/agents/grok-07-login-url.png)

3. ブラウザで xAI(X)アカウントにログインし、**ブラウザに表示されたコードが URL の `user_code` と同じか確認してから** 承認します。自分で始めたサインインでないコードは承認しないでください。
4. CLI の出力が「Waiting for authorization...」から進み、`grok login` が終わるとパネルが再接続して「Grok Build に接続済み。」になります。

Grok Build のサインインはデバイス認証なので、ブラウザは別のパソコンやスマートフォンでもかまいません。

## API キーで使う

OS の環境変数 `XAI_API_KEY` を設定し、エディタ(Unity Hub から起動している場合は Unity Hub も)を再起動します。API キーでの利用は従量課金です。パネルはキーを保存しません。

## うまくいかないとき

| 症状 | 対処 |
|---|---|
| 「サインイン中...」のまま進まない | ブラウザで承認したか確認します。やり直すときは「キャンセル」→「サインイン」 |
| 承認したのに接続済みにならない | 「再接続」を押します。それでも未サインインなら、ターミナルで `grok login` を実行してから「再接続」 |
| X 検索や画像生成が使えない | アカウントのプランを確認します(使える機能はプランによって異なります)。使用例は [エージェント別の使用例](../../AGENT-SHOWCASE.md) |

---

[← Codex のサインイン](codex.md) · [操作ガイドの目次](../../USER-GUIDE.md) · [Gemini CLI の設定 →](gemini-cli.md)
