// Options/PlexOptions.cs
namespace PlexProxy.Options;

public sealed class PlexOptions
{
    public string BaseUrl { get; set; } = "";   // e.g., "https://bridges-shop.tailc97a23.ts.net"
    public string Token { get; set; } = "";    // your PMS token
}
