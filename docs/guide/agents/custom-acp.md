# その他の ACP エージェント

[操作ガイド](../../USER-GUIDE.md) > [エージェントの導入とサインイン](README.md) > その他の ACP エージェント

プリセットに無い ACP(Agent Client Protocol)対応の CLI(Qwen Code、Kimi CLI、OpenCode など)も、起動コマンドを手で入れれば使えます。CLI の導入とサインインはパネルの外で、各 CLI の手順に従って済ませてください。

## 1. CLI を入れてサインインしておく

ターミナルで CLI をインストールし、その CLI 自身のサインイン(または API キーの設定)を済ませます。パネルはカスタムの CLI のログインコマンドを知らないので、「サインイン」ボタンは出ません。

例: Qwen Code

```sh
npm install -g @qwen-code/qwen-code
qwen          # 初回起動でサインイン方法を選ぶ
```

## 2. 起動コマンドを入れる

1. 設定(⚙)> 接続 > エージェント の **「エージェント」** で **その他の ACP エージェント(カスタムコマンド)** を選びます。
2. **「詳細設定(実行ファイル / コマンド)」** を開き、**「コマンド」** に ACP モードで起動するコマンド、**「引数」** にその引数を入れます。コマンドは PATH 上の名前でもフルパスでもかまいません。

   ![「コマンド」に qwen、「引数」に --acp を入れた状態](../../images/guide/agents/custom-02-command.png)

3. 上部の **「今すぐ再接続」** を押します。コマンドが見つかると「解決結果」にそのパスが出ます。接続できると右上のバッジが「サインイン済み」になり、「接続済み」と表示されます。

   ![「解決結果: /usr/local/bin/opencode」、接続済み](../../images/guide/agents/custom-03-resolved.png)

| CLI | 入れ方 | コマンド | 引数 | 先にターミナルで |
|---|---|---|---|---|
| OpenCode | `npm install -g opencode-ai` | `opencode` | `acp` | 不要(無料モデルで動きます)。有料モデルは `opencode auth login` |
| Qwen Code | `npm install -g @qwen-code/qwen-code` | `qwen` | `--acp` | `qwen` を一度起動してサインイン |
| GitHub Copilot CLI | `npm install -g @github/copilot` | `copilot` | `--acp` | `copilot login` |
| Cline | `npm install -g cline` | `cline` | `--acp` | 接続時にデバイスコードのリンクがチャットに出ます。先に `cline auth` でも可 |
| Auggie(Augment) | `npm install -g @augmentcode/auggie` | `auggie` | `--acp` | `auggie login`(必須。パネルからのサインインは非対応) |
| Mistral Vibe | `uv tool install mistral-vibe` | `vibe-acp` | | `vibe` を一度起動してサインイン(接続時にブラウザも開きます) |
| Goose | 公式インストールスクリプト | `goose` | `acp` | `goose configure` でプロバイダとキーを設定 |
| Kimi Code CLI | 公式インストールスクリプト | `kimi` | `acp` | `kimi login` |
| fast-agent | `uv tool install fast-agent-mcp` | `fast-agent-acp` | | モデルとキーを `fast-agent.yaml` または環境変数で |
| その他 | | 各 CLI の説明書にある ACP モードの起動方法 | | |

### 例: OpenCode(アカウント不要)

OpenCode は無料モデルが同梱されているので、サインインせずにそのまま使えます。「コマンド」に `opencode`、「引数」に `acp` を入れて「今すぐ再接続」を押すと、ヘッダーにモデル名が出て、質問できるようになります。

![「コマンド」に opencode、「引数」に acp を入れた状態](../../images/guide/agents/opencode-01-command.png)

![OpenCode に接続した直後のチャット画面。ヘッダーに opencode/big-pickle](../../images/guide/agents/opencode-02-connected.png)

![OpenCode に Assets フォルダについて聞いたところ。思考、Read ツールのカード、回答が並ぶ](../../images/guide/agents/opencode-03-reply.png)

表の CLI は 2026-10-07 時点の版で実際に接続を確認したものです(確認内容は `docs/design-notes/2026-10-07-acp-other-agents.md`)。版が上がると起動フラグが変わることがあります(Qwen Code は `--experimental-acp` から `--acp` になりました)。

- **権限モード**: ヘッダーの権限モードは、エージェントが同じ意味のモードを持つときだけ切り替わります(Goose: default→approve、acceptEdits→auto。OpenCode: plan のみ)。対応するモードが無いときは「no session mode matching」というエラーが出て、エージェント側の既定のままになります。
- **モデル**: モデル一覧を返すエージェント(OpenCode、Goose など)はヘッダーのモデルピッカーに出ます。

## 3. サインインが必要と言われたら

接続したときにエージェントがサインインを求めると、パネルはエージェントの提示するサインイン方式で認証を始めます(ブラウザが開きます)。方式を指定したいときは、**「サインイン方式」** に ACP の認証メソッド ID を入れて「今すぐ再接続」を押します。どの ID があるかは CLI によって異なります。この欄はエージェントごとに保存されるので、Gemini CLI など他のエージェント用に入れた値はここには出ません。

ターミナルでしかサインインできない CLI(Qwen Code、GitHub Copilot CLI、Kimi Code など)では、パネルは認証を試みず、実行するコマンド(例: `copilot login`)を接続エラーとして表示します。そのコマンドをターミナルで一度実行してから「再接続」を押してください。

![Qwen Code: 「run `qwen --auth-type=openai` once, then press Reconnect」と、再接続を繰り返さなかった旨の注記](../../images/guide/agents/qwen-01-terminal-signin.png)

![GitHub Copilot CLI: 「run `copilot login` once」](../../images/guide/agents/copilot-01-terminal-signin.png)

ACP 経由のサインインに対応していない CLI(Auggie)は、エージェント自身のメッセージ(`auggie login` を実行するよう促す文)がそのまま出ます。

![Auggie: 「Please run `auggie login` from your terminal」を含むエラー](../../images/guide/agents/auggie-01-login-required.png)

エージェントがサインイン用のリンクを出力した場合は、チャットに「このリンクを開いてください」の注記が出ます(Cline のデバイスコードなど)。

![Cline: ブラウザでのサインインを待つ注記と、デバイスコード付きのリンク](../../images/guide/agents/cline-01-device-link.png)

うまくいかないときは、ターミナルでその CLI を普通に起動してサインインを済ませてから、パネルの「再接続」を押してください。

- 接続できない理由は 設定 > 接続 > **CLI の出力(stderr)** に出ます。

---

[← Gemini CLI の設定](gemini-cli.md) · [操作ガイドの目次](../../USER-GUIDE.md) · [画面とチャット →](../chat.md)
