# 各エージェントの実機スクリーンショット(Claude / Grok / Codex)

日付: 2026-09-17

## 目的

「Claude だけでなく Grok や ChatGPT(Codex)も同じパネルから使える」ことと、
特に **Grok / ChatGPT にしかできない作業(X のリアルタイム検索、画像生成)も
Unity 操作と地続きで頼める**ことを示す宣伝用スクリーンショット。

疑似 CLI の台本(`2026-09-13-shop-images.md`)ではなく **本物のエージェント**で
撮った。理由は 2 つ: 各社固有の機能を台本で演出すると未検証の機能を謳うことに
なる、そして Codex / Grok との実接続は設計ノート上「実機未検証」のままだった
(`2026-09-10-acp-backends.md` §5、`2026-09-13-acp-feature-parity.md`)ので、
撮影がそのまま初の実機検証になる。

## 撮ったもの(`docs/images/agents/`)

| ファイル | エージェント | 依頼 | 見どころ |
|---|---|---|---|
| `claude-common-task.png` | Claude Code (opus-5) | Player の子に暖色 PointLight、Crate を 1.5 倍、変更点を表に | uap ツール連鎖と Markdown 表 |
| `codex-common-task.png` | Codex (gpt-5.6-terra, ChatGPT ログイン) | Crate を 3 個に増やして円形配置、赤緑青のマテリアル | `GameObject/Duplicate` が無い環境で自力で代替(Cube 新規作成)し完了 |
| `grok-x-search-tools.png` / `grok-x-search.png` | Grok Build (grok-4.6) | X で話題の VRChat ワールドのライティング傾向を調べ、要点 3 つ → ライト調整 | X 検索ツール 7 件 → 実在ワールド名を引いた要約 → Directional/Point ライト変更 → マーカー配置 → 表 |
| `grok-imagegen-texture.png` | Grok Build | 石畳テクスチャを生成して `Assets/Textures/cobblestone.png` に保存、Ground に適用 | 生成画像のサムネイルがツールカードに並び、シーンビューに石畳が敷かれる |
| `codex-imagegen-free-plan.png` | Codex | 同上 | **free プランでは `image_gen` が無い**と正直に報告し、API キーのフォールバックを提案(採用せず) |
| `codex-imagegen-texture.png` | Codex (gpt-6-astra, Plus) | 木箱テクスチャを生成して 3 つの Crate に適用 | Plus 化後は組み込み `image_gen` が使え、木箱 3 つに木目テクスチャ |

各 `*-panel.png` はパネル領域(680x1560)だけの切り抜き。

**依頼文つきの版**: 結果の画面はパネルが最下部までスクロールされていて依頼文
(「あなた」の吹き出し)が写らないので、保存済みセッションを復元して最上部も撮り
(Claude は実行中に撮った最上部)、次の 2 種類を合成した。
`*-with-request.png` = 「① 依頼」のパネル切り抜き + 「② 結果」のエディタ全体、
`*-panels.png` = 依頼と結果のパネル切り抜きを横並び。背景と文字色は `BRAND.md` の
暗背景 `#161B22` / `#E6EDF3`。

**合成について**: `codex-common-task.png` と `grok-imagegen-texture.png` は、
最初の撮影に「修正を依頼」チップ(原因と修正プランは
`2026-09-17-tool-caused-console-errors.md`)が写り込んだため、Console を
クリアして保存済みセッションを復元し、パネル領域とステータスバーだけを撮り直した
ものを元画像に貼っている(シーンビューは実行当時のまま)。会話内容は同じ
セッションの復元表示。`codex-common-task.png` のヘッダーのモデル名は復元時点の
`gpt-6-astra` になっている(実行時は `gpt-5.6-terra`)。

## 撮影方法(Windows 版キット `ci/shop-images/windows/`)

Linux 版(docker + Xvfb + xdotool)は使えないので、手元の Windows で
AITemp サンドボックス(2022.3.22f1)を GUI 起動して撮った。

- `ShotDriver.cs` + `ShotDriver.asmdef`: `Assets/Editor/ShotDriver/` に置く
  ファイル監視ドライバ。`<project>/ShotDriver/cmd.txt` に
  `scene` / `panel` / `dock` / `backend <int>` / `approve <level>` / `fresh` /
  `send <text>` / `allow` / `scroll` / `status` を書くと `out.txt` に結果を返す。
  `dock` は `GetWindow<AgentPanelWindow>(typeof(InspectorWindow))` で Inspector
  横にドックする(Linux では効かなかったが Windows では効いた)。
