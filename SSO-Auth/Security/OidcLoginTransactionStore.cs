using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Stores short-lived, provider-bound OIDC login transactions.
/// </summary>
/// <remarks>
/// This class is intentionally not wired into the production flow yet. It establishes
/// the state-management API that will replace the controller's static dictionary in
/// the next refactor step.
/// </remarks>
public sealed class OidcLoginTransactionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OidcLoginTransaction> _pendingByProtocolState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OidcLoginTransaction> _readyByExchangeToken = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginTransactionStore"/> class.
    /// </summary>
    /// <param name="timeProvider">Time source used for expiry checks.</param>
    /// <param name="lifetime">Maximum lifetime of a login transaction.</param>
    public OidcLoginTransactionStore(TimeProvider timeProvider = null, TimeSpan? lifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromMinutes(2);

        if (_lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Transaction lifetime must be positive.");
        }
    }

    /// <summary>
    /// Registers a pending OIDC protocol state for a provider.
    /// </summary>
    /// <param name="provider">Configured provider name.</param>
    /// <param name="protocolState">OIDC protocol state returned by the client library.</param>
    public void Register(string provider, string protocolState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolState);

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (_pendingByProtocolState.ContainsKey(protocolState))
            {
                throw new InvalidOperationException("An OIDC transaction with this protocol state already exists.");
            }

            var now = _timeProvider.GetUtcNow();
            _pendingByProtocolState.Add(
                protocolState,
                new OidcLoginTransaction(
                    provider,
                    protocolState,
                    now,
                    now.Add(_lifetime),
                    OidcLoginTransactionStatus.PendingCallback,
                    null));
        }
    }

    /// <summary>
    /// Marks a validated callback ready for one-time exchange.
    /// </summary>
    /// <param name="provider">Configured provider name.</param>
    /// <param name="protocolState">OIDC protocol state being completed.</param>
    /// <param name="exchangeToken">Generated one-time browser exchange token.</param>
    /// <returns><see langword="true"/> when the pending transaction was found and transitioned.</returns>
    public bool TryMarkReady(string provider, string protocolState, out string exchangeToken)
    {
        exchangeToken = null;

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (!_pendingByProtocolState.TryGetValue(protocolState, out var pending)
                || !string.Equals(pending.Provider, provider, StringComparison.Ordinal))
            {
                return false;
            }

            _pendingByProtocolState.Remove(protocolState);

            var token = CreateExchangeToken();
            while (_readyByExchangeToken.ContainsKey(token))
            {
                token = CreateExchangeToken();
            }

            _readyByExchangeToken.Add(
                token,
                new OidcLoginTransaction(
                    pending.Provider,
                    pending.ProtocolState,
                    pending.CreatedAt,
                    pending.ExpiresAt,
                    OidcLoginTransactionStatus.ReadyForExchange,
                    token));

            exchangeToken = token;
            return true;
        }
    }

    /// <summary>
    /// Atomically consumes a provider-bound exchange token.
    /// </summary>
    /// <param name="provider">Configured provider name.</param>
    /// <param name="exchangeToken">One-time exchange token.</param>
    /// <param name="transaction">Consumed transaction metadata.</param>
    /// <returns><see langword="true"/> only on the first valid consumption.</returns>
    public bool TryConsume(string provider, string exchangeToken, out OidcLoginTransaction transaction)
    {
        transaction = null;

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (!_readyByExchangeToken.TryGetValue(exchangeToken, out var ready)
                || !string.Equals(ready.Provider, provider, StringComparison.Ordinal))
            {
                return false;
            }

            _readyByExchangeToken.Remove(exchangeToken);
            transaction = new OidcLoginTransaction(
                ready.Provider,
                ready.ProtocolState,
                ready.CreatedAt,
                ready.ExpiresAt,
                OidcLoginTransactionStatus.Consumed,
                ready.ExchangeToken);
            return true;
        }
    }

    private void PurgeExpiredLocked()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var key in new List<string>(_pendingByProtocolState.Keys))
        {
            if (_pendingByProtocolState[key].ExpiresAt <= now)
            {
                _pendingByProtocolState.Remove(key);
            }
        }

        foreach (var key in new List<string>(_readyByExchangeToken.Keys))
        {
            if (_readyByExchangeToken[key].ExpiresAt <= now)
            {
                _readyByExchangeToken.Remove(key);
            }
        }
    }

    private static string CreateExchangeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>
/// Lifecycle state of an OIDC login transaction.
/// </summary>
public enum OidcLoginTransactionStatus
{
    /// <summary>Waiting for the validated OIDC callback.</summary>
    PendingCallback = 0,

    /// <summary>Validated and ready for a one-time browser exchange.</summary>
    ReadyForExchange = 1,

    /// <summary>The one-time browser exchange has been consumed.</summary>
    Consumed = 2
}

/// <summary>
/// Immutable metadata for an OIDC login transaction.
/// </summary>
public sealed class OidcLoginTransaction
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginTransaction"/> class.
    /// </summary>
    public OidcLoginTransaction(
        string provider,
        string protocolState,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        OidcLoginTransactionStatus status,
        string exchangeToken)
    {
        Provider = provider;
        ProtocolState = protocolState;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = status;
        ExchangeToken = exchangeToken;
    }

    /// <summary>Gets the configured provider name.</summary>
    public string Provider { get; }

    /// <summary>Gets the OIDC protocol state.</summary>
    public string ProtocolState { get; }

    /// <summary>Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the hard expiry time.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the current transaction status.</summary>
    public OidcLoginTransactionStatus Status { get; }

    /// <summary>Gets the one-time exchange token after callback validation.</summary>
    public string ExchangeToken { get; }
}
