using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ModelEffortShorthandTests
{
    [TestMethod]
    public void SplitsKnownEffortSuffix()
    {
        (string model, string? effort) = ChatRepl.SplitModelEffortShorthand("gpt-primary:high");

        Assert.AreEqual("gpt-primary", model);
        Assert.AreEqual("high", effort);
    }

    [TestMethod]
    public void KeepsColonModelIdsWithoutEffortSuffix()
    {
        // Ollama-style ids contain colons; ":cloud" is not an effort level.
        (string model, string? effort) = ChatRepl.SplitModelEffortShorthand("minimax-m3:cloud");

        Assert.AreEqual("minimax-m3:cloud", model);
        Assert.IsNull(effort);
    }

    [TestMethod]
    public void HandlesEffortSuffixOnColonModelId()
    {
        (string model, string? effort) = ChatRepl.SplitModelEffortShorthand("minimax-m3:cloud:medium");

        Assert.AreEqual("minimax-m3:cloud", model);
        Assert.AreEqual("medium", effort);
    }

    [TestMethod]
    public void IgnoresLeadingOrTrailingColons()
    {
        Assert.AreEqual((":high", (string?)null), ChatRepl.SplitModelEffortShorthand(":high"));
        Assert.AreEqual(("model:", (string?)null), ChatRepl.SplitModelEffortShorthand("model:"));
    }

    [TestMethod]
    public void PlainModelIdPassesThrough()
    {
        (string model, string? effort) = ChatRepl.SplitModelEffortShorthand("gpt-primary");

        Assert.AreEqual("gpt-primary", model);
        Assert.IsNull(effort);
    }
}
