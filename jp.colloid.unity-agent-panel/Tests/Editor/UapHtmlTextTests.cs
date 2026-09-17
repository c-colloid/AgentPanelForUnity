using System;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// HTML reduction for uap_web_fetch (design note
    /// 2026-09-17-web-fetch-tool.md section 4.4): scripts and styles gone,
    /// headings and list items on their own lines, images and links
    /// collected as absolute URLs, main/article preferred over the page.
    /// </summary>
    [TestFixture]
    public class UapHtmlTextTests
    {
        private static readonly Uri Base = new Uri("https://example.com/docs/page.html");

        [Test]
        public void DropsScriptsStylesComments_KeepsTitleAndText()
        {
            string html = "<!DOCTYPE html><html><head><title>My &amp; Page</title><style>p{color:red}</style>"
                + "<script>var x = '<p>not text</p>';</script></head><body><!-- comment -->"
                + "<h1>Heading</h1><p>Hello&nbsp;<b>world</b> &#169; &#x263A;</p><noscript>no js</noscript>"
                + "<script type=\"text/javascript\">alert(1)</script></body></html>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            Assert.AreEqual("My & Page", r.Title);
            StringAssert.Contains("# Heading", r.Text);
            StringAssert.Contains("Hello world © ☺", r.Text);
            StringAssert.DoesNotContain("color:red", r.Text);
            StringAssert.DoesNotContain("not text", r.Text);
            StringAssert.DoesNotContain("alert", r.Text);
            StringAssert.DoesNotContain("no js", r.Text);
            StringAssert.DoesNotContain("comment", r.Text);
        }

        [Test]
        public void ListItemsAndHeadings_OnOwnLines_BlankRunsCollapsed()
        {
            string html = "<div>\n\n\n   <h2>Steps</h2>\n<ul>\n  <li>One</li>\n  <li>Two</li>\n</ul>\n<p>a</p><p>b</p></div>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            Assert.AreEqual("## Steps\n\n- One\n- Two\n\na\n\nb", r.Text);
        }

        [Test]
        public void ImagesAndLinks_Absolute_Deduplicated_Capped()
        {
            var sb = new System.Text.StringBuilder("<body>");
            sb.Append("<img src=\"/img/a.png\" alt=\"Alpha\"><img src='b.png'><img src=\"/img/a.png\" alt=\"dup\">");
            sb.Append("<img src=\"data:image/png;base64,AAAA\"><img alt=\"no src\">");
            sb.Append("<a href=\"#top\">Top</a><a href=\"javascript:void(0)\">JS</a>");
            sb.Append("<a href=\"../other.html\">Other  page</a><a href=\"https://x.example/z\">Z</a><a href=\"../other.html\">Other again</a>");
            for (int i = 0; i < 50; i++)
            {
                sb.Append("<a href=\"/l/").Append(i).Append("\">L").Append(i).Append("</a>");
            }
            for (int i = 0; i < 30; i++)
            {
                sb.Append("<img src=\"/i/").Append(i).Append(".jpg\">");
            }
            sb.Append("</body>");
            UapHtmlText.Result r = UapHtmlText.Extract(sb.ToString(), Base);

            Assert.AreEqual(UapHtmlText.MaxImages, r.Images.Count);
            Assert.AreEqual(2 + 30, r.ImageCount, "data: and src-less images are not counted; a repeated URL counts once");
            Assert.AreEqual("Alpha", r.Images[0].Key);
            Assert.AreEqual("https://example.com/img/a.png", r.Images[0].Value);
            Assert.AreEqual("https://example.com/docs/b.png", r.Images[1].Value);

            Assert.AreEqual(UapHtmlText.MaxLinks, r.Links.Count);
            Assert.AreEqual(2 + 50, r.LinkCount, "fragment-only and javascript: links are skipped");
            Assert.AreEqual("Other page", r.Links[0].Key);
            Assert.AreEqual("https://example.com/other.html", r.Links[0].Value);
            Assert.AreEqual("https://x.example/z", r.Links[1].Value);
            StringAssert.Contains("[img: Alpha]", r.Text);
            StringAssert.Contains("Other page", r.Text);

            string rendered = UapHtmlText.Render(r);
            StringAssert.Contains("Images (20 of 32):", rendered);
            StringAssert.Contains("- Alpha -- https://example.com/img/a.png", rendered);
            StringAssert.Contains("Links (40 of 52):", rendered);
            StringAssert.Contains("- Other page -- https://example.com/other.html", rendered);
        }

        [Test]
        public void MainElement_PreferredOverNavigationAndFooter()
        {
            string html = "<body><nav><a href=\"/\">Home</a> <a href=\"/about\">About</a></nav>"
                + "<main><article><h1>Article</h1><p>Body text.</p></article></main>"
                + "<footer>Copyright</footer></body>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            StringAssert.Contains("Body text.", r.Text);
            StringAssert.DoesNotContain("Home", r.Text);
            StringAssert.DoesNotContain("Copyright", r.Text);
            // Links are still collected from the whole page.
            Assert.AreEqual(2, r.Links.Count);
        }

        [Test]
        public void NoMain_WholeBodyUsed()
        {
            string html = "<body><nav>Home</nav><p>Body</p></body>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            StringAssert.Contains("Home", r.Text);
            StringAssert.Contains("Body", r.Text);
        }

        [Test]
        public void TableCells_SeparatedWithBars_BreakSelfClosing()
        {
            string html = "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table><p>x<br/>y</p>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            Assert.AreEqual("A | B\n1 | 2\n\nx\ny", r.Text);
        }

        [Test]
        public void TitleWithoutHead_AttributeQuotingAndBareValues()
        {
            string html = "<title>T</title><p title=\"has > angle\">p</p><img src=/bare.png alt=bare><a href=\"/x\" data-href=\"/wrong\">l</a>";
            UapHtmlText.Result r = UapHtmlText.Extract(html, Base);
            Assert.AreEqual("T", r.Title);
            StringAssert.Contains("p", r.Text);
            Assert.AreEqual("https://example.com/bare.png", r.Images[0].Value);
            Assert.AreEqual("bare", r.Images[0].Key);
            Assert.AreEqual("https://example.com/x", r.Links[0].Value);
        }

        [Test]
        public void Decode_Entities()
        {
            Assert.AreEqual("a < b & c > d \"q\" 'a' — …", UapHtmlText.Decode("a &lt; b &amp; c &gt; d &quot;q&quot; &apos;a&apos; &mdash; &hellip;"));
            Assert.AreEqual("&unknown; &", UapHtmlText.Decode("&unknown; &"));
            Assert.AreEqual("A日", UapHtmlText.Decode("&#65;&#x65E5;"));
            Assert.AreEqual(string.Empty, UapHtmlText.Decode(null));
        }

        [Test]
        public void Empty_And_Unclosed_DoNotThrow()
        {
            Assert.AreEqual(string.Empty, UapHtmlText.Extract(null, Base).Text);
            Assert.AreEqual(string.Empty, UapHtmlText.Extract(string.Empty, Base).Text);
            UapHtmlText.Result r = UapHtmlText.Extract("<p>open <b>bold <a href='x'>link", Base);
            StringAssert.Contains("open bold link", r.Text);
            r = UapHtmlText.Extract("<script>never closed", Base);
            Assert.AreEqual(string.Empty, r.Text);
            r = UapHtmlText.Extract("text <", Base);
            Assert.AreEqual("text", r.Text);
        }
    }
}
