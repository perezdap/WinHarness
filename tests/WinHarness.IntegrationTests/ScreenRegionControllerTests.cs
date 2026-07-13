using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Cli.Rendering;
using WinHarness.Configuration;
using WinHarness.Context;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Tools;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ScreenRegionControllerTests
{
    private const int Width = 120;

    [TestMethod]
    public void LayoutIsInactiveWhenNotOptedIn()
    {
        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: false, redirected: false, Width, height: 40);

        Assert.IsFalse(layout.Active);
    }

    [TestMethod]
    public void LayoutIsInactiveWhenRedirectedEvenIfOptedIn()
    {
        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: true, Width, height: 40);

        Assert.IsFalse(layout.Active);
    }

    [TestMethod]
    public void LayoutIsInactiveWhenTerminalTooShort()
    {
        Assert.IsFalse(ScreenRegionLayout.Resolve(optedIn: true, redirected: false, Width, height: 5).Active);
    }

    [TestMethod]
    public void LayoutIsInactiveWhenTerminalTooNarrow()
    {
        Assert.IsFalse(ScreenRegionLayout.Resolve(optedIn: true, redirected: false, width: 10, height: 40).Active);
    }

    [TestMethod]
    public void LayoutPinsHeaderRowOneAndLeavesTwoFooterRowsWhenOptedIn()
    {
        const int height = 40;

        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: false, Width, height);

        Assert.IsTrue(layout.Active);
        Assert.AreEqual(Width, layout.Width);
        Assert.AreEqual(height, layout.Height);
        // Header occupies row 1; the scroll region starts at row 2.
        Assert.AreEqual(2, layout.ScrollTop);
        // Two footer rows: region ends at height - 2.
        Assert.AreEqual(height - 2, layout.ScrollBottom);
    }

    [TestMethod]
    public void LayoutAtMinimumHeightHasExactlyThreeScrollingRows()
    {
        int min = ScreenRegionLayout.MinimumHeight;

        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: false, Width, min);

        Assert.IsTrue(layout.Active);
        Assert.AreEqual(2, layout.ScrollTop);
        Assert.AreEqual(min - 2, layout.ScrollBottom);
        Assert.AreEqual(3, layout.ScrollBottom - layout.ScrollTop + 1);
    }

    [TestMethod]
    public void InactiveControllerIsNoOpAndDoesNotThrow()
    {
        // Redirected/one-shot paths get an inactive controller; calling every
        // method must be a safe no-op so the default chat path is unchanged.
        ScreenRegionController controller = new(ScreenRegionLayout.Resolve(optedIn: false, redirected: false, Width, height: 40));

        Assert.IsFalse(controller.IsActive);

        controller.Enter();
        controller.SetHeader("x");
        controller.SetFooter("y");
        controller.Repaint();
        controller.OnResize();
        controller.Exit();
        controller.Dispose();
    }

    // The formatters are plain text (the fixed rows are written via raw
    // Console.Write, so Spectre markup would render literally). These tests pin
    // the shape and assert no Spectre markup leaks into the output.

    private static ChatSession NewSession() => new("openai", "gpt-4o", renderMarkdown: true);

    private static ChatSession NewSessionWithContext(ProjectContext context)
        => new(SessionManager.InMemory("."), new StubContextLoader(context), ".", "openai", "gpt-4o", renderMarkdown: true);

    [TestMethod]
    public void HeaderFormatterProducesCondensedBannerWithoutMarkup()
    {
        WinHarnessOptions options = new();
        ChatSession session = NewSession();
        session.ReasoningEffort = "medium";

        string header = ScreenHeaderFormatter.Format(session, options);

        Assert.AreEqual("WinHarness chat · openai · gpt-4o · effort medium", header);
        Assert.IsFalse(header.Contains('['), "header must not contain Spectre markup");
    }

    [TestMethod]
    public void HeaderFormatterFallsBackToDefaultEffortWhenUnset()
    {
        WinHarnessOptions options = new();
        ChatSession session = NewSession();

        string header = ScreenHeaderFormatter.Format(session, options);

        StringAssert.Contains(header, "effort default");
    }

    [TestMethod]
    public void FooterFormatterIncludesMarkdownAndOmitsAbsentSegments()
    {
        ChatSession session = NewSession();

        string footer = ScreenFooterFormatter.Format(session);

        Assert.AreEqual("md on", footer);
        Assert.IsFalse(footer.Contains('['), "footer must not contain Spectre markup");
    }

    [TestMethod]
    public void FooterFormatterIncludesContextAndToolsWhenPresent()
    {
        ChatSession session = NewSessionWithContext(new ProjectContext(null, null, "x"));
        session.ToolFilter = new ToolFilter(Allow: ["read_file"], Exclude: ["bash"]);

        string footer = ScreenFooterFormatter.Format(session);

        StringAssert.Contains(footer, "md on");
        StringAssert.Contains(footer, "AGENTS.md");
        StringAssert.Contains(footer, "allow:read_file");
        StringAssert.Contains(footer, "exclude:bash");
        Assert.IsFalse(footer.Contains('['), "footer must not contain Spectre markup");
    }

    private sealed class StubContextLoader : IContextFileLoader
    {
        private readonly ProjectContext _context;

        public StubContextLoader(ProjectContext context) => _context = context;

        public ProjectContext Load(string workspaceRoot) => _context;
    }
}
