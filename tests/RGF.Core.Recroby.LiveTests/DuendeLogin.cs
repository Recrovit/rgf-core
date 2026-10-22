using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace RGF.Core.Recroby.LiveTests;

internal sealed record UserAccessToken(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[protected user access token]";
}

internal static class DuendeLogin
{
    internal static async Task<UserAccessToken> AuthenticateAsync(LiveClientSettings settings, string username,
        string password, CancellationToken cancellationToken)
    {
        if (settings.Provider != "Duende")
            throw new InvalidOperationException("LiveTests currently automate only the configured Duende login; no provider fallback is used.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            using var discovery = await client.GetAsync(new Uri(settings.Authority, "/.well-known/openid-configuration"), cancellationToken);
            EnsureSuccess(discovery, "Duende discovery");
            using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.GetProperty("issuer").GetString()?.TrimEnd('/') != settings.Authority.AbsoluteUri.TrimEnd('/'))
                throw new InvalidOperationException("Duende discovery issuer differs from the configured authority.");
            var authorize = SameAuthority(settings, root.GetProperty("authorization_endpoint").GetString()!);
            var tokenEndpoint = SameAuthority(settings, root.GetProperty("token_endpoint").GetString()!);
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var url = new Uri(QueryHelpers.AddQueryString(authorize.AbsoluteUri, new Dictionary<string, string?>
            {
                ["client_id"] = settings.ClientId, ["response_type"] = "code", ["response_mode"] = "query",
                ["redirect_uri"] = settings.RedirectUri.AbsoluteUri, ["scope"] = string.Join(' ', settings.Scopes),
                ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["state"] = state
            }));
            string? code = null;
            var submitted = false;
            for (var step = 0; step < 16; step++)
            {
                if (IsCallback(url, settings.RedirectUri))
                {
                    code = ReadCallbackCode(url, settings.RedirectUri, state);
                    break;
                }
                SameAuthority(settings, url.AbsoluteUri);
                using var response = await client.GetAsync(url, cancellationToken);
                if (Redirect(response, url) is { } location) { url = location; continue; }
                EnsureSuccess(response, "Duende authorization");
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                if (submitted || !html.Contains("Input.Username", StringComparison.Ordinal))
                    throw new InvalidOperationException("Duende authorization requires an unsupported interaction (for example consent or MFA).");
                var fields = LoginFields(html);
                fields["Input.Username"] = username;
                fields["Input.Password"] = password;
                fields["Input.RememberLogin"] = "false";
                fields["Input.Button"] = "login";
                var actionMatch = Regex.Match(html, "<form\\b[^>]*\\baction=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                var action = actionMatch.Success ? new Uri(url, WebUtility.HtmlDecode(actionMatch.Groups[1].Value)) : url;
                SameAuthority(settings, action.AbsoluteUri);
                using var content = new FormUrlEncodedContent(fields);
                using var loginResponse = await client.PostAsync(action, content, cancellationToken);
                fields.Clear();
                url = Redirect(loginResponse, action)
                    ?? throw new InvalidOperationException($"Duende login was rejected (HTTP {(int)loginResponse.StatusCode}); check user credentials and login policy.");
                submitted = true;
            }
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Duende returned no authorization code within the redirect limit.");
            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret, ["redirect_uri"] = settings.RedirectUri.AbsoluteUri,
                ["code_verifier"] = verifier, ["code"] = code
            });
            using var tokenResponse = await client.PostAsync(tokenEndpoint, tokenContent, cancellationToken);
            EnsureSuccess(tokenResponse, "Duende token exchange");
            using var tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = tokens.RootElement.GetProperty("access_token").GetString();
            var tokenType = tokens.RootElement.GetProperty("token_type").GetString();
            var lifetime = tokens.RootElement.GetProperty("expires_in").GetInt32();
            if (string.IsNullOrWhiteSpace(accessToken) || !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || lifetime <= 0)
                throw new InvalidOperationException("Duende returned no usable bearer access token.");
            return new UserAccessToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(lifetime));
        }
        catch (HttpRequestException)
        {
            // Protocol URLs may contain one-time codes, so do not expose the original network exception.
            throw new InvalidOperationException("Duende could not be reached over trusted HTTPS; check the running IDP and development certificate.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Duende returned an invalid protocol response; response bodies are not logged.");
        }
    }

    internal static string ReadCallbackCode(Uri callback, Uri expected, string state)
    {
        if (!IsCallback(callback, expected)) throw new InvalidOperationException("Unexpected OIDC callback destination.");
        var query = QueryHelpers.ParseQuery(callback.Query);
        if (query.ContainsKey("error") || query["state"].Count != 1 || query["state"][0] != state
            || query["code"].Count != 1 || string.IsNullOrWhiteSpace(query["code"][0]))
            throw new InvalidOperationException("OIDC callback has an error, invalid state or missing authorization code.");
        return query["code"][0]!;
    }

    private static bool IsCallback(Uri actual, Uri expected) => actual.GetLeftPart(UriPartial.Path) == expected.GetLeftPart(UriPartial.Path)
        && string.IsNullOrEmpty(actual.Fragment) && string.IsNullOrEmpty(actual.UserInfo);

    private static Uri SameAuthority(LiveClientSettings settings, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Authority != settings.Authority.Authority || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Duende protocol navigation attempted to leave the configured HTTPS authority.");
        return uri;
    }

    private static Dictionary<string, string> LoginFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match tag in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var name = Regex.Match(tag.Value, "\\bname=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
            if (name is not ("Input.ReturnUrl" or "__RequestVerificationToken")) continue;
            fields[name] = WebUtility.HtmlDecode(Regex.Match(tag.Value, "\\bvalue=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
        }
        if (!fields.ContainsKey("__RequestVerificationToken") || !fields.ContainsKey("Input.ReturnUrl"))
            throw new InvalidOperationException("Duende login form is missing its antiforgery token or return URL.");
        return fields;
    }

    private static Uri? Redirect(HttpResponseMessage response, Uri source) =>
        response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            ? response.Headers.Location is { } location ? new Uri(source, location)
                : throw new InvalidOperationException("Duende redirect has no destination.")
            : null;

    private static void EnsureSuccess(HttpResponseMessage response, string stage)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{stage} failed (HTTP {(int)response.StatusCode}); response bodies are not logged.");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
