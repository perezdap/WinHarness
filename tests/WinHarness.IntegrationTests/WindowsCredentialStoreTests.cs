using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Platform;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class WindowsCredentialStoreTests
{
    [TestMethod]
    public async Task StoresReadsAndDeletesSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Credential Manager test requires Windows.");
        }

        WindowsCredentialStore store = new();
        string target = "WinHarness:Test:" + Guid.NewGuid().ToString("N");

        await store.SetSecretAsync(target, "secret-value", CancellationToken.None);
        string? secret = await store.GetSecretAsync(target, CancellationToken.None);
        IReadOnlyList<string> targetNames = await store.ListTargetNamesAsync(CancellationToken.None);
        await store.DeleteSecretAsync(target, CancellationToken.None);
        string? deleted = await store.GetSecretAsync(target, CancellationToken.None);

        Assert.AreEqual("secret-value", secret);
        CollectionAssert.Contains(targetNames.ToList(), target);
        Assert.IsNull(deleted);
    }

    [TestMethod]
    public async Task StoresReadsAndDeletesLargeSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Credential Manager test requires Windows.");
        }

        WindowsCredentialStore store = new();
        string target = "WinHarness:TestLarge:" + Guid.NewGuid().ToString("N");
        string secret = string.Concat(Enumerable.Repeat("🔐oauth-token-", 500));

        await store.SetSecretAsync(target, secret, CancellationToken.None);
        string? stored = await store.GetSecretAsync(target, CancellationToken.None);
        IReadOnlyList<string> targetNames = await store.ListTargetNamesAsync(CancellationToken.None);
        await store.DeleteSecretAsync(target, CancellationToken.None);
        string? deleted = await store.GetSecretAsync(target, CancellationToken.None);

        Assert.AreEqual(secret, stored);
        CollectionAssert.Contains(targetNames.ToList(), target);
        Assert.IsFalse(targetNames.Any(name => name.StartsWith(target + ":chunk:", StringComparison.Ordinal)));
        Assert.IsNull(deleted);
    }

    [TestMethod]
    public async Task PreservesTargetNamesContainingChunkMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Credential Manager test requires Windows.");
        }

        WindowsCredentialStore store = new();
        string target = "WinHarness:Test:chunk:" + Guid.NewGuid().ToString("N") + ":0";

        await store.SetSecretAsync(target, "secret-value", CancellationToken.None);
        IReadOnlyList<string> targetNames = await store.ListTargetNamesAsync(CancellationToken.None);
        await store.DeleteSecretAsync(target, CancellationToken.None);

        CollectionAssert.Contains(targetNames.ToList(), target);
    }
}
