# uLoop 自前化フェーズ 3 の実装(`uap_execute_code`)

Date: 2026-09-28
対象: `jp.colloid.unity-agent-panel`(Core)v0.61.0-beta.1(フェーズ 1・2 と同じベータ)
関連: `docs/design-notes/2026-09-22-uloop-full-native-roadmap.md` §4(計画)、
`docs/design-notes/2026-09-22-uloop-phase1.md`、
`docs/design-notes/2026-09-23-uloop-phase2.md`、
`docs/design-notes/2026-08-01-phase5-unity-ops-design.md` §8(「動的コードは自動承認しない」の継承元)

## 0. 結論(先に)

1. ロードマップ §4 のフェーズ 3(`uap_execute_code`)を実装した。§7 項 1 の推奨どおり、このパッケージ(Core)に置いた(uLoop を入れる最後の動機を消すという §6 の目的のため)。
2. コンパイル経路は `uap_scripts_commit` と同じ `UnityEditor.Compilation.AssemblyBuilder`。参照集めは
   `UapScriptsCommitTool.GatherAdditionalReferences`(エンジンモジュール + プリコンパイル済みアセンブリ)を
   そのまま再利用し、その上に **プロジェクト自身の既にコンパイル済みの Editor/Player アセンブリ出力**
   (`CompilationPipeline.GetAssemblies(AssembliesType.Editor/Player)` の `outputPath`)をファイル名で
   重複排除しながら追加した -- `uap_scripts_commit` はステージ中のスクリプトが「これから」その
   アセンブリ群に加わる候補として検証されるだけなので不要だったが、`uap_execute_code` は独立した
   別アセンブリとして単発実行されるため、プロジェクト型(`PlayerHealth` など)を呼ぶにはこれが要る。
3. 安全モデルの核は 3 つ:
   - **静的スキャンは拒否せず `confirm:true` を要求するだけ**(条件付き破壊的)。フラグが無ければ
     初回呼び出しでそのまま実行される。`UapDestructiveToolBase` は「confirm 無しは常に拒否」という
     設計(dispatch が確認フラグの有無だけで分岐し、フラグの中身を見ない)なので、今回はそぐわず
     `IUapDestructiveTool` を直接実装した(`Preview` は提供するが、実際の分岐は自前で書いた)。
   - **どの自動承認レベルでも自動承認しない**。既存の `AutoApprovePolicy.ShouldAutoApprove` は
     `AllUnityOps`(UapOps 全ツール自動許可)と `AllTools`(全ツール自動許可)を無条件で `true` にする
     実装で、個別ツールを除外する仕組みが無かった(2026-08-01 design note §8 の「動的コードは
     自動承認しない」という宣言は、当時はコード自体が存在しなかったので未実装のままだった)。
     新しいマーカーインタフェース `IUapNeverAutoApproveTool` を追加し、`AutoApprovePolicy` に
     `neverAutoApprove` 引数を持つ 6 引数オーバーロードを足して(既存の 5 引数版はそれへ `false` で
     委譲)、`AgentHub.TryAutoApproveUapOpsTool` が `ResolveEffectiveTool` した結果を
     `is IUapNeverAutoApproveTool` で判定して渡すようにした。`IUapTool` 自体にフラグを足さなかった
     理由は、既存の全実装クラスに触れる必要が出るため(オプトインのマーカーインタフェースなら不要)。
   - **Undo の正直な報告**。`Undo.GetCurrentGroup()` は「今のグループ番号」を返すだけで、
     `Undo.RecordObject` を呼んでも自動では変化しない(グループ番号は `IncrementCurrentGroup` を
     呼んだときだけ進む)ため、ブリーフの「group indices の比較」だけでは実際に記録されたかを
     判定できない。`UnityCsReference` の `Editor/Mono/Undo/Undo.bindings.cs` を直接確認したところ、
     `internal static void GetRecords(List<string>, out int)` という非公開メソッドが唯一「今の
     アンドゥ履歴の一覧」を返せる手段だった。実行前後でこのリストの件数を比較する形にし、
     リフレクション経由の呼び出しなので失敗時は `null`(=「わからない」)を返し、返信では
     「わからない」と正直に書く(嘘の true/false を返さない)。

