namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>
    /// The one number the steering line needs from Application.unityVersion
    /// (design note section 3.2 item 3): the major, so "2022.3.22f1" reads
    /// 2022 and "6000.0.1f1" reads 6000. Anything unparseable is -1, which
    /// the caller treats as "older than 6" -- the caveat is cheap to show
    /// and expensive to miss.
    /// </summary>
    public static class UnityVersionParser
    {
        /// <summary>First major of the Unity 6 line; every version at or above it is "Unity 6+".</summary>
        public const int Unity6Major = 6000;

        public static int Major(string unityVersion)
        {
            if (string.IsNullOrEmpty(unityVersion))
            {
                return -1;
            }
            int value = 0;
            int digits = 0;
            for (int i = 0; i < unityVersion.Length; i++)
            {
                char c = unityVersion[i];
                if (c < '0' || c > '9')
                {
                    break;
                }
                if (digits >= 9)
                {
                    return -1;
                }
                value = value * 10 + (c - '0');
                digits++;
            }
            return digits == 0 ? -1 : value;
        }

        /// <summary>True when the version parses and its major is at or above <see cref="Unity6Major"/>.</summary>
        public static bool IsUnity6OrNewer(string unityVersion)
        {
            return Major(unityVersion) >= Unity6Major;
        }
    }
}
