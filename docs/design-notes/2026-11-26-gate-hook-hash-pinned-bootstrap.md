# ゲートフックを「ハッシュ固定のブートストラップ+本体ファイル」にする

日付: 2026-11-26 / 対象: Core v0.62.0-beta.5

## 背景

スクリプト検証ゲートの PreToolUse フックは、`GateHookInstaller` が CLI の起動ごとに
`--settings` の JSON として生成している。2026-08-22 の SEC-1 対応
(`2026-08-22-gate-hook-inline-and-quotearg-tab.md`)で、フックの PowerShell スクリプトを
`.ps1` ファイルに置くのをやめ、スクリプト全体を UTF-16 の base64 にして
`powershell -EncodedCommand <base64>` の 1 行に埋め込む形にした。エージェントが書き換えられる
実行ファイルをディスクに残さないためである。

利用者から、Claude Code がこのフックを「Windows で渡せない長さ」として実行を拒否している、
という報告を受けた(2026-11-26)。

## 原因(実測)

| | 文字数 |
|---|---|
| フックスクリプト本体(151 行) | 6,440 |
| その UTF-16 base64 | 17,176 |
| コマンド全体(拒否メッセージ込み、実際に生成されたもの) | 17,918 |
| cmd.exe のコマンドライン上限 | 8,191 |
| CreateProcess の上限 | 32,767 |

- 生成されたコマンドを `cmd.exe /c` で実行すると「コマンド ラインが長すぎます。」で
  **起動できない**(終了コード 1、フックの出力なし)。2 つのプロジェクトパスで再現した。
- 長くなったのは 2026-09-15(v0.53.0、`2026-09-15-script-gate-steering-and-powershell.md`)。
  シェルコマンドの書き込み先を走査する `Get-UapShellWriteTargets` をフック内に足した時点で、
  スクリプトの大半がこの関数になった。
- 当時この長さを見張るテストはなく、既存のテストは「base64 を戻すとスクリプトに一致する」こと
  しか見ていなかった。

### 影響

`acceptEdits` では can_use_tool の事前フィルタが発火しないので、`Assets/**/*.cs` /
`*.asmdef` への直接書き込みを止めるのはこのフックだけである(設計 8.7)。フックが起動できないと、
その層がなくなる。しかも `IsHookRunnable()` は powershell.exe の有無しか見ないため、
フックが有効な扱いのまま、代替の `permissions.deny` も出力されない。v0.53.0 以降の版が該当する。

確認していないこと: 拒否されたフックを Claude Code がブロックとして扱うか素通しにするかは
実機で確かめていない。どちらでも直す必要があるのは同じ(素通しならゲートが黙って無効、
ブロックなら全書き込みが止まる)。

## 選択肢

| 案 | コマンド長 | 評価 |
|---|---|---|
| A. `.ps1` に戻して `-File` で実行 | 約 200 | SEC-1 の再発。不可 |
| B. インデント除去などの最小化 | 約 13,700 | 上限に届かない |
| C. gzip 圧縮+展開ブートストラップ | 約 7,400 | 収まるが余裕が 1 割。走査を足せば再発する |
| D. 短いブートストラップ+本体はファイル、SHA-256 をコマンド側に固定 | 約 2,700(パス長にだけ依存) | 採用 |

D を採った理由:

- **長さがスクリプトの大きさに依存しない。** コマンドに載るのは 64 桁のハッシュとファイルの
  パスだけ。今後フックの走査を増やしても変わらない。
- **SEC-1 を閉じたままにできる。** 後述。

## 設計

### 生成物

`<projectRoot>/UserSettings/AgentPanel/` に 2 つ書く。

1. **フックスクリプト** `uap-gate-hook-<ハッシュ先頭 16 桁>.ps1`。内容は `BuildHookScript` の
   出力そのまま(変更なし)。UTF-8・BOM なし。
2. **`uap-gate-settings.json`**。フックのコマンドは
   `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand <ブートストラップの base64>`。

### ブートストラップ(`BuildBootstrapScript`)

```powershell
$ErrorActionPreference = 'Stop'
try {
    $bytes = [IO.File]::ReadAllBytes('<フックスクリプトの絶対パス>')
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    if ($hash -ne '<SHA-256>') { throw 'hash mismatch' }
    $block = [scriptblock]::Create([Text.Encoding]::UTF8.GetString($bytes))
} catch {
    '<拒否の JSON>'
    exit 0
}
& $block
```

- **読むのは 1 回。** 読んだバイト列をハッシュし、同じバイト列を実行する。検査と実行の間に
  ファイルを差し替える余地がない。
- **検証前の失敗はすべて拒否(fail closed)。** ファイルがない、読めない、ハッシュが違う、の
  どれでも拒否の JSON を出して終了コード 0。スクリプトがないと「ゲート対象の書き込みか」を
  判定できないので、matcher に掛かったツール呼び出しはすべて拒否する。拒否文
  (`UnverifiedHookDenyMessage`)は「Agent Panel から新しいセッションを始めれば再生成される。
  自分で作り直したり編集したりしない」と伝える。
- **スクリプト自体は try の外で実行する。** スクリプト側の「壊れた stdin は黙って許可」
  (設計 8.7 の 5)はそのまま残る。

### SEC-1 が閉じたままである理由

