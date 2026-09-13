namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Japanese catalog factory (docs/design-notes/2026-08-01-i18n.md #2).
    /// This is the one file GlyphAuditTests' ASCII-source-byte scan excludes
    /// (see that test's own comment) -- it is the single legitimate home for
    /// non-ASCII UI text in this package, and every string here still flows
    /// through the same CJK-font + FE0F-strip display chokepoints
    /// (AgentPanelWindow.ApplyCjkUiFont / IconLoader.StripVariationSelectors)
    /// as any other text once rendered, so nothing here bypasses those
    /// safeguards -- only the raw-source-byte restriction is lifted for this
    /// file specifically.
    ///
    /// Every argument below is passed by name and there are no defaults on
    /// the constructor it calls (see UiStrings' internal constructor doc
    /// comment) -- a field added to UiStrings without a matching named
    /// argument here is a compile error, not a silently-still-English
    /// string shown to a user who picked Japanese.
    /// </summary>
    internal static class UiStringsJa
    {
        /// <summary>
        /// Japanese endonym for the language-picker's "Japanese" option --
        /// deliberately ALWAYS shown in Japanese script regardless of the
        /// active UI language (the same convention "English" follows for
        /// its own option), so the option is recognizable to a reader of
        /// either language. Lives here (not on UiStrings) because it is not
        /// itself translated per active language; it is a fixed proper
        /// noun, and this is the one source file GlyphAuditTests' ASCII
        /// scan excludes.
        /// </summary>
        public const string JapaneseLanguageDisplayName = "日本語";

        public static UiStrings Create()
        {
            return new UiStrings(
                firstRunCliNotFoundTitle: "Claude Code CLI が見つかりません",
                firstRunCliNotFoundBody: "Agent Panel は Claude Code CLI をサブプロセスとして実行します。インストールするか、既存の実行ファイルをパネルに指定してください。",
                firstRunBrowseButton: "参照...",
                firstRunRedetectButton: "再検出",
                firstRunLoginTitle: "Claude にサインイン",
                firstRunLoginLead: "Claude Code CLI はインストール済みですが、サインインしていません。「ログイン」を押すと CLI のサインインフローが始まります。設定の「アカウント」カードが開き、ブラウザ用リンクと確認コードの入力欄が表示され、完了するとパネルは自動的に再接続します。",
                firstRunLoginAltFoldout: "別の方法:ターミナルからサインイン",
                firstRunLoginAltBody: "ターミナルで下記のコマンドを実行し、その中で /login コマンドを使用してください。完了したらここで「再確認」を押してください。",
                firstRunLoginBody2: "Claude Pro/Max または Console アカウントでのサインインを推奨します。このエディタの環境に"
                    + " ANTHROPIC_API_KEY があればそれも使われ、課金がそのキーに切り替わります。どちらで接続しているかは"
                    + " 設定 > アカウント で確認できます。",
                firstRunCheckAgainButton: "再確認",
                firstRunBrowseDialogTitle: "claude 実行ファイルを選択",
                bannerContinueButton: "続行",
                bannerReconnectButton: "再接続",
                bannerErroredText: "エージェントのプロセスが終了しました。",
                bannerInterruptedText: "直前のターンはスクリプトの再読み込みにより中断されました。",
                bannerReconnectingText: "前回のセッションに再接続しています...",
                bannerResumedText: "セッションを再開しました。",
                permWaitingInWindowText: "権限確認を待っています - ウィンドウに表示中",
                permShowHereButton: "ここに表示",
                permShowHereTooltip: "フローティングウィンドウを閉じてインラインで回答します",
                permChevronTooltip: "リクエストの詳細を表示/非表示します",
                permOpenWindowTooltip: "このリクエストをフローティングウィンドウで開きます",
                permTitleFmt: "{agent} が {0} を使用しようとしています",
                permTitleWithDescriptionFmt: "{agent} が {0} を使用しようとしています - {1}",
                permInlineTitleWithDescriptionFmt: "{0} - {1}",
                permAllowButton: "許可 (Y)",
                permAlwaysButtonLabel: "常に許可",
                permAlwaysTooltip: "常に許可します(範囲を選択)",
                permQueueDepthFmt: "あと {0} 件待機中",
                permDenyButton: "拒否 (N)",
                permDenyFieldTooltip: "{agent} に送る拒否理由(任意)",
                permKeyboardHint: "このカードにフォーカスがある間、Y で許可、N で拒否します。拒否の前に上の欄に代替案を入力すると、{agent} に伝わります。",
                permSetModeSuggestionFmt: "権限モードを設定: {0} ({1})",
                permTruncatedMoreLinesFmt: "...(他 {0} 行)",
                permQuestionTitleSingle: "{agent} から質問があります",
                permQuestionTitlePlural: "{agent} から複数の質問があります",
                permQuestionTabFallbackFmt: "Q{0}",
                permOtherOptionLabel: "その他...",
                permOtherPlaceholder: "自由に入力",
                permOtherFieldCaption: "自由回答",
                permDenyFieldCaption: "代わりの指示があれば入力(任意)",
                permSubmitButton: "送信",
                permSkipButton: "スキップ",
                permSkipTooltip: "回答せずに続行します",
                permWindowTitle: "{agent} - 権限リクエスト",
                ctxAttachSelectionButton: "+ 選択中",
                ctxAttachSelectionTooltip: "現在の Hierarchy/Project の選択をコンテキストとして添付します",
                ctxAttachSceneButton: "+ シーン",
                ctxAttachSceneTooltip: "アクティブなシーンの要約をコンテキストとして添付します",
                ctxSceneAttachedButton: "シーン添付済み",
                ctxFixErrorsButton: "{agent} に修正を依頼",
                ctxRemoveTooltip: "削除",
                ctxSelectionChipTitleFmt: "選択: {0}",
                ctxSceneChipLabelFmt: "シーン: {0}",
                ctxFixErrorsPrompt: "添付されているコンテキストに記載されている Unity コンソールのエラーを修正してください。それぞれの原因を調査し、修正を適用してください。",
                ctxConsoleErrorTitleSingle: "コンソールエラー (1件)",
                ctxConsoleErrorTitlePluralFmt: "コンソールエラー ({0}件)",
                ctxDefaultChipLabel: "(コンテキスト)",
                ctxPingTooltip: "クリックして Unity 内でピングします",
                ctxErrorSingle: "コンソールエラー 1件",
                ctxErrorPluralFmt: "コンソールエラー {0}件",
                ctxErrorDismissForNowTooltip:
                    "これらのエラーを今回だけ非表示にします。新しいエラーの発生やリロード後には再表示されます。恒久的に無視するにはメニューを使ってください。",
                ctxErrorMenuTooltip: "これらのエラーに対するその他の操作",
                ctxErrorIgnoreForeverMenuItem: "このエラーを今後無視",
                ctxErrorIgnoredNoticeSingle: "エラー 1件を無視しました。",
                ctxErrorIgnoredNoticePluralFmt: "エラー {0}件を無視しました。",
                ctxErrorIgnoreUndoButton: "元に戻す",
                ctxGameObjectTitleFmt: "GameObject: {0}",
                ctxObjectTitleTypeFirstFmt: "{0}: {1}",
                ctxUnnamedFallback: "(名前なし)",
                toolCardDefaultName: "ツール",
                historyTitle: "履歴",
                historyRefreshTooltip: "セッション一覧を更新します",
                historyEmptyMessage: "このプロジェクトの保存済みセッションはまだありません。",
                historyNoPreview: "(プレビューはありません)",
                historyCurrentBadge: "現在",
                historySwitchConfirmText: "このセッションに切り替えますか? 現在の会話は一時停止されます。",
                historyCancelButton: "キャンセル",
                historySwitchButton: "切り替え",
                historyRelativeJustNow: "たった今",
                historyRelativeOneMinuteAgo: "1分前",
                historyRelativeMinutesAgoFmt: "{0}分前",
                historyRelativeOneHourAgo: "1時間前",
                historyRelativeHoursAgoFmt: "{0}時間前",
                historyRelativeOneDayAgo: "1日前",
                historyRelativeDaysAgoFmt: "{0}日前",
                historyRelativeOneMonthAgo: "1か月前",
                historyRelativeMonthsAgoFmt: "{0}か月前",
                historyRelativeOneYearAgo: "1年前",
                historyRelativeYearsAgoFmt: "{0}年前",
                historySizeBytesFmt: "{0} B",
                historySizeKbFmt: "{0} KB",
                historySizeMbFmt: "{0} MB",
                historySearchPlaceholder: "セッションを検索",
                historySearchNoMatch: "検索条件に一致するセッションはありません。",
                historyGroupByLabel: "グループ",
                historyGroupByDate: "日付",
                historyGroupByScene: "シーン",
                historyGroupByProject: "プロジェクト",
                historyGroupByCustom: "カスタムグループ",
                historyShowArchivedLabel: "アーカイブを表示",
                historyGroupPinned: "ピン留め",
                historyGroupToday: "今日",
                historyGroupYesterday: "昨日",
                historyGroupLast7: "過去7日間",
                historyGroupLast30: "過去30日間",
                historyGroupOlder: "それ以前",
                historyGroupNoScene: "シーン未記録",
                historyGroupNoProject: "プロジェクト不明",
                historyGroupUngrouped: "未分類",
                historyShowMoreFmt: "さらに{0}件表示",
                historyMenuTooltip: "セッション操作",
                historyActionPin: "ピン留め",
                historyActionUnpin: "ピン留めを解除",
                historyActionRename: "名前を変更...",
                historyActionArchive: "アーカイブ",
                historyActionUnarchive: "アーカイブを解除",
                historyActionDelete: "削除...",
                historyActionGroupSubmenu: "グループへ移動",
                historyActionGroupNone: "(なし)",
                historyActionGroupNew: "新規グループ...",
                historyMenuGroupBack: "戻る",
                historyDeleteDisabledForeignTooltip:
                    "このトランスクリプトは別プロジェクトのものです。そのプロジェクトのパネルから削除してください。",
                historyActionOpen: "開く",
                historyOpenDisabledForeignTooltip:
                    "このセッションは別プロジェクトのものです。そのプロジェクトのパネルから開いてください。",
                historyDeleteDisabledLiveTooltip:
                    "この会話は現在実行中です。停止するか別の会話に切り替えてから削除してください。",
                historyRenamePlaceholder: "会話タイトル",
                historyRenameSaveButton: "保存",
                historyRenameResetButton: "既定に戻す",
                historyDeleteConfirmTextFmt:
                    "このセッションを削除しますか？ トランスクリプトは {0} へ退避され、手動で復元できます。",
                historyDeleteButton: "削除",
                historyDeleteFailed:
                    "トランスクリプトを退避できませんでした。他のプログラムが開いている可能性があります。",
                historyNewGroupPlaceholder: "グループ名",
                historyNewGroupCreate: "作成",
                historyForeignProjectNote:
                    "このセッションは別のUnityプロジェクトのものです。ここでは再開できません。",
                headerDefaultTitle: "新規セッション",
                statusModelUnknown: "-",
                headerModelPickerTooltip: "モデルを切り替えます",
                headerHistoryButton: "履歴",
                headerHistoryTooltip: "セッション履歴",
                headerCompactTooltip: "モデルと自動承認レベル",
                headerCompactModelItemFmt: "モデル: {0}...",
                headerCompactAutoApproveItemFmt: "自動承認: {0}...",
                headerNewSessionTooltip: "新しいセッションを開始します",
                headerSettingsTooltip: "設定",
                headerReconnectTooltip: "{agent} プロセスが終了しました -- クリックして再接続します",
                headerModelPickerConnectFirst: "接続後にモデルを選択できます",
                headerModelOptionWithDescriptionFmt: "{0} ({1})",
                autoApproveLevelAsk: "毎回確認する",
                autoApproveLevelReadOnly: "読み取りのみ自動",
                autoApproveLevelUndoable: "元に戻せる操作まで自動",
                autoApproveLevelAllUnityOps: "Unity操作は全部自動",
                autoApproveLevelAllTools: "全ツール自動(Bash・編集・MCP)",
                autoApproveShortAllTools: "全ツール",
                autoApproveConfirmAllToolsTitle: "すべてのツールを自動承認しますか？",
                autoApproveConfirmAllToolsBody:
                    "シェルコマンド、ファイル編集、Web アクセス、Unity 操作、他の MCP サーバなど、すべての許可要求が"
                    + "確認なしで承認されます。エージェントからの質問(AskUserQuestion)だけは引き続き回答を待ちます。"
                    + "スクリプト検証ゲートによる .cs 直接書込のブロックは有効なままです。"
                    + "設定は即時に反映され、待機中の要求にも適用されます。",
                autoApproveShortAsk: "確認",
                autoApproveShortReadOnly: "読取",
                autoApproveShortUndoable: "戻せる",
                autoApproveShortAllUnityOps: "全操作",
                autoApproveMenuTitle: "Unity操作の自動承認",
                headerAutoApproveTooltipFmt:
                    "自動承認: {0}。クリックで変更でき、待機中の許可申請にも即座に適用されます。"
                    + "シェルコマンド・ファイル編集・uloopはどのレベルでも必ず確認します。",
                autoApproveConfirmAllTitle: "Unity操作をすべて自動承認しますか？",
                autoApproveConfirmAllBody:
                    "エージェントが要求するすべてのUnity操作が確認なしで実行されます。"
                    + "Ctrl+Zで取り消せない操作も含まれます。ターン終了時の警告は事後通知となり、"
                    + "事前に止める機会はありません。待機中の許可申請にも即座に適用されます。",
                autoApproveConfirmAllConfirmButton: "すべて自動承認する",
                autoApproveConfirmAllCancelButton: "キャンセル",
                hubAutoContinuePendingWillContinue:
                    "このターンでステージ済みスクリプトをコミットしました。まもなくUnityがコンパイルして"
                    + "ドメインリロードします。完了後、パネルがコンパイル結果を添えた継続ターンを1回だけ"
                    + "自動送信します。",
                hubAutoContinuePendingOff:
                    "このターンでステージ済みスクリプトをコミットしました。まもなくUnityがコンパイルして"
                    + "ドメインリロードします。パネルは自動的に再接続しますが、設定の「コンパイル後に"
                    + "自動継続する」がOFFのため、完了後は再度指示を送ってください。",
                hubAutoContinuePendingAlreadyContinued:
                    "このターンでステージ済みスクリプトをコミットしました。まもなくUnityがコンパイルして"
                    + "ドメインリロードします。パネルは自動的に再接続しますが、このターン自体が自動継続"
                    + "だったため、続けて2回目の自動継続は行いません。完了後は再度指示を送ってください。",
                hubAutoContinueResuming:
                    "自動継続: スクリプトコミットによるコンパイルとリロードの後、「続きを実行」を送信しました。",
                hubCompactedManualFmt:
                    "/compact でコンテキストを圧縮しました(圧縮前 {0} トークン)。ここより上の会話は、モデルにとっては要約になっています。",
                hubCompactedManual:
                    "/compact でコンテキストを圧縮しました。ここより上の会話は、モデルにとっては要約になっています。",
                hubCompactedAutoFmt:
                    "コンテキストウィンドウの残りが少なくなったため、CLI が自動的に圧縮しました(圧縮前 {0} トークン)。"
                    + "ここより上の会話は、モデルにとっては要約になっています。",
                hubCompactedAuto:
                    "コンテキストウィンドウの残りが少なくなったため、CLI が自動的に圧縮しました。"
                    + "ここより上の会話は、モデルにとっては要約になっています。",
                hubAutoContinueSendAbandonedFmt:
                    "自動継続: 待機していた継続メッセージを送信できず({0})、破棄しました。"
                    + "何も送信されていません。続きは手動で指示してください。",
                hubAutoContinueInterruptedResuming:
                    "自動継続: ドメインリロードで中断されたターンに「続きを実行」を送信しました。",
                hubReloadDroppedPermissionFmt:
                    "{0} の許可要求はドメインリロードで破棄されました。許可も拒否もされておらず、ツールは実行されていません。"
                    + "必要であればエージェントが改めて要求します。",
                hubSessionCacheUnreadable:
                    "保存済みの会話ログを読み込めなかったため(他のプログラムがファイルをロックしています)、"
                    + "空の会話を表示しています。失われたものはありません。ファイルを保護するため保存を一時停止しており、"
                    + "次のドメインリロードで会話ログは元に戻ります。会話自体にも影響はなく、エージェントは文脈をすべて保持しています。",
                settingsUloopSectionTitle: "uLoop連携",
                settingsUloopStatusInstalled: "導入済み",
                settingsUloopStatusMissing: "未導入",
                settingsUloopInstallButton: "uLoopを導入",
                settingsUloopInstallConfirmTitle: "uLoopを導入しますか？ 変更内容を確認してください。",
                settingsUloopInstallApply: "導入する",
                settingsUloopInstallCancel: "キャンセル",
                settingsUloopInstallViaGitFmt:
                    "Package Manager API でパッケージを追加します: {0}。manifest.json は手編集しません。",
                settingsUloopInstallViaManifest:
                    "manifest.json に OpenUPM の scoped registry を追加し、Package Manager にパッケージを要求します。",
                settingsUloopInstallPathsTooltipFmt:
                    "{0} を編集します。編集前のコピーを {1} に保存します。",
                settingsUloopInstallDiffFoldout: "変更内容を表示",
                settingsUloopInstallingFmt:
                    "導入中... {0}秒(Package Manager が OpenUPM から解決しています。実測でおよそ30秒)",
                settingsUloopInstallAsyncFailedFmt:
                    "Package Manager がパッケージを解決できませんでした: {0}。manifest.json のレジストリ項目は残っていても無害です。接続を確認するか、Package Manager ウィンドウから再試行してください。",
                settingsUloopInstallStalled:
                    "数分たっても解決しません。Package Manager ウィンドウで理由を確認してください。",
                settingsUnityPluginSectionTitle: "Unity 公式プラグイン",
                settingsUnityPluginStatusCliUnavailable: "Claude Code CLI が見つからないため、プラグインの状態は不明です。",
                settingsUnityPluginStatusNotInstalled: "未導入です。",
                settingsUnityPluginStatusDisabled: "導入済みですが無効化されています。ターミナルで有効化してください: claude plugin enable unity@unity-agent-plugin",
                settingsUnityPluginStatusInstalledFmt: "導入済み(v{0})。新規チャットごとに読み込まれ、スキルは /unity:... として現れます。",
                settingsUnityPluginStatusEnabledNotLoadedFmt: "導入済み(v{0})ですが、現在のチャットは読み込んでいません。新規チャットを開始してください。",
                settingsUnityPluginStatusLoadedFmt: "現在のチャットで読み込み済み(v{0})。スキルは /unity:... として現れます。",
                settingsUnityPluginStatusLoadErrorFmt: "読み込みに失敗しました: {0}",
                settingsUnityPluginInstallButton: "プラグインを導入",
                settingsUnityPluginInstallConfirmTitle: "Claude Code のユーザー設定に Unity 公式プラグインを追加します。",
                settingsUnityPluginCaveatScope: "ユーザースコープ(~/.claude)に導入されるため、他プロジェクトの Claude Code にも効きます。",
                settingsUnityPluginCaveatUnity6: "プラグインのスキルは Unity 6 以降が前提です。それより古い Unity では当てはまらない指針があります。",
                settingsUnityPluginInstallApply: "導入",
                settingsUnityPluginInstallCancel: "キャンセル",
                settingsUnityPluginInstallingFmt: "導入中... {0}秒(マーケットプレイス登録の後にプラグインを導入。実測は約 5 秒)",
                settingsUnityPluginInstalled: "導入しました。新規チャットから読み込まれます。現在のチャットには効きません。",
                settingsUnityPluginInstallFailedFmt: "{0}に失敗しました(終了コード {1})。{2}",
                settingsUnityPluginStageMarketplace: "マーケットプレイス登録",
                settingsUnityPluginStageInstall: "プラグイン導入",
                settingsUnityPluginInstallStalled: "数分経っても確認できません。ターミナルで確認してください: claude plugin list",
                settingsUnityPluginDetailsFoldout: "詳細",
                settingsUnityPluginDetailsBody: "提供元は Unity Technologies、ライセンスは Unity Companion License。本パッケージには同梱せず、Claude Code CLI が導入します。削除はターミナルで: claude plugin uninstall unity@unity-agent-plugin",
                settingsUnityPluginRepoLabel: "リポジトリ",
                settingsUnityPluginSteeringLabel: "Claude に公式スキルの使い分けを指示する",
                settingsUnityPluginSteeringTooltip: "システムプロンプトに短い注記を足します: UI・パッケージ・検索は /unity: スキルへ、Editor 操作はパネル自身のツールに留め、古い Unity では Unity 6 限定の指針を飛ばす。次のチャットから反映されます。",
                settingsUloopCaveatVcc:
                    "このプロジェクトは VCC/VPM 管理下です(vpm-manifest.json を検出)。"
                    + "ここでの変更は VCC に上書き・巻き戻される可能性があるため、VCC 側での導入を推奨します。",
                settingsUloopCaveatOffline:
                    "ネットワーク接続を検出できません。導入にはレジストリへの接続が必要です。",
                settingsUloopCaveatManifestUnreadable:
                    "manifest.json を読み取れないため、変更内容を安全に提示できません。",
                settingsUloopCaveatRegistryConflict:
                    "同名の scoped registry が異なる設定で既に存在します。"
                    + "上書きせず、手動で解決してください。",
                settingsUloopInstallFailed:
                    "導入を適用できませんでした。変更は行われていません(バックアップを取得していた場合は復元済み)。",
                settingsUloopInstallFailedManifestUnknownFmt:
                    "導入に失敗し、自動での巻き戻しにも失敗しました。Packages/manifest.json が"
                    + "不完全な状態の可能性があります — 確認するまで Unity にパッケージを解決させないでください。"
                    + "元のファイルの控えは {0} にあります。詳細: {1}",
                settingsUloopPresetButton: "uloopコマンドを許可",
                settingsUloopPresetHelp:
                    "compile / get-logs / list / run-tests だけを確認なしで許可します。それ以外は従来どおり確認します。",
                settingsUloopPresetTooltip:
                    "安全な uloop サブコマンドだけを短い許可リストとして追加します。そこに無いものは従来どおり確認を求めます — エディタ内で任意の C# をコンパイルして実行する execute-dynamic-code も含みます。過去にこのエディタをフリーズさせた update / sync / launch は加えて明示的に不許可にし、許可ツールに既にある広い Bash(uloop *) は取り除きます。",
                settingsUloopPresetApplied: "プリセットを許可ツールに追加しました。",
                settingsUloopSnippetButton: "指示スニペットを挿入",
                settingsUloopSnippetHelp:
                    "uloop execute-dynamic-code よりこのパネルの Unity ツールを優先させる指示を追記します。",
                settingsUloopSnippetTooltip:
                    "カスタム指示に短い指示文を追記します。まずこのパネル独自の Unity ツールを使い、それで表現できない操作にだけ uloop execute-dynamic-code を使い、コードを書いてコンパイルするのは最後の手段とする、という内容です。",
                settingsUloopSnippetApplied: "スニペットをカスタム指示に追記しました。",
                settingsAutoContinueLabel: "コンパイル後に自動継続する",
                settingsAutoContinueHelp:
                    "既定 OFF。エージェント自身のスクリプト変更でリロードが起きたとき、継続ターンを1回だけ自動送信します。会話ログには必ず明示されます。",
                settingsAutoContinueTooltip:
                    "エージェント自身のスクリプト変更でコンパイルとドメインリロードが発生した場合に、コンパイル結果を添えた継続ターンを1回だけ送り、作業がそこで止まらないようにします。1ターンにつき最大1回、エージェント自身の .cs/.asmdef 変更に起因する場合のみです。無通知の裏ターンは行わず、必ず会話ログに残ります。",
                settingsAutoContinueInterruptedLabel: "中断後に自動継続する",
                settingsAutoContinueInterruptedHelp:
                    "既定 OFF。リロードやPlayモードでターンが中断されたとき、「続きを実行」を自動送信します。",
                settingsAutoContinueInterruptedTooltip:
                    "エージェント起因ではないドメインリロード(スクリプトを保存してエディタに戻った、手動で再コンパイルした、Playモードに入った等)で実行中のターンが中断された場合、パネルはセッションを再接続したうえで、バナーの [続きを実行] ボタンと同じメッセージをクリックを待たずに送信します。ターンが完了しないまま連続で送るのは最大3回まで、接続が停止中は送らず、必ず会話ログに残ります。",
                settingsPlayModeReloadOnHint:
                    "このプロジェクトは Play 開始時にドメインをリロードするため、Play を押すと実行中のターンが中断されます。",
                settingsPlayModeReloadOffHint:
                    "このプロジェクトは Play 開始時にドメインを保持する設定(Reload Domain オフ)のため、Play でターンは中断されません。",
                settingsPlayModeReloadTooltip:
                    "Project Settings > Editor > Enter Play Mode Settings の現在値です。\"Reload Domain\" がオン(Unity の既定)だと Play 開始時にドメインリロードが起き、パネルは CLI を kill して再開するため実行中のターンが中断されます(Play 終了時はリロードしません)。Reload Domain をオフにすると Play をまたいでターンが継続しますが、static 状態がリセットされないことにプロジェクトが耐えられる場合に限ります。スクリプトの再コンパイルによるリロードはこの設定に関係なく起きます。パネルがこの設定を勝手に変えることはありません。",
                settingsPlayModeOpenProjectSettingsButton: "Project Settings > Editor を開く",
                settingsAutoApproveLabel: "自動承認レベル",
                settingsAutoApproveMovedHint:
                    "自動承認レベルは「会話」セクションの権限モードの隣で設定します。",
                settingsAutoApproveMovedTooltip:
                    "このパネル独自の Unity ツールをどこまで確認なしで実行するかを決める設定のため、他の権限関連の設定と同じ場所に置いています。",
                settingsAutoApproveHelp:
                    "「全ツール自動」未満はこのパネル独自の Unity ツールのみが対象。「全ツール自動」は質問以外をすべて承認します。",
                settingsAutoApproveTooltip:
                    "確認 / 読み取りのみ / 元に戻せる操作 / Unity操作は全部 の 4 段階はこのパネル独自の Unity ツールだけが対象で、シェル・ファイル編集・他の MCP サーバはカードが出ます。「全ツール自動」はエージェントからの質問(AskUserQuestion)以外のすべての許可要求を承認します。スクリプト検証ゲートと、Undo で戻せない操作を実行した際のターン末の警告は、最も緩いレベルを含めどのレベルでも有効なままです。",
                toolCardSectionInput: "入力",
                toolCardSectionResult: "結果",
                toolCardSectionError: "エラー",
                toolCardSectionChanges: "変更内容",
                toolCardMoreLinesFmt: "… あと {0} 行(コピーで全文)",
                toolCardMoreCharsFmt: "… あと {0} 文字(コピーで全文)",
                toolCardDurationSecondsFmt: "{0}秒",
                toolGroupCountFmt: "ツール {0}件",
                toolGroupCountWithDurationFmt: "ツール {0}件 ({1})",
                chatRoleUser: "あなた",
                chatRoleAssistant: "{agent}",
                chatDefaultAttachTitle: "添付コンテキスト",
                chatThinkingStreaming: "思考中...",
                chatThinkingDone: "思考",
                chatThinkingIndicatorStreaming: "思考中...",
                chatThinkingIndicatorStreamingTokensFmt: "思考中... (~{0}トークン)",
                chatThinkingIndicatorDone: "思考済み",
                chatThinkingIndicatorDoneTokensFmt: "思考済み (~{0}トークン)",
                chatPruneNoteFmt: "{0}件の過去のメッセージは非表示です",
                chatJumpToLatestButton: "最新",
                chatCompactingIndicator:
                    "コンテキストを圧縮しています... モデルがここまでの会話を要約中です。しばらく時間がかかることがあります。",
                statusDisconnected: "切断",
                statusCliNotFound: "CLI が見つかりません",
                statusConnecting: "接続中...",
                statusConnectingSlow: "接続中...(通常より時間がかかっています)",
                statusIdle: "待機中",
                statusResponding: "応答中...",
                statusCompacting: "コンテキスト圧縮中...",
                statusRunningTool: "ツール実行中...",
                statusWaitingPermission: "権限確認待ち",
                statusError: "エラー",
                statusApplyingSettings: "設定を適用しています...",
                statusContextPercentFmt: "{0}%",
                statusContextWarnTooltip: "コンテキストウィンドウの残りが少なくなっています。CLI がまもなく自動圧縮する可能性があります。",
                statusContextCompactedLabel: "圧縮済み",
                statusContextCompactedTooltip: "コンテキストを圧縮した直後です。次のターンが完了するとメーターは実際の値に戻ります。",
                statusUsageZeroTokens: "0トークン",
                statusUsageTokensFmt: "{0}トークン",
                statusUsageTokensWithCostFmt: "{0}トークン - ${1}",
                statusUsagePopoverTitle: "このセッションの使用状況",
                statusUsagePopoverEmpty: "まだ完了したターンがありません。",
                statusUsagePopoverBreakdownPending:
                    "合計はセッションキャッシュから復元済みです。モデル別の内訳は、次のターン完了後に表示されます。",
                statusUsagePopoverLineFmt: "入力 {0} - 出力 {1} - キャッシュ {2}",
                statusUsagePopoverCostSuffixFmt: "{0} - ${1}",
                statusUsagePopoverContextFmt: "コンテキスト: {1} 中 {0}%",
                emptyTitle: "Agent Panel for Unity",
                emptySubtitle: "このプロジェクトについて何でも {agent} に聞いてください。",
                emptySuggestionProject: "このプロジェクトの構成を説明して",
                emptySuggestionScene: "開いているシーンの構造を要約して",
                emptySuggestionErrors: "コンソールのエラーを修正して",
                emptySuggestionSelection: "選択中のオブジェクトを説明して",
                composerQuickButton: "クイック",
                composerQuickTooltip: "クイックアクションを挿入します",
                composerAttachButton: "+",
                composerAttachTooltip: "画像を添付: ファイル、または今の Scene/Game ビュー",
                composerImageOnlyDisplay: "(画像)",
                attachMenuImageFile: "画像ファイル...",
                attachMenuSceneView: "Scene ビュー",
                attachMenuGameView: "Game ビュー",
                attachMenuSceneViewWindow: "Scene ビュー(表示のまま)",
                attachImageRemoveTooltip: "この画像を外す",
                attachImageErrorTitle: "画像の添付",
                attachImageErrorOk: "OK",
                attachImageUnsupportedFmt: "{0}: 添付できるのは PNG と JPEG だけです。",
                attachImageMissingFmt: "ファイルが見つかりません: {0}",
                attachImageLoadFailedFmt: "{0} を画像として読めませんでした({1})。",
                attachImageTooLargeFmt: "{0} は符号化後 {1} MB で、上限 {2} MB を超えています。",
                attachImageTooManyFmt: "1 メッセージに添付できる画像は {0} 枚までです。",
                attachMenuClipboard: "クリップボードの画像を貼り付け",
                attachClipboardEmpty: "クリップボードに画像がありません。",
                attachClipboardFailedFmt: "クリップボードの画像を読み取れませんでした({0})。",
                chatImageOpenTooltip: "画像を開く",
                chatImageMissing: "(画像ファイルは既にありません)",
                chatImageDefaultCaption: "画像",
                composerSendButton: "送信",
                composerStopButton: "停止",
                composerNoQuickActionsMenuItem: "クイックアクションがまだありません(設定で追加してください)",
                composerQuickActionUntitled: "(無題)",
                composerPlaceholderCtrlEnter: "{agent} に聞く... (Ctrl+Enter で送信)",
                composerPlaceholderEnter: "{agent} に聞く... (Enter で送信、Shift+Enter で改行)",
                composerPlaceholderPermissionPending: "代わりの指示をここに書いて「拒否」を押してください...",
                composerHintCompiling: "Unity がコンパイル中です。メッセージはキューに入れられます。",
                composerHintQueuedFmt: "{0}件のメッセージがキューにあります",
                composerHintEscToStop: "Esc で停止",
                composerHintTurnSendAndStop: "Enter で実行中のターンに送信 / Esc で停止",
                composerSendTooltip: "メッセージを送信",
                composerStopTooltip: "現在の応答を停止",
                composerSlashHint: "↑↓ で選択、Tab で補完、Enter で送信",
                composerSlashNoMatch: "一致するコマンドがありません",
                slashCompactDescription: "ここまでの会話を要約してコンテキストを空けます(任意: 要約の指示)",
                slashClearDescription: "新しいチャットを開始します(「新規チャット」ボタンと同じ)",
                subagentDefaultType: "サブエージェント",
                subagentDefaultDescription: "サブエージェント",
                subagentDropNoteFmt: "{0}件の過去のステップは省略されています",
                subagentProgressToolFmt: "{0} ({1})",
                subagentProgressTokensFmt: "{0} -- {1}トークン",
                subagentProgressWorking: "実行中...",
                subagentProgressStepsFmt: "{0} -- {1}ステップ",
                subagentProgressUpdatedAgoFmt: "{0} -- {1}秒前に更新",
                settingsTitle: "設定",
                settingsGroupConversation: "エージェント",
                settingsGroupDisplay: "パネル",
                settingsGroupUnity: "Unity 連携",
                settingsGroupConnection: "接続とアカウント",
                settingsSectionCli: "CLI",
                settingsCliPathLabel: "実行ファイルのパス",
                settingsCliPathHint:
                    "空欄で自動検出します。",
                settingsCliPathTooltip:
                    "次回の再接続後に適用されます。すぐに適用するには下の「今すぐ再接続」を使ってください。",
                settingsRedetectButton: "再検出",
                settingsReconnectNowButton: "今すぐ再接続",
                settingsCliResolvedFmt: "解決結果: {0}",
                settingsCliNotFoundFmt: "見つかりません。確認したパス: {0}",
                settingsReconnectPendingHint: "一部の変更は次回の再接続後に適用されます。",
                settingsReconnectPendingPill: "保留中",
                settingsReconnectPendingHintDeferred: "現在のターンが終了すると自動的に適用されます。",
                settingsReconnectPendingPillDeferred: "ターン終了後に適用",
                settingsSectionConversation: "会話",
                settingsPermissionModeLabel: "権限モード",
                settingsPermissionModeTooltip:
                    "{agent} が接続中であればすぐに適用され、次回のセッションにも引き継がれます。",
                settingsCtrlEnterLabel: "Ctrl+Enter で送信",
                settingsCtrlEnterHint:
                    "オフの場合、Enter で送信、Shift+Enter で改行します(IME に配慮した既定動作)。",
                settingsCtrlEnterTooltip:
                    "すぐに適用されます。",
                settingsAllowedToolsLabel: "許可するツール",
                settingsAllowedToolsHint:
                    "1行に1つのツールを指定します(例: Bash(git:*))。空の場合は制限なしです。",
                settingsAllowedToolsTooltip:
                    "次回の再接続後に適用されます。",
                settingsDisallowedToolsLabel: "禁止するツール",
                settingsDisallowedToolsHint:
                    "1行に1つ指定します。",
                settingsDisallowedToolsTooltip:
                    "次回の再接続後に適用されます。",
                settingsDangerZoneTitle: "危険な設定",
                settingsDangerZoneWarning: "権限確認をスキップすると、{agent} はこのマシン上で確認なしに読み書き・実行を何でも行えるようになります -- ファイルの削除、任意のシェルコマンドの実行、ソフトウェアのインストールを含みます。失っても構わない使い捨てのサンドボックスプロジェクトでのみ有効にしてください。既定ではオフであり、ここで明示的にオンにしない限りオフのままです。",
                settingsDangerZoneToggle: "すべての権限確認をスキップ(危険)",
                settingsDangerZoneTooltip:
                    "次回の再接続後に適用されます。",
                settingsSectionCustomInstructions: "カスタム指示",
                settingsCustomInstructionsHint:
                    "毎ターン {agent} のシステムプロンプトに追記されます。",
                settingsCustomInstructionsTooltip:
                    "--append-system-prompt として渡されます。「常に日本語で返答する」のような、このプロジェクト固有の約束事を書く場所です。",
                settingsSectionDisplay: "表示",
                settingsShowThinkingLabel: "思考ブロックを表示",
                settingsShowThinkingTooltip:
                    "表示/非表示の切り替え自体はすぐに適用されます。空のブロックではなく実際の思考本文を表示するには CLI v2.1.218 以降が必要で、自動的な短い再接続を経て有効になります。",
                settingsExpandSubagentLabel: "サブエージェントカードを既定で展開",
                settingsExpandSubagentHint: "まだ手動で展開/折りたたみを行っていないサブエージェントカードにのみ影響します。",
                settingsShowCostLabel: "USD でコストを表示",
                settingsShowCostHint:
                    "OFF のときはトークン数のみ表示します。サブスクリプション認証では金額に意味がないため有用です。",
                settingsSectionQuickActions: "クイックアクション",
                settingsQuickActionsHint:
                    "再利用できるプロンプトの定型です。選んでも本文が入力欄に入るだけで、自動送信はしません。",
                settingsQuickActionsTooltip:
                    "空の状態の提案の先頭と、入力欄のクイックメニューに表示されます。選ぶとプロンプトが入力欄に挿入されるので、送信前に編集できます。",
                settingsAddQuickActionButton: "+ クイックアクションを追加",
                settingsQuickActionLabelTooltip: "ラベル",
                settingsQuickActionPromptTooltip: "プロンプト",
                settingsQuickActionRemoveButton: "削除",
                settingsQuickActionNewDefaultLabel: "新しいアクション",
                settingsSectionNotifications: "通知",
                settingsNotificationsHint: "ビープ音は Agent Panel ウィンドウがフォーカスされていないときのみ鳴ります。",
                settingsPermissionBeepLabel: "権限リクエスト時にビープ音を鳴らす",
                settingsTurnCompleteBeepLabel: "ターン完了時にビープ音を鳴らす",
                settingsSectionConsoleErrors: "コンソールエラー",
                settingsConsoleErrorsHint: "エラーチップの × で消したエラーは恒久的に無視されます。ここで管理できます。",
                settingsConsoleErrorsTooltip:
                    "Console がエラーを捕捉するとコンテキストバーにチップが表示されます。チップの × を押すと、"
                    + "そのとき表示中のエラー全件が（メッセージ完全一致で）このプロジェクトに記録され、以後は"
                    + "チップにも Claude への「修正」プロンプトにも含まれず、エディタを再起動しても隠れたままです。"
                    + "ここで削除するとすぐに再表示されるようになります。",
                settingsIgnoredErrorPatternsLabel: "無視パターン",
                settingsIgnoredErrorPatternsHint: "1行に1つ: その文字列を含むコンソールエラーを無視します。",
                settingsIgnoredErrorPatternsTooltip:
                    "単純な部分一致で、1行に1パターンです（正規表現は使えません）。毎回少しずつ文言が変わる"
                    + "SDK・拡張のエラーは × の完全一致では捕まえられないので、こちらを使ってください。"
                    + "編集は即座に反映されます。",
                settingsIgnoredErrorsClearAllButton: "無視リストを全解除",
                settingsIgnoredErrorRemoveButton: "解除",
                settingsSectionAppearance: "外観",
                settingsFontSizeLabel: "フォントサイズ",
                settingsFontSizeTooltip:
                    "開いているすべてのパネルウィンドウにすぐに適用されます。",
                settingsFontSizeValueFmt: "{0}px",
                settingsCjkToggleLabel: "CJK対応フォントを優先する",
                settingsCjkDiagnosticNone: "このマシンでは一致する CJK フォントが見つかりませんでした。このトグルの状態に関わらずエディタの既定フォントが使用されます。",
                settingsCjkDiagnosticDetectedFmt: "検出されたフォント: {0}",
                settingsCjkDiagnosticViaFontFixFmt: "検出されたフォント: {0}(UITK Font Fix 経由)",
                settingsSectionDiagnostics: "診断",
                settingsDiagnosticsHint: "最近の CLI の stderr 出力です(このエディタセッション限定。ディスクには保存されません)。",
                settingsCopyButton: "コピー",
                settingsClearButton: "クリア",
                settingsDiagnosticsEmpty: "(stderr 出力はまだありません)",
                settingsSectionAbout: "About",
                settingsPackageVersionFmt: "パッケージバージョン: {0}",
                settingsPackageVersionUnknown: "不明",
                settingsCliVersionFmt: "CLI バージョン: {0}",
                settingsCliVersionNotConnected: "未接続",
                settingsOpenChangelogButton: "CHANGELOG を開く",
                settingsOpenGitHubButton: "GitHub で開く",
                settingsLanguageLabel: "言語",
                settingsLanguageTooltip:
                    "すぐに適用され、開いているパネルウィンドウは再構築されます。",
                settingsLanguageOptionAuto: "自動",
                hubWindowTitle: "Agent Panel",
                hubPermissionPendingTooltip: "{agent} が権限の判断を待っています",
                navBackButtonLabel: "チャット",
                navBackButtonTooltip: "チャットに戻る",
                markdownCodeDefaultLang: "コード",
                markdownCodeCopyButton: "コピー",
                dragDropOverlayLabel: "ドロップして添付",
                ctxSelectionExtraSuffixFmt: " (他{0}件)",
                ctxSelectionSummaryHeaderSingularFmt: "エディタの選択(1個のオブジェクト):",
                ctxSelectionSummaryHeaderPluralFmt: "エディタの選択({0}個のオブジェクト):",
                ctxSelectionMoreObjectsFmt: "(他{0}個のオブジェクトは表示されていません)",
                ctxSelectionAssetFmt: "アセット: {0} ({1})",
                ctxSelectionGenericFmt: "{0} ({1})",
                ctxSceneUntitledFallback: "(無題)",
                ctxSelectionSceneLabelFmt: "シーン: {0}",
                ctxSelectionActiveLabelFmt: "アクティブ: {0}",
                ctxSelectionYes: "はい",
                ctxSelectionNo: "いいえ",
                ctxSelectionInactiveInHierarchy: " (Hierarchy内で非アクティブ)",
                ctxSelectionComponentsLabel: "コンポーネント: ",
                ctxSelectionMissingScript: "(スクリプトが見つかりません)",
                ctxSelectionMoreComponentsFmt: "、他{0}個",
                ctxSceneSummaryHeaderFmt: "アクティブシーン: {0}",
                ctxSceneSummaryPathFmt: " ({0})",
                ctxSceneLoadedLabelFmt: "読み込み済み: {0}",
                ctxSceneRootObjectsLabelFmt: "ルートオブジェクト: {0}",
                ctxSceneRootInactiveSuffix: " (非アクティブ)",
                ctxSceneMoreRootsFmt: "...他{0}個のルートオブジェクトがあります。",
                ctxErrorsDigestHeaderFmt: "Unity コンソールエラー({0}件、重複除く):",
                ctxErrorsDigestEntryFmt: "[{0}] {1}",
                ctxErrorsDigestOccurrencesFmt: " (x{0})",
                ctxErrorsDigestLocationFmt: "場所: {0}",
                ctxErrorsDigestMoreFmt: "...他{0}件のエラーがあります。",
                hubProcessDiedReconnectingFmt: "エージェントのプロセスが終了しました({0})。再接続しています...",
                hubProcessDiedSuspendedFmt: "エージェントのプロセスが繰り返し終了しました({0})。自動再接続を停止しました。CLI のログイン状態を確認し、「再接続」を押してください。",
                hubTurnStalledNote: "CLI から10分間応答がなかったため、このターンを終了しました。",
                hubCliErrorFmt: "CLI エラー: {0}",
                hubSyntheticResponse: "合成応答",
                hubCliNotFoundErrorFmt: "Claude CLI の実行ファイルが見つかりません。確認したパス: {0}。パネル設定で手動パスを指定してください。",
                hubCliStartFailedFmt: "Claude CLI の起動に失敗しました: {0}",
                hubPermissionDefaultToolName: "ツール",
                hubPermissionDeniedFmt: "{0} を拒否しました",
                settingsCliVersionUnconfirmedFmt: "CLI バージョン: {0}(未接続)",
                chatThinkingRedactedNote: "一部の思考は安全のため非表示です。",
                settingsSectionModel: "モデル",
                settingsDefaultModelLabel: "デフォルトモデル",
                settingsDefaultModelHint:
                    "ヘッダーの + で新しいセッションを始めるときに使われます。現在のセッションはヘッダーのピッカーで変更します。",
                settingsDefaultModelHintNoCatalog: "CLI が利用可能なモデルを報告できるよう、一度接続してください。それまでは「(デフォルト)」のみ選択できます。",
                settingsModelChoiceDefaultLabel: "(デフォルト)",
                settingsAgentOverridesHint:
                    "特定のサブエージェントにタイプ別の既定モデルを設定します。エージェント自身の明示的な選択が優先されます。",
                settingsAgentOverridesTooltip:
                    "general-purpose や Explore といった特定のサブエージェントに、タイプ別の既定モデルを設定します。エージェントが呼び出し単位で明示的に選んだモデルは、これらの設定より優先されます。「(デフォルト)」のままにした、あるいは表に無いエージェントは、上のデフォルトモデルを使います。",
                settingsAddAgentOverrideButton: "+ モデルの上書きを追加",
                settingsAgentOverrideNameTooltip: "エージェント名(例: general-purpose)。使用できるのは英数字・「-」・「_」のみです。",
                settingsAgentOverrideModelTooltip: "このエージェントのモデル",
                settingsAgentOverrideRemoveButton: "削除",
                settingsAgentOverrideDuplicateNameWarning: "同じエージェント名の行が複数あります。実際に適用されるのは最後の行だけです。重複を削除するかリネームしてください。",
                settingsAgentOverridesNewSessionHint: "変更は新しいセッションから有効です。ヘッダーの + から新規セッションを開始すると適用されます。",
                settingsSubagentModelLabel: "サブエージェントモデルを強制固定",
                settingsSubagentModelHint:
                    "全サブエージェントへの強制的なコスト上限です。下のタイプ別既定も、エージェント自身の選択も上書きします。",
                settingsSubagentModelTooltip:
                    "Task ツールで起動されるすべてのサブエージェント(カスタムタイプ含む)に対して、下のタイプ別既定と、エージェント自身の呼び出し単位の選択の両方を上書きします。既定値ではなく上限です。変更は数秒以内に、現在実行中のセッションにも反映されます。呼び出し単位/タイプ別の選択をそのまま使わせたい場合は「(デフォルトと同じ)」のままにしてください。",
                settingsSubagentModelSameAsDefaultLabel: "(デフォルトと同じ)",
                settingsAgentOverridesFoldoutTitle: "タイプ別の詳細設定(上級)",
                settingsSubagentPrecedenceWarning: "サブエージェントモデルが設定されているため、これらのタイプ別の上書きは(クリアするまで)効果がありません。",
                settingsAgentOverrideNamePlaceholder: "(エージェントを選択)",
                firstRunLoginButton: "ログイン",
                settingsSectionAccount: "アカウント",
                settingsAccountCheckingStatus: "サインイン状態を確認しています...",
                settingsAccountUnavailable: "サインイン状態を確認できません(CLI が見つかりません)。",
                settingsAccountNotLoggedIn: "ログインしていません。",
                settingsAccountLoggedInFmt: "{0} ({1}) としてログイン中です。",
                settingsAccountSubscriptionUnknown: "不明なプラン",
                settingsAccountLoginButton: "ログイン",
                settingsAccountSwitchButton: "アカウントを切り替え",
                settingsAccountLogoutButton: "ログアウト",
                settingsAccountLogoutConfirmTitle: "Claude からログアウトしますか?",
                settingsAccountLogoutConfirmBody: "現在の Claude Code セッションを停止し、CLI からサインアウトします。いつでも再度ログインできます。",
                settingsAccountLogoutConfirmButton: "ログアウト",
                settingsAccountLogoutCancelButton: "キャンセル",
                settingsAccountLoginStarting: "サインインを開始しています... まもなくブラウザが開きます。",
                settingsAccountLoginWaitingForCode: "ブラウザに表示された認証コードを下に貼り付けてください。",
                settingsAccountLoginVerifying: "コードを確認しています...",
                settingsAccountLoginFailedNotice: "ログインが完了しませんでした。コードが誤っているか期限切れの可能性があります。「ログイン」からやり直してください。",
                settingsAccountLoginUrlLabel: "サインインURL",
                settingsAccountOpenBrowserButton: "ブラウザで開く",
                settingsAccountCopyButton: "コピー",
                settingsAccountCodeLabel: "認証コード",
                settingsAccountSubmitCodeButton: "送信",
                settingsAccountCancelLoginButton: "キャンセル",
                settingsAccountLoggedInNoDetail: "ログイン済みです。",
                settingsAccountEnvTokenNote: "環境変数トークン(CLAUDE_CODE_OAUTH_TOKEN)による認証です。ここでのログイン/ログアウトは保存済みクレデンシャルを変更しますが、このトークンが設定されている間のセッションには影響しない場合があります。",
                settingsSubagentCostPolicyLabel: "サブエージェントのコスト方針",
                settingsSubagentCostPolicyHint:
                    "検索やファイル一覧のような単純なサブタスクは Haiku、複雑な作業はセッションのモデルへ誘導します。",
                settingsSubagentCostPolicyTooltip:
                    "Claude のシステムプロンプトに1行のガイダンスを追加します。検索・ファイル一覧・一括リネーム・ログ調査のような単純な機械的サブタスクには Haiku、複雑な作業にはセッションのモデル、という誘導です。強制ではなくガイダンスなので、タスクごとに Claude が判断して上書きできます。この CLI より前に作成されたセッションでは、タスク単位のモデル指定が無視される場合があります(ヘッダーの + で始めた新しいセッションでは確実に有効です)。",
                settingsSubagentCostPolicyOptionAgentDecides: "おまかせ(推奨)",
                settingsSubagentCostPolicyOptionHaikuForSimpleTasks: "コスト重視: 単純作業は Haiku",
                settingsDefaultModelChoiceResolvedFmt: "デフォルト(推奨: {0})",
                headerModelOptionDefaultSuffixFmt: "{0}(既定)",
                settingsSectionUapOps: "Unity操作(UapOps)",
                settingsUapOpsHint:
                    "小さなローカル MCP サーバーを動かし、C# を書かずに型付きツールで Unity を操作できるようにします。",
                settingsUapOpsTooltip:
                    "サーバーはこのエディタ内で動き、ループバック限定・セッションごとのトークンで保護されています。オブジェクト作成やプロパティ設定(今後のバージョンでさらに追加予定)といった型付きの Unity 操作を、C# を書いてコンパイルすることなく行えるようにします。",
                settingsUapOpsEnabledLabel: "Unity操作(UapOps)を有効化",
                settingsUapOpsEnabledTooltip:
                    "次回の再接続後に適用されます。",
                settingsUapOpsModuleCoreLabel: "コア",
                settingsUapOpsModuleCoreHint: "シーン/コンポーネント/プロパティ/アセット操作ツール一式 + スクリプト検証ゲートのコミットツール。",
                settingsUapOpsStatusRunningFmt: "ポート{0}で実行中",
                settingsUapOpsStatusStopped: "停止中",
                settingsUapOpsStatusDisabled: "無効",
                hubScriptGateAutoDeniedFmt: "スクリプトの直接書き込みをブロックしました: {0}(スクリプト検証ゲート) -- UapStaging/(プロジェクトルート、Assets外)配下へのステージングと uap_scripts_commit の使用が必要です。",
                settingsUapOpsGateEnabledLabel: "ステージング済みスクリプトを要求(検証ゲート)",
                settingsUapOpsGateEnabledHint:
                    "Claude は Assets/**/*.cs を直接編集できなくなり、ステージングしてコンパイル検証を通してから反映します。推奨: ON。",
                settingsUapOpsGateEnabledTooltip:
                    "ゲートが有効な間、Claude 自身の Write/Edit/MultiEdit は Assets/**/*.cs と *.asmdef に触れません。ステージングフォルダに書き込んでから uap_scripts_commit を呼ぶ必要があり、commit は Assets へ反映する前にコンパイル検証を行います。生成された PreToolUse フックが強制するため、acceptEdits の権限モード下でも有効です(既存の権限カードのチェックに加えて)。",
                settingsUapOpsStagingFolderHintFmt:
                    "ステージングフォルダ: {0}(プロジェクトルート、Assets の外)。",
                settingsUapOpsStagingFolderTooltip:
                    "Assets の外にあるため、Unity のアセットインポートの対象になりません。uap_scripts_commit がここから読み取り、コンパイルが通った場合にだけ Assets へ移動します。",
                permUndoNotSupportedBadge: "Undo対象外",
                permUndoNotSupportedTooltip:
                    "この操作は Unity の Undo（Ctrl+Z）では元に戻せません。許可する前に内容をよく確認してください。",
                permDiffShowAllFmt: "全{0}行を表示",
                hubTurnNonUndoableWarningFmt: "このターンにはCtrl+Zで元に戻せない操作が含まれています: {0}。変更内容を確認してから続行してください。",
                subagentNoDetails: "このサブエージェントの詳細記録はありません。",
                settingsUapOpsModulePrefabLabel: "プレハブ",
                settingsUapOpsModulePrefabHint: "プレハブの作成/オーバーライドの適用・巻き戻しツール一式。",
                settingsUapOpsModuleEditorLabel: "エディタ",
                settingsUapOpsModuleEditorHint: "スクリーンショット撮影(Scene/Gameビュー) + 任意のエディタメニューコマンド実行。",
                settingsUapOpsModuleAnimLabel: "アニメーション",
                settingsUapOpsModuleAnimHint: "アニメーションクリップ/AnimatorControllerの編集、マテリアル/シェーダプロパティ、インポータ/アバター設定。既定OFF。",
                settingsUapOpsModuleMarkersLabel: "シーンビューのマーカー",
                settingsUapOpsModuleMarkersHint: "エージェントが Scene ビューに置く番号付き 3D マーカー(uap_marker_add/list/clear)。表示だけでシーンは変えません。",
                ctxMarkersChipSingle: "マーカー 1 個",
                ctxMarkersChipPluralFmt: "マーカー {0} 個",
                ctxMarkersClearTooltip: "Scene ビューのマーカーとピンをすべて消す",
                ctxPinButton: "ピン",
                ctxPinButtonArmed: "Scene ビューをクリック...",
                ctxPinToolbarLabel: "ピン",
                ctxPinTooltip: "Scene ビューにピンを置いてエージェントに「ここ」を伝えます。次の左クリックで配置(Shift で連続、Esc で中止)。",
                ctxPinChipLabelFmt: "ピン P{0}",
                ctxPinChipTitleFmt: "シーンマーカー P{0}",
                ctxPinMarkerLabel: "ピン",
                ctxPinHitSurface: "カーソル下の面(コライダーなし)",
                ctxPinHitNothingFmt: "カーソル下に何もないため視線方向 {0} m に配置",
                ctxPinPayloadHeaderFmt: "Scene marker P{0} (user-placed pin in the Scene view)",
                ctxPinPayloadPositionFmt: "world position: {0}",
                ctxPinPayloadHitFmt: "hit object: {0}",
                ctxPinPayloadNearestFmt: "nearest objects: {0}",
                ctxPinPayloadCameraFmt: "scene view camera: position {0}, pivot {1}",
                settingsSectionExtensionProfiles: "拡張プロファイル",
                settingsExtensionProfilesHint:
                    "検出されたサードパーティ SDK の知識ブロックを、{agent} のシステムプロンプトに追加します。",
                settingsExtensionProfilesTooltip:
                    "検出されたサードパーティ SDK(VRChat SDK3、UniVRM、MagicaCloth2、FinalIK など)について、コンポーネント型をあらかじめ把握させるための短い知識ブロックを {agent} のシステムプロンプトに追加します。同梱プロファイル(Agent Panel Pro などの拡張パッケージが提供)は自動で注入されますが、.uap-profiles/*.json に置いたユーザー定義のプロファイルは、下での明示的な承認が必要です。",
                settingsExtensionProfilesEnabledLabel: "拡張プロファイルを有効化",
                settingsExtensionProfilesEnabledTooltip:
                    "次回の再接続後に適用されます。",
                settingsExtensionProfilesEmptyHint: "このプロジェクトではまだ対応SDKが検出されていません。",
                settingsExtensionProfilesStatusBundled: "同梱",
                settingsExtensionProfilesStatusApproved: "承認済み",
                settingsExtensionProfilesStatusPending: "承認待ち",
                settingsExtensionProfilesApproveButton: "確認して承認",
                settingsExtensionProfilesRevokeButton: "取り消す",
                settingsExtensionProfilesApproveDialogTitleFmt: "拡張プロファイルを承認しますか: {0}?",
                settingsExtensionProfilesApproveDialogBodyFmt: "このプロファイルがこのプロジェクトで検出されている間、取り消すまで毎回{agent}のシステムプロンプトに以下のテキストが追加されます:\n\n{0}",
                settingsExtensionProfilesApproveDialogConfirmButton: "承認",
                settingsExtensionProfilesApproveDialogCancelButton: "キャンセル",
                permAddRuleSuggestionFmt: "{0} を常に許可",
                permRuleAllCallsSuffixFmt: "{0}(このツールへの全呼び出し)",
                hubScriptGateInertWarning: "スクリプト検証ゲートはONですが、この構成では何もブロックできません:"
                    + " Windows以外ではPreToolUseフックが導入されず、「すべての許可プロンプトをスキップ」により"
                    + "CLIが確認を求めないためcan_use_toolの事前フィルタも動作しません。"
                    + "Assets/**/*.cs への直接書き込みは阻止されていません。"
                    + "「すべての許可プロンプトをスキップ」をオフにするとゲートが復帰します。",
                settingsPermissionModeOptionDefault: "デフォルト(毎回確認)",
                settingsPermissionModeOptionPlan: "プランモード",
                settingsPermissionModeOptionAcceptEdits: "編集を自動承認",
                hubAcpCommandNotFoundErrorFmt: "{0} のコマンド '{1}' が見つかりません。確認したパス: {2}。設定の CLI セクションでコマンドを指定してください。",
                hubAcpStartFailedFmt: "{0} の起動に失敗しました: {1}",
                firstRunAcpNotFoundTitleFmt: "{0} が見つかりません",
                firstRunAcpNotFoundBody: "Agent Panel はこのエージェントを Agent Client Protocol(ACP)経由のサブプロセスとして実行します。インストールするか、下にコマンド(PATH 上の名前、またはフルパス)を入力してください。",
                firstRunAcpLoginHintFmt: "サインインが必要になるとエージェントがブラウザを開き、完了後はパネルが自動で続行します。ターミナルでの代替: {0}",
                settingsBackendLabel: "エージェント",
                settingsBackendTooltip: "パネルが起動するエージェント CLI。次回の再接続時に適用され、現在のセッションは終了して選択したエージェントで新しいセッションが始まります。",
                settingsBackendOptionCustom: "その他の ACP エージェント(カスタムコマンド)",
                settingsAcpCommandLabel: "コマンド",
                settingsAcpArgumentsLabel: "引数",
                settingsAcpCommandHintFmt: "空欄なら `{0}` を使います。名前だけなら PATH から探し、フルパスはそのまま使います。",
                settingsAcpCommandHintCustom: "ACP モードでエージェントを起動するコマンド(例: `qwen --experimental-acp`)。必須です。",
                settingsAcpAuthMethodLabel: "認証メソッド ID",
                settingsAcpAuthMethodHint: "任意。空欄ならエージェントが最初に提示するサインイン方法を使います。",
                settingsAcpAuthMethodTooltip: "エージェントがサインインを要求したときにブリッジが試す ACP の authenticate メソッド ID(Gemini CLI: oauth-personal / gemini-api-key / vertex-ai)。空欄ならエージェントが最初に提示するものを使います。",
                settingsAcpLoginHintFmt: "必要になるとブラウザでサインインが開きます。ターミナルでの代替: {0}",
                settingsAcpLimitationsHint: "一部の機能は Claude Code 専用です(ホバーで一覧を表示)。",
                settingsAcpLimitationsTooltip: "ACP エージェントでは、セッション履歴ブラウザ・パネル内ログイン・サブエージェントのモデル設定・スクリプトゲートのフックは使えません(Claude Code 専用)。カスタム指示は新しいセッションの最初に送られます。権限カードと Unity 操作ツールは同じように動作します。",
                installButtonFmt: "{0} をインストール",
                installManualFoldout: "手動でインストールする場合",
                installManualBody: "ターミナルで次を実行してから「再検出」を押してください。",
                installRunningFmt: "{0} をインストール中... {1}秒",
                installDoneFmt: "{0} をインストールしました。接続しています...",
                installFailedFmt: "インストールに失敗しました(終了コード {0}): {1}",
                installTimedOut: "インストーラが 10 分以内に終わらなかったため停止しました。",
                installShellMissing: "インストーラを起動できませんでした(シェル、curl、PowerShell のいずれかがありません)。",
                installNodeMissing: "npm が見つかりません。先に Node.js(LTS)をインストールしてから、もう一度「インストール」を押してください。",
                installOpenNodeButton: "Node.js を入手",
                installConfirmTitleFmt: "{0} をインストールしますか?",
                installConfirmBodyFmt: "パネルが次のコマンドを実行します:\n\n{0}\n\nこのコンピュータにソフトウェアをダウンロードしてインストールします。",
                installConfirmButton: "インストール",
                installCancelButton: "キャンセル",
                hubAcpSignInStartedNoteFmt: "{0} へのサインインが必要です({1})。開いたブラウザで完了してください。完了後はパネルが自動で続行します。",
                hubAcpSignInUrlNoteFmt: "ブラウザが開かない場合は、このリンクを開いてください: {0}",
                hubAcpSignInFailedNoteFmt: "{0} へのサインインに失敗しました: {1}",
                statusWaitingSignIn: "ブラウザでのサインインを待っています...",
                settingsAccountAcpConnectedFmt: "{0} に接続済み。",
                settingsAccountAcpNotConnected: "未接続。",
                settingsAccountAcpSignInPendingFmt: "ブラウザでの {0} へのサインインを待っています...",
                settingsAccountAcpSignInButton: "サインイン / 再接続",
                settingsAccountAcpHint: "エージェント自身のブラウザ手順でサインインします。始まらない場合はボタンを押してください。",
                hubAcpSignInRequiredNoteFmt: "{0} にサインインしていないため、再接続を繰り返しませんでした。設定 > アカウントの「サインイン / 再接続」でブラウザのサインインをやり直すか、ターミナルで `{1}` を実行してから再接続してください。",
                hubAcpHandshakeDeathNoteFmt: "{0} が接続確立前に終了したため({1})、自動では再試行しませんでした。インストールとサインイン(`{2}`)を確認してから、設定 > アカウントの「サインイン / 再接続」を押してください。",
                agentGenericName: "エージェント",
                hubSessionNotResumedAcrossBackendsNoteFmt: "ここまでの会話は {0} とのものです。{1} は新しいセッションを開始し、次のメッセージと一緒に上の会話ログを引き継ぎます。",
                settingsClaudeAuthLabel: "APIキー認証",
                settingsClaudeAuthHint: "「自動」は環境の ANTHROPIC_API_KEY を優先(従量課金)。「サブスクリプションのみ」はそれを取り除きます。",
                settingsClaudeAuthTooltip: "Claude Code 専用(ACPエージェントには影響しません)。「自動」(推奨・デフォルト)は"
                    + "起動する CLI の環境から ANTHROPIC_API_KEY を取り除きません。ターミナルで実行した場合と全く同じ"
                    + "認証になります: そこに API キーがあれば優先され従量課金になり、なければ保存済みの"
                    + "サブスクリプションログインが使われます。「サブスクリプションのみ」は CLI の環境からその変数を"
                    + "取り除くため、このエディタのプロセスに ANTHROPIC_API_KEY があっても常にサブスクリプション"
                    + "ログインが使われます。",
                settingsClaudeAuthOptionAuto: "自動(推奨)",
                settingsClaudeAuthOptionSubscriptionOnly: "サブスクリプションのみ",
                settingsAccountApiKeyAuthNoteFmt: "APIキー({0})で接続中です -- 利用料はサブスクリプションではなくこのキーに"
                    + "課金されます。ここでのログイン/ログアウトはこれを変えません。常にサブスクリプションログインを"
                    + "使うには、上の「APIキー認証」を「サブスクリプションのみ」に設定してください。",
                hubApiKeyAuthNoteFmt: "APIキー({0})で接続中です -- 利用料はサブスクリプションではなくこのキーに課金されます。",
                acpAuthSummaryGemini: "サインイン: Gemini API キー(GEMINI_API_KEY、または ~/.gemini/.env)、"
                    + "または Code Assist Standard/Enterprise。",
                acpAuthSummaryCodex: "サインイン: ChatGPT アカウント、または CODEX_API_KEY / OPENAI_API_KEY の API キー。",
                acpAuthSummaryGrok: "サインイン: SuperGrok / X Premium+ のアカウント、または XAI_API_KEY の API キー。",
                acpAuthDetailGemini: "個人向けの「Login with Google」(Gemini Code Assist for individuals / "
                    + "Google AI Pro / Google AI Ultra)は 2026-06-18 に終了したため、個人アカウントは"
                    + "Gemini API キーでサインインします。Google ログインが続くのは Gemini Code Assist "
                    + "Standard / Enterprise だけで、こちらは Google Cloud プロジェクトが必要です。",
                acpAuthDetailCodex: "ChatGPT のサブスクリプションログインを推奨します。CODEX_API_KEY または "
                    + "OPENAI_API_KEY の API キーでも動作しますが、その場合はサブスクリプションではなく"
                    + "そのキーに課金されます。",
                acpAuthDetailGrok: "SuperGrok / X Premium+ のログインを推奨します。XAI_API_KEY の API キーでも"
                    + "動作しますが、その場合はサブスクリプションではなくそのキーに課金されます。",
                acpAuthKeysNotStoredNote: "パネルは API キーを保存しません。各エージェント CLI 自身の環境変数か"
                    + "設定ファイルに置いてください。OS の環境変数はエディタを再起動しないと反映されません。",
                hubAcpApiKeyGuidanceFmt: "代わりに API キーで {0} を使うには、{1} を設定してから再接続してください"
                    + "(OS の環境変数はエディタを再起動しないと反映されません)。パネルは API キーを保存しません。",
                settingsAccountAcpAuthMethodFmt: "サインイン方式: {0}",
                settingsAccountAcpAuthMethodStored: "保存済みのログインで接続しています。",
                settingsAccountAcpApiKeyAuthNoteFmt: "{0} は API キー方式です -- 利用料はサブスクリプションではなく"
                    + "そのキーに課金されます。パネルはキーを保存しません。キーはエージェント CLI 自身の環境変数か"
                    + "設定ファイルにあります。",
                hubAcpApiKeyAuthNoteFmt: "{0} に API キー方式({1})で接続しました -- 利用料はサブスクリプションではなく"
                    + "そのキーに課金されます。",
                settingsUapOpsProAbsentHintFmt: "{0} 別売の Agent Panel Pro 拡張パッケージが必要です(未導入)。",
                settingsUapOpsProAbsentTooltip: "このモジュールのツールは別売の Agent Panel Pro パッケージ(jp.colloid.agent-panel-pro)に"
                    + "収録されており、このプロジェクトには導入されていません。パネル本体は Pro なしでも全機能が動作します。"
                    + "追加するには Pro パッケージをプロジェクトの Packages/ 直下に展開してください。次回のドメインリロード後に"
                    + "このトグルが有効になります。詳細は README の「Core と Pro」を参照してください。",
                settingsExtensionProfilesNoBundledHint: "同梱の SDK プロファイルは未導入です。プロジェクトの .uap-profiles/*.json は引き続き使えます。",
                settingsExtensionProfilesNoBundledTooltip: "VRChat SDK3 / UniVRM / MagicaCloth2 / Final IK / Bakery / RPG Maker Unite の同梱プロファイルは、"
                    + "別売の Agent Panel Pro パッケージ(jp.colloid.agent-panel-pro)に収録されています。Pro なしでは、この一覧には"
                    + "プロジェクト直下の .uap-profiles/*.json に自分で置いたプロファイルだけが表示され、それぞれ下で承認してから注入されます。",
                settingsUapOpsModuleUiLabel: "UI 操作",
                settingsUapOpsModuleUiHint: "UI Toolkit のエディタウィンドウの一覧・ダンプ・クリック・値設定(uap_editor_ui_*)。既定OFF。",
                settingsSectionProUpdates: "Agent Panel Pro の更新",
                settingsProUpdatesHint: "購入時の製品キーを一度貼り付けると、以後 Pro は Package Manager から更新できます。",
                settingsProUpdatesTooltip: "キーは Unity 自身の資格情報ファイル(~/.upmconfig.toml、または UPM_USER_CONFIG_DIR のディレクトリ)に書き込み、"
                    + "このプロジェクトの Packages/manifest.json に jp.colloid.agent-panel-pro 用の scoped registry を追加します。"
                    + "パネルはキーを保存しません。以後は Window > Package Manager > My Registries に Agent Panel Pro が並び、"
                    + "新しい版が公開されると Update が押せます。レジストリ URL は秘密ではなく、キーと一緒に届いたものです。",
                settingsProUpdatesUrlLabel: "レジストリ URL",
                settingsProUpdatesKeyLabel: "製品キー",
                settingsProUpdatesApplyButton: "キーを保存",
                settingsProUpdatesStatusAppliedFmt: "保存しました。キーは {0} に書き込み、レジストリは manifest.json に追加済みです。Package Manager が解決中です。"
                    + "My Registries で Agent Panel Pro を確認してください。",
                settingsProUpdatesStatusErrorEmptyKey: "先に製品キーを入力してください。",
                settingsProUpdatesStatusErrorUrl: "レジストリ URL は https:// で始まるアドレスにしてください。",
                settingsProUpdatesStatusErrorForeignFmt: "別の scoped registry(\"{0}\")が manifest.json で {1} を既に対象にしています。そのスコープを外してから保存し直してください。",
                settingsProUpdatesStatusErrorManifestFmt: "Packages/manifest.json を読めませんでした: {0}",
                settingsProUpdatesStatusErrorWriteFmt: "ファイルを書き込めませんでした: {0}",
                settingsProUpdatesVccButton: "VCC / ALCOM に追加",
                settingsProUpdatesVccTooltip: "VRChat Creator Companion または ALCOM に Pro の VPM リポジトリを登録する vcc:// リンクを開きます。"
                    + "製品キーはリポジトリの Authorization ヘッダーとして渡されます。何も開かない場合は手動で追加してください: "
                    + "Settings > Packages > Add Repository にステータス行の listing URL を貼り、ヘッダー設定に Authorization: Bearer <キー> を追加します。",
                settingsProUpdatesStatusVccOpenedFmt: "VCC / ALCOM を開いてリポジトリを追加します。手動追加用の listing URL: {0}",
                settingsProUpdatesStatusErrorVccNoKey: "先に製品キーを入力するか、「キーを保存」を押して .upmconfig.toml から読み戻せるようにしてください。");
        }
    }
}
