using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Runtime;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class SteeringQueueTests
{
    [TestMethod]
    public void DrainReturnsMessagesInFifoOrder()
    {
        SteeringQueue queue = new();
        queue.Enqueue("first");
        queue.Enqueue("second");

        IReadOnlyList<string> drained = queue.DrainAll();

        CollectionAssert.AreEqual(new[] { "first", "second" }, drained.ToArray());
        Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void DrainOnEmptyQueueReturnsEmpty()
    {
        SteeringQueue queue = new();

        Assert.AreEqual(0, queue.DrainAll().Count);
    }

    [TestMethod]
    public void EnqueueRejectsBlankMessages()
    {
        SteeringQueue queue = new();

        Assert.ThrowsExactly<ArgumentException>(() => queue.Enqueue("   "));
    }
}
