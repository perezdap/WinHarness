using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Rendering;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ScreenRegionControllerTests
{
    [TestMethod]
    public void LayoutIsInactiveWhenNotOptedIn()
    {
        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: false, redirected: false, height: 40);

        Assert.IsFalse(layout.Active);
    }

    [TestMethod]
    public void LayoutIsInactiveWhenRedirectedEvenIfOptedIn()
    {
        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: true, height: 40);

        Assert.IsFalse(layout.Active);
    }

    [TestMethod]
    public void LayoutIsInactiveWhenTerminalTooShort()
    {
        // MinimumHeight = header(1) + footer(2) + 3 scrolling rows = 6.
        Assert.IsFalse(ScreenRegionLayout.Resolve(optedIn: true, redirected: false, height: 5).Active);
    }

    [TestMethod]
    public void LayoutPinsHeaderRowOneAndLeavesTwoFooterRowsWhenOptedIn()
    {
        const int height = 40;

        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: false, height: height);

        Assert.IsTrue(layout.Active);
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

        ScreenRegionLayout layout = ScreenRegionLayout.Resolve(optedIn: true, redirected: false, height: min);

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
        ScreenRegionController controller = new(ScreenRegionLayout.Resolve(optedIn: false, redirected: false, height: 40));

        Assert.IsFalse(controller.IsActive);

        controller.Enter();
        controller.SetHeader("x");
        controller.SetFooter("y");
        controller.Repaint();
        controller.OnResize();
        controller.Exit();
        controller.Dispose();
    }
}
