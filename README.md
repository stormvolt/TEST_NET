# Cliente de consola AviHub

Cliente de ejemplo en C#/.NET para consultar equipos de AviHub y mostrar un resumen de los campos que devuelve el servidor. Usa `HttpClientFactory` mediante inyección de dependencias, `System.Text.Json` y opciones enlazadas desde configuración. Los DTOs conservan los campos JSON dinámicos, ya que la documentación del esquema no pudo consultarse desde este entorno.

## Endpoint y formato

La ruta solicitada es `GET https://avihub.pesoexato.com/api/v3/equipments`, con `Accept: application/json` y `Authorization: Bearer <access_token>`. La documentación indicada (`https://avihub.pesoexato.com/api/v3/docs`) no estuvo disponible durante esta implementación: la consulta web no pudo abrirla y la conexión directa falló por resolución DNS. Por tanto, no se pudo verificar el esquema real, la paginación, los nombres de campos ni los endpoints y grants OAuth admitidos.

Como supuesto explícito, el cliente reconoce una lista JSON directa o una lista bajo `data`, `items`, `results` o `equipments`; también contempla `data` como objeto envoltorio con una lista interna de esas claves. Conserva metadatos comunes de paginación (`meta`, `pagination`, `links`, etc.) si aparecen. Los campos de cada equipo se imprimen tal como vengan, sin asumir propiedades como `id` o `name`. El parser tolerante no implica que estos formatos estén confirmados por AviHub; conviene ajustarlo cuando se disponga de la especificación o una respuesta real.

Los estados HTTP no exitosos generan un error que muestra estado y mensaje (si está en JSON); los cuerpos vacíos, JSON sintácticamente inválido y estructuras JSON sin una colección reconocible se informan explícitamente.

## OAuth 2.0

El cliente obtiene un token mediante OAuth 2.0 y lo envía a AviHub como `Authorization: Bearer <access_token>`. Como no se pudo consultar la configuración del proveedor, se implementó el grant `client_credentials`, habitual para integraciones servicio a servicio, contra un endpoint configurable. La solicitud envía `grant_type=client_credentials`, un `scope` opcional y autentica el cliente con HTTP Basic. Se espera una respuesta estándar con `access_token`, `token_type: Bearer` y `expires_in`; el token se reutiliza hasta poco antes de expirar.

Este flujo usa OAuth 2.0, pero el grant, endpoint de token, scope y credenciales no se pudieron validar contra AviHub. Confirma esos datos con el proveedor antes de usarlo. Si AviHub exige autorización de usuario u otro grant, este flujo debe adaptarse.

`AviHub.Client/appsettings.json` no contiene credenciales. Configúralas por variables de entorno o un almacén de secretos; no guardes secretos reales en archivos versionados:

```sh
export OAuth__TokenUrl="https://identity.example.com/oauth/token"
export OAuth__ClientId="tu-client-id"
export OAuth__ClientSecret="tu-client-secret"
export OAuth__Scope="equipments.read"
dotnet run --project AviHub.Client/AviHub.Client.csproj
```

## Requisitos y ejecución

Se requiere el SDK .NET 10. `Microsoft.AspNetCore.App` se referencia como framework compartido para usar hosting, DI y `HttpClientFactory` sin agregar paquetes externos.

```sh
dotnet build AviHub.Client/AviHub.Client.csproj
dotnet run --project AviHub.Client/AviHub.Client.csproj
```

Configuración predeterminada en `AviHub.Client/appsettings.json`: URL base `https://avihub.pesoexato.com/api/v3/` y ruta `equipments`. Debes definir los valores OAuth anteriores; si faltan, el cliente se detiene con un mensaje de configuración.

## Pruebas locales

Ejecuta las comprobaciones automatizadas con:

```sh
dotnet run --project AviHub.Client.Tests/AviHub.Client.Tests.csproj
```

Cubren deserialización flexible, paginación, errores HTTP y respuestas vacías o JSON inválido, además del intercambio OAuth simulado y el envío del token como Bearer. No se pudo probar contra AviHub ni contra el proveedor de identidad real.



## Herramientas y aceleradores

- SDK .NET 10.0.403 y sus herramientas de compilación.
- Asistencia de Codex (modelo de lenguaje) para inspección, implementación y documentación.
- Se intentó consultar la documentación pública con el navegador web y `curl`; el acceso falló y no se usó como evidencia del esquema.
- No se utilizaron Skills.

## Limitaciones

- No se confirmó con AviHub el esquema de `equipments`, la paginación, el endpoint OAuth ni el grant permitido.
- Se necesita un proveedor OAuth válido que acepte `client_credentials`, además de su URL de token, credenciales y scope requeridos.
- El cliente solicita una sola página de equipos; muestra metadatos de paginación, pero no recorre páginas automáticamente.
