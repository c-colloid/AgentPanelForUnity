using System.Runtime.CompilerServices;

// Lets Tests/Editor (Colloid.AgentPanel.Editor.Tests) drive a few
// switching/view-lifecycle internals directly (AgentPanelWindow.SetActiveView/
// RequestActiveView) without going through EditorWindow.GetWindow/Show --
// this codebase's EditMode tests deliberately avoid real window-manager
// side effects (see AgentPanelWindowTests's own re-entrancy test), so the
// only way to exercise those code paths from a test is internal access.
// No public API surface is added by this.
[assembly: InternalsVisibleTo("Colloid.AgentPanel.Editor.Tests")]

// 2026-09-11 core/pro split (docs/design-notes/2026-09-11-core-pro-split.md):
// the tools that moved to jp.colloid.agent-panel-pro (namespace
// Colloid.AgentPanel.Ops unchanged) use internal members of shared Core
// helpers -- UapToolResults, UapAssetFolderHelper, and similar -- exactly
// as they did before the move. Do NOT widen those members to public;
// grant Pro's assemblies the same internal access Core's own tools have.
[assembly: InternalsVisibleTo("Colloid.AgentPanel.Pro.Editor")]
[assembly: InternalsVisibleTo("Colloid.AgentPanel.Pro.Editor.Tests")]
