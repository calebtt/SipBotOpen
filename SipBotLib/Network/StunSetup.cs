using Serilog;
using SIPSorcery.Net;
using System.Net;

namespace SipBot;

public static class StunHelper
{
    public static string? PublicIPAddress { get; private set; }
    public static byte[]? PublicIPAddressBytes { get; private set; }

    private static readonly List<(string server, int port)> stunServers = new()
    {
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun2.l.google.com", 19302),
        ("stun.stunprotocol.org", 3478)
    };

    public static bool SetupStun(string fallbackIp = "")
    {
        if (PublicIPAddress != null)
        {
            // Already resolved (possibly by an earlier SipClient instance in this process).
            // STUN lookups are several blocking network round-trips each; don't redo them
            // for every SipClient constructed.
            Log.Debug($"STUN public IP already resolved: {PublicIPAddress}. Skipping lookup.");
            return true;
        }

        foreach (var (server, port) in stunServers)
        {
            try
            {
                Log.Information($"Resolving public IP via STUN server {server}:{port}...");
                var publicEP = STUNClient.GetPublicIPAddress(server, port);

                if (publicEP != null)
                {
                    PublicIPAddress = new IPAddress(publicEP.GetAddressBytes()).ToString();
                    PublicIPAddressBytes = publicEP.GetAddressBytes();
                    Log.Debug($"STUN public IP resolved: {PublicIPAddress}");
                    return true;
                }
                else
                {
                    Log.Information($"STUN lookup failed for {server}:{port}. Trying next server...");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Exception during STUN lookup for {server}:{port}: {ex.Message}");
            }
        }

        // Fallback to hardcoded public IP if all STUN servers fail
        if (!string.IsNullOrWhiteSpace(fallbackIp) && IPAddress.TryParse(fallbackIp, out var parsedFallback))
        {
            PublicIPAddress = fallbackIp;
            PublicIPAddressBytes = parsedFallback.GetAddressBytes();
            Log.Error($"All STUN servers failed. Using hardcoded public IP: {PublicIPAddress}");
        }
        else
        {
            Log.Error("All STUN servers failed and no valid fallback IP was provided. Public IP remains unresolved.");
        }
        return false;
    }
}