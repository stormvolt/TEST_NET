using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.Configure<AviHubOptions>(builder.Configuration.GetSection("AviHub"));
builder.Services.Configure<OAuthOptions>(builder.Configuration.GetSection("OAuth"));
builder.Services.AddHttpClient("OAuthToken");
builder.Services.AddSingleton<OAuthTokenProvider>();
builder.Services.AddHttpClient<AviHubClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<AviHubOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

using var host = builder.Build();
var options = host.Services.GetRequiredService<IOptions<AviHubOptions>>().Value;
try
{
    var response = await host.Services.GetRequiredService<AviHubClient>().GetEquipmentsAsync(options.EquipmentsPath);
    Console.WriteLine($"AviHub — equipos (HTTP {response.StatusCode})");
    Console.WriteLine($"Elementos: {response.Items.Count}");
    if (response.Items.Count == 0) Console.WriteLine("No se encontraron elementos.");
    for (var i = 0; i < response.Items.Count; i++)
    {
        Console.WriteLine($"\n[{i + 1}]");
        foreach (var field in response.Items[i].Fields)
            Console.WriteLine($"  {field.Key}: {field.Value}");
    }
    if (response.Pagination is not null) Console.WriteLine($"\nPaginación: {response.Pagination}");
}
catch (OAuthConfigurationException ex)
{
    Console.Error.WriteLine($"Configuración OAuth incompleta: {ex.Message}");
    return 1;
}
catch (OAuthTokenException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (AviHubHttpException ex)
{
    Console.Error.WriteLine($"Error HTTP {(int)ex.StatusCode} ({ex.StatusCode}): {ex.Message}");
    return 1;
}
catch (AviHubEmptyResponseException ex)
{
    Console.Error.WriteLine($"Respuesta vacía: {ex.Message}");
    return 1;
}
catch (AviHubJsonException ex)
{
    Console.Error.WriteLine($"JSON inválido o no reconocido: {ex.Message}");
    return 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"No se pudo conectar con AviHub: {ex.Message}");
    return 1;
}

return 0;