## 1. スニペットの包み方とハッシュ

`namespace UapSnippets { public static class S_<hash> { public static object Run() { <code>\nreturn null;}}}`
という最小限の包み方にした。**`using` 群は何本あっても常に 1 行、namespace/class/method の開始も
常に 1 行** にまとめることで、「ユーザーコードの手前に何行あるか」(`PreambleLineCount`)が
**usings の本数に関係なく常に定数 2** になるようにした。これによりコンパイルエラーの行番号も、
実行時例外のスタックトレース行番号も、キャッシュ命中時(その回の `usings` を知らない)でも
同じ定数 1 つで逆算できる。

ハッシュは「usings(既定 5 つ + 検証済み・重複排除・ソート済みのカスタム分)を 1 行ずつ + 区切り行 +
`code` 生テキスト」を SHA-256 した 64 桁の16進数。usings の並び順に依存しないことをテストで固定した
(同じ集合なら順序が違っても同じハッシュ)。

**`usings` の値は素朴な "using X;" として直接ソースへ埋め込む**ため、`^[A-Za-z_][A-Za-z0-9_]*
(\.[A-Za-z_][A-Za-z0-9_]*)*$` という「単純なドット区切り識別子のみ」の正規表現で検証した。これが
無いと `"System; }} public class Evil {{ ... "` のような値で `using` 文の外へ抜け出し、静的スキャンが
一切見ない任意コードを注入できてしまう(スキャンは `code` 引数だけを見るため)。

## 2. 静的スキャン

コメント・文字列リテラルを空白に置き換えてから(改行は保持、行番号を壊さない)トークン/正規表現で
判定する、という設計はブリーフの指示どおり。実装で特に注意したのは:

- **補間文字列(`$"..."`)は「リテラル部分だけ」を消し、`{式}` の穴は残す**。
  `$"{Process.Start(x)}"` のように式の中に危険な呼び出しを隠す手を防ぐため。
  ネストした `{}` の深さを数えて穴の終わりを判定する専用のヘルパー(`StripInterpolatedBody`)を書いた。
  既知の限界: 穴の中の文字列リテラルが `}` を含む場合(`$"{Foo("}")}"` )に深さ計算を誤りうる
  (完全なパーサではない、という `ScriptGate` と同じ立場)。
- **絶対パス検出は行わなかった**。ロードマップは「absolute path outside project root when
  detectable」と書いているが、この Unity 非依存ファイルはプロジェクトルートを知らない。
  代わりに `File`/`Directory` の書き込み系メソッド呼び出しは**パスを問わず全部**フラグを立てる
  (これは絶対パス検出の安全な上位集合)。
- **無限ループの検出はコード全体での "break/return/throw の有無" という粗い判定**。
  `while(true)` を含むループの「その中に」break があるかを正確に見るには構文解析が要るため、
  コード全体のどこかに break/return/throw があれば抑制する、という保守的だが単純なルールにした
  (無関係な別ループの break でも抑制されるが、`ScriptGate` と同じ「完全なパーサではない」という
  立場を踏襲)。

## 3. 結果の形

`UnityEngine.Object` の判定・パス解決(シーンオブジェクトは `UapAddressing.DescribeHierarchyPath`、
アセットは `AssetDatabase.GetAssetPath`)は Unity 依存なので、`UapExecuteCodePolicy.ShapeReturnValue`
は `TryDescribeUnityObject` という delegate を引数に取る形にして、判定ロジック自体は Unity 非依存に
保った(`UapGameWatchPolicy.ClassifyValue` が Unity 型を構造で判定する設計と同じ「Unity 型に触れず
smoke で検証できるようにする」考え方)。

`IEnumerable` の返り値は `{"items":[...], "count":N, "truncated":bool}` というオブジェクトに包んだ
(ブリーフは「array capped at 200 items with truncated flag」とだけ書いていて、フラグの置き場所を
指定していなかったための設計判断)。**無限列挙可能なオブジェクトを返しても止まらないよう**、
`foreach` ではなく明示的な `IEnumerator` で「201 件目が取れた時点で即座に打ち切る」実装にした
(実装中に気づいて直した具体的なバグ: 最初の実装は `count++` を毎回行ってから 200 件で `continue`
するだけだったため、無限列挙に対しては永久に終わらなかった)。

