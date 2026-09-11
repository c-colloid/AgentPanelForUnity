using System;
using Colloid.AgentPanel.Core.Client;
using UnityEditor;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Bridges the CLI reader threads to the editor main thread
    /// (ARCHITECTURE.md D6 threading model): subscribes to
    /// EditorApplication.update and drains the attached AgentClient with a
    /// per-frame budget (20 lines or 2 ms). When anything was processed it
    /// raises RepaintRequested so the window can repaint without polling.
    /// SynchronizationContext capture is deliberately avoided -- it does
    /// not survive domain reloads.
    /// </summary>
    public static class EditorUpdatePump
    {
        private const int MaxLinesPerFrame = 20;
        private const double MaxMillisPerFrame = 2.0;

        private static AgentClient _client;

        /// <summary>Raised on the main thread after a frame that processed lines.</summary>
        public static event Action RepaintRequested;

        /// <summary>The currently pumped client, or null.</summary>
        public static AgentClient Client
        {
            get { return _client; }
        }

        /// <summary>Starts pumping the given client (detaches any previous one).</summary>
        public static void Attach(AgentClient client)
        {
            if (client == null)
            {
                return;
            }
            Detach();
            _client = client;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>Stops pumping. Safe to call when not attached.</summary>
        public static void Detach()
        {
            if (_client != null)
            {
                EditorApplication.update -= OnEditorUpdate;
                _client = null;
            }
        }

        private static void OnEditorUpdate()
        {
            AgentClient client = _client;
            if (client == null)
            {
                return;
            }
            int processed = client.Pump(MaxLinesPerFrame, MaxMillisPerFrame);
            if (processed > 0)
            {
                Action handler = RepaintRequested;
                if (handler != null)
                {
                    handler();
                }
            }
        }
    }
}
