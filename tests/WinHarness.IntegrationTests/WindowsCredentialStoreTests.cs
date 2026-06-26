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
        await store.DeleteSecretAsync(target, CancellationToken.None);
        string? deleted = await store.GetSecretAsync(target, CancellationToken.None);

        Assert.AreEqual("secret-value", secret);
        Assert.IsNull(deleted);
    }
}
