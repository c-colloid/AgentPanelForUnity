# 設定のパスが変な位置で改行され、`\t` の文字が消える(UI Toolkit のエスケープ解釈)

日付: 2026-09-18

## 問題

設定 > 概要 > セットアップの「エージェント」行に出る実行ファイルのパス
`C:\Users\x\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`
が、実機(Windows、Unity 2022.3.22f1)では

```
C:\Users\x\AppData\Roaming
pm
ode_modules\@anthropic-ai\claude-code\bin\claude.exe
```

の 3 行になる。`\npm` と `\node_modules` の `\n` が改行に化け、続く `n` が
消えている。同じ理由で `\t` を含む文字列(`C:\tools\...`、コード中の
`"\t"`)はタブ 1 個に置き換わり文字が消える。

## 根本原因

Unity 2022.3 の `TextElement` には `parseEscapeSequences` があり、true の
とき文字列中の 2 文字 `\n` / `\t` をレイアウト前に改行 / タブへ書き換える
(`UITKTextHandle.ConvertUssToTextGenerationSettings` が
`tgs.parseControlCharacters = m_TextElement.parseEscapeSequences` で
テキストエンジンへ渡す)。既定値が経路で違う:

- C# の `new Label(...)` / `new Button(...)` / `TextField` 内部の
  テキスト要素: バッキングフィールド `m_ParseEscapeSequences = true`
- UXML で置いた要素: 属性 `parse-escape-sequences` の既定 false

このパネルの文字列(パス、ツールの入出力、モデルの本文、翻訳)はどれも
バックスラッシュを字面どおりに意味するので、どこでも true であってはなら
ない。v0.58.0 §13 で概要行を UXML に切り出したため当該行だけは偶然直った
が、C# で作る数百のラベル(ツールカードの入出力、コードブロック、履歴、
設定の他の行、実行ファイル欄の `TextField` 自体)は今も解釈している。

## 対策

`Editor/UI/TextEscapes.cs`(`TextEscapes.Disable(VisualElement root)`)を
追加。root 自身と配下のすべての `TextElement` に
`parseEscapeSequences = false` を書く(`Query<TextElement>` は `TextField`
の内側も拾う)。呼ぶ場所は「部分木を作り終えた / 育てた」箇所:

- `AgentPanelWindow.CreateGUI`(3 ビューを組み終えた直後の window root)、
  `PermissionWindow.CreateGUI`、`SettingsView.CreateGUI`(テストで単独
  ビルドされても効くように)
- 後から生える部分木: `MessageBlockFactory.CreateMessageElement`、
  `MarkdownRenderer.Render` / `CreateRichLabel`、`CodeBlockElement`、
  `ToolActivityCard`(コンストラクタ / `BuildDetails` / `RenderDiffLines`
  / `CreateGroupRow`)、`SubagentCard`(コンストラクタ / `PopulateDetails`)、
  `PermissionCard`(variant 構築後 / `RenderDiffLines`)、`HistoryView`
  (`RenderRows` / `AppendRowEditors`)、`ContextBarView.AddChip`、
  `ComposerView`(スラッシュ候補 / 添付列)、`StatusBarView.BuildModelRow`、
  `EmptyStateView.AddChip`、`SettingsSearch.EnsureCrumb`、設定の動的行
  (クイックアクション / 無視エラー / モデル上書き / 拡張プロファイル /
  uLoop の注意書き)

テキスト変更のたびに走査するのではなく、部分木が完成した時点で 1 回だけ
走査する(数百要素の `Query` は安価、変更ごとでは無駄)。

## 併せて確認したこと

同じスクリーンショットではタブ帯の文字(概要 / エージェント / ...)も
見えていなかったが、これは v0.58.0 §10 で直した USS `color: inherit`
(UI Toolkit に無い値)の件で、main の v0.58.0 には修正済み。撮影された
ビルドには検索欄の虫眼鏡とプレースホルダ(§13)も無いので、§10 より前の
ブランチ途中の版が入っていたと判断。パッケージを main から入れ直せば直る。

