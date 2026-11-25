using System;
using System.Collections.Generic;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The set of <see cref="UapPlaybook"/>s this editor session can serve
    /// through <c>uap_help topic:"&lt;id&gt;"</c> (design note
    /// docs/design-notes/2026-10-02-uap-help-playbooks.md): Core's built-in
    /// ones (<see cref="UapBuiltInPlaybooks"/>) plus whatever add-on
    /// <see cref="IUapPlaybookProvider"/>s contribute, minus the ones whose
    /// <see cref="UapPlaybook.RequiredTools"/> are not all available right now.
    ///
    /// Unity-free on purpose: the module/registry facts it needs arrive as a
    /// predicate (<see cref="AvailabilityFor"/> builds the standard one), so
    /// the license-free smoke harness and the steering composer both run it
    /// without an Editor.
    /// </summary>
    public sealed class UapPlaybookCatalog
    {
        // Kept in sync BY CONSTRUCTION with UapToolGateway.ToolsToolName /
        // CallToolName (UapHelpPlaybookTests asserts it), not referenced:
        // UapToolGateway touches ToolRegistry and with it UnityEditor, which
        // this file must stay free of (same reason as UapToolListModePolicy).
        private const string GatewayToolsName = "uap_tools";
        private const string GatewayCallName = "uap_call";

        private readonly List<UapPlaybook> _playbooks = new List<UapPlaybook>();
        private readonly Dictionary<string, UapPlaybook> _byId =
            new Dictionary<string, UapPlaybook>(StringComparer.Ordinal);

        /// <summary>
        /// A catalog holding exactly <paramref name="builtIn"/>. A bad or
        /// duplicate id here is a programming error in this package's own
        /// playbook list, so it throws (the smoke harness and EditMode tests
        /// build the real list and would fail at once).
        /// </summary>
        public UapPlaybookCatalog(IEnumerable<UapPlaybook> builtIn)
        {
            if (builtIn == null)
            {
                return;
            }
            foreach (UapPlaybook playbook in builtIn)
            {
                string problem = Validate(playbook);
                if (problem != null)
                {
                    throw new ArgumentException(problem, "builtIn");
                }
                if (_byId.ContainsKey(playbook.Id))
                {
                    throw new ArgumentException("Duplicate playbook id '" + playbook.Id + "'.", "builtIn");
                }
                Add(playbook);
            }
        }

        /// <summary>Core's built-in playbooks only.</summary>
        public static UapPlaybookCatalog CreateBuiltIn()
        {
            return new UapPlaybookCatalog(UapBuiltInPlaybooks.Create());
        }

        /// <summary>
        /// Core's built-ins, then every provider's playbooks in the order
        /// given (<see cref="AddFromProvider"/> rules). A provider that throws
        /// is logged and skipped, never fatal -- same stance as tool
        /// providers in ToolRegistry.RegisterProviderTools.
        /// </summary>
        public static UapPlaybookCatalog Create(IEnumerable<IUapPlaybookProvider> providers, Action<string> log)
        {
            UapPlaybookCatalog catalog = CreateBuiltIn();
            if (providers == null)
            {
                return catalog;
            }
            foreach (IUapPlaybookProvider provider in providers)
            {
                if (provider == null)
                {
                    continue;
                }
                string providerName = provider.GetType().FullName;
                IEnumerable<UapPlaybook> created;
                try
                {
                    created = provider.CreatePlaybooks();
                }
                catch (Exception ex)
                {
                    Log(log, "Playbook provider '" + providerName + "' threw while creating playbooks; skipped: " + ex);
                    continue;
                }
                catalog.AddFromProvider(created, providerName, log);
            }
            return catalog;
        }

        /// <summary>All playbooks, built-ins first, in registration order.</summary>
        public IList<UapPlaybook> All
        {
            get { return _playbooks.AsReadOnly(); }
        }

        public int Count
        {
            get { return _playbooks.Count; }
        }

        /// <summary>Looks up a playbook by exact id regardless of availability, or null.</summary>
        public UapPlaybook Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            UapPlaybook playbook;
            return _byId.TryGetValue(id, out playbook) ? playbook : null;
        }

        /// <summary>
        /// Merges a provider's playbooks. A playbook with an invalid shape or
        /// an id that is already taken (Core's, or an earlier provider's) is
        /// rejected with one log line and the rest still merge -- Core always
        /// wins. Returns how many were added.
        /// </summary>
        public int AddFromProvider(IEnumerable<UapPlaybook> playbooks, string providerName, Action<string> log)
        {
            if (playbooks == null)
            {
                return 0;
            }
            int added = 0;
            foreach (UapPlaybook playbook in playbooks)
            {
                string problem = Validate(playbook);
                if (problem != null)
                {
                    Log(log, "Playbook from '" + providerName + "' rejected: " + problem);
                    continue;
                }
                if (_byId.ContainsKey(playbook.Id))
                {
                    Log(log, "Playbook '" + playbook.Id + "' from '" + providerName
                        + "' rejected: the id is already taken (Core and earlier providers win).");
                    continue;
                }
                Add(playbook);
                added++;
            }
            return added;
        }

        /// <summary>
        /// The playbooks whose every required tool satisfies
        /// <paramref name="isToolAvailable"/> (null predicate: nothing is
        /// available), in catalog order.
        /// </summary>
        public List<UapPlaybook> Filter(Func<string, bool> isToolAvailable)
        {
            var result = new List<UapPlaybook>();
            if (isToolAvailable == null)
            {
                return result;
            }
            for (int i = 0; i < _playbooks.Count; i++)
            {
                if (AllAvailable(_playbooks[i], isToolAvailable))
                {
                    result.Add(_playbooks[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// The standard availability predicate: a tool counts when
        /// <paramref name="moduleOfTool"/> knows it (null result = not
        /// registered) AND its module is in <paramref name="enabledModules"/>;
        /// the two gateway tools (uap_tools/uap_call), which are never
        /// registered, count exactly when <paramref name="compactToolList"/>.
        /// </summary>
        public static Func<string, bool> AvailabilityFor(IEnumerable<string> enabledModules,
            Func<string, string> moduleOfTool, bool compactToolList)
        {
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            if (enabledModules != null)
            {
                foreach (string module in enabledModules)
                {
                    if (!string.IsNullOrEmpty(module))
                    {
                        enabled.Add(module);
                    }
                }
            }
            return delegate(string toolName)
            {
                if (string.IsNullOrEmpty(toolName))
                {
                    return false;
                }
                if (string.Equals(toolName, GatewayToolsName, StringComparison.Ordinal)
                    || string.Equals(toolName, GatewayCallName, StringComparison.Ordinal))
                {
                    return compactToolList;
                }
                string module = moduleOfTool != null ? moduleOfTool(toolName) : null;
                return module != null && enabled.Contains(module);
            };
        }

        /// <summary>
        /// The one steering line naming the available playbooks, e.g.
        /// <c>Playbooks (uap_help topic:"&lt;id&gt;"): verify-gameplay, ui-click</c>;
        /// empty when none is available.
        /// </summary>
        public static string FormatIndex(IEnumerable<UapPlaybook> available)
        {
            if (available == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            foreach (UapPlaybook playbook in available)
            {
                sb.Append(sb.Length == 0 ? "Playbooks (uap_help topic:\"<id>\"): " : ", ").Append(playbook.Id);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Null when <paramref name="playbook"/> is well-formed, else why
        /// not: a kebab-case id, and a non-empty title, summary and body.
        /// </summary>
        public static string Validate(UapPlaybook playbook)
        {
            if (playbook == null)
            {
                return "null playbook";
            }
            if (!IsKebabCase(playbook.Id))
            {
                return "id '" + playbook.Id + "' is not kebab-case (a-z, 0-9 and single hyphens)";
            }
            if (string.IsNullOrWhiteSpace(playbook.Title))
            {
                return "'" + playbook.Id + "' has no title";
            }
            if (string.IsNullOrWhiteSpace(playbook.Summary))
            {
                return "'" + playbook.Id + "' has no summary";
            }
            if (string.IsNullOrWhiteSpace(playbook.Body))
            {
                return "'" + playbook.Id + "' has no body";
            }
            return null;
        }

        private static bool IsKebabCase(string id)
        {
            if (string.IsNullOrEmpty(id) || id[0] == '-' || id[id.Length - 1] == '-')
            {
                return false;
            }
            bool previousHyphen = false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool hyphen = c == '-';
                if (!(hyphen || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) || (hyphen && previousHyphen))
                {
                    return false;
                }
                previousHyphen = hyphen;
            }
            return true;
        }

        private static bool AllAvailable(UapPlaybook playbook, Func<string, bool> isToolAvailable)
        {
            for (int i = 0; i < playbook.RequiredTools.Length; i++)
            {
                if (!isToolAvailable(playbook.RequiredTools[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private void Add(UapPlaybook playbook)
        {
            _byId[playbook.Id] = playbook;
            _playbooks.Add(playbook);
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }
    }
}
