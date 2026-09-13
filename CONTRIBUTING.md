# Contributing / 開発者向け情報

*English summary at the end.*

> **公開リポジトリ([c-colloid/AgentPanelForUnity](https://github.com/c-colloid/AgentPanelForUnity))で読んでいる方へ**
> 公開版には Core パッケージ(`jp.colloid.unity-agent-panel/`)と Core に関するドキュメントだけが
> 含まれます。本書のうち別売パッケージ Agent Panel Pro(`jp.colloid.agent-panel-pro/`)、
> `ci/`、`.github/workflows/`、`docs/research/`、`docs/verify/` に触れる箇所は開発用モノレポの
> 説明で、公開版には該当ファイルがありません。Core だけで開発・テストする手順(サンドボックス
> プロジェクトへのリンク、Test Runner)はそのまま使えます。Issue / Pull Request は公開
> リポジトリで受け付けています。

## リポジトリ構成

```
├── jp.colloid.unity-agent-panel/   # Core: UPM パッケージ本体(Editor 専用・依存ゼロ・MIT)
├── jp.colloid.agent-panel-pro/     # Pro: 別売の追加ツール(プロプライエタリ。公開版には無い)
│   ├── Editor/Core/                # プロセス管理・stream-json プロトコル・JSON(Unity 非依存)
│   ├── Editor/Model/               # セッション/メッセージのデータモデル・永続化
│   ├── Editor/UI/                  # UI Toolkit ビュー・テーマ
│   ├── Editor/Integration/         # ドメインリロード対応・Unity 連携(AgentHub)
│   ├── Editor/Ops/                 # UapOps: 内蔵 MCP サーバと Unity 操作ツール
│   ├── Editor/Boot/                # メニュー項目・初期化
│   ├── Tests/Editor/               # EditMode テスト + 実測プロトコルフィクスチャ
│   ├── Documentation~/             # Package Manager 用ドキュメント
│   └── CHANGELOG.md                # 変更履歴(Keep a Changelog・英語)
├── ci/
│   ├── HostProject/                # EditMode テストを CI で回すための最小ホストプロジェクト
│   ├── SmokeTests/                 # Unity 不要の .NET スモークテスト
│   └── FakeCli/                    # ドメインリロード E2E テスト用の CLI スタンドイン
├── docs/                           # 設計・調査・検証ドキュメント(索引: docs/README.md)
└── .github/workflows/              # CI(smoke / EditMode / タグ付け)
```

## 開発の流れ

本リポジトリは Unity **パッケージ**であり、単体では Unity プロジェクトとして開けません。
変更は、検証専用の使い捨て Unity プロジェクト(サンドボックス)の `Packages/` 直下に
`jp.colloid.unity-agent-panel` をジャンクション/シンボリックリンクで配置して確認します
(Windows: `mklink /J <サンドボックス>\Packages\jp.colloid.unity-agent-panel <clone先>\jp.colloid.unity-agent-panel`)。
開発中の実プロジェクトに直接リンクすることは推奨しません。

### コンパイル確認と EditMode テスト(バッチモード)

```
Unity.exe -batchmode -quit -projectPath <サンドボックス> -logFile <ログ>
Unity.exe -batchmode -runTests -testPlatform EditMode -testResults <結果xml> -projectPath <サンドボックス> -logFile <ログ>
```

- `-runTests` 使用時は `-quit` を付けません。
- 実 CLI を使う統合テスト(`LiveCli` カテゴリ、要ログイン)は既定でスキップされます。有効化するには `UAP_LIVE_CLI=1` を設定してください。
- ドメインリロード E2E テストは `UAP_FAKE_CLI=<ci/FakeCli/claude へのパス>` があるときだけ動きます(CI では常に有効)。
- Unity のバッチモード実行は数分かかることがあるため、十分なタイムアウトを設定してください。

エディタ内からは `Window > General > Test Runner`(EditMode)でも実行できます。

### Core 単体で確認する

公開ミラーの利用者は Core(`jp.colloid.unity-agent-panel`)だけを受け取るため、
Core は Pro(`jp.colloid.agent-panel-pro`)なしでコンパイルとテストが通らなければ
なりません。手元で確かめるには、サンドボックスの `Packages/manifest.json` から
Pro の行を消す(または Pro のリンクを外す)だけです。Pro は Core を参照する側なので、
Pro を外しても Core 側は壊れません(逆に Core だけを外すことはできません)。
Test Runner には `Colloid.AgentPanel.Editor.Tests` だけが並びます。

Pro のツールを書くときは、Core 側のコードやテストに Pro の型への参照が逆流していない
ことをこの状態で確認してください。CI も同じ確認を毎回行います(下記)。

### CI

- `dotnet-smoke.yml` — Unity 不要・ライセンス不要の高速ゲート。JSON コアやスクリプト検証ゲートなど Unity 非依存のソースを .NET でそのままコンパイルして検証します。
- `editmode-tests.yml` — GameCI の Editor イメージ上で EditMode スイートを 2 回実行します。1 回目は Core + Pro(`ci/HostProject`)、2 回目は Core だけ(`ci/HostProjectCoreOnly`、`ci/make-core-only-host.sh` が `ci/HostProject` から Pro を除いて組み立てる)。Core 単体でも通ることを毎回保証するためです。Unity のアカウント資格情報(Actions シークレット `UNITY_EMAIL` / `UNITY_PASSWORD`)が必要です。フォークでは設定するまで実行されません。`ci/HostProject/Packages/manifest.json` を変えたら `ci/HostProjectCoreOnly/Packages/manifest.json` も同じ変更(Pro の行を除く)を入れてください。ずれていると CI が止まります。
- `tag-release.yml` — `package.json` の version が main に入ったときに `vX.Y.Z` タグを自動で打ちます。

詳細は [ci/README.md](ci/README.md) を参照してください。

## コーディング規約

- Editor 専用・外部パッケージ依存ゼロを維持します(`Editor/Core/` は UnityEngine/UnityEditor にも依存させません)。
- CLI の stdin に書く JSON は必ず `OutboundMessages` + `JsonWriter` 経由の 1 行 JSON にします(不正な行 1 つで CLI が即死します)。
- 新しい `.cs` ファイルには対応する `.meta` ファイル(新規 GUID)を追加します。
- ユーザーに見える文字列は `L10n` を通して日英両方を用意します。
- 設計判断は `docs/design-notes/YYYY-MM-DD-<slug>.md`(日本語)に残し、CHANGELOG からリンクします。

## 変更履歴とリリース

- ユーザーに見える変更は `jp.colloid.unity-agent-panel/CHANGELOG.md` の `[Unreleased]` に英語で記録します(`### Added` / `### Changed` / `### Fixed`)。
- バージョン規約とリリース手順は [docs/RELEASING.md](docs/RELEASING.md) を参照してください。タグは CI が打つので手動で作らないでください。

## Pull Request

- 1 PR = 1 トピックを目安にしてください。
- CI の smoke テストが通ること、EditMode テストが手元のサンドボックスで全緑であることを PR に書いてください(フォークでは EditMode CI が動かないため)。
- 挙動の変更には CHANGELOG エントリと、必要に応じて設計ノートを添えてください。

---

## English summary

- **Reading this in the public repository (c-colloid/AgentPanelForUnity)?** It carries only the Core package (`jp.colloid.unity-agent-panel/`) and Core's docs; every mention of Agent Panel Pro (`jp.colloid.agent-panel-pro/`), `ci/`, `.github/workflows/`, `docs/research/` or `docs/verify/` describes the private development monorepo. The Core-only development loop (sandbox project + Test Runner) works as written.
- The repository is a UPM **package** (`jp.colloid.unity-agent-panel/`), not a Unity project. Develop by junction-linking the package folder into a throwaway sandbox project's `Packages/` and running the Editor in batch mode (`-batchmode -runTests -testPlatform EditMode ...`), or use `Window > General > Test Runner`.
- `LiveCli` tests need a logged-in CLI and `UAP_LIVE_CLI=1`; reload E2E tests need `UAP_FAKE_CLI=<path to ci/FakeCli/claude>`.
- CI: `dotnet-smoke.yml` (no Unity needed), `editmode-tests.yml` (needs `UNITY_EMAIL` / `UNITY_PASSWORD` secrets, so it does not run on forks until configured), `tag-release.yml` (tags automatically). See [ci/README.md](ci/README.md).
- Rules: Editor-only, zero package dependencies, `Editor/Core/` stays free of UnityEngine/UnityEditor, every line written to the CLI's stdin goes through `OutboundMessages` + `JsonWriter`, every new `.cs` gets a `.meta`, every user-visible string goes through `L10n` in both languages.
- Record user-visible changes under `[Unreleased]` in the package CHANGELOG (English, Keep a Changelog) and design decisions in `docs/design-notes/` (Japanese). Release process: [docs/RELEASING.md](docs/RELEASING.md); never create tags by hand.
