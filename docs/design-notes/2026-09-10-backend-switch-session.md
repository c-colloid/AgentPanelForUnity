# 会話中のエージェント切替と再接続ループ

日付: 2026-09-10 / 対象: v0.39.1 / 関連: 2026-09-10-acp-backends.md、2026-09-10-in-panel-install-and-sign-in.md 2.4

## 1. 現象

会話中に設定でエージェントを切り替えると、「エージェントのプロセスが
終了しました(...)。再接続しています...」が延々と続くことがある。

## 2. 原因

- 切替時の再接続(auto-apply / Reconnect)は `Session.sessionId` をそのまま
  `--resume` / `session/load` に渡す。この id は「前のエージェント」が
  発行したもので、新しいエージェントには意味が無い。
  - ACP ブリッジは `session/load` 失敗時に `session/new` へ倒すので無害。
  - Claude Code は未知の `--resume` id で終了する。OnProcessDied は同じ
    resume id で再起動するため、同じ失敗を繰り返す。
- クラッシュカウンタ(`_consecutiveDeaths`、上限 3)は Starting→Ready で
  0 に戻る。init 直後に終了するパターンでは毎回 Ready を経由するため
  カウンタが効かず、無限になる。

## 3. 修正

1. **セッションの所有者を記録する**: `ChatSession.agentBackend`(int、-1 =
   不明)。`OnSessionIdChanged` で現在のバックエンドを書き込み、
   SessionCache にも保存/復元する。`StartClient` は resume id の所有者が
   起動するバックエンドと違えば resume を捨てて新規セッションで起動し、
   会話欄にノート(「ここまでの会話は X とのものです。Y は新しいセッションを
   開始します」)を出す。会話ログと古い id はそのまま残す(戻せば再開できる)。
   -1 は従来どおり通す(旧キャッシュは Claude のもの)。
2. **カウンタのリセット条件**: 直近の死亡から 60 秒以内の Ready では
   リセットしない(`ShouldResetCrashCounterOnReady`)。ターン完了
   (エラーでない result)で必ずリセットする。これで「Ready 直後に死ぬ」
   パターンも 3 回で「自動再接続を停止しました」に到達する。

## 4. 会話の引き継ぎ

新しいセッションになっても会話が途切れないよう、切替後の最初の送信で
`ConversationHandover.Build`(Editor/Model、Unity 非依存)が会話ログを
テキスト化し、ワイヤ上のメッセージ先頭に付ける(吹き出しにはユーザーの
入力だけを表示)。

- 対象: user / assistant のテキスト、コンテキストチップは `[attached: 名前]`、
  ツール呼び出しは `[tool 名前: 要約]` の 1 行。システムノート・thinking・
  権限カードは除外。
- 上限 24,000 文字。超える場合は古い方から落とし `(earlier messages omitted)`
  を付ける。
- ヘッダで「ユーザーは {前} と話していて {新} に切り替えた。繰り返したり
  要約し直したりせず続けること」と指示する。
- 保留状態は `SessionStateBridge.HandoverPendingFrom`(ドメインリロードを
  またぐ)。所有バックエンドの再開・「新しいチャット」で解除。

## 5. 検証

- `AgentBackendsTests`: `ResumeAllowedForBackend` の組み合わせ、
  `ShouldResetCrashCounterOnReady` の猶予、SessionCache の往復と既定値 -1。
- 実機での切替は未検証(Claude Code の未知 `--resume` id の挙動は
  バージョン差があり得るが、どちらでも修正 1 で resume しなくなる)。