## 4. テスト

`UapExecuteCodePolicyTests.cs`(純粋ロジック)、`UapExecuteCodeToolTests.cs`
(引数規則・`action:status`/`reset`・フラグ付きスニペットが確認無しでは実行されないこと -- 実際に
`System.IO.File.WriteAllText` で作るはずのマーカーファイルが存在しないことをアサート -- と、
`confirm:true` で実際にファイルが出来ること、そして `UapScriptsCommitToolTests` と同じ
`[UnityTest]` + `Poll()` ポーリングパターンで「`return 1 + 2;`」の実コンパイル+実行(`returnValue`
が 3 になること、2 回目は同じハッシュがキャッシュに命中して 1 回の `Poll` で終わること)、
コンパイルエラー時の行マッピング)、`UapCodeToolsMetadataTests.cs`(新規、"code" モジュールの
メタデータ)を書いた。

## 5. 検証済みシグネチャ

`UnityEditor.Compilation.AssemblyBuilder`(コンストラクタ、`scriptPaths`、`additionalReferences`、
`referencesOptions`、`compilerOptions`、`buildFinished` の `Action<string, CompilerMessage[]>`、
`Build()` の戻り値)、`UnityEditor.Compilation.CompilationPipeline.GetAssemblies(AssembliesType)`
(`Assembly.outputPath`/`compiledAssemblyReferences`)、`UnityEditor.Compilation.ScriptCompilerOptions.
CodeOptimization` -- いずれも `Unity-Technologies/UnityCsReference` の `2022.3` ブランチを直接 clone
して `Editor/Mono/Scripting/ScriptCompilation/AssemblyBuilder.cs` および同ディレクトリの
`CompilationPipeline.cs` を確認した。`6000.0` ブランチとの diff も確認: `AssemblyBuilder` クラスに
`[Obsolete("... Similar functionality can be achieved using the Roslyn compiler")]` が付いている
(警告のみ、コンパイルは通る)ことを確認した -- これは既存の `UapScriptsCommitTool` も同じ理由で
既に抱えている既知の状態で、このタスクが新たに持ち込むリスクではない。`UnityEditor.Undo` の
`IncrementCurrentGroup`/`GetCurrentGroup`/`SetCurrentGroupName`/`CollapseUndoOperations` は本パッケージ
既存の `UapTurnScope.cs` が同じ形で使用済み。`internal static void Undo.GetRecords(List<string>,
out int)` は `Editor/Mono/Undo/Undo.bindings.cs` を直接確認(非公開 API、リフレクション経由)。
`UnityEditor.EditorUtility.RequestScriptReload()` は `Editor/Mono/EditorUtility.bindings.cs` で確認。
`UnityEngine.Application.LogCallback`/`logMessageReceived` は `Runtime/Export/Application/
Application.cs` で確認。

`ci/SmokeTests` に一時的に `UapExecuteCodePolicy.cs` と検証用メソッドを組み込み、この環境に
`dotnet-install.sh` で .NET 8 SDK を導入した上で実際に `dotnet build` / `dotnet run` した:
ビルドは 0 警告 0 エラー、実行結果は既存分と合わせて「531 passed, 0 failed」(この機能追加前は
480 件前後)。確認後、`Program.cs` と `.csproj` は元の内容へ復元した(`git diff` で無変更を確認済み)
-- この 2 ファイルはこのタスクの「触ってはいけない共有ファイル」リストに入っているため、検証だけ
行い、実際の追記はこのノートの `smoke_check_code`/`smoke_compile_includes` を統合担当が適用する形にした。

## 6. 未検証(実機 Unity Editor が必要)

- `UapExecuteCodeTool.cs` 自体は Unity Editor が無いためこのセッションでは一度もコンパイルできて
  いない(Unity 依存の型を使うため net8 では検証不可)。ブレース対応・型名の曖昧性
  (`System.Reflection.Assembly` と `UnityEditor.Compilation.Assembly` の衝突)は手動で洗い出して
  修正し、`UapExecuteCodePolicy.cs` 側は実際にビルド+実行して確認したが、Tool 側は目視レビューのみ。
