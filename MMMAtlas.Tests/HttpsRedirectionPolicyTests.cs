using Microsoft.Extensions.Configuration;
using MMMAtlas.Configuration;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class HttpsRedirectionPolicyTests
{
    [TestMethod]
    public void HttpOnlyBindingDoesNotEnableRedirection()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["urls"] = "http://localhost:5163"
        });

        var policy = HttpsRedirectionPolicy.From(configuration);

        Assert.IsFalse(policy.Enabled);
        Assert.IsNull(policy.HttpsPort);
    }

    [TestMethod]
    public void HttpsBindingEnablesRedirectionToItsPort()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["urls"] = "https://localhost:7176;http://localhost:5163"
        });

        var policy = HttpsRedirectionPolicy.From(configuration);

        Assert.IsTrue(policy.Enabled);
        Assert.AreEqual(7176, policy.HttpsPort);
    }

    [TestMethod]
    public void ExplicitRedirectionUsesTheConfiguredPublicPort()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["HttpsRedirection:HttpsPort"] = "8443"
        });

        var policy = HttpsRedirectionPolicy.From(configuration);

        Assert.IsTrue(policy.Enabled);
        Assert.AreEqual(8443, policy.HttpsPort);
    }

    private static IConfiguration CreateConfiguration(
        Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