SEC-1 は「`acceptEdits` のエージェントが `.ps1` を書き換えると、次の Write / Edit で CLI が
それを `-ExecutionPolicy Bypass` で実行する」という権限昇格だった。今回 `.ps1` はディスクに
戻るが、次の 2 点で昇格にならない。

- 書き換えたファイルはコマンド側のハッシュと一致しないので、**実行されずに拒否される**。
- ハッシュは settings JSON の中にあり、CLI は settings を**起動時にしか読まない**。
  セッション中に settings を書き換えても動作中のプロセスには効かず、次の起動で
  `EnsureInstalled` が両方を作り直す(この性質は 08-22 の設計と同じ)。

残る影響は、エージェントが自分でフックスクリプトを壊すと、そのセッションの Write / Edit /
シェルが次の起動まですべて拒否されること。安全側に倒れるだけなので許容する。既定 / plan モード
では従来どおり `ScriptGate.IsProtectedGateFile` がこれらのファイルへの Write / Edit を
先に拒否する(新しいファイル名も対象に含めた)。

### ファイル名を内容アドレスにする理由

CLI のプロセスはドメインリロードをまたいで再接続されるので、パッケージ更新より長く生きる
ことがある。ファイル名が固定だと、更新後の新しい起動がスクリプトを書き換えた時点で、
古いプロセスのコマンドに固定されたハッシュと合わなくなり、そのセッションが全拒否になる。
ハッシュをファイル名に入れておけば、版ごとに別のファイルを見るので衝突しない。

- 名前は置き場所を決めるだけで、検証はブートストラップが 256 ビット全体で行う。
- 他の版のファイルは消さない(動作中のセッションが使っているかもしれない)。1 つ数 KB で、
  スクリプトの文面が変わったときにしか増えない。
- 08-22 より前の固定名 `uap-gate-hook.ps1` は従来どおり削除する。

### 長さの上限とフォールバック

- `MaxHookCommandLength = 8000`(cmd.exe の 8,191 に余裕を持たせた値)。
- `BuildSettingsJson` は、コマンドがこれを超えるとフックではなく `permissions.deny` を出す。
  起動できないフックは何も止めないので、汎用の拒否文になっても止まる方を選ぶ。現実には
  プロジェクトのパスが数千文字ないと起きない。
- フックスクリプトを書けなかったときも同じく `permissions.deny` にする(全拒否のフックを
  配らない)。

## 検証

- **スモークテスト**(`ci/SmokeTests`、本番ソースをそのままコンパイル): 931 件成功、失敗 0。
  ローカルに .NET SDK がないため、Unity 2022.3.22f1 同梱の Roslyn と .NET 6 ランタイムで
  ビルドして実行した。
- **実際の cmd.exe + Windows PowerShell 5.1 での実行**: 本番コードで生成したコマンドを
  `cmd.exe /c` に通し、stdin にフックの JSON を渡して応答を確認した。ASCII のパスと、
  カタカナ・空白・アポストロフィを含むパスの 2 通りで、各 16 項目すべて成功。
  - 旧コマンド(17,918 文字)は起動できない。新コマンドは 2,662 / 2,690 文字。
  - `Assets/**/*.cs`、`..` を含む絶対パスの `.asmdef`、`Set-Content Assets/Foo.cs`、
    `echo hi > Assets/Foo.cs` は、ステージングの案内つきで拒否。
  - `UapStaging/Foo.cs`、`Assets/readme.txt`、`git status`、壊れた stdin は出力なしで許可。
  - フックスクリプトを「ファイルを 1 つ書いて exit 0」に書き換える → 拒否され、
    そのファイルは作られない(実行されていない)。1 バイト足す、削除する、でも拒否。
  - 次の `EnsureInstalled` で元のバイト列に戻り、同じコマンドがまた通る。
- **EditMode テスト**: `GateHookInstallerTests` / `ScriptGateTests` は同じ Roslyn で
  コンパイルが通ることだけ確認した。Unity 上では実行していない(CI に任せる)。
- **未実施**: 実際の Claude Code CLI にこの settings を渡したライブ E2E。

## 回帰ガード

- `BuildInlineHookCommand_StaysFarBelowTheCommandLineLimit`: 240 文字のパスでも上限の半分未満。
- `BuildInlineHookCommand_LengthDoesNotDependOnTheScript`: スクリプトが 1 文字でも
  10 万文字でもコマンド長が同じ。
- `MaxHookCommandLength_IsBelowTheCmdExeLimit`: 上限値そのものが 8,191 未満。
- `BuildSettingsJson_CommandOverTheLimit_FallsBackToPermissionsDeny`。
- `EnsureInstalled_HookShape_WritesTheScriptTheCommandPins`: ディスク上のバイト列の
  ハッシュが、コマンドに固定された値と一致する(BOM なしも確認)。
- `EnsureInstalled_TamperedHookScript_IsRestoredOnTheNextInstall`、
  `EnsureInstalled_LeavesAnotherVersionsHookScriptAlone`。
- スモークテストにも同じ内容を入れた(ライセンスなしの CI で毎 push 走る)。

## 残課題

- フックのコマンドは `powershell`(Windows PowerShell)固定だが、`IsHookRunnable` は PATH 上の
  `pwsh.exe` だけでも「実行可能」と判定する。今回の範囲外で、変更していない。
- 古い内容アドレスのスクリプトは掃除しない。溜まるようなら、最終更新が十分古いものだけ
  消す処理を足す。