- `CompilationPipeline.GetAssemblies(AssembliesType.Player)` が実プロジェクトで実際に有効な
  `outputPath`(既にビルド済みのファイル)を返すかどうか(`File.Exists` ガードはあるが、実機未確認)。
- リフレクションによる `Undo.GetRecords` 呼び出しが実際の Editor で例外なく動くか、また
  スニペットが `Undo.RecordObject` を呼んだ場合に本当に件数が増えるとして観測できるか。
- コンパイルエラー時、Roslyn が実際にどの行番号を診断として報告するか(テストは厳密な行番号を
  ピン留めせず、`"(line 0,"` が出ないことだけを見る形に緩めた)。
- `ToolCardDescriber`/`PermissionCard` の新しい表示(モノスペースのコードブロック、usings 行、
  リスクフラグ一覧)は UI Toolkit のレイアウトとして実際にレンダリングして見ていない。
- Unity 6(6000.x)での `AssemblyBuilder` の Obsolete 警告が CI のビルド設定(警告をエラー扱いに
  していないか)に影響しないか。
- CI の `ci/HostProject` / `ci/HostProjectCoreOnly` どちらでも、新しい "code" モジュールの
  Editor/Ops ソースが追加パッケージ無しでそのままコンパイルできることは設計上保証しているつもりだが、
  実際に CI を走らせて確認してはいない。

## 7. レビューで直したこと(3 観点を順に実施)

- **セキュリティ**:
  - `uap_call` 経由で呼ばれたとき、権限カードが外側の封筒(`{name, arguments}`)を
    見ていたため、コード全文・usings・リスク表示がカードから消えていた。
    `ResolveEffectiveTool` の `effectiveInput` を使うように直し、テストを追加した。
  - 静的スキャンに「名前によるリフレクション」(`Type.GetType` /
    `Activator.CreateInstance` / `InvokeMember` / `BindingFlags`)の規則が無く、
    `Type.GetType("System"+".Diagnostics.Process")` のような呼び出しが確認なしで
    走っていた。規則を足した。
  - スキャンは目安であってサンドボックスではない。この点はツールの説明とカードに
    書いてある。
- **コンパイル / API**: 追加の修正は無し。`AssemblyBuilder` は 6000.0 で
  `[Obsolete]`(警告)だが、これは `uap_scripts_commit` も同じ。
- **ライフサイクル**:
  - 同じコードを同時に 2 回呼ぶと、同じ出力 DLL に 2 つのビルドが書き込む恐れがあった。
    ハッシュごとに実行中の件数を数え、2 本目は別ディレクトリでビルドするようにした。
    解放は完了時・例外時・SEC-7 の打ち切り時に行う。ただし CI(run 36406880492)で
    わかったとおり、実際の Editor では 1 本目の `AssemblyBuilder` が
    `EditorApplication.isCompiling` を立てる。そのため 2 本目は「コンパイル中」で断られ、
    この仕組みまで届くことはまず無い(防御として残す)。当初のテストは 2 本目も完走する
    前提で書かれていた。その断りで例外になり、1 本目のビルドを走らせたまま終わった結果、
    後続の入力ツールのテスト 5 件が「コンパイル中」の拒否で巻き添えに失敗した。テストは
    「断られるか走るか、どちらでも 1 本目は必ず最後まで回す」形に書き直した。
  - `IsValidUsing` は内部で trim してから照合していたため、末尾に空白のある値も
    通していた。検証は trim せず `\A..\z` で厳密に行い、trim は `BuildEffectiveUsings`
    側で先に済ませる形にした。
  - 破壊的ツールの一覧を固定するテスト(`UapDestructiveToolBaseTests`)が「code」
    モジュールを列挙していなかった。
- **統合時の判断**: `unsafe` はフラグを立てるが、スニペットは `/unsafe` 無しで
  コンパイルするので、`confirm:true` でも通らない。サンドボックスを広げる方向の
  変更は製品判断になるため、そうはしていない。フラグの文言で「確認してもコンパイル
  できない」と明示した。

