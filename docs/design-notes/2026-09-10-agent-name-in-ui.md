# UI 文言のエージェント名をバックエンドに追従させる

日付: 2026-09-10 / 対象: v0.39.1 / 関連: 2026-09-10-acp-backends.md

## 1. 現象

設定でエージェントを Gemini / Codex / Grok に切り替えても、会話欄の
ロール表示「Claude」、空状態の「Claude に何でも聞いてください」、入力欄の
「Claude に聞く...」、コンテキストバーの「Claude に修正を依頼」、権限
カード/ウィンドウの「Claude が {0} を使用しようとしています」、再接続
ボタンのツールチップ、設定のヒント(権限モード / 危険ゾーン / カスタム指示 /
Unity 操作ツール / 拡張プロファイル)がすべて Claude のままだった。

## 2. 設計

- 文字列カタログ(`UiStrings` / `UiStringsJa`)の該当フィールドで固有名詞
  「Claude」を `{agent}` プレースホルダに置き換える(両カタログ同時)。
  数値プレースホルダ `{0}` とは別物なので、L10nTests のプレースホルダ集合
  検査には影響しない。
- `L10n.AgentName`(描画時に毎回解決。キャッシュしない):
  1. `AgentBackends.ShortName(現在のバックエンド)` = Claude / Gemini /
     Codex / Grok。
  2. カスタム ACP エージェントは `AgentHub.CurrentAcpAgentName`
     (system/init の version 文字列 "name version" の name。数字始まりは
     バージョンなので不採用。プロセス起動時にクリア)。
  3. どちらも無ければ `AgentGenericName`("Agent" / 「エージェント」)。
- `L10n.F` は string.Format の前に `{agent}` を展開する。書式化しない
  文字列は呼び出し側で `L10n.A(L10n.S.X)` を通す。
- 構築時に一度だけ文字列を入れる要素(ヘッダの再接続ツールチップ、空状態の
  サブタイトル、コンテキストバーの修正ボタン)は各 Refresh / SetVisible で
  再評価し、バックエンド切替が次の再描画で反映されるようにした。入力欄の
  プレースホルダとメッセージのロール行は元々描画ごとに生成される。
- 過去メッセージのロール行は生成時の名前のまま(Claude の返答は Claude と
  表示され続ける。履歴としては正しい)。
- Claude Code 専用機能の文言(Unity 公式プラグイン、ログアウト、サブ
  エージェントのモデル方針、スクリプトゲート、初回ログインカード)は
  そのまま。ACP では表示されない/無効化されるセクション。

## 3. アイコンとアクセント色

- ヘッダ・空状態・返答のロール行に出るスパーク記号「✱」は Claude のロゴ
  由来なので、バックエンドごとの記号に切り替える(`IconLoader.AgentGlyph`):
  Claude ✱(U+2731)/ Gemini ◆(U+25C6)/ Codex ◯(U+25EF)/ Grok ✕(U+2715、
  既存の白抜き×。xAI の X)/ カスタム ◇(U+25C7)。
- 新規の 3 文字は Inter 3.19 の `Inter-Regular.otf` の cmap に収録されて
  いることを確認した(2026-09-10、fontTools 無しで cmap を直接読解)。
  既存の ✱ や ⚙ は Inter 自体には無くエディタのフォールバックで描画されて
  いるので、新規分の方が安全側。`SafeGlyphCodepoints` に追加し、
  GlyphAuditTests の走査対象にする。
- アクセント色 `--uap-accent-agent` はルート要素の `uap-agent--<backend>`
  クラスで上書き(ThemeDark/ThemeLight.uss の末尾)。`IconLoader.
  ApplyAgentAccent` が ApplyThemeAndStyles と hub 更新ごとに 1 つだけ付け替える。
  Claude は従来色、Gemini 青、Codex 緑、Grok 灰、カスタム 紫。
- ヘッダと空状態の記号は Refresh / SetVisible で再評価、ロール行は
  メッセージ生成時の記号のまま(過去の返答は当時のエージェントの記号)。

## 4. 検証

- `AgentBackendsTests`: ShortName の対応表、`ResolveAgentName` の優先順位、
  `ExtractAgentProductName`、`{agent}` が `L10n.A` / `L10n.F` の両方で
  展開されること(`AgentNameOverride` で固定)。
- 既存の PermissionCardTests / ToolCardDescriberTests は期待値側も
  `L10n.A` を通すよう更新。
