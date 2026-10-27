using Microsoft.Extensions.Configuration;

namespace Tourbooking.Presentation;

public static class ServiceAssetPath
{
    public static string For(IConfiguration configuration, string path)
    {
        var service = configuration["Service:Name"];
        if (string.IsNullOrWhiteSpace(service))
            throw new InvalidOperationException("Service:Name must be configured to generate service asset URLs.");

        return $"/assets/{service}/{path.TrimStart('~', '/')}";
    }
}
