# コンパイルのたびに会話履歴が消える(SessionCache の一時的な読み取り失敗)

日付: 2026-09-12 / 対象: v0.42.3 / 関連: MODEL-1・MODEL-2(`AtomicFile`)、
2026-08-05-boot-model-usage.md

## 1. 現象

実プロジェクトからの報告: **コンパイルが走るたびにパネルが新規セッションに
切り替わり、会話履歴が消える**。

計測(利用者の `Logs/Editor.log`、Unity セッション 1 回ぶん):

```
[AgentPanel] Session cache 'C:\...\UserSettings/AgentPanel/SessionCache.json'
  is temporarily unreadable (keeping the file):
  Sharing violation on path C:\...\UserSettings\AgentPanel\SessionCache.json
  ← SessionCacheFile:Load → AgentHub:get_Session → ChatView:RefreshNow
```

同じ行が 6 回。いずれもドメインリロード直後、ChatView の再描画から。
事後の `SessionCache.json` は 241 バイト、`sessionId:""`、`messages:[]`
になっていた(= 空セッションで上書き済み)。

## 2. 原因

3 段の連鎖で、最後の 1 段だけが本当のバグ。

1. `beforeAssemblyReload` が `SessionCache.json` を書き戻す
   (`AtomicFile`: `.tmp` + `File.Replace`)。
2. リロード後の最初の読み出しが **Sharing violation** で失敗する。
   `File.ReadAllText` は `FileShare.Read` で開くため、**書き込みアクセスを
   持つ他ハンドルが存在するだけで開けない**。置き換え直後のファイルを
   開くもの — アンチウイルスのリアルタイムスキャン、検索インデクサ、
   同期エージェント — がまさにそれ。`AtomicFile` 自身の書き込みが、
   自身の読み取りの失敗要因を作っている。
3. `SessionCacheFile.Load` は MODEL-1 どおり「一時障害なのでファイルは
   消さない」と判定して `null` を返す。**ここまでは無害**。
4. `AgentHub.Session` が `_session = loaded ?? new ChatSession();` で
   **失敗を「キャッシュ無し」と同一視**し、空セッションをそのドメインの
   確定値としてキャッシュする → 画面が新規セッションになる。
5. 直後の任意の `SessionCache.Save(Session, ...)` がその空セッションを
   本物の上に書く → **数ミリ秒のロックが恒久的なデータ損失になる**。

MODEL-1 が「ロックでファイルを消さない」ところまでは守っていたのに、
書き戻し側が同じ不変条件を持っていなかった、という形の穴。

## 3. 修正

### 3.1 MODEL-3: 読み取りを共有可能にする(`AtomicFile.ReadAllText`)

`FileShare.ReadWrite | FileShare.Delete` で開き、IO 失敗は短くリトライ
(4 回 / 15・30・45ms、合計 90ms 上限)。共有モードを広げることは、
スキャナのハンドルと**競合せず共存する**ことを意味し、発生源そのものを
消す。リトライは相手が共有を一切許さない一瞬(スキャン中の排他オープン)
のためだけの保険。存在しないファイルはリトライしない(ロックではないし、
予算はメインスレッド時間)。それでも失敗するものは従来どおり throw し、
`IsCorruption` の分類は変わらない。

`AtomicFile` は全サイドカー(SessionMeta / QuickAction / CustomInstructions /
Ops 生成ファイル)の共通経路なので、この 1 か所で全部が直る。

### 3.2 読めなかったキャッシュには書き戻さない

- `SessionCacheFile.Load` に `out bool unreadable` を追加。`null` の
  3 通り — 「ファイルが無い」「壊れていたので削除した」「読めなかった」 —
  のうち最後だけが true。前 2 つは**保存してよい** null、最後は
  **保存してはいけない** null で、書き込み側から見ると正反対。
- `AgentHub` は `_sessionCacheUnreadable` として保持し、保存は全部
  `SaveSessionCache()` の 1 経路に集約(呼び出し口 14 か所を置換)。
  フラグが立っている間は書かない。新しい呼び出し口を足しても不変条件が
  抜けない形にするのが集約の目的。
- 例外は **利用者が明示的にセッションを差し替える 2 経路**
  (`StartFresh` = New chat、`SwitchToSession` = History 選択)。
  こちらは `ForgetUnreadableSessionCache()` でブロックを解いてから書く。

### 3.3 同じドメイン内で復帰する

`Session` getter は、フラグが立っている間は再読込を試みる
(`TryRecoverUnreadableSessionCache`、最大 5 回・250ms 間隔)。
更新フックを増やさず ChatView の再描画に相乗りする。成功したら
`MergeRecoveredCache` で **復元したログの後ろに、ロック中に届いた
メッセージを連結**する(置き換えない)。仮セッションは 0 から始まって
追記されるだけなので、そのメッセージは時系列で必ず後ろ、そのカウンタは
そのままロック中の増分になる(コストだけは CLI 側の累計なので
`AccumulateTurn` と同じく最大値を採る)。メッセージは参照ごと移すため、
ストリーミング中の吹き出しや進行中のツールカードも生きたまま。

5 回でも読めなければ、そのドメインは空表示のまま続く(保存は止まったまま
なのでファイルは無傷、次のリロードで戻る)。このとき初めて会話欄に警告
ノートを 1 つ出す(`HubSessionCacheUnreadable`)。**「履歴が消えた」と
区別できないまま黙って空を出す**のが、この不具合が報告された形そのもの
だったため。保存がブロックされているので、このノート自体は永続化されない。

## 4. 検証

- `AtomicFileTests`: 書き込みハンドル(`FileShare.Read`)保持中でも読める /
  完全排他では従来どおり `IOException`(= 削除されない) / 不存在は
  リトライしない。
- `SessionCacheFileTests`: 排他ロック中は `unreadable=true` でファイル維持、
  ロック解除後は同じファイルが読める。「無い」「壊れている」は false。
- `AgentHubSessionCacheRecoveryTests`(新規): 実ファイルを実際に排他
  ロックして、**空セッションで上書きされないこと**、解除後に履歴が戻る
  こと、ロック中に届いたメッセージが失われないこと、マージ算術。
- `ci/SmokeTests`: MODEL-3 の 2 ケースを Unity 非依存ゲートにも追加。

## 5. 補足: 利用者側の緩和策

根治はパッケージ側だが、発生源を減らせる設定もある — プロジェクトフォルダ
(特に `UserSettings/`)をアンチウイルスのリアルタイムスキャン除外に
入れる。Unity のインポート時間にも効く一般的な推奨設定。
