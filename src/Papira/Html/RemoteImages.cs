using System.Net;
using System.Net.Sockets;

namespace Papira.Html;

/// <summary>
/// Fetches the pictures a document points at over the network. Everything a document needs is fetched at
/// once, before it is laid out, so that a page of pictures costs one round of waiting rather than one per
/// picture; a picture that cannot be fetched is left out and the document is still written.
/// </summary>
internal sealed class RemoteImages(TimeSpan timeout, int maximumBytes, bool allowPrivateNetworks)
{
    private static readonly HttpClient Client = Create();

    private readonly Dictionary<string, byte[]?> _fetched = new(StringComparer.Ordinal);

    private static HttpClient Create()
    {
        // The timeout is set per request, so that one picture waiting does not hold up the rest.
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.Add("User-Agent", "Papira");
        return client;
    }

    /// <summary>True for the addresses this is about at all; everything else is a file or a data URI.</summary>
    public static bool IsRemote(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>What was fetched for an address, or null where nothing could be.</summary>
    public byte[]? Get(string source) => _fetched.GetValueOrDefault(source);

    /// <summary>Fetches everything that has not been fetched yet, all of it at the same time.</summary>
    public void Fetch(IEnumerable<string> sources)
    {
        var wanted = sources.Where(source => IsRemote(source) && !_fetched.ContainsKey(source)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            return;

        var results = Task.WhenAll(wanted.Select(FetchOne)).GetAwaiter().GetResult();
        for (var i = 0; i < wanted.Count; i++)
            _fetched[wanted[i]] = results[i];
    }

    private async Task<byte[]?> FetchOne(string source)
    {
        try
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !await Allowed(uri).ConfigureAwait(false))
                return null;

            using var cancellation = new CancellationTokenSource(timeout);
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellation.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                // A server that keeps sending is cut off rather than allowed to fill the machine's memory.
                if (buffer.Length + read > maximumBytes)
                    return null;

                buffer.Write(chunk, 0, read);
            }

            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or SocketException or UriFormatException)
        {
            // A picture that cannot be fetched is left out; the document is written without it.
            return null;
        }
    }

    /// <summary>
    /// Whether the address may be fetched. Only http and https are, and by default only addresses outside
    /// the machine and its own network: a document from elsewhere must not be able to read what only this
    /// machine can reach, such as the address a cloud gives its own settings on.
    /// </summary>
    private async Task<bool> Allowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (allowPrivateNetworks)
            return true;

        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            var addresses = IPAddress.TryParse(uri.IdnHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.IdnHost, cancellation.Token).ConfigureAwait(false);

            return addresses.Length > 0 && Array.TrueForAll(addresses, address => !IsPrivate(address));
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPrivate(address.MapToIPv4());

            var first = address.GetAddressBytes()[0];
            return address.Equals(IPAddress.IPv6Any) || (first & 0xFE) == 0xFC;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 => true,
            169 => bytes[1] == 254,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }
}
