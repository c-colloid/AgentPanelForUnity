# 中継プログラムのビルドが Windows で必ず失敗する(参照にネイティブ DLL が混ざる)

日付: 2026-11-27
関連: `docs/design-notes/2026-11-25-cli-relay.md`(CLI 中継)、
`docs/design-notes/2026-09-22-uloop-phase1.md` §2.7(stdio 中継とそのビルド)

## 症状

Windows の Unity 2022.3.22f1 で「新しいセッション」を押すたびに Console へ次の行が出る。

```
[AgentPanel] [UapStdioRelayBuilder] CLI relay: reusing cached failure (csc exited 1:
warning CS2023: (文字化け)
error CS0009: (文字化け) '...\NetCoreRuntime\shared\Microsoft.NETCore.App\6.0.21\ucrtbase.dll' ...
error CS0009: (文字化け) '...\6.0.21\System.IO.Compression.Native.dll' ...
```

CLI 中継は作れず、パネルは CLI を直接起動する経路へ戻る(動作は続くが、ドメインリロードを
またいで CLI を生かす機能は効かない)。同じビルド手順を使う stdio 中継も同様に作れない。

## 根本原因(実測)

`UapStdioRelayBuilder.Compile` は共有フレームワークのフォルダを
`Directory.GetFiles(dir, "*.dll")` で列挙し、全部を csc の `/r:` に渡していた。

- macOS / Linux ではこのフォルダの `.dll` はすべてマネージドで、ネイティブは `.dylib` / `.so`
  なので問題が出ない。
- Windows ではランタイムのネイティブ DLL も同じフォルダに `.dll` で並ぶ。2022.3.22f1 同梱の
  6.0.21 では `coreclr.dll`、`clrjit.dll`、`hostpolicy.dll`、`ucrtbase.dll`、
  `api-ms-win-*.dll`、`System.IO.Compression.Native.dll`、`msquic.dll` など。
- csc はマネージドのメタデータを持たない PE を参照に渡されると CS0009 でエラーにする。

同じ応答ファイルを Unity 同梱の `dotnet.exe` + `DotNetSdkRoslyn/csc.dll` に手で渡して再現した。
CS0009 が 55 件、終了コード 1。マネージドだけ(165 個)に絞ると両方の中継が終了コード 0 で
ビルドできた。

既存の実コンパイルテスト(`CapturePaths_AndRealCompile_...`)は同梱ツールチェーンがある
エディタでしか走らず、Windows の 2022.3 で回されていなかったため検出できなかった。

### 付随して見つかったこと

- メッセージの文字化け: csc は既定でコンソールのコードページ(日本語 Windows では CP932)と
  UI 言語で出力するが、こちらは UTF-8 として読んでいた。
- `warning CS2023`: `/noconfig` は応答ファイルの中では無視される。

## 選択肢

1. **PE ヘッダを読んでマネージドだけを渡す(採用)。** DOS ヘッダ → PE シグネチャ →
   オプションヘッダのデータディレクトリ 14(CLI ヘッダ)の RVA とサイズが非ゼロならマネージド。
   名前に依存しないので、ランタイムの版が変わってネイティブ DLL が増減しても壊れない。
   Unity API を使わないのでビルドのワーカースレッドからそのまま呼べる。
2. 名前で除外する(`api-ms-win-*`、`*.Native.dll`、既知の名前の一覧)。一覧が版ごとに変わり、
   漏れた 1 個でまた全体が失敗する。却下。
3. `System.*.dll` と `Microsoft.*.dll` と `netstandard.dll` / `mscorlib.dll` だけを通す許可リスト。
   `System.IO.Compression.Native.dll` が `System.` で始まるため、結局は例外の名前を持つことになる。却下。
4. `Microsoft.NETCore.App.Ref` の参照アセンブリを使う。Unity 2022.3 には同梱されていない。却下。

## 実装

- `UapStdioRelayBuilder.IsManagedAssembly(path)`: 上記の PE 判定。読めないファイルや PE でない
  ファイルは false、例外は投げない。
- `UapStdioRelayBuilder.ManagedFrameworkReferences(dir)`: `*.dll` を列挙して上の判定で絞り、
  序数順に並べる。`Compile` はこれを `/r:` に使う。1 個も残らなければ
  「has no managed dlls」で失敗する。
- csc の引数: 応答ファイルに `/utf8output` と `/preferreduilang:en-US` を追加。`/noconfig` は
  応答ファイルから外し、コマンドラインに `-noconfig` として渡す。

失敗のキャッシュはメモリ上(ドメインごと)なので、パッケージを更新して再コンパイルが走れば
古い失敗は残らない。利用者側の後始末は不要。

## 回帰ガード

`UapStdioRelayBuilderTests` に 3 件追加。

- `IsManagedAssembly_TellsManagedFromNativeAndGarbage`: 合成した PE32+(CLI ヘッダあり / なし)、
  テキスト、空ファイル、存在しないパス、実在のマネージドアセンブリ。
- `ManagedFrameworkReferences_DropsNativeDllsFromTheReferenceList`: Windows の並び
  (マネージドとネイティブの混在)を偽のフォルダで作り、マネージドだけが残ること。
  OS を問わず走る。
- `ManagedFrameworkReferences_RealSharedFramework_HoldsCoreLibAndNoNativeDll`: 実際の同梱
  フレームワークで、`System.Private.CoreLib.dll` があり既知のネイティブ名が無いこと
  (ツールチェーンが無いエディタでは Ignore)。

## 検証

- Windows 11 / Unity 2022.3.22f1、`ci/HostProject` を複製したスクラッチプロジェクトのバッチ
  モードで `UapStdioRelayBuilderTests` 18 件が成功。実コンパイルテストで `relay.dll` が生成され、
  応答ファイルの `/r:` は 165 個。
- 生成された参照一覧で CLI 中継のソース(`CliRelayCore.cs` / `CliRelayMain.cs`)を同じ csc に
  渡し、終了コード 0 で `cli-relay.dll` 相当が出ることを確認。

## 未確認

- 実際のパネルで「リロードをまたいで CLI を維持」を ON にしたセッション開始(GUI)。
- Unity 6000.5.2f1(Windows)。この版は csc が `DotNetSdk/sdk/<版>/Roslyn/bincore/csc.dll` に
  あり `DotNetSdkRoslyn` が無いので、今回の修正とは別にツールチェーンの検出自体が
  「見つからない」になる見込み。今回は扱っていない。
