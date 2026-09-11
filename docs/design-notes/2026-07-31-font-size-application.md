# 設計ノート: フォントサイズ設定の適用方式(なぜ inline `style.fontSize` ではダメか)

- 日付: 2026-07-31 / ステータス: 採用済み
- 関連: ライブフィードバック「本文が薄く/小さく読みづらい」への恒久対応(先送りされていた項目)、CJK フォント機構(`FontLoader.ApplyJapaneseUi` / `AgentPanelWindow.ApplyCjkUiFont`)

## 根本原因

タスク指示は「CJK フォントと同じコンテントルートに inline な font-size を当てる」という素朴な移植を想定していたが、これは **効果がほぼゼロ** になることが USS のカスケード規則から分かった。

- CJK フォント機構が機能するのは、`-unity-font-definition` を **`AgentPanel.uss`/`ThemeDark.uss`/`ThemeLight.uss` のどこも設定していない**ため。ウィンドウ root への 1 箇所のインライン代入が、継承によってそのまま全ての子孫まで届く。
- ところが `font-size` は事情が違う。`AgentPanel.uss` はほぼ全てのテキストクラス(`.uap-text`, `.uap-md-p`, `.uap-msg-role`, `.uap-status-text`, `.uap-tool-summary` …)に **`font-size: var(--uap-font-size-body)` 等を個別に明示指定**している。
- USS(CSS 同様)の継承規則は「そのプロパティが要素自身に明示的な値を持たない場合にのみ祖先から継承する」。ウィンドウ root に `style.fontSize` を直接代入しても、子孫の各要素は自分自身の USS ルールで既に font-size を持っているため、祖先の値を継承しにいかない。**結果、ルートへの inline 代入はどの要素にも実質届かない**。

つまり CJK フォントの「1 箇所で全体に効く」という性質は `-unity-font-definition` が USS 側で誰にも触られていないという**偶然の前提**の上に成り立っており、`font-size` には同じ前提が成立しない。

## 検討した選択肢

| 案 | 内容 | 判定 | 根拠 |
|---|---|---|---|
| A | ルート要素に `style.fontSize` を inline 代入(タスク原文どおり) | 棄却 | 上記の通りほぼ無効。動作確認すれば「何も変わらない」設定項目になる |
| B | `MessageBlockFactory` / `MarkdownRenderer` / `ToolActivityCard` / `PermissionCard` など全リーフ要素に個別に inline font-size を設定 | 棄却 | ファイル所有権外(Editor/UI の大半は他エンジニア所有)。変更範囲が広すぎて「設定 1 つ追加」という見積りに全く見合わない |
| C | ルートクラスで `--uap-font-size-*` **カスタムプロパティ自体を上書き**する(`uap-fontscale-N` クラスを追加/削除) | **採用** | 全てのテキストクラスは値ではなく `var(--uap-font-size-body)` 経由で読んでいる。カスタムプロパティは通常の継承プロパティと違い、「未設定なら親の値を使う」という解決を **var() 参照側で** 行うため、ルート 1 箇所への上書きが既存のテーマ切替(`uap-theme-dark`/`light`)と全く同じ仕組みでカスケードする |

## C 案の実装上の注意(カスケード順の罠)

`uap-fontscale-N` と `uap-theme-dark`/`uap-theme-light` は同じ root 要素に同時に付与される。両方が同じカスタムプロパティ(`--uap-font-size-body` 等)を定義するため、**通常の詳細度・読み込み順のルールでは、後から読み込まれるテーマ側(`ThemeDark.uss`/`ThemeLight.uss` は `AgentPanel.uss` の後にロードされる)が勝ってしまい、フォントサイズ設定が常に無視される**。

対策として `uap-fontscale-N` の各宣言に **`!important`** を付けた(Unity USS がサポートする標準機能)。これによりロード順・詳細度に依存せず確実に上書きされる。将来 `AgentPanel.uss` の読み込み順を変更しても壊れない。

## 決定

- `PanelSettings.fontSizePx`(11〜16、既定 12)を追加。`ClampFontSize` で常にクランプ(スライダー入力・アセット読込みの両方に適用)。
- `AgentPanel.uss` に `uap-fontscale-11`〜`uap-fontscale-16` の 6 クラスを追加。各クラスは `--uap-font-size-body/meta/code/small/title/spark/h1/h2/h3` を `!important` 付きで再定義(`spark-xl` は装飾用途のため対象外)。
- `AgentPanelWindow.ApplyFontScale(root)` が現在の設定値に対応する 1 クラスだけを root に付与(他は除去)。`ApplyThemeAndStyles` から `ApplyCjkUiFont` の直後に呼ばれ、ウィンドウ生成/再生成のたびに再適用される。
- ライブ変更: `SettingsView` のスライダー変更時に `AgentPanelWindow.ReapplyContentRootStyling()` を呼び、開いている `AgentPanelWindow`/`PermissionWindow` 全てのルートへ即座に反映する(次回オープンを待たない)。

## 派生効果

- CJK フォントのライブ反映も同じ `ReapplyContentRootStyling()` に相乗りさせた(タスク要件の「CJK トグルもライブ再適用」を同じ経路で満たす)。
- `!important` を使うのは本パッケージでこの 1 箇所のみ。将来 USS を読む人が驚かないよう、クラス定義のコメントに理由を明記した。

## 未検証事項

`!important` が Unity USS の「プロパティ宣言」全般(通常プロパティ)に効くことは公式ドキュメントに明記された挙動だが、**カスタムプロパティ(`--uap-*` 変数)宣言に対しても同様に働くことは、本ラウンドでは実機の Unity エディタ上で目視確認していない**(この環境から Unity エディタを起動できないため)。理屈上は USS パーサがカスタムプロパティも通常のプロパティ宣言と同じ構文要素として扱うため効くはずだが、次に `uloop compile` 等でエディタを開いた際に、Settings のフォントサイズスライダーを動かして実際に本文サイズが変わることを目視確認し、変わらない場合は D 案(テーマ側スタイルシートの読み込み順を `AgentPanel.uss` が最後に来るよう入れ替える -- 変更は `AgentPanelWindow.LoadStyleSheets` の配列順のみ)にフォールバックすること。
