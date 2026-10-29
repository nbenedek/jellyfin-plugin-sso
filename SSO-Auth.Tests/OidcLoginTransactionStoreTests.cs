using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcLoginTransactionStoreTests
{
    [Fact]
    public void ExchangeTokenIsProviderBoundAndOneTime()
    {
        var store = new OidcLoginTransactionStore();
        store.Register("keycloak", MakeAuthorizeState("protocol-state"), OidcLoginPurpose.Authenticate);

        Assert.True(store.TryMarkReady("keycloak", "protocol-state", MakeValidatedLogin(), out var exchangeToken));
        Assert.NotEqual("protocol-state", exchangeToken);

        Assert.False(store.TryConsume("other-provider", exchangeToken, OidcLoginPurpose.Authenticate, out _));
        Assert.True(store.TryConsume("keycloak", exchangeToken, OidcLoginPurpose.Authenticate, out var transaction));
        Assert.Equal(OidcLoginTransactionStatus.Consumed, transaction.Status);
        Assert.False(store.TryConsume("keycloak", exchangeToken, OidcLoginPurpose.Authenticate, out _));
    }

    [Fact]
    public void ExpiredProtocolStateCannotBeCompleted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-29T20:00:00+01:00"));
        var store = new OidcLoginTransactionStore(clock, TimeSpan.FromMinutes(1));
        store.Register("keycloak", MakeAuthorizeState("protocol-state"), OidcLoginPurpose.Authenticate);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(store.TryGetPending("keycloak", "protocol-state", out _));
        Assert.False(store.TryMarkReady("keycloak", "protocol-state", MakeValidatedLogin(), out _));
    }

    [Fact]
    public void ProviderMismatchCannotAccessPendingState()
    {
        var store = new OidcLoginTransactionStore();
        store.Register("keycloak", MakeAuthorizeState("protocol-state"), OidcLoginPurpose.Authenticate);

        Assert.False(store.TryGetPending("other-provider", "protocol-state", out _));
        Assert.False(store.TryMarkReady("other-provider", "protocol-state", MakeValidatedLogin(), out _));
    }

    [Fact]
    public void PurposeMismatchDoesNotConsumeToken()
    {
        var store = new OidcLoginTransactionStore();
        store.Register("keycloak", MakeAuthorizeState("protocol-state"), OidcLoginPurpose.Link);
        Assert.True(store.TryMarkReady("keycloak", "protocol-state", MakeValidatedLogin(), out var exchangeToken));

        Assert.False(store.TryConsume("keycloak", exchangeToken, OidcLoginPurpose.Authenticate, out _));
        Assert.True(store.TryConsume("keycloak", exchangeToken, OidcLoginPurpose.Link, out _));
    }

    [Fact]
    public void ExpiredExchangeTokenCannotBeConsumed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-29T20:00:00+01:00"));
        var store = new OidcLoginTransactionStore(clock, TimeSpan.FromMinutes(1));
        store.Register("keycloak", MakeAuthorizeState("protocol-state"), OidcLoginPurpose.Authenticate);
        Assert.True(store.TryMarkReady("keycloak", "protocol-state", MakeValidatedLogin(), out var exchangeToken));

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(store.TryConsume("keycloak", exchangeToken, OidcLoginPurpose.Authenticate, out _));
    }

    [Fact]
    public async Task ConcurrentFlowsRemainIndependent()
    {
        var store = new OidcLoginTransactionStore();
        var tokens = new ConcurrentBag<string>();

        await Task.WhenAll(
            Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            {
                var state = $"state-{i}";
                store.Register("keycloak", MakeAuthorizeState(state), OidcLoginPurpose.Authenticate);
                Assert.True(store.TryMarkReady("keycloak", state, MakeValidatedLogin(), out var token));
                tokens.Add(token);
            })));

        Assert.Equal(50, tokens.Distinct(StringComparer.Ordinal).Count());
        foreach (var token in tokens)
        {
            Assert.True(store.TryConsume("keycloak", token, OidcLoginPurpose.Authenticate, out _));
        }
    }

    private static AuthorizeState MakeAuthorizeState(string state)
        => new()
        {
            State = state,
            StartUrl = "https://idp.example/authorize"
        };

    private static OidcValidatedLogin MakeValidatedLogin()
        => new(
            "benedek",
            false,
            Array.Empty<string>(),
            false,
            false);

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
