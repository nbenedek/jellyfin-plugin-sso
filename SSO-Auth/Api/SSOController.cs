using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// The sso api controller.
/// </summary>
[ApiController]
[Route("[controller]")]
public class SSOController : ControllerBase
{
    private static readonly SemaphoreSlim IdentityGate = new(1, 1);

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SSOController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OidcLoginTransactionStore _transactionStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOController}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="cryptoProvider">Instance of the <see cref="ICryptoProvider"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="transactionStore">OIDC login transaction store.</param>
    public SSOController(
        ILogger<SSOController> logger,
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        IUserManager userManager,
        ICryptoProvider cryptoProvider,
        IHttpClientFactory httpClientFactory,
        OidcLoginTransactionStore transactionStore)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _cryptoProvider = cryptoProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _transactionStore = transactionStore;
        _logger.LogInformation("SSO Controller initialized");
    }

    /// <summary>
    /// The GET endpoint for OpenID provider to callback to. Returns a webpage that parses client data and completes auth.
    /// </summary>
    /// <param name="provider">The ID of the provider which will use the callback information.</param>
    /// <param name="state">The current request state.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    // Actually a GET: https://github.com/IdentityModel/IdentityModel.OidcClient/issues/325
    [AllowAnonymous]
    [HttpGet("OID/redirect/{provider}")]
    public async Task<ActionResult> OidPost(
        [FromRoute] string provider,
        [FromQuery] string state)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config?.Enabled == true)
        {
            if (!OidcProviderSecurityPolicy.IsSecureForAuthentication(config))
            {
                return BadRequest(OidcProviderSecurityPolicy.ConfigurationError);
            }

            if (string.IsNullOrEmpty(state))
            {
                return BadRequest("Missing state");
            }

            if (!_transactionStore.TryGetPending(provider, state, out var transaction))
            {
                return BadRequest("Invalid or expired state");
            }

            var scopes = config.OidScopes == null ? new string[2] : config.OidScopes;
            var oidcClient = new OidcClient(OidcClientOptionsFactory.Create(
                config,
                GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/redirect/{provider}",
                scopes,
                _loggerFactory,
                () => _httpClientFactory.CreateClient()));
            var currentState = transaction.AuthorizeState;
            var result = await oidcClient.ProcessResponseAsync(Request.QueryString.Value, currentState).ConfigureAwait(false);

            if (result.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error logging in: {result.Error} - {result.ErrorDescription}");
            }

            if (result.User is null)
            {
                return ReturnError(StatusCodes.Status401Unauthorized, "OIDC response did not contain an authenticated principal");
            }

            // ProcessResponseAsync has already validated the ID token. The issuer and subject
            // must come from that token, not from optional UserInfo/display claims.
            if (!OidcIdentityKey.TryReadValidatedToken(result.IdentityToken, out var issuer, out var subject))
            {
                return ReturnError(StatusCodes.Status401Unauthorized, "OIDC identity token has no usable issuer or subject");
            }

            var principalSubject = result.User?.FindFirst("sub")?.Value;
            if (principalSubject != null && !string.Equals(principalSubject, subject, StringComparison.Ordinal))
            {
                return ReturnError(StatusCodes.Status401Unauthorized, "OIDC identity subject mismatch");
            }

            var evaluation = OidcClaimsEvaluator.Evaluate(config, result.User.Claims);

            if (evaluation.IsValid && string.IsNullOrWhiteSpace(evaluation.Username))
            {
                return ReturnError(StatusCodes.Status400BadRequest, "OIDC response did not contain a usable username claim");
            }

            if (evaluation.IsValid)
            {
                var validatedLogin = new OidcValidatedLogin(
                    issuer,
                    subject,
                    evaluation.Username,
                    evaluation.IsAdministrator,
                    evaluation.Folders,
                    evaluation.EnableLiveTv,
                    evaluation.EnableLiveTvManagement);

                if (!_transactionStore.TryMarkReady(provider, state, validatedLogin, out var exchangeToken))
                {
                    return BadRequest("Invalid or expired state");
                }

                _logger.LogInformation("OIDC callback validated for provider {Provider}", provider);
                return Content(WebResponse.Generator(data: exchangeToken, provider: provider, baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride), mode: "OID"), MediaTypeNames.Text.Html);
            }
            else
            {
                _logger.LogWarning(
                    "OIDC login rejected for provider {Provider}: expected roles not found. Claim types: {@ClaimTypes}",
                    provider,
                    result.User.Claims.Select(claim => claim.Type).Distinct().ToArray());

                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }
        }

        // If the config doesn't have an active provider matching the requeset, show an error
        return BadRequest("No matching provider found");
    }

    /// <summary>
    /// Initiates the login flow for OpenID. This redirects the user to the auth provider.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="isLinking">Whether or not this request is to link accounts (Rather than authenticate).</param>
    /// <returns>An asynchronous result for the authentication.</returns>
    [AllowAnonymous]
    [HttpGet("OID/p/{provider}")]
    [HttpGet("OID/start/{provider}")]
    public async Task<ActionResult> OidChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        if (isLinking)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Self-service OIDC linking is disabled.");
        }

        if (!SSOPlugin.Instance.Configuration.OidConfigs.TryGetValue(provider, out var config) || config?.Enabled != true)
        {
            return BadRequest("No matching provider found");
        }

        if (!OidcProviderSecurityPolicy.IsSecureForAuthentication(config))
        {
            return BadRequest(OidcProviderSecurityPolicy.ConfigurationError);
        }

        if (config.Enabled)
        {
            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/redirect/{provider}";

            var oidcClient = new OidcClient(OidcClientOptionsFactory.Create(
                config,
                redirectUri,
                config.OidScopes ?? Array.Empty<string>(),
                _loggerFactory,
                () => _httpClientFactory.CreateClient()));
            var state = await oidcClient.PrepareLoginAsync().ConfigureAwait(false);

            if (state.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error preparing login: {state.Error} - {state.ErrorDescription}");
            }

            _transactionStore.Register(
                provider,
                state,
                OidcLoginPurpose.Authenticate);

            return Redirect(state.StartUrl);
        }

        return BadRequest("No matching provider found");
    }

    /// <summary>
    /// Adds an OpenID auth configuration. Requires administrator privileges. If the provider already exists, it will be removed and readded.
    /// </summary>
    /// <param name="provider">The name of the provider to add.</param>
    /// <param name="config">The OID configuration (deserialized from a JSON post).</param>
    /// <returns>204 when saved, or 400 for invalid input.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("OID/Add/{provider}")]
    public ActionResult OidAdd(string provider, [FromBody] OidConfig config)
    {
        if (string.IsNullOrWhiteSpace(provider) || config is null)
        {
            return BadRequest("A provider name and configuration are required.");
        }

        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs[provider] = config;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return NoContent();
    }

    /// <summary>
    /// Deletes an OpenID provider.
    /// </summary>
    /// <param name="provider">Name of provider to delete.</param>
    /// <returns>204 when deleted, or 404 for an unknown provider.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("OID/Del/{provider}")]
    public ActionResult OidDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        if (!configuration.OidConfigs.Remove(provider))
        {
            return NotFound("No matching provider found");
        }

        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return NoContent();
    }

    /// <summary>
    /// Lists the OpenID providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Get")]
    public ActionResult OidProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs);
    }

    /// <summary>
    /// Lists the OpenID providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [AllowAnonymous]
    [HttpGet("OID/GetNames")]
    public ActionResult OidProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs
            .Where(provider => provider.Value?.Enabled == true)
            .Select(provider => provider.Key)
            .ToArray());
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">Name of provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [AllowAnonymous]
    [HttpPost("OID/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidAuth(string provider, [FromBody] AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config?.Enabled != true)
        {
            return BadRequest("No matching provider found");
        }

        if (!OidcProviderSecurityPolicy.IsSecureForAuthentication(config))
        {
            return BadRequest(OidcProviderSecurityPolicy.ConfigurationError);
        }

        if (string.IsNullOrWhiteSpace(response?.Data))
        {
            return BadRequest("Missing OIDC exchange token");
        }

        if (!_transactionStore.TryConsume(provider, response.Data, OidcLoginPurpose.Authenticate, out var transaction))
        {
            return Unauthorized("Invalid, expired, or already-used login transaction");
        }

        var login = transaction.ValidatedLogin;
        Guid userId = await ResolveOidcIdentityAsync(provider, login).ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Conflict("This account requires explicit owner-authorized SSO linking or migration.");
        }

        var authenticationResult = await Authenticate(
            userId,
            login.IsAdministrator,
            config.EnableAuthorization,
            config.EnableAllFolders,
            login.Folders,
            login.EnableLiveTv,
            login.EnableLiveTvManagement,
            response,
            config.DefaultProvider?.Trim())
            .ConfigureAwait(false);

        return Ok(authenticationResult);
    }

    /// <summary>
    /// Removes a user from SSO auth and switches it back to another auth provider. Requires administrator privileges.
    /// </summary>
    /// <param name="username">The username to switch to the new provider.</param>
    /// <param name="provider">The new provider to switch to.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Unregister/{username}")]
    public async Task<ActionResult> Unregister(string username, [FromBody] string provider)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(provider))
        {
            return BadRequest("A username and authentication provider are required.");
        }

        User user = _userManager.GetUserByName(username);
        if (user is null)
        {
            return NotFound("No matching Jellyfin user found.");
        }

        user.AuthenticationProviderId = provider;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<Guid> ResolveOidcIdentityAsync(string provider, OidcValidatedLogin login)
    {
        await IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = SSOPlugin.Instance.Configuration;
            if (!configuration.OidConfigs.TryGetValue(provider, out var config) || config?.Enabled != true)
            {
                _logger.LogWarning("OIDC resolver rejected sign-in: provider {Provider} not configured or disabled.", provider);
                return Guid.Empty;
            }

            // Only a short prefix of the already-hashed issuer/subject identity key
            // is logged. Never log the raw claims, ID token, credentials, or username.
            var identityFingerprint = login.IdentityKey.Substring("oidc-v1-".Length, 12);
            var userId = OidcIdentityLinks.Find(config, login.IdentityKey);
            if (userId != Guid.Empty)
            {
                if (_userManager.GetUserById(userId) is null)
                {
                    // A stale mapping must not silently create or claim another account.
                    _logger.LogWarning(
                        "OIDC resolver rejected sign-in: linked Jellyfin account missing. Provider {Provider}; fingerprint {Fingerprint}.",
                        provider,
                        identityFingerprint);
                    return Guid.Empty;
                }

                if (!config.SubjectNames.TryGetValue(login.IdentityKey, out var name)
                    || !string.Equals(name, login.Username, StringComparison.Ordinal))
                {
                    config.SubjectNames[login.IdentityKey] = login.Username;
                    SSOPlugin.Instance.UpdateConfiguration(configuration);
                }

                return userId;
            }

            // There must be no fallback to CanonicalLinks or GetUserByName for authentication.
            // Users with pre-existing Jellyfin or legacy SSO accounts must explicitly link
            // from a session authorized to modify that account.
            var existingUsername = _userManager.GetUserByName(login.Username) is not null;
            var legacyUsernameMapping = config.CanonicalLinks.ContainsKey(login.Username);
            if (existingUsername || legacyUsernameMapping)
            {
                _logger.LogWarning(
                    "OIDC resolver rejected sign-in: no subject link matches. Provider {Provider}; fingerprint {Fingerprint}; issuerMatchesAuthority {IssuerMatchesAuthority}; subjectLinks {SubjectLinks}; linkedDisplayNameMatches {DisplayNameMatches}; jellyfinUsernameExists {UsernameExists}; legacyUsernameMapping {LegacyUsernameMapping}.",
                    provider,
                    identityFingerprint,
                    string.Equals(login.Issuer, config.OidEndpoint?.Trim(), StringComparison.Ordinal),
                    config.SubjectLinks.Count,
                    config.SubjectNames.Values.Any(name => string.Equals(name, login.Username, StringComparison.Ordinal)),
                    existingUsername,
                    legacyUsernameMapping);
                return Guid.Empty;
            }

            var user = await _userManager.CreateUserAsync(login.Username).ConfigureAwait(false);
            user.AuthenticationProviderId = GetType().FullName;
            user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

            if (!OidcIdentityLinks.TryLink(config, login.IdentityKey, login.Username, user.Id))
            {
                return Guid.Empty;
            }

            SSOPlugin.Instance.UpdateConfiguration(configuration);
            return user.Id;
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    /// <summary>
    /// Administratively associates an OIDC issuer and subject with an existing Jellyfin user.
    /// </summary>
    /// <param name="provider">Configured OIDC provider.</param>
    /// <param name="jellyfinUserId">Existing Jellyfin account to associate.</param>
    /// <param name="request">Issuer, subject, and display name entered by the administrator.</param>
    /// <returns>204 on success, or an error for invalid or conflicting mappings.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("oid/Link/{provider}/{jellyfinUserId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> AddCanonicalLink(
        [FromRoute] string provider,
        [FromRoute] Guid jellyfinUserId,
        [FromBody] AdminOidcLinkRequest request)
    {
        if (request is null || !request.IsValid())
        {
            return BadRequest("A valid HTTPS issuer, subject, and display username are required.");
        }

        if (jellyfinUserId == Guid.Empty || _userManager.GetUserById(jellyfinUserId) is null)
        {
            return NotFound("Target Jellyfin user does not exist.");
        }

        await IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = SSOPlugin.Instance.Configuration;
            if (!configuration.OidConfigs.TryGetValue(provider, out var config) || !config.Enabled)
            {
                return NotFound("Unknown or disabled OIDC provider.");
            }

            var identityKey = OidcIdentityKey.Create(request.Issuer, request.Subject);
            if (!OidcIdentityLinks.TryLink(config, identityKey, request.Username, jellyfinUserId))
            {
                return Conflict("This OIDC identity is already linked to another Jellyfin user.");
            }

            SSOPlugin.Instance.UpdateConfiguration(configuration);
            return NoContent();
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    /// <summary>
    /// Unregisters a given mapping from id within provider to user.
    /// </summary>
    /// <param name="mode">The mode of the function; currently only OID is supported.</param>
    /// <param name="provider">The name of the provider from which the link should be removed.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to unlink from the provider.</param>
    /// <param name="canonicalName">The user ID within jellyfin to unlink.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("{mode}/Link/{provider}/{jellyfinUserId}/{canonicalName}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> DeleteCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromRoute] string canonicalName)
    {
        if (!mode.Equals("oid", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only OIDC links are supported.");
        }

        await IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = SSOPlugin.Instance.Configuration;
            if (!configuration.OidConfigs.TryGetValue(provider, out var config))
            {
                return NotFound("Unknown OIDC provider.");
            }

            var result = OidcIdentityLinks.Unlink(config, jellyfinUserId, canonicalName);
            if (result == OidcUnlinkResult.Ambiguous)
            {
                return Conflict("Multiple OIDC identities have this display name.");
            }

            if (result == OidcUnlinkResult.NotFound)
            {
                return NotFound("No matching link owned by this user.");
            }

            SSOPlugin.Instance.UpdateConfiguration(configuration);
            return NoContent();
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    /// <summary>
    /// Gets all the oid links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("oid/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<SerializableDictionary<string, IEnumerable<string>>> GetOidLinksByUser(Guid jellyfinUserId)
    {
        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.OidConfigs;

        foreach (var providerName in providerList.Keys)
        {
            mappings[providerName] = OidcIdentityLinks.ListNames(providerList[providerName], jellyfinUserId);
        }

        return mappings;
    }

    /// <summary>
    /// Authenticates the user with the given information.
    /// </summary>
    /// <param name="userId">The user id of the user to authenticate.</param>
    /// <param name="isAdmin">Determines whether this user is an administrator.</param>
    /// <param name="enableAuthorization">Determines whether RBAC is used for this user.</param>
    /// <param name="enableAllFolders">Determines whether all folders are enabled.</param>
    /// <param name="enabledFolders">Determines which folders should be enabled for this client.</param>
    /// <param name="enableLiveTv">Determines whether live TV access is allowed for this user.</param>
    /// <param name="enableLiveTvAdmin">Determines whether live TV can be managed by this user.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <param name="defaultProvider">The default provider of the user to be set after logging in.</param>
    private async Task<AuthenticationResult> Authenticate(Guid userId, bool isAdmin, bool enableAuthorization, bool enableAllFolders, string[] enabledFolders, bool enableLiveTv, bool enableLiveTvAdmin, AuthResponse authResponse, string defaultProvider)
    {
        User user = _userManager.GetUserById(userId);
        if (enableAuthorization)
        {
            // Jellyfin persists permission and preference rows through UpdatePolicyAsync.
            // Updating the user entity alone can leave policy changes only in memory.
            var policy = _userManager.GetUserDto(user).Policy;
            OidcAuthorizationPolicy.Apply(
                policy,
                enableAuthorization,
                isAdmin,
                enableAllFolders,
                enabledFolders,
                enableLiveTv,
                enableLiveTvAdmin);
            OidcAuthorizationPolicy.PrepareForPersistence(
                policy,
                userId,
                user.HasPermission(PermissionKind.EnableLyricManagement));

            if (!string.IsNullOrEmpty(defaultProvider))
            {
                policy.AuthenticationProviderId = defaultProvider;
            }

            await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
            user = _userManager.GetUserById(userId);
        }
        else if (!string.IsNullOrEmpty(defaultProvider))
        {
            // A disabled authorization provider must not rewrite any Jellyfin permissions.
            user.AuthenticationProviderId = defaultProvider;
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(defaultProvider))
        {
            _logger.LogInformation("Set default login provider to {Provider}", defaultProvider);
        }

        var authRequest = new AuthenticationRequest();
        authRequest.UserId = user.Id;
        authRequest.Username = user.Username;
        authRequest.App = authResponse.AppName;
        authRequest.AppVersion = authResponse.AppVersion;
        authRequest.DeviceId = authResponse.DeviceID;
        authRequest.DeviceName = authResponse.DeviceName;
        _logger.LogInformation("Auth request created...");

        return await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
    }

    private string GetRequestBase(string schemeOverride = null, int? portOverride = null)
    {
        int requestPort;

        if (portOverride != null)
        {
            requestPort = portOverride.Value;
        }
        else
        {
            requestPort = Request.Host.Port ?? -1;
        }

        if ((requestPort == 80 && string.Equals(Request.Scheme, "http", StringComparison.OrdinalIgnoreCase)) || (requestPort == 443 && string.Equals(Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            requestPort = -1;
        }

        if (schemeOverride != "http" && schemeOverride != "https")
        {
            schemeOverride = null;
        }

        return new UriBuilder
        {
            Scheme = schemeOverride ?? Request.Scheme,
            Host = Request.Host.Host,
            Port = requestPort,
            Path = Request.PathBase
        }.ToString().TrimEnd('/');
    }

    private ContentResult ReturnError(int code, string message)
    {
        var errorResult = new ContentResult();
        errorResult.Content = message;
        errorResult.ContentType = MediaTypeNames.Text.Plain;
        errorResult.StatusCode = code;
        return errorResult;
    }
}

/// <summary>
/// The data the client should pass back to the API.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the auth data of the client (for authorizing the response).
    /// </summary>
    public string Data { get; set; }
}
