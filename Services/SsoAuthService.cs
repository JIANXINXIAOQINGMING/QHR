using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QHR.Services;

public sealed record SsoLoginResult(string Username, string AccessToken);

public sealed class SsoAuthService
{
    private const string PublicKeyBase64 =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAre5YnMJtO+gtwkeeq07f5UfywHM2LQ5T4jzVzYUYJOQN0AUAYkmoIt7rIfAN6q5nEby3zupXznBo/Y5SsRtXDoG53xHucpqE5SXD4J6kNnxj+JjecQ7ef0ev5MTOb+eREybymosgs7xr/eprv2O4GmipUwXVTWusbL/xjPzfP603JGz/r6xR94k5K8NXqHLQVBKURa5QK3x9sUyX5ZxYop6llF3BdkIafB8aERw5iJa7i8fFK6UmIbhGQ8rLYYGj61229NayMgIuWJ3SGmUWWq0RyCEjt96I6ZWwOqygkiEXj3PoQEmTUIxmEgrWQ5UxSHT/XDai5sbj7IueMqncPQIDAQAB";

    private static readonly Uri[] Endpoints =
    [
        new("https://sso-web.quectel.com/api/uaa/oauth/token"),
        new("https://sso-web.quectel.com/uaa/oauth/token")
    ];

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<SsoLoginResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("请输入账号和密码");
        }

        var encryptedPassword = EncryptPassword(password);
        var errors = new List<string>();

        foreach (var endpoint in Endpoints)
        {
            var endpointUnavailable = false;
            foreach (var candidate in BuildUsernameCandidates(username))
            {
                try
                {
                    var token = await LoginOnceAsync(
                        endpoint,
                        candidate,
                        encryptedPassword,
                        GetAuthType(candidate),
                        cancellationToken);
                    return new SsoLoginResult(candidate, token);
                }
                catch (SsoEndpointUnavailableException ex)
                {
                    errors.Add(ex.Message);
                    endpointUnavailable = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{candidate}: {ex.Message}");
                }
            }

            if (!endpointUnavailable && errors.Count > 0)
            {
                break;
            }
        }

        throw new InvalidOperationException(errors.LastOrDefault() ?? "SSO 登录失败");
    }

    private async Task<string> LoginOnceAsync(
        Uri endpoint,
        string username,
        string encryptedPassword,
        string authType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = encryptedPassword,
                ["scope"] = "ui",
                ["client_id"] = "quectel",
                ["client_secret"] = "quectel",
                ["auth_type"] = authType
            })
        };
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new SsoEndpointUnavailableException($"SSO 端点不可用 ({(int)response.StatusCode})");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadErrorMessage(body, $"HTTP {(int)response.StatusCode}"));
        }

        using var document = JsonDocument.Parse(body);
        var token = FindToken(document.RootElement);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(ReadErrorMessage(body, "响应中没有 token"));
        }

        return NormalizeToken(token);
    }

    private static string EncryptPassword(string password)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1));
    }

    private static IReadOnlyList<string> BuildUsernameCandidates(string username)
    {
        var text = username.Trim();
        var candidates = new List<string>();
        if (text.Contains('@'))
        {
            candidates.Add(text.Split('@', 2)[0]);
        }
        candidates.Add(text);
        if (text.Contains('\\'))
        {
            candidates.Add(text[(text.LastIndexOf('\\') + 1)..]);
        }
        return candidates.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetAuthType(string username) =>
        username.Contains('@') ? "email" : username.All(char.IsDigit) ? "phone" : "rsa_area";

    private static string NormalizeToken(string token)
    {
        var text = token.Trim();
        if (text.StartsWith("bearer", StringComparison.OrdinalIgnoreCase))
        {
            return text[6..].Trim();
        }
        return text;
    }

    private static string? FindToken(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Name is "access_token" or "token" or "value" or "authorization" or "Authorization")
                {
                    return property.Value.GetString();
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                var value = FindToken(property.Value);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindToken(item);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static string ReadErrorMessage(string body, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "msg", "message", "error_description", "error" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? fallback;
                }
            }
        }
        catch (JsonException)
        {
        }
        return fallback;
    }

    private sealed class SsoEndpointUnavailableException(string message) : Exception(message);
}