- `shot.ps1`: `PrintWindow` で Unity メインウィンドウだけを PNG に落とす
  (`-Focus` でフォアグラウンド化)。`click.ps1`: ウィンドウ相対座標をクリック。
  **UITK のパネル内ボタンには合成クリックが届かない**(IMGUI のタブには届く)ので、
  許可・スクロールはドライバの verb で行う。
- uloop は使わなかった: ディスパッチャ(beta.37)とパッケージ(beta.48)の版ずれで
  `CLI_UPDATE_REQUIRED` を繰り返すだけだった。
- 手順: Unity 起動 → Ctrl+R でドライバをコンパイル → `scene` → `dock` →
  `approve 3`(全 Unity 操作を自動承認)→ `backend N` → `send ...` →
  `allow` を 6 秒おきに回す → `scroll` → `shot.ps1 -Out`。
- ドライバを再コンパイルするとドメインリロードで進行中の許可要求は破棄される
  (「ドメインリロードで破棄されました」バナー)。ターン中は触らないこと。

## 実機接続で分かったこと

### 動いたもの

- Grok Build 1.0.30(`grok agent stdio`)、codex-acp 1.12.0 + Codex 0.154.0 とも
  ACP ブリッジで初期化・プロンプト・ツール呼び出し・許可・usage 表示まで動いた。
  Grok の X 検索ツール、Grok / Codex の画像生成ツールは ACP の `tool_call` として
  流れ、画像は `Library/AgentPanel/Attachments/` に保存されてサムネイル表示される。

### Codex で画像生成できなかった原因

最初の Codex セッションは「画像生成ツールがこの環境で利用できない」と答えた。
調べた結果:

- Codex 側の機能フラグ `image_generation` は `stable / true`(`codex features list`)。
- codex-acp 抜きで `codex exec` に同じ質問をしても「`image_gen` は無い」と答えた
  → パネル / codex-acp の問題ではない。
- `~/.codex/auth.json` の id_token の `chatgpt_plan_type` が **`free`** だった。
  組み込み `image_gen`(gpt-image-2)は ChatGPT Plus / Pro / Business 以上の
  ChatGPT ログインでのみ提供され、Free と API キー認証では出てこない。
- ユーザーが Plus に変更したところ(トークンの `plan_type` が `plus` に更新)、
  同じ `codex exec` が「Yes」と答え、パネル経由でも生成→保存→マテリアル適用まで
  完了した(`codex-imagegen-texture.png`)。モデルも `gpt-5.6-terra` から
  `gpt-6-astra` に変わった。

### 見つかった不具合(別タスクに切り出し)

1. **ACP のツール名写像の取りこぼし**: Grok / Codex は UapOps ツールを
   `unity-ops__uap_ping` の形で呼ぶ。許可カードの表題がその生の名前のままで、
   自動承認レベル「全 Unity 操作」が効かず、`uap_*` ごとに許可カードが出た
   (§3.3 の `mcp__unity-ops__uap_<snake>` への写像に `unity-ops__` 形が無い)。
   Grok の非 Unity ツール(X 検索、画像生成)はカードに「Tool」とだけ出て名前が
   分からない。
2. **`uap_editor_execute_menu` の `File/Save` で Editor が固まる**: Codex が
   未保存の Untitled シーンに `File/Save` を実行 → Unity がネイティブの保存
   ダイアログを開き、`ExecuteMenuItem → Internal_FocusChanged` の中で uloop の
   「could not save dirty Scene files」が出た。ツールは「15 秒経っても main thread
   で実行中」を返し、その後 Codex が `uap_job_status` + `uap_object_inspect` ×3 を
   並列で投げたタイミングで Editor が「Hold on (busy) ... EditorUpdatePump.
   OnEditorUpdate」のまま約 5 分止まった(17:58:37 → 18:03:01)。ダイアログは
   ウィンドウ列挙に出ず、誰がどう閉じたかは未特定。モーダルを開くメニュー
   (File/Save(Untitled)、File/Open、Build Settings など)の扱いを決める必要がある。
3. 軽微: Grok は `_x.ai/session/prompt_complete` `_x.ai/task_completed`
   `_auth/status_update`、Codex は `session_info_update` を送り、パネルは
   「Ignored」を Console に毎回出す(情報ログだが 1 ターンで十数行)。

## 版

パッケージの変更なし(ドキュメントと撮影キットのみ)。リリースは切らない
(CLAUDE.md の規則 4)。
