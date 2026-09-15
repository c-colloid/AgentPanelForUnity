# スクリプト検証ゲート: 事前指示の追加と PowerShell ツールの穴 (2026-09-15)

## 1. 報告と観測

「ステージングを無視してスクリプトを Assets 直下に書こうとする動作が最近多発する」
という報告。添付されたトランスクリプトでは、エージェントが
`Write Assets/地上/Editor/SAMeshKit.cs` を試み、ゲートに拒否されてから
「UapStaging/ 経由で入れる必要があるようです」と述べて経路を切り替えていた。
拒否のシステムノート自体は、後続の `uloop compile` の **後ろ** に描画されていた。

調べた結果、原因は 3 つに分解できた。いずれもモデル側の不注意ではなく
パネル側の構造による。

### 1.1 事前に何も伝えていない

`--append-system-prompt` に注入する文面(`ComposeUapOpsSteeringSection`)は
uap_* ツールの使い分け・ジョブ待ち・ライトマップ等のみで、`UapStaging/` と
`uap_scripts_commit` には触れていなかった。ゲートは 2026-08-01 設計 §8.2 B1 /
§8.7 の「mechanism, not instruction」方針で、`ScriptGate.DenyMessage` を
**拒否後の tool_result で読ませて自己修正させる** 前提だった。つまり
「まず Assets に書く → 拒否 → UapStaging に書き直す」が仕様上の正常動作で、
新規セッション・`/compact` 後・ドメインリロード後の resume など、モデルの
文脈からゲートの記憶が抜けるたびに同じ往復が起きる。

§8.3 には「指示スニペットにも『ターン中のコンパイル起動は commit 経由のみ』と
明記」とあるが、そのスニペットは実装されていなかった。

### 1.2 Windows のシェルツールは "PowerShell" という名前で来る

can_use_tool 側の事前フィルタ(`AgentHub.TryAutoDenyForScriptGate`)は
`tool.ToolName == "Bash"` の厳密比較だけをシェル経路として見ていた。CLI は
Windows ではシェルツールを `PowerShell` という名前で公開しており(パネル自身の
`ToolCardDescriber` はそれを知っていて、2026-08-02 のノートにも
「PowerShell x24」の観測がある)、`Set-Content Assets/Foo.cs` は素通りだった。

生成する PreToolUse フックの matcher も `Write|Edit|MultiEdit` のみで、
シェル経路には一切掛かっていなかった。さらに `ScriptGate.ExtractBashWriteTargets`
は `>` / `tee` / `sed -i` / `cp` など bash 系のイディオムしか知らず、
`Set-Content` / `Out-File` / `Add-Content` / `New-Item` / `Copy-Item` /
`[IO.File]::WriteAllText` は候補にすら挙がらなかった。

`uloop execute-dynamic-code` からの `File.WriteAllText` は C# 実行なので
どの層からも見えない。これは設計で明記済みの限界で、本ノートでも変えない
(§2 の事前指示で「動的コードツールでも作らない」と言うにとどめる)。

### 1.3 拒否ノートの描画位置

拒否時のシステムノートは `Session.AddMessage` で **別メッセージ** として末尾に
追加されるが、ストリーミング中のアシスタント発話は 1 つの `ChatMessage`
(`_streamingAssistant`)に蓄積され続けるため、ノートはターン全体の後に描画される。
「uloop compile の後にブロック表示が出た」ように見えたのはこのためで、実際の
発火は Write の時点だった。

## 2. 事前指示 (system prompt)

`AgentHub.ComposeScriptGateSteeringSection(bool uapScriptGateEnabled)` を新設し、
ゲートが有効な時だけ UapOps ステアリング節の直後に 2 行を注入する:

- `*.cs` / `*.asmdef` は Assets/ に直接書かない。Write/Edit も、Bash / PowerShell
  のリダイレクトや `Set-Content` / `Out-File` / `Copy-Item` 等のシェル書き込みも
  ゲートが拒否する。動的コードツールでも作らない。
- `UapStaging/`(プロジェクトルート、Assets 外)に、意図する Assets/ 配下の
  サブパスをそのまま鏡写しにして置き(`UapStaging/Editor/Foo.cs` →
  `Assets/Editor/Foo.cs`。`ScriptStagingScanner.ToAssetsRelativePath` は恒等)、
  `uap_scripts_commit` を呼ぶ。commit がコンパイルして通った時だけ Assets へ移し、
  失敗ならエラーを返す。ステージ済みスクリプトのために自分でコンパイルや
  リフレッシュを起こさない(§8.3 の未実装分)。

ゲートが OFF の時は空文字列(誰も強制しない規則を書かない)。フォルダ名と
ツール名は `ScriptGate.StagingFolder` / `DenyMessage` と同じものを使い、
事前指示と事後の拒否文が食い違わないことをテストで固定する。

「mechanism, not instruction」は撤回しない。強制はゲートのままで、指示は
初回の往復を省くためのもの。

## 3. ゲートのシェル経路を PowerShell へ広げる

### 3.1 can_use_tool 層 (`ScriptGate` / `AgentHub`)

