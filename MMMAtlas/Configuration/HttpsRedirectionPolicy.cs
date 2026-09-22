namespace MMMAtlas.Configuration;

public sealed record HttpsRedirectionPolicy(bool Enabled, int? HttpsPort)
{
    private const int DefaultHttpsPort = 443;

    public static HttpsRedirectionPolicy From(IConfiguration configuration)
    {
        var configuredPort = configuration.GetValue<int?>(
            "HttpsRedirection:HttpsPort");
        var endpointPort = FindHttpsPort(configuration);
        var configuredEnabled = configuration.GetValue<bool?>(
            "HttpsRedirection:Enabled");
        var enabled = configuredEnabled
            ?? (configuredPort.HasValue || endpointPort.HasValue);

        return new HttpsRedirectionPolicy(
            enabled,
            enabled
                ? configuredPort ?? endpointPort ?? DefaultHttpsPort
                : null);
    }

    private static int? FindHttpsPort(IConfiguration configuration)
    {
        var configuredUrls = new[]
        {
            configuration["urls"],
            configuration["ASPNETCORE_URLS"]
        };

        foreach (var configuredUrl in configuredUrls)
        {
            var port = FindHttpsPort(configuredUrl);
            if (port.HasValue)
            {
                return port;
            }
        }

        foreach (var endpoint in configuration
            .GetSection("Kestrel:Endpoints")
            .GetChildren())
        {
            var port = FindHttpsPort(endpoint["Url"]);
            if (port.HasValue)
            {
                return port;
            }
        }

        return null;
    }

    private static int? FindHttpsPort(string? configuredUrls)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return null;
        }

        foreach (var value in configuredUrls.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
        {
            if (!value.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return uri.IsDefaultPort ? DefaultHttpsPort : uri.Port;
            }

            var separator = value.LastIndexOf(':');
            if (separator >= 0
                && int.TryParse(value[(separator + 1)..], out var port))
            {
                return port;
            }

            return DefaultHttpsPort;
        }

        return null;
    }
}
