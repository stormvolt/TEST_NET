using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

public sealed class AviHubOptions
{
    public string BaseUrl { get; set; } = "https://avihub.pesoexato.com/api/v3/";
    public string EquipmentsPath { get; set; } = "equipments";
}

public sealed class OAuthOptions
{
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "";
}

public sealed class OAuthTokenProvider(IHttpClientFactory httpClientFactory, IOptions<OAuthOptions> options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expiresAt;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        if (!Uri.TryCreate(config.TokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps)
            throw new OAuthConfigurationException("Configura OAuth:TokenUrl con la URL HTTPS del proveedor OAuth.");
        if (string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.ClientSecret))
            throw new OAuthConfigurationException("Configura OAuth:ClientId y OAuth:ClientSecret mediante variables de entorno o un almacén de secretos.");

        if (accessToken is not null && DateTimeOffset.UtcNow < expiresAt - TimeSpan.FromSeconds(30)) return accessToken;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && DateTimeOffset.UtcNow < expiresAt - TimeSpan.FromSeconds(30)) return accessToken;
            var client = httpClientFactory.CreateClient("OAuthToken");
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            var fields = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
            if (!string.IsNullOrWhiteSpace(config.Scope)) fields.Add(new("scope", config.Scope));
            request.Content = new FormUrlEncodedContent(fields);
            // Credenciales de cliente por HTTP Basic (RFC 6749 §2.3.1), no se envían en la URL.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{WebUtility.UrlEncode(config.ClientId)}:{WebUtility.UrlEncode(config.ClientSecret)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new OAuthTokenException(response.StatusCode, body.Length > 300 ? body[..300] + "…" : body);
            if (string.IsNullOrWhiteSpace(body)) throw new OAuthTokenException(response.StatusCode, "El proveedor devolvió una respuesta vacía.");
            try
            {
                var token = JsonSerializer.Deserialize<OAuthTokenResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                    throw new OAuthTokenException(response.StatusCode, "La respuesta OAuth no contiene access_token.");
                if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                    throw new OAuthTokenException(response.StatusCode, "El proveedor no devolvió token_type Bearer.");
                accessToken = token.AccessToken;
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.ExpiresIn));
                return accessToken;
            }
            catch (JsonException ex) { throw new OAuthTokenException(response.StatusCode, "El proveedor devolvió JSON OAuth inválido.", ex); }
        }
        finally { gate.Release(); }
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

public sealed class AviHubClient(HttpClient httpClient, OAuthTokenProvider tokenProvider)
{
    public async Task<EquipmentResponse> GetEquipmentsAsync(string path, CancellationToken cancellationToken = default)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AviHubHttpException(response.StatusCode, ExtractError(body, response.ReasonPhrase));
        if (string.IsNullOrWhiteSpace(body)) throw new AviHubEmptyResponseException("El servidor devolvió un cuerpo vacío.");
        try
        {
            using var document = JsonDocument.Parse(body);
            return EquipmentResponse.Parse(document.RootElement, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new AviHubJsonException("El cuerpo no contiene JSON válido.", ex);
        }
    }

    private static string ExtractError(string body, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback ?? "La API rechazó la petición.";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var name in new[] { "message", "error_description", "error", "detail" })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
                    return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] + "…" : body;
    }
}

public sealed record EquipmentRecord(IReadOnlyDictionary<string, string> Fields)
{
    internal static EquipmentRecord From(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new EquipmentRecord(new Dictionary<string, string> { ["valor"] = element.ToString() });
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) fields[property.Name] = Format(property.Value);
        return new EquipmentRecord(fields);
    }
    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "null",
        JsonValueKind.Null => "null",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.ToString()
    };
}

public sealed record EquipmentResponse(int StatusCode, IReadOnlyList<EquipmentRecord> Items, string? Pagination)
{
    private static readonly string[] CollectionKeys = ["data", "items", "results", "equipments"];
    private static readonly string[] PaginationKeys = ["meta", "pagination", "links", "page", "total", "per_page", "current_page", "next"];

    public static EquipmentResponse Parse(JsonElement root, int statusCode = 200)
    {
        var collection = FindCollection(root);
        if (collection is null) throw new AviHubJsonException("El JSON no contiene una lista de equipos reconocible (raíz o data/items/results/equipments).");
        var items = collection.Value.ValueKind == JsonValueKind.Array
            ? collection.Value.EnumerateArray().Select(EquipmentRecord.From).ToArray()
            : new[] { EquipmentRecord.From(collection.Value) };
        string? pagination = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var parts = root.EnumerateObject().Where(p => PaginationKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                .Select(p => $"{p.Name}={p.Value.GetRawText()}").ToArray();
            if (parts.Length > 0) pagination = string.Join(", ", parts);
        }
        return new EquipmentResponse(statusCode, items, pagination);
    }

    private static JsonElement? FindCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in CollectionKeys)
        {
            if (!TryGet(root, key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array) return value;
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in CollectionKeys)
                    if (TryGet(value, nested, out var inner) && inner.ValueKind == JsonValueKind.Array) return inner;
                // `data` as a single object is tolerated; empty envelopes are not mistaken for an item.
                if (value.EnumerateObject().Any()) return value;
            }
        }
        return null;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
}

public sealed class AviHubHttpException(HttpStatusCode statusCode, string message) : Exception(message)
{ public HttpStatusCode StatusCode { get; } = statusCode; }
public sealed class AviHubEmptyResponseException(string message) : Exception(message);
public sealed class AviHubJsonException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class OAuthConfigurationException(string message) : Exception(message);
public sealed class OAuthTokenException(HttpStatusCode statusCode, string message, Exception? inner = null) : Exception($"Falló la obtención del token OAuth (HTTP {(int)statusCode}): {message}", inner)
{ public HttpStatusCode StatusCode { get; } = statusCode; }