- `ScriptGate.IsShellToolName`: `Bash` / `PowerShell` / `Shell` を同じ扱いにする。
  コマンド文字列は `command`、無ければ `script`(`ToolCardDescriber` と同じ順)。
- `ExtractBashWriteTargets` に PowerShell のイディオムを追加。既存のトークナイザ
  (引用符を保ったまま `; | & 改行` で分割)の上に載せる:
  - `Set-Content` / `Add-Content` / `Out-File` / `New-Item`(別名 `ac` / `ni`):
    `-Path` / `-LiteralPath` / `-FilePath` / `-PSPath`(`-Path:value` 形式も)が
    あればその値、無ければ最初の位置引数。`-Force` 等の既知スイッチは値を取らず、
    それ以外の `-Parameter` は次のトークンを消費すると見なす(取りこぼす方向に
    倒す = fail-open、パラメータ値を誤って標的にしない)。
  - `Copy-Item` / `Move-Item`(別名 `cpi` / `mi` / `copy` / `move`):
    `-Destination` があればその値、無ければ **2 番目** の位置引数(1 番目は
    ソースで、Assets から外へ移す正当な操作を止めないため)。
  - `::WriteAllText(` / `WriteAllLines(` / `WriteAllBytes(` の第 1 引数
    (引用符付きのみ)。
- 抽出した候補はすべて既存の `IsGatedScriptPath`(`..` の畳み込み、
  大文字小文字無視、`Assets/` セグメント探索)に流す。

### 3.2 フック層 (`GateHookInstaller`)

matcher を `Write|Edit|MultiEdit|Bash|PowerShell` に広げ、生成する PowerShell
スクリプトを 2 関数に整理した:

- `Test-UapGatedPath`: 従来のパス判定をそのまま関数化。
- `Get-UapShellWriteTargets`: §3.1 と同じ抽出をスクリプト内で再実装
  (フックは独立プロセスなのでマネージドコードを共有できない、という §8.7 と
  同じ理由)。正規表現(リダイレクト / `tee` / `WriteAll*`)+ トークン走査
  (`sed -i` / `cp` `mv` `install` / `dd of=` / 上記 cmdlet 群)。

Write/Edit/MultiEdit は `file_path` を、Bash/PowerShell/Shell は `command`
(無ければ `script`)を見る。判定・拒否文は従来どおり、パース失敗は exit 0
(fail-open)。

pwsh 7.4 で生成スクリプトに実際の stdin を流して確認した(Assets 直書きの
Write / `Set-Content -Path` / `Out-File -FilePath "H:\...\Assets\...\X.cs"` /
位置引数 / `WriteAllText` / `Copy-Item -Destination` / `New-Item ... Assets/Sub/../Foo.cs`
はすべて deny、`Get-Content Assets/Foo.cs | ...` / `.txt` / `UapStaging/` 宛 /
`uloop execute-dynamic-code` / 非 JSON はすべて allow)。C# 側の
`TryFindGatedBashTarget` に同じ入力を通して結果が一致することも確認した。

なぜフックにも載せるか: Bash/PowerShell は acceptEdits でも can_use_tool を
通る(§8.7 実測)が、ユーザーが「常に許可」した接頭辞ルール
(`PowerShell(Set-Content:*)` など)があると CLI 側で許可が確定して can_use_tool
自体が来ない。フックはその場合でも発火する。

### 3.3 変えないもの

`uloop execute-dynamic-code` 等の動的コードや、変数で組み立てたパス、
`python -c` 相当のインタプリタ一行は、どちらの層からも見えない。既存の
限界の記述(`ScriptGate.ExtractBashWriteTargets` の doc)のまま。

## 4. 拒否ノートの位置

`TryAutoDenyForScriptGate` は、ストリーミング中のアシスタント発話
(`_streamingAssistant`)があればその **ブロック** として `SystemNote` を追加し、
無い時だけ従来どおり別メッセージにする。ブロックの描画は
`MessageBlockFactory` が種別で分岐するので、アシスタント発話内の `SystemNote`
はそのまま既存の見た目で出る。拒否した Write のカードの直後にノートが並ぶ。

## 5. 回帰ガード

- `ScriptGateTests`: `IsShellToolName`、PowerShell 書き込みイディオム 15 例が
  deny、読み取り / 非スクリプト / ステージング宛 / uloop 起動 7 例が allow、
  `Out-File` の名前付きパラメータの標的抽出。
- `AgentHubScriptGateTests`: `tool_name: "PowerShell"` の `Set-Content` が
  カード無しで deny され、`UapStaging/` 宛は通常のカードに落ちる。
- `GateHookInstallerTests`: 生成スクリプトの行単位ピンを新しい本文に更新、
  matcher のピンを更新。
- `AgentHubStartClientArgTests`: ゲート OFF で空、ON で `UapStaging/` /
  `uap_scripts_commit` / `.asmdef` / `PowerShell` を含む、拒否文と同じ
  フォルダ名を引用する、3 行以内。
- `ci/SmokeTests`(dotnet、Unity 不要): matcher のピンと PowerShell イディオム
  11 例。本ノートの変更で 144/144 通過。
