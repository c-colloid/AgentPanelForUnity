# 「常に許可」が WebFetch に効かない (2026-08-12)

ユーザー報告: WebFetch を「常に許可」しているのに毎回許可を求められる。

## 1. ユーザー環境の実測

- `DevelopmentProject/.claude/settings.local.json` には常に許可のルールが**存在する**
  (`mcp__unity-ops__uap_editor_screenshot` など)— 仕組み自体は動いた実績がある。
  しかし **WebFetch のルールだけが無い**。
- パネルの `allowedTools`(State.asset)にも WebFetch 系のエントリは**無い**。
- つまり「常に許可」を押した結果が、CLI 側にもパネル側にも残っていない。

付随する重要な訂正: `claude auth status --json` は `loggedIn:false` を返すが、
**パネルのターンは正常に走っている**。「status が false = パネルも死ぬ」という
以前の私の主張は誤り。ヘッドレスの `-p` spawn だけが OAuth リフレッシュに失敗する
(実測: プローブは "OAuth session expired" で死ぬ)。この2つの状態は独立。

## 2. 根本原因(コードから確定)

`TryExtractAddRuleToolName` が `rules[].ruleContent` を**黙って捨てていた**。
WebFetch の suggestion はドメイン限定
(`{"toolName":"WebFetch","ruleContent":"domain:example.com"}`)の形を取る。
この黙殺は同時に2方向へ壊れる:

1. **スコープ拡大**(より深刻): パネル側永続化が動いた場合、書かれるのは素の
   `WebFetch` — ユーザーが承認した「このドメインだけ」より**広い**許可。
   メニューの表示も `Always allow WebFetch` となり、スコープを隠す。
2. **蒸発**: CLI の suggestion の `destination` が `"session"` のとき、ルールは
   CLI プロセスの寿命しか持たない。パネルは**ドメインリロードと設定自動適用のたびに
   CLI を再起動する**ので、ターミナルでは何時間も持つ「常に許可」がパネルでは
   数分で消える。パネル側永続化(allowedTools → 毎 spawn の --allowedTools)が
   これを恒久化する唯一の砦なのに、そこが (1) の形で壊れていた。

## 3. 修正

- `ExtractAddRuleStrings`: ルールを設定ファイルの文法(`Tool` / `Tool(content)`)で
  **全件**取り出す(従来は最初の1件のみ)。
- 永続化はスコープ付きの正確な形を保存(`WebFetch(domain:example.com)`)。
  素の名前をスコープ付きルールから作ることは**二度としない**(専用の回帰テストで固定
  — ここの退行は無言の権限拡大であり、最悪の方向)。
- メニュー表示もスコープ込みの全ルールを表示。

## 4. 検証

- サンドボックス 2055 tests / 0 failed(WebFetch 形の回帰6件を含む)。
- 端から端の確認は、次にユーザーが WebFetch の「常に許可」を押した後の
  State.asset に `WebFetch(domain:...)` が入ることで行う(ヘッドレス CLI は
  認証の件で使えないため)。suggestion の実形状が想定(toolName+ruleContent)と
  違った場合も、未知の形は従来どおり生 JSON ラベル+そのまま返送に落ちるので
  退行はない。
