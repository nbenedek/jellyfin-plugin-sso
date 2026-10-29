using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Duende.IdentityModel.OidcClient;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Stores short-lived, provider-bound OIDC login transactions.
/// </summary>
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
    /// Registers a pending OIDC flow.
    /// </summary>
    public void Register(string provider, AuthorizeState authorizeState, OidcLoginPurpose purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(authorizeState);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizeState.State);

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (_pendingByProtocolState.ContainsKey(authorizeState.State))
            {
                throw new InvalidOperationException("An OIDC transaction with this protocol state already exists.");
            }

            var now = _timeProvider.GetUtcNow();
            _pendingByProtocolState.Add(
                authorizeState.State,
                new OidcLoginTransaction(
                    provider,
                    authorizeState,
                    purpose,
                    now,
                    now.Add(_lifetime),
                    OidcLoginTransactionStatus.PendingCallback,
                    null,
                    null));
        }
    }

    /// <summary>
    /// Gets an unexpired pending transaction bound to the specified provider.
    /// </summary>
    public bool TryGetPending(string provider, string protocolState, out OidcLoginTransaction transaction)
    {
        transaction = null;

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(protocolState))
        {
            return false;
        }

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (!_pendingByProtocolState.TryGetValue(protocolState, out var candidate)
                || !string.Equals(candidate.Provider, provider, StringComparison.Ordinal))
            {
                return false;
            }

            transaction = candidate;
            return true;
        }
    }

    /// <summary>
    /// Retires protocol state after a validated callback and issues a separate one-time exchange token.
    /// </summary>
    public bool TryMarkReady(
        string provider,
        string protocolState,
        OidcValidatedLogin validatedLogin,
        out string exchangeToken)
    {
        exchangeToken = null;
        ArgumentNullException.ThrowIfNull(validatedLogin);

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
                    pending.AuthorizeState,
                    pending.Purpose,
                    pending.CreatedAt,
                    pending.ExpiresAt,
                    OidcLoginTransactionStatus.ReadyForExchange,
                    token,
                    validatedLogin));

            exchangeToken = token;
            return true;
        }
    }

    /// <summary>
    /// Atomically consumes an unexpired, provider- and purpose-bound exchange token.
    /// </summary>
    public bool TryConsume(
        string provider,
        string exchangeToken,
        OidcLoginPurpose purpose,
        out OidcLoginTransaction transaction)
    {
        transaction = null;

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(exchangeToken))
        {
            return false;
        }

        lock (_gate)
        {
            PurgeExpiredLocked();

            if (!_readyByExchangeToken.TryGetValue(exchangeToken, out var ready)
                || !string.Equals(ready.Provider, provider, StringComparison.Ordinal)
                || ready.Purpose != purpose
                || ready.ValidatedLogin is null)
            {
                return false;
            }

            _readyByExchangeToken.Remove(exchangeToken);
            transaction = new OidcLoginTransaction(
                ready.Provider,
                ready.AuthorizeState,
                ready.Purpose,
                ready.CreatedAt,
                ready.ExpiresAt,
                OidcLoginTransactionStatus.Consumed,
                ready.ExchangeToken,
                ready.ValidatedLogin);
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
/// Purpose of an OIDC login transaction.
/// </summary>
public enum OidcLoginPurpose
{
    Authenticate = 0,
    Link = 1
}

/// <summary>
/// Lifecycle state of an OIDC login transaction.
/// </summary>
public enum OidcLoginTransactionStatus
{
    PendingCallback = 0,
    ReadyForExchange = 1,
    Consumed = 2
}

/// <summary>
/// Validated identity and authorization data derived from an OIDC response.
/// </summary>
public sealed class OidcValidatedLogin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcValidatedLogin"/> class.
    /// </summary>
    public OidcValidatedLogin(
        string username,
        bool isAdministrator,
        string[] folders,
        bool enableLiveTv,
        bool enableLiveTvManagement)
    {
        Username = username ?? throw new ArgumentNullException(nameof(username));
        IsAdministrator = isAdministrator;
        Folders = folders ?? Array.Empty<string>();
        EnableLiveTv = enableLiveTv;
        EnableLiveTvManagement = enableLiveTvManagement;
    }

    public string Username { get; }

    public bool IsAdministrator { get; }

    public string[] Folders { get; }

    public bool EnableLiveTv { get; }

    public bool EnableLiveTvManagement { get; }
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
        AuthorizeState authorizeState,
        OidcLoginPurpose purpose,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        OidcLoginTransactionStatus status,
        string exchangeToken,
        OidcValidatedLogin validatedLogin)
    {
        Provider = provider;
        AuthorizeState = authorizeState;
        Purpose = purpose;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = status;
        ExchangeToken = exchangeToken;
        ValidatedLogin = validatedLogin;
    }

    public string Provider { get; }

    public AuthorizeState AuthorizeState { get; }

    public OidcLoginPurpose Purpose { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public OidcLoginTransactionStatus Status { get; }

    public string ExchangeToken { get; }

    public OidcValidatedLogin ValidatedLogin { get; }
}
