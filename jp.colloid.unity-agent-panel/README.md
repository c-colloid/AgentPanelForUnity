# Agent Panel for Unity (`jp.colloid.unity-agent-panel`)

Claude Code CLI を Unity エディタ内のドッキング可能なチャットパネルとして統合する Editor 専用 UPM パッケージです。CLI を常駐プロセスとして起動し、双方向の `stream-json` プロトコルで通信することで、ストリーミング応答・インライン権限カード・サブエージェント表示・Unity 操作ツール(内蔵 MCP サーバ)・ドメインリロードをまたぐセッション復元などを提供します。外部パッケージへの依存はありません。

- 対応 Unity: 2022.3 LTS 以降(Unity 6 / 6000.x を含む)
- 必要なもの: Claude Code CLI(v2.1.218 以降)と Claude のサブスクリプション。`ANTHROPIC_API_KEY` は設定しないでください
- 開く場所: **Window > Agent Panel**

インストール方法・使い方・トラブルシューティングなどの詳細はリポジトリの README を参照してください:
https://github.com/c-colloid/UnityAgentPanel ([日本語](https://github.com/c-colloid/UnityAgentPanel/blob/main/README.md) / [English](https://github.com/c-colloid/UnityAgentPanel/blob/main/README.en.md))

変更履歴は [CHANGELOG.md](CHANGELOG.md)、ライセンスは [MIT License](LICENSE.md) です。
