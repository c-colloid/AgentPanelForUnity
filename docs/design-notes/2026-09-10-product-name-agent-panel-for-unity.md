# 製品名を「Agent Panel for Unity」に変更

Date: 2026-09-10
Status: approved(ユーザー決定「製品名は Agent Panel for Unity にしましょう」)
Ships as: v0.39.2

## 1. 背景

有料販売(BOOTH / Gumroad)を視野に入れた法務調査(同日、チャット上で実施)で、
現行名「Unity Agent Panel」には次の 2 つの問題があった。

- **Unity の商標条件**: Unity の Asset Store 提出ガイドラインおよび商標
  ガイドラインは、製品名が「Unity」で始まる・Unity との提携を示唆する名前を
  認めていない(「Tools for Unity software」は可、「Unity Tools」は不可)。
  Asset Store を使わない場合でも商標ガイドライン自体は流通経路を問わない。
- **Anthropic の名称条件**: Claude Code Legal ページは「製品名・機能名に
  Claude Code / Anthropic の名称やロゴを含めない。平文で『Claude Code を
  実行する』と書くのは可」と定めている。現行名は該当しないが、改名時に
  「Claude」を入れない制約として記録する。

「Agent Panel for Unity」は「〜 for Unity」形式で両方の条件を満たす。

## 2. 変更範囲

ユーザーの目に触れる「製品名」だけを変える。識別子は変えない。

| 変える | 変えない(理由) |
|---|---|
| README.md / README.en.md の見出しと冒頭文 | GitHub リポジトリ URL `c-colloid/UnityAgentPanel`(インストール URL と既存リンクを壊さない。リポジトリ改名は所有者の判断で、GitHub のリダイレクトが効く) |
| package.json `displayName`(Package Manager の表示名) | package 名 `jp.colloid.unity-agent-panel`(UPM の識別子。変えると再インストールが必要) |
| 空状態タイトル `EmptyTitle` / `emptyTitle` | 名前空間 `Colloid.AgentPanel` / asmdef 名 |
| ACP `initialize` の `clientInfo.title`、スタンディング指示の見出し | メニュー `Window > Agent Panel`(元から「Unity」を含まない) |
| スクリプトゲートのフックが返す拒否メッセージ | `docs/design-notes/` `docs/research/` の過去ノート(履歴) |
| Documentation~/index.md、パッケージ内 README、docs/ 配下の現行文書見出し | テスト内のパス文字列 `Dev\UnityAgentPanel`(フィクスチャの実パス) |

## 3. 影響

- 機能の変更なし。Package Manager 上の表示名と、エージェントに渡る自己紹介
  文字列(ACP の title、スクリプトゲートの拒否文)だけが変わる。
- ACP の `clientInfo.title` を見て挙動を変えるエージェントは確認した範囲で
  存在しないため、互換性の影響はない。
