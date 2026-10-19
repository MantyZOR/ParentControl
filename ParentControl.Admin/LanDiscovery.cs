using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParentControl.Core;

namespace ParentControl.Admin;

internal sealed record DiscoveryMatch(string Host, string Machine, string LocalAddress);
internal sealed record DiscoveryInterface(IPAddress Local, IPAddress Broadcast, IPAddress Mask);

internal static class LanDiscovery
{
    public static async Task<DiscoveryMatch?> FindAsync(byte[] key, TimeSpan timeout,
        string? knownHost = null, CancellationToken cancellationToken = default)
    {
        if (key.Length != 32) throw new ArgumentException("Ключ подключения должен содержать 32 байта.", nameof(key));
        if (!string.IsNullOrWhiteSpace(knownHost))
        {
            var direct = await VerifyHostAsync(key, knownHost.Trim());
            if (direct != null) return direct;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var probe = new DiscoveryProbe();
        var bytes = Encoding.UTF8.GetBytes(Wire.Encrypt(probe, key));
        var sockets = new List<UdpClient>();
        try
        {
            // Binding one socket per local IPv4 address avoids Windows choosing the wrong
            // adapter when Ethernet and Wi-Fi have the same subnet and broadcast address.
            foreach (var network in Interfaces())
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(network.Local, 0)) { EnableBroadcast = true };
                    sockets.Add(socket);
                    await SendProbe(socket, bytes, network.Broadcast, deadline.Token);
                    await SendProbe(socket, bytes, IPAddress.Broadcast, deadline.Token);
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { return null; }
            }
            if (sockets.Count == 0)
            {
                var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                sockets.Add(socket);
                await SendProbe(socket, bytes, IPAddress.Broadcast, deadline.Token);
            }

            var pending = sockets.ToDictionary(s => s.ReceiveAsync(deadline.Token).AsTask(), s => s);
            while (pending.Count > 0 && !deadline.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(pending.Keys);
                var socket = pending[completed];
                pending.Remove(completed);
                UdpReceiveResult packet;
                try { packet = await completed; }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                pending.Add(socket.ReceiveAsync(deadline.Token).AsTask(), socket);
                if (packet.Buffer.Length is < 30 or > 4096) continue;
                try
                {
                    var reply = Wire.Decrypt<DiscoveryReply>(Encoding.UTF8.GetString(packet.Buffer), key);
                    if (!DiscoveryProtocol.Matches(probe, reply)) continue;
                    var host = packet.RemoteEndPoint.Address.ToString();
                    var verified = await VerifyHostAsync(key, host);
                    if (verified != null && verified.Machine.Equals(reply.Machine, StringComparison.OrdinalIgnoreCase))
                        return verified;
                }
                catch (CryptographicException) { }
                catch (FormatException) { }
                catch (JsonException) { }
                catch (InvalidDataException) { }
            }
            return null;
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    public static async Task<DiscoveryMatch?> VerifyHostAsync(byte[] key, string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(':') || host.Contains(' ')) return null;
        var sources = new List<string>();
        if (IPAddress.TryParse(host, out var remote) && remote.AddressFamily == AddressFamily.InterNetwork)
        {
            var address = remote.GetAddressBytes();
            foreach (var network in Interfaces())
            {
                var local = network.Local.GetAddressBytes();
                var mask = network.Mask.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (address[i] & mask[i]) == (local[i] & mask[i])))
                    sources.Add(network.Local.ToString());
            }
        }
        sources.Add(""); // Keep routed connections and DNS names available as a fallback.
        var pending = sources.Distinct().Select(source => VerifySourceAsync(key, host, source)).ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var match = await completed;
            if (match != null) return match;
        }
        return null;
    }

    private static async Task<DiscoveryMatch?> VerifySourceAsync(byte[] key, string host, string source)
    {
        try
        {
            var candidate = new ManagedClient { Host = host, Key = Convert.ToBase64String(key), LocalAddress = source };
            var status = await ClientApi.Post<ClientStatus>(candidate, "/api/status");
            return string.IsNullOrWhiteSpace(status.Machine) ? null : new DiscoveryMatch(host, status.Machine, source);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (CryptographicException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (FormatException) { return null; }
        catch (SocketException) { return null; }
    }

    private static async Task SendProbe(UdpClient socket, byte[] bytes, IPAddress address, CancellationToken token)
    {
        try { await socket.SendAsync(bytes, new IPEndPoint(address, DiscoveryProtocol.Port), token); }
        catch (SocketException) { }
    }

    private static IEnumerable<DiscoveryInterface> Interfaces()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new DiscoveryInterface(address.Address, new IPAddress(broadcast), address.IPv4Mask);
            }
        }
    }
}
