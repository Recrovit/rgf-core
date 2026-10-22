using Xunit;

namespace RGF.Core.Recroby.LiveTests;

[Collection(LiveTestCollection.Name)]
public sealed class RecrobyLiveAuthenticationTests(LiveTestFixture fixture)
{
    public static bool LiveEnabled => LiveTestFixture.LiveEnabled;

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Requires live opt-in and the local Duende demo environment.")]
    public async Task ConfiguredClientAuthenticatesRealUserAgainstBackend()
    {
        var catalog = await fixture.ReadCatalogAsync();
        var provider = catalog.GetProperty("defaultProvider").GetString();
        Assert.False(string.IsNullOrWhiteSpace(provider));
        Assert.Contains(catalog.GetProperty("providers").EnumerateArray(), item =>
            item.GetProperty("id").GetString() == provider
            && !string.IsNullOrWhiteSpace(item.GetProperty("defaultModel").GetString()));
    }
}
