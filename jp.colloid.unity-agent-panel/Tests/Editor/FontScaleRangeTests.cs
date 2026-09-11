using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UXIA-L5's regression guard: ApplyFontScale is a `.uap-fontscale-N`
    /// CLASS LOOKUP, so a font size whose class is missing from
    /// FontScale.uss is a SILENT no-op -- the slider moves, nothing
    /// changes. Every step the PanelSettings constants allow must have a
    /// matching rule; raising MaxFontSizePx without appending classes (the
    /// exact mistake this batch could have made) fails here instead of in
    /// the user's hands.
    /// </summary>
    [TestFixture]
    public class FontScaleRangeTests
    {
        private const string FontScaleUssPath =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/Uss/FontScale.uss";

        [Test]
        public void EveryAllowedFontSize_HasItsFontScaleClass()
        {
            string uss = File.ReadAllText(Path.GetFullPath(FontScaleUssPath));
            for (int px = PanelSettings.MinFontSizePx; px <= PanelSettings.MaxFontSizePx; px++)
            {
                StringAssert.Contains(".uap-fontscale-" + px + " {", uss,
                    "MinFontSizePx..MaxFontSizePx=" + PanelSettings.MinFontSizePx + ".."
                    + PanelSettings.MaxFontSizePx + " but FontScale.uss has no rule for " + px
                    + "px -- that size would be a silent no-op");
            }
        }

        [Test]
        public void DefaultFontSize_IsInsideTheAllowedRange()
        {
            Assert.GreaterOrEqual(PanelSettings.DefaultFontSizePx, PanelSettings.MinFontSizePx);
            Assert.LessOrEqual(PanelSettings.DefaultFontSizePx, PanelSettings.MaxFontSizePx);
        }
    }
}
