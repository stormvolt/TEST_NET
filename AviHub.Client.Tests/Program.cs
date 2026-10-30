using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

var checks = 0;
var parsed = EquipmentResponse.Parse(System.Text.Json.JsonDocument.Parse("""{"data":[{"id":7,"name":"Balanza"}],"meta":{"page":1,"total":1}}""").RootElement);
Check(parsed.Items.Count == 1 && parsed.Items[0].Fields["name"] == "Balanza", "deserializa un envoltorio y preserva campos reales");
Check(parsed.Pagination?.Contains("meta") == true, "conserva metadatos de paginación");
Check(EquipmentResponse.Parse(System.Text.Json.JsonDocument.Parse("[]").RootElement).Items.Count == 0, "acepta una lista directa vacía");
Check(EquipmentResponse.Parse(System.Text.Json.JsonDocument.Parse("""{"results":[{"code":"X"}]}""").RootElement).Items[0].Fields["code"] == "X", "acepta results");
try { EquipmentResponse.Parse(System.Text.Json.JsonDocument.Parse("{" ).RootElement); throw new Exception("JSON inválido no rechazado"); }
catch (System.Text.Json.JsonException) { checks++; }
try { EquipmentResponse.Parse(System.Text.Json.JsonDocument.Parse("""{"unexpected":true}""").RootElement); throw new Exception("envoltorio desconocido no rechazado"); }
catch (AviHubJsonException) { checks++; }
var tokenHandler = new StubHandler(HttpStatusCode.OK, """{"access_token":"local-access-token","token_type":"Bearer","expires_in":3600}""");
var tokenProvider = new OAuthTokenProvider(new StubFactory(tokenHandler), Options.Create(new OAuthOptions
{
    TokenUrl = "https://identity.example.test/oauth/token", ClientId = "test-client", ClientSecret = "test-secret", Scope = "equipments.read"
}));
var acquired = await tokenProvider.GetAccessTokenAsync();
Check(acquired == "local-access-token", "obtiene el access_token OAuth");
Check(tokenHandler.LastAuthorization?.StartsWith("Basic ") == true && tokenHandler.LastBody?.Contains("grant_type=client_credentials") == true,
    "envía client_credentials con autenticación Basic");
var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"message":"Token inválido"}""");
var client = new AviHubClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, tokenProvider);
try { await client.GetEquipmentsAsync("equipments"); throw new Exception("HTTP 401 no rechazado"); }
catch (AviHubHttpException ex) { Check(ex.StatusCode == HttpStatusCode.Unauthorized && ex.Message == "Token inválido", "expone estado y mensaje de error HTTP"); }
Check(handler.LastAuthorization == "Bearer local-access-token", "envía access token como Bearer a AviHub");
var emptyClient = new AviHubClient(new HttpClient(new StubHandler(HttpStatusCode.OK, "")) { BaseAddress = new Uri("https://example.test/") }, tokenProvider);
try { await emptyClient.GetEquipmentsAsync("equipments"); throw new Exception("cuerpo vacío no rechazado"); }
catch (AviHubEmptyResponseException) { checks++; }
var invalidClient = new AviHubClient(new HttpClient(new StubHandler(HttpStatusCode.OK, "no es json")) { BaseAddress = new Uri("https://example.test/") }, tokenProvider);
try { await invalidClient.GetEquipmentsAsync("equipments"); throw new Exception("JSON inválido no rechazado"); }
catch (AviHubJsonException ex) { Check(ex.InnerException is System.Text.Json.JsonException, "informa JSON inválido recibido por HTTP"); }
Console.WriteLine($"Correcto: {checks} comprobaciones.");
return 0;

void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }

sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public string? LastAuthorization { get; private set; }
    public string? LastBody { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
