using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NetworkMonitor.Core.Sync;

internal static class SyncHttp
{
    public static HttpClient CreateClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
            },
        })
        {
            Timeout = timeout,
        };

    public static StringContent Json(string payload) => new(payload, Encoding.UTF8, "application/json");

    public static bool TryParseHttpUri(string value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            !string.IsNullOrEmpty(uri.Host))
        {
            return true;
        }

        uri = null;
        return false;
    }
}