## テスト

`Tests/Editor/TextEscapesTests.cs`(4 本): 走査が root / 子 / `TextField`
内部に効き本文を変えないこと、null 安全、`MarkdownRenderer.Render` の木と
`AgentPanelWindow.CreateGUI` の木に解釈中の `TextElement` が 1 つも残らな
いこと。

## 実機確認(2026-09-18、Unity 2022.3.22f1 Linux / Xvfb)

ユーザーの環境と同じ 2022.3.22f1 を作業コンテナに展開し(CI と同じ Licensing
Client でパーソナルシートを認証)、`ci/HostProject` で確認した。

- **EditMode**: 4373 本、失敗 0、スキップ 13(LiveCli)。`TextEscapesTests` 4 本を含む。
- **再現と修正の撮影**: `ci/HostProject/Assets/Editor/UapShotEscapes.cs`(新規の撮影
  ドライバ)が、Linux 上で `\n` / `\t` を字面に含むファイル名
  (`/tmp/uap-cli/C:\Users\chen\AppData\Roaming\npm\node_modules\...\claude.exe`、
  `fake-claude.py` のラッパー)を実行ファイルに指定して概要 / 接続 / パネルの
  各タブを撮る。
  - 修正前(main = v0.58.0): 接続タブの「解決結果」行が
    「...\Roaming」「pm」「ode_modules\...」の 3 行に割れ、ユーザー報告と同じ症状を
    Linux でも再現(`docs/images/verify-2026-09-18-escape-before.png`)。概要タブの
    エージェント行は §13 で UXML 化されたため main でも正常。
  - 修正後(v0.58.1): 同じ行がバックスラッシュ込みの 1 本のパスとして折り返す
    (`docs/images/verify-2026-09-18-escape-after.png`)。
- **UITK Font Fix の関与**: 検証ホストに UITKFontFix 0.4.1 を `file:` 依存で埋め込み
  (コミットには含めない)、`FontFixBridge.IsAvailable = true`、CJK フォントが
  「Noto Sans CJK JP - Regular [UITK Font Fix]」で解決されている状態でも、main では
  同じ割れ方(`docs/images/verify-2026-09-18-escape-before-fontfix.png`)、修正版では
  正常。FontFix のコードも `TextElement` に対しては `MarkDirtyRepaint` しか呼ばない
  (`FontFix.RepaintElementsUsing`)。**FontFix は原因ではない。**
- **タブ文字**: 4 回の撮影すべて(FontFix あり / なし × main / 修正版)でタブ帯の
  5 語は見えており、main(v0.58.0)の時点で直っている。報告のスクリーンショットは
  §10 の修正より前のブランチ途中の版(検索欄の虫眼鏡とプレースホルダも無い)。

## 補足: ユーザー環境の Console エラーとタブ文字(2026-09-18)

ユーザー側のエージェントは、手元のパッケージ(`AgentPanel.uss:3420` に
`color: inherit;` が残る版)で Console の
「Trying to read value of type Color while reading a value of type Keyword」
(`GUIUtility:ProcessEvent`、x12)を同じ宣言に帰着させた。USS は `inherit` を
Keyword トークンとして取り込み、スタイル解決時に `StyleSheet.CheckAccess` が
例外を投げるため、色が読めず文字が透明になる。これは v0.58.0 §10 で削除した
宣言そのもので、main には残っていない(本ノート上の実機確認でも 4 回の撮影
すべてでタブ文字は表示)。再発防止として、ユーザー側と同じ趣旨の
`UssHygieneTests.SourceScan_NoDeclaration_UsesTheCascadeInheritKeyword`
(コメントを除いた USS に `<prop>: inherit;` の宣言が無いこと)をこのリポジトリ
にも追加した。修正前の USS には 1 件一致し、main には 0 件(Unity 2022.3.22f1 で
`UssHygieneTests` 11 本合格)。
