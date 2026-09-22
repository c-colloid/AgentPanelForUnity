# パネルから uLoop を削除する（導入ボタンの対）

対象: `jp.colloid.unity-agent-panel`（Core）
関連: `docs/design-notes/2026-09-21-uloop-always-loaded-cost.md` §5 案 B、
同 §4（パネルが切れるもの / 切れないもの）、
`docs/design-notes/2026-08-12-uloop-install-progress.md`（導入側の進捗機構）

---

## 0. 結論（先に）

1. uLoop の常駐を**本当に**止める唯一の方法はパッケージを外すことで、
   それはこのパネルからできる。導入ボタンと対称に「削除」を置いた。
2. 削除は 2 段階にした。**(1) `Client.Remove` を投げる**（依存行は Unity が
   消す）→ 完了を検出してから **(2) manifest.json の OpenUPM 設定を片付ける**。
   順序を逆にも同時にもできない理由は §2 に書いた。実測ではなく
   ファイルの所有権とタイミングから導かれる制約なので、先に明文化しておく。
3. **片付け（段階 2）は best-effort** で、失敗しても削除自体は成功のまま
   残骸の場所を名指しして報告する。片付けは整頓であって、削除の成否では
   ないため。
4. **scopes は「自分が書いた完全一致の 1 行」しか消さない。** より広い
   スコープ（`io.github.hatayama` のような接頭辞）や、他の依存が
   まだ必要としているスコープには触れない。§3 が規則の全体。
5. パネルが書いた**設定側の残骸**（`Bash(uloop ...)` の許可行、指示
   スニペット）は**消さない**。ユーザーの設定であり、こちらが黙って
   書き換えてよいものではないので、確認カードで残ることを明記する。

---

## 1. 何を削除するのか

導入（`UloopInstaller`）が書いたものは 2 つだけ:

| 書いたもの | 誰が書いたか |
|---|---|
| `scopedRegistries` の OpenUPM エントリ（`scopes` に uLoop の id） | このパネル（manifest.json への直接書き込み） |
| `dependencies` の `io.github.hatayama.uloopmcp` 行 | Unity（`Client.Add` の結果） |

したがって削除も対称に、**依存行は Unity（`Client.Remove`）に任せ、
`scopedRegistries` だけをこちらで片付ける**。導入側が
「`dependencies` は絶対に手書きしない」（バージョン定数が陳腐化するため）を
貫いているので、削除側も同じ境界を守る。

---

## 2. なぜ 2 段階なのか（順序の制約）

片付けと `Client.Remove` は、どちらを先にしても単純には成立しない。

**(a) manifest を先に書く（レジストリ行を消してから `Client.Remove`）** — 不可。
Unity は manifest.json の変更を監視して再解決する。依存行が残ったまま
レジストリだけ消えた瞬間があると、その一瞬で
「`io.github.hatayama.uloopmcp` が見つからない」という解決エラーが
コンソールに出る。こちらが「安全です」と言って実行させた操作で
エラーを出すのは筋が悪い。

**(b) `Client.Remove` を先に投げて、その直後に `ManifestAfter` を書く** — 破壊的。
`Client.Remove` は manifest.json を**自分で書き換える**（依存行を消す）。
`Plan` 時点のテキストから作った `ManifestAfter` にはまだ依存行が入っている
ので、後から書けば**消したはずの依存を復活させる**。

**(c) 採用: `Client.Remove` を先に投げ、完了を検出してから片付けを
「その時点の manifest に対して」再計算して書く。** 依存行はもう無いので
(a) の解決エラーは起きず、`ManifestAfter` を持ち回らないので (b) の
復活も起きない。代償は、確認カードが見せる差分が**予測**になること。
これは差分に「Unity が依存行を消したあと、このパネルがここを消します」と
書いて明示する（`ManifestAfterProjected`）。

段階 2 は、削除が着地したこと（`UloopDetector` が「無い」と言うこと）を
条件に走る。ここでも導入側と同じ原則を守る:
**成否は予測ではなく観測から取る**（`UloopInstallProgress` の規則 1 と同じ）。

---

## 3. scopedRegistries の片付け規則

`UloopUninstaller.PlanRegistryCleanup` が決める。対象は
**OpenUPM の URL に一致するエントリ 1 つだけ**（URL 正規化は導入側の
`UrlsMatchAsRegistry` と同じ規則）。

1. **完全一致しか消さない。** `scopes` の要素が uLoop の既知 id と
   `Ordinal` で完全一致するものだけが削除候補。`io.github.hatayama` の
   ような接頭辞スコープは**こちらが書いたものではない**し、同じ接頭辞の
   別パッケージを巻き添えにするので触らない。
2. **他の依存が使っているスコープは残す。** Unity のスコープはドット区切りの
   接頭辞マッチなので、`dependencies` の中に `scope` そのもの、または
   `scope + "."` で始まる id が uLoop 以外に存在すれば、そのスコープは残す
   （`DependenciesCoveredByScope` が返す id を `ScopeHolders` に積み、確認カードに出す）。
3. 削除候補が無ければ `None`（何も書かない）。
4. 削除後に `scopes` が空になるなら、**エントリごと消す**（`DropRegistry`）。
   スコープが空のレジストリは何も配れないので、残す意味が無い。
   空にならないなら、その行だけ消す（`DropScope`）。
