using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

/// <summary>
/// Characterizes the existing browser handoff before Stage 7 changes its HTML or JavaScript.
/// These checks intentionally avoid rewriting the handoff implementation.
/// </summary>
public sealed class WebResponseCompatibilityTests
{
    [Fact]
    public void CallbackHandoffPostsToTheExistingOidcExchangeRoute()
    {
        string html = WebResponse.Generator(
            "one-time-test-exchange",
            "test-provider",
            "https://jellyfin.example",
            "OID");

        Assert.Contains("https://jellyfin.example/sso/OID/Auth/test-provider", html);
        Assert.Contains("var data = 'one-time-test-exchange';", html);
        Assert.Contains("xhr.open('POST', url, true);", html);
        Assert.Contains("xhr.setRequestHeader('Content-Type', 'application/json');", html);
    }

    [Fact]
    public void CallbackHandoffRetainsJellyfinCredentialsAndRedirectCompatibility()
    {
        string html = WebResponse.Generator(
            "one-time-test-exchange",
            "test-provider",
            "https://jellyfin.example",
            "OID");

        Assert.Contains("localStorage.getItem(\"jellyfin_credentials\")", html);
        Assert.Contains("localStorage.getItem(\"_deviceId2\")", html);
        Assert.Contains("localStorage.setItem('jellyfin_credentials', JSON.stringify(jfCreds));", html);
        Assert.Contains("localStorage.setItem('enableAutoLogin', 'true');", html);
        Assert.Contains("window.location.replace('https://jellyfin.example/web/index.html');", html);
    }

    [Fact]
    public void CallbackHandoffRetainsEmbeddedJellyfinWebBootstrap()
    {
        string html = WebResponse.Generator(
            "one-time-test-exchange",
            "test-provider",
            "https://jellyfin.example",
            "OID");

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("document.getElementById('iframe-main').src = 'https://jellyfin.example/web/index.html';", html);
        Assert.Contains("sandbox='allow-same-origin allow-forms allow-scripts'", html);
        Assert.Contains("document.addEventListener('DOMContentLoaded'", html);
    }
}
