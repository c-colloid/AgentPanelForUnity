using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Reload/resume status strip at the top of the chat view
    /// (ARCHITECTURE.md D4: Resuming is a first-class UI state, not an
    /// error). Four appearances:
    /// - info: "Reconnecting to the previous session..." while the client
    ///   is Starting over an existing transcript,
    /// - ok: "Session resumed." transient (auto-hides after 4 s),
    /// - warn: mid-turn interruption nudge with a one-click Continue
    ///   button (never auto-continues; R05 section 3.9),
    /// - error: the CLI process exited and auto-reconnect gave up; shows a
    ///   Reconnect button wired to AgentHub.EnsureStarted (R05 section 3.8
    ///   requires an explicit recovery affordance in the Errored state).
    /// </summary>
    public sealed class ResumeBanner
    {
        private const long OkAutoHideMillis = 4000;

        private enum ButtonMode
        {
            None,
            Continue,
            Reconnect
        }

        private readonly VisualElement _root;
        private readonly Label _text;
        private readonly Button _action;

        private AgentClientState _previousState = AgentClientState.NotStarted;
        private ButtonMode _buttonMode = ButtonMode.None;
        private bool _nudgeDismissed;
        private bool _showTransientOk;
        private IVisualElementScheduledItem _hideItem;

        private static readonly string[] Variants =
        {
            "uap-banner--info",
            "uap-banner--ok",
            "uap-banner--warn"
        };

        public VisualElement Root
        {
            get { return _root; }
        }

        public ResumeBanner()
        {
            _root = new VisualElement();
            _root.AddToClassList("uap-banner");
            _root.style.display = DisplayStyle.None;

            _text = new Label(string.Empty);
            _text.AddToClassList("uap-banner-text");
            _root.Add(_text);

            _action = new Button(OnActionClicked) { text = L10n.S.BannerContinueButton };
            _action.AddToClassList("uap-banner-btn");
            _root.Add(_action);
        }

        /// <summary>Re-evaluates which strip (if any) should be visible.</summary>
        public void Refresh(AgentClient client, bool resumedMidTurn, bool hasHistory)
        {
            AgentClientState state = client != null ? client.State : AgentClientState.NotStarted;

            // Transient success strip when a reconnect over history completes.
            if (_previousState == AgentClientState.Starting
                && state == AgentClientState.Ready && hasHistory)
            {
                _showTransientOk = true;
                if (_hideItem != null)
                {
                    _hideItem.Pause();
                }
                _hideItem = _root.schedule.Execute(() =>
                {
                    _showTransientOk = false;
                    Apply(null, null, ButtonMode.None);
                });
                _hideItem.ExecuteLater(OkAutoHideMillis);
            }
            _previousState = state;

            if (!resumedMidTurn)
            {
                _nudgeDismissed = false;
            }

            // The Errored strip wins: without it the panel has no reconnect
            // affordance at all once auto-reconnect is suspended.
            if (state == AgentClientState.Errored)
            {
                Apply("uap-banner--warn",
                    L10n.S.BannerErroredText, ButtonMode.Reconnect);
                return;
            }
            if (resumedMidTurn && !_nudgeDismissed
                && state != AgentClientState.NotStarted)
            {
                Apply("uap-banner--warn",
                    L10n.S.BannerInterruptedText, ButtonMode.Continue);
                return;
            }
            if (state == AgentClientState.Starting && hasHistory)
            {
                _showTransientOk = false;
                Apply("uap-banner--info", L10n.S.BannerReconnectingText,
                    ButtonMode.None);
                return;
            }
            if (_showTransientOk)
            {
                Apply("uap-banner--ok", L10n.S.BannerResumedText, ButtonMode.None);
                return;
            }
            Apply(null, null, ButtonMode.None);
        }

        private void OnActionClicked()
        {
            switch (_buttonMode)
            {
                case ButtonMode.Continue:
                    _nudgeDismissed = true;
                    CompileGate.SendOrQueue(
                        "Continue the task that was interrupted by the reload.");
                    Apply(null, null, ButtonMode.None);
                    break;
                case ButtonMode.Reconnect:
                    // EnsureStarted restarts from Errored and resumes the
                    // cached session id; Changed refreshes this banner.
                    AgentHub.EnsureStarted();
                    break;
            }
        }

        private void Apply(string variantClass, string text, ButtonMode buttonMode)
        {
            _buttonMode = buttonMode;
            if (variantClass == null)
            {
                _root.style.display = DisplayStyle.None;
                return;
            }
            _root.style.display = DisplayStyle.Flex;
            for (int i = 0; i < Variants.Length; i++)
            {
                _root.EnableInClassList(Variants[i], Variants[i] == variantClass);
            }
            _text.text = text ?? string.Empty;
            _action.text = buttonMode == ButtonMode.Reconnect
                ? L10n.S.BannerReconnectButton : L10n.S.BannerContinueButton;
            _action.style.display = buttonMode == ButtonMode.None
                ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
