using MMMAtlas.Configuration;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class AtlasBuildPolicyTests
{
    [TestMethod]
    public void DevelopmentEnablesDebugDevelopmentDataAndTestFeatures()
    {
        var policy = AtlasBuildPolicy.For(AtlasBuildMode.Development);

        Assert.IsTrue(policy.DebugEnabled);
        Assert.IsTrue(policy.UsesDevelopmentDatabase);
        Assert.IsTrue(policy.TestFeaturesEnabled);
        Assert.AreEqual("MMMAtlasDevelopment", policy.DatabaseConnectionName);
    }

    [TestMethod]
    public void TestDisablesDebugButKeepsDevelopmentDataAndTestFeatures()
    {
        var policy = AtlasBuildPolicy.For(AtlasBuildMode.Test);

        Assert.IsFalse(policy.DebugEnabled);
        Assert.IsTrue(policy.UsesDevelopmentDatabase);
        Assert.IsTrue(policy.TestFeaturesEnabled);
        Assert.AreEqual("MMMAtlasDevelopment", policy.DatabaseConnectionName);
    }

    [TestMethod]
    public void ProductionDisablesDebugAndTestFeaturesAndUsesProductionData()
    {
        var policy = AtlasBuildPolicy.For(AtlasBuildMode.Production);

        Assert.IsFalse(policy.DebugEnabled);
        Assert.IsFalse(policy.UsesDevelopmentDatabase);
        Assert.IsFalse(policy.TestFeaturesEnabled);
        Assert.AreEqual("MMMAtlasProduction", policy.DatabaseConnectionName);
    }
}
