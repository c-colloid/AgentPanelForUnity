# AcpCommandProbe のテストが Windows で落ちる(区切り文字の期待値)

日付: 2026-09-17

## 問題

Windows(Unity 2022.3.22f1、batchmode のフルスイート)で EditMode テストが
2 件落ちる。保留中の変更とは無関係で、Linux の CI では通っている。

- `AgentBackendsTests.Probe_BareName_ScansPathWithPlatformExtensions`
  -- Expected `/usr/bin/gemini` But was `/usr/bin\gemini`
- `AgentBackendsTests.Probe_BareName_AlsoChecksVendorInstallDirectories`
  -- `/home/u/.grok/bin/grok` を期待、実際は `/usr/bin\grok`、
  `/home/u\.grok\bin\grok` ...

## 根本原因

`AcpCommandProbe` は候補パスを `Path.Combine` で組み立てる。`Path.Combine` の
区切りは**ホスト OS** のもので、テスト用コンストラクタの `isWindows` とは
無関係。テストの POSIX 側(`isWindows: false` に `/usr/bin` などを渡す部分)は
期待値を `/` 固定の文字列リテラルで書いていたので、Windows ホストでは
`\` で連結された実際の値と一致しない。同じテストの Windows 側
(`isWindows: true`)は最初から期待値を `Path.Combine` で作っており、だから
Linux の CI で通っていた。つまり片側だけホスト非依存に書けていなかった。

## 選択肢

1. **プローブに区切り文字を注入する**(`isWindows` に合わせて `/` か `\` で
   自前連結)。テストの見かけ上の「OS シミュレーション」は完全になる。
   しかし `isWindows` はテスト用の継ぎ目で、製品コードの公開コンストラクタは
   常にホスト OS と一致する値を渡す。製品の挙動は何も変わらないのに、
   `Path.Combine` がやってくれるルート付きパスや末尾区切りの扱いを自前で
   持つことになり、製品コードの変更(=リリース)も必要になる。
2. **テストの期待値を `Path.Combine` で作る**(採用)。プローブが守るべき契約は
   「PATH の順 → ベンダーのインストール先の順に、ディレクトリとコマンド名を
   ホストのやり方で連結する」であって、区切り文字そのものではない。
   同じテストの Windows 側と書き方が揃い、製品コードは触らない。

## 対応

`Tests/Editor/AgentBackendsTests.cs` の POSIX 側の期待値 6 か所を
`System.IO.Path.Combine(...)` に置き換えた。順序の検証(`candidates[0]` が
PATH の先頭、`[2]` が `/usr/local/bin`)はそのまま残るので、検証の強さは
変わらない。Linux では `Path.Combine("/usr/bin", "gemini")` は従来の
リテラルと同じ `/usr/bin/gemini` になる。

## 回帰ガード

この 2 テスト自体がガード。期待値をホスト非依存にしたので、Windows の
フルスイートと Linux の CI の両方で同じアサーションが効く。今後
`AcpCommandProbe` のテストを足すときは、連結結果の期待値をリテラルで
書かず `Path.Combine` で作ること(テスト内コメントにも記載)。

## リリース

テストのみの変更で、パッケージ利用者に見える変更はない。CHANGELOG の追記も
リリースも不要。