5. **他所のレジストリには触らない。** OpenUPM 以外の URL のエントリが
   uLoop の id を scopes に持っていても、それは会社ミラーなり VPM なりを
   プロジェクトが意図して向けた先であって、こちらの持ち物ではない。
   警告 `foreign-registry-kept` を出して、そのまま残す。

---

## 4. 安全側の設計（導入側から引き継いだもの）

- **書く前に差分を見せる。** manifest.json はユーザーの資産という
  ARCHITECTURE リスク #12 の原則。導入と同じ確認カード・同じ
  `BuildManifestDiffText`・同じ折りたたみ。
- **`.uap-backup` を取ってから書く。** 書き込みに失敗したら復元し、
  復元にも失敗したら**別の失敗コード**で「ディスク上の状態が不明」と
  言い切る（導入側の `ApplyFailureManifestWriteFailedRestoreFailed` と同じ扱い）。
- **`Plan` は純粋、`Apply` だけが書く。** `Apply` は `Plan` が見た
  manifest テキストとバイト単位で一致しない限り何もしない
  （`ApplyFailureManifestChangedSincePlan`）。段階 2 は Unity が書き換えた
  後なので同じ比較はできない代わりに、**依存がまだ残っていれば必ず拒否する**
  （`CleanupFailurePackageStillPresent`）。生きている依存のレジストリを
  剥がすのが最悪の失敗なので、そこだけは別のガードを立てる。
- **削除側は書き込みが段階 2 だけ。** 段階 1 は `Client.Remove` を投げるのみで
  manifest に触らないため、導入側にあった「書いた直後に Client が投げて
  ロールバック」という経路自体が存在しない。削除のほうが構造的に安全。

---

## 5. 進捗表示は導入側の機械を再利用する

`UloopInstallProgress.Evaluate` は「生きたリクエスト / SessionState の
フラグ / 検出器」の 3 者が食い違ったときにどれを信じるかという
優先順位の機械で、削除でもその優先順位はそのまま正しい。違うのは
**検出器の読み方だけ**（導入は「有る」が成功、削除は「無い」が成功）。

そこで `EvaluateRemoval` を 1 つ足し、`detectorSaysInstalled` を反転して
同じ `Evaluate` に渡す。優先順位の表テストは 1 か所のまま、
ユーザーに見える文字列だけ削除用に分ける（「削除中… {0}s」等）ので、
「Installing」という語が削除中に出ることはない。

---

## 6. 残骸として残すもの（確認カードに明記）

| 残るもの | なぜ消さないか |
|---|---|
| `allowedTools` / `disallowedTools` の `Bash(uloop ...)` 行 | ユーザーの設定。プリセットで足した後にユーザーが編集している可能性があり、黙って書き換えない |
| カスタム指示のスニペット | 同上（サイドカーはユーザーの文書） |
| uLoop が配置したスキルファイル | uLoop の持ち物。パッケージを外せば配置元も消えるが、既に書かれたファイルの後始末は uLoop 側の領分 |
| `Library/`・`packages-lock.json` | Unity が自分で更新する |

`uloopAgentUseEnabled`（§5 案 A のトグル）も**触らない**。削除後に
オフのままでも実害は無く（コマンド自体が無い）、ユーザーが再導入したときの
意図を勝手に上書きしないほうが良い。

---

## 7. 検証結果と未確認事項

**実機（Unity 2022.3.22f1、GameCI コンテナ）**（`26fce65`）:

| 構成 | total | passed | failed | skipped |
|---|---:|---:|---:|---:|
| Pro あり（`ci/HostProject`） | 4483 | 4456 | **0** | 27 |
| Core のみ（`ci/HostProjectCoreOnly`） | 3669 | 3642 | **0** | 27 |

案 A 時点（4458 / 3644）から +25。内訳は `UloopUninstallerTests` 20 ケース
（片付け規則の 4 つの「やり過ぎない」経路と、両フェーズの失敗系）、
`EvaluateRemoval` の反転 4 ケース、確認カードの文面 1 ケース。

**未確認:**


- **実機での `Client.Remove` の所要時間とドメインリロードの有無**は未計測。
  導入は実測 30.5s（2026-08-04）だが、削除は解決にネットワークが要らない
  ぶん速いはず、というのは推測。`StaleThresholdSeconds` は導入側の 300s を
  そのまま使う（短すぎて誤って Stalled と言うより、長すぎるほうが害が小さい）。
- 段階 2 の書き込みが実機で意図どおり動くかは、EditMode テスト（シームに
  差し込んだ偽のファイル IO）までしか押さえていない。実際の
  Package Manager を伴う往復は手動サンドボックス確認として残す。

## 8. 既知の取りこぼし（仕様として許容）

段階 2 のトリガは SessionState のフラグなので、**削除の着地を観測する前に
エディタを終了すると、片付けは走らない**。残るのは何も配らない
scopedRegistries エントリ 1 つで、実害は無く、次に「uLoop を削除」を押しても
既に依存が無いのでプランは `not-installed` で止まる。

これを埋めるには「依存は無いが自分が書いたスコープだけ残っている」状態を
検出して片付けを提案する導線が要るが、それは削除ボタンとは別の機能
（整頓ボタン）であって、今回の「導入ボタンの対」という要求には含まれない。
`SettingsUloopRemoveCleanupFailed` と同じく、**残骸の場所を正直に言う**
ところまでが今回の範囲。
