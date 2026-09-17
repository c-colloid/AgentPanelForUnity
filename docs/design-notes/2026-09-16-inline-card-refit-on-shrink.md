# 権限/質問カード: パネルを縮めたときのインラインカードの再判定

日付: 2026-09-16 / 対象: v0.54.4 / 関連: 2026-09-16-askuserquestion-detached-ghost-card.md、
2026-09-12-askuserquestion-prompt-visibility.md、2026-08-01-perm-card-collapsed-crush.md、
2026-08-23-phase2-batch-a-small-robustness.md(UICODE-8)、2026-09-05-ui-redesign.md(2.5 narrow)

## 1. 現象

ユーザー報告: 「ウィンドウを小さくしたときに、モデル変更や質問カードの残像等で
バグが残っているように感じる」。

v0.54.3 で直したのは「カードをウィンドウへ分離したあと」の空カードだった。今回
コードを追い直して見つかったのは、それとは別経路の同じ見た目の欠陥:

1. パネルが十分大きい状態で AskUserQuestion(または展開したツール権限)が届く。
   インライン/ウィンドウの自動判定は「インライン」になり、質問カードは展開状態
   (`.uap-perm--question`、下限 220px、`flex-shrink: 0`)で表示される。
2. そのままパネルの端をドラッグしてインライン最小(高さ 380px / 幅 300px、
   `PermissionCardLayout.ShouldOpenWindow`)より小さくする。
3. 自動判定は **リクエストごとに 1 回だけ**(`s_autoWindowDecidedRequestId`、
   UICODE-8: 閉じたウィンドウを再び開かないための設計)なので再評価されない。
   カードは展開されたまま、下限 220px を保つ。
4. ヘッダー 28 + 会話下限 60 + カード 220+余白 + コンテキストバー 28 + コンポーザー 68
   + ステータスバー 20 は 300px 前後のパネルに収まらない。ルートに外側の
   スクロールバーは無く、`.uap-view-container` がクリップするため、**コンポーザーと
   ステータスバーが画面外へ押し出され**、カードだけが残って見える。回答するまで
   戻らない。

ウィンドウの最小サイズは 300×200 なので、この状態は普通に作れる。
「小さくすると質問カードが居座る」という報告に一致する。

## 2. 根本原因

ChatView の `RefreshPermissionHosting` はリクエスト到着時の 1 回しか
サイズを見ない。到着後のリサイズを見ているのは `OnRootGeometryChanged` だが、
そこでやっているのは 40% の高さ上限(`SetMaxCardHeight`)だけで、下限
(`min-height`)は USS が上限に優先するため、縮小には効かない。

「ここに表示」(`OnShowPermissionInlineRequested`)には既に「小さすぎるなら
折りたたんで戻す」という判断があった。同じ判断がリサイズ経路に無かった。

## 3. 検討した選択肢

1. **縮小時にフローティングウィンドウを自動で開く**: 判定を「1 回だけ」に
   したのは、ユーザーが閉じたウィンドウを勝手に開き直さないため(UICODE-8)。
   ドラッグ中にウィンドウが飛び出すのも乱暴。却下。
2. **縮小時に折りたたむ(採用)**: 「ここに表示」の `tooSmall` と同じ答え。
   折りたたみで `.uap-perm--expanded` / `.uap-perm--question` の下限は
   本体と一緒に外れる(v0.54.3 のゲート)ので、カードは要約行 1 行に戻り、
   コンポーザーが見える。シェブロンで意図的に展開する自由は残る。
3. **下限を小さいパネルでは無効化する USS**: USS にコンテナクエリは無く、
   `.uap-narrow` は幅 340px の 1 段しかない(高さは見ない)。却下。

## 4. 決定と実装

- `PermissionCardLayout.ResolveInlineFit(wasTooSmall, tooSmall, expanded,
  collapsedBySize)`: 純関数。`tooSmall` の **遷移** にだけ反応する。
  - 「小さすぎる」に入った瞬間、本体が展開中なら `Collapse`。
  - 「小さすぎる」から出た瞬間、**この方針が畳んだ**カードがまだ畳まれた
    ままなら `Expand`(一瞬縮めただけでユーザーに手間をかけない)。
  - 遷移が無ければ `None`: 小さいまま、ユーザーがシェブロンで展開し直した
    カードは次のリサイズフレームでも触らない。ユーザー自身が畳んだカード
    (またはツールカードの既定の折りたたみ)は拡大しても勝手に開かない。
- `ChatView.RefitInlineCard(width, height)`: `OnRootGeometryChanged` から
  呼ぶ。実測でない寸法(NaN / 0、`IsKnownGeometry`)は無視(折りたたまれた
  ホストの 0 で「縮んだ/広がった」と誤判定しない)。ウィンドウに表示中
  (`IsShownInWindow`)のカードは待機バーなので触らない。
- `s_sizeCollapsedRequestId`(static): この方針(または「ここに表示」の
  `tooSmall`)が畳んだリクエスト id。`s_autoWindowDecidedRequestId` と同じ
  理由で static(ChatView の再構築を跨いでも「畳んだのは自分」を忘れない)、
  同じ場所(pending 消失)でクリア。
- `PermissionCard.IsExpanded` / `IsShownInWindow`: ホストが読む状態。
  ウィンドウホストの `IsExpanded` は常に true。

## 5. 回帰ガード

- `PermissionHybridHostTests`: `ResolveInlineFit` の全分岐 5 本 +
  static フィールドと呼び出し位置のソーススキャン 1 本。
- `PermissionCardTests`: `IsExpanded` がインラインで `SetExpanded` を映し、
  折りたたみで質問下限クラスが外れること / ウィンドウホストで常に true /
  `IsShownInWindow` がインラインだけで動くこと(3 本)。

## 6. 報告の「モデル変更」について

ヘッダーのモデルチップは幅 340px 未満で `.uap-narrow` により非表示になり、
モデル名を載せた折りたたみチップ(`uap-header-compact-btn`)に置き換わる。
`RefreshModelPicker` は両方のテキストを同時に更新し、ステータスバーのモデル名も
同じ規則で隠れる。コード上は取り残される要素を見つけられなかった。
v0.54.4 の `ScrollReflowThrottle` はドラッグ中に会話の内容幅をピン留めするため、
縮小中は右端がクリップされ、マウスが止まって約 120ms 後に整う。これは設計どおりの
挙動だが、「一瞬古い幅のまま残る」ように見えるのはこれかもしれない。
再現手順(パネルのサイズ、どの操作の直後か、スクリーンショット)があれば
別途追う。

## 7. 教訓

「到着時に 1 回だけ」の判定には、その後に前提が変わる経路(ここではリサイズ)
が必ずある。1 回判定を守るなら、前提が崩れたときの**退避先**(ここでは
折りたたみ)を同じサイズ関数から導く。
