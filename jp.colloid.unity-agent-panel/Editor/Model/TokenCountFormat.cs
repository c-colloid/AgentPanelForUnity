using System.Globalization;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// "84.2k" / "1.3M" / "512": the short token-count form the status bar
    /// has always used (StatusBarView.FormatTokens delegates here now, so
    /// the compaction note and the meter agree on the notation).
    /// </summary>
    public static class TokenCountFormat
    {
        public static string Short(long count)
        {
            if (count >= 1000000)
            {
                return (count / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            }
            if (count >= 1000)
            {
                return (count / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            }
            return count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
