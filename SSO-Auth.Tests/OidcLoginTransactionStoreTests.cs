using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcLoginTransactionStoreTests
{
    [Fact]
    public void ExchangeTokenIsProviderBoundAndOneTime()
    {
        var store = new OidcLoginTransactionStore();
        store.Register("keycloak", "protocol-state");

        Assert.True(store.TryMarkReady("keycloak", "protocol-state", out var exchangeToken));
        Assert.NotNull(exchangeToken);
        Assert.NotEqual("protocol-state", exchangeToken);

        Assert.False(store.TryConsume("other-provider", exchangeToken, out _));
        Assert.True(store.TryConsume("keycloak", exchangeToken, out var transaction));
        Assert.Equal(OidcLoginTransactionStatus.Consumed, transaction.Status);
        Assert.False(store.TryConsume("keycloak", exchangeToken, out _));
    }

    [Fact]
    public void ExpiredProtocolStateCannotBeCompleted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-29T20:00:00+01:00"));
        var store = new OidcLoginTransactionStore(clock, TimeSpan.FromMinutes(1));

        store.Register("keycloak", "protocol-state");
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(store.TryMarkReady("keycloak", "protocol-state", out _));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
