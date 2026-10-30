using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Duende.IdentityModel.OidcClient;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Purpose of an OIDC login transaction.
/// </summary>
public enum OidcLoginPurpose
{
    /// <summary>Authenticates the validated identity.</summary>
    Authenticate = 0,
    /// <summary>Links the validated identity to an existing Jellyfin account.</summary>
    Link = 1
}

/// <summary>
/// Lifecycle state of an OIDC login transaction.
/// </summary>
public enum OidcLoginTransactionStatus
{
    /// <summary>Awaiting the OIDC callback.</summary>
    PendingCallback = 0,
    /// <summary>Validated and available for a single exchange.</summary>
    ReadyForExchange = 1,
    /// <summary>The exchange token has already been consumed.</summary>
    Consumed = 2
}

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
    /// <param name="timeProvider">Clock used to determine transaction expiry.</param>
    /// <param name="lifetime">Maximum permitted transaction lifetime.</param>
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
    /// <param name="provider">Identity provider identifier.</param>
    /// <param name="authorizeState">The OIDC authorization state.</param>
    /// <param name="purpose">The permitted transaction purpose.</param>
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
    /// <param name="provider">Identity provider identifier.</param>
    /// <param name="protocolState">OIDC state to look up.</param>
    /// <param name="transaction">The matching transaction if found.</param>
    /// <returns>Whether a matching unexpired pending transaction exists.</returns>
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
    /// <param name="provider">Identity provider identifier.</param>
    /// <param name="protocolState">Validated OIDC protocol state.</param>
    /// <param name="validatedLogin">Validated login data.</param>
    /// <param name="exchangeToken">The new one-time exchange token.</param>
    /// <returns>Whether the pending transaction was successfully completed.</returns>
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
    /// <param name="provider">Identity provider identifier.</param>
    /// <param name="exchangeToken">Single-use token to consume.</param>
    /// <param name="purpose">Expected purpose of the exchange.</param>
    /// <param name="transaction">The consumed transaction if successful.</param>
    /// <returns>Whether a matching unused token was consumed.</returns>
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
/// Validated identity and authorization data derived from an OIDC response.
/// </summary>
public sealed class OidcValidatedLogin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcValidatedLogin"/> class.
    /// </summary>
    /// <param name="username">Validated display username.</param>
    /// <param name="isAdministrator">Whether the user has administrator privileges.</param>
    /// <param name="folders">Permitted Jellyfin folders.</param>
    /// <param name="enableLiveTv">Whether Live TV access is enabled.</param>
    /// <param name="enableLiveTvManagement">Whether Live TV management is enabled.</param>
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

    /// <summary>Gets the validated display username.</summary>
    public string Username { get; }

    /// <summary>Gets whether administrator privileges were granted.</summary>
    public bool IsAdministrator { get; }

    /// <summary>Gets the validated folder permissions.</summary>
    public string[] Folders { get; }

    /// <summary>Gets whether Live TV access is enabled.</summary>
    public bool EnableLiveTv { get; }

    /// <summary>Gets whether Live TV management is enabled.</summary>
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
    /// <param name="provider">Identity provider identifier.</param>
    /// <param name="authorizeState">OIDC authorization state.</param>
    /// <param name="purpose">Transaction purpose.</param>
    /// <param name="createdAt">Time at which the transaction was created.</param>
    /// <param name="expiresAt">Time at which the transaction expires.</param>
    /// <param name="status">Transaction lifecycle status.</param>
    /// <param name="exchangeToken">The one-time exchange token, if issued.</param>
    /// <param name="validatedLogin">Validated login data, if available.</param>
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

    /// <summary>Gets the configured OIDC provider.</summary>
    public string Provider { get; }

    /// <summary>Gets the OIDC authorization state.</summary>
    public AuthorizeState AuthorizeState { get; }

    /// <summary>Gets the transaction purpose.</summary>
    public OidcLoginPurpose Purpose { get; }

    /// <summary>Gets the transaction creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the expiration time.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the transaction status.</summary>
    public OidcLoginTransactionStatus Status { get; }

    /// <summary>Gets the single-use exchange token.</summary>
    public string ExchangeToken { get; }

    /// <summary>Gets the validated login data.</summary>
    public OidcValidatedLogin ValidatedLogin { get; }
}
