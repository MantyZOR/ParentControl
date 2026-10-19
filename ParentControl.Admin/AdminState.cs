using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using ParentControl.Core;

namespace ParentControl.Admin;

public sealed class ManagedClient
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string LocalAddress { get; set; } = ""; // Network adapter used to reach this client.
    public string Key { get; set; } = "";
    public DateTime? LastSeenUtc { get; set; }
    public ClientStatus? LastStatus { get; set; }
    public List<LogEntry> History { get; set; } = [];
    public long LastRemoteSequence { get; set; }
    public bool StateResetWarning { get; set; }
    public List<Command> Pending { get; set; } = [];
    public string RulesMarkdown { get; set; } = "";
    public List<BackgroundRecord> BackgroundRecords { get; set; } = [];
    public List<string> CreatedPromoCodes { get; set; } = []; // Legacy format, migrated on first UI launch.
    public List<PromoRecord> PromoRecords { get; set; } = [];
    public override string ToString() => Name + "  ·  " + Host;
}

public sealed class BackgroundRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ThumbnailBase64 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? DeletionRequestedUtc { get; set; }
}

public sealed class PromoRecord
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Minutes { get; set; }
    public int TotalUses { get; set; }
    public int RemainingUses { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredUtc { get; set; }
    public DateTime? ExhaustedUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public DateTime? DeletionRequestedUtc { get; set; }
    public bool DeletedByAdmin { get; set; }
    public bool MissingOnClient { get; set; }
}

public sealed class AdminState
{
    public List<ManagedClient> Clients { get; set; } = [];
    public static string PathName => Path.Combine(
        Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParentControl"), "admin.dat");
    public static AdminState Load()
    {
        if (!File.Exists(PathName)) return new AdminState();
        return JsonSerializer.Deserialize<AdminState>(LocalSecret.Unprotect(File.ReadAllBytes(PathName)), Wire.Json) ?? new AdminState();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temp = PathName + ".tmp";
        File.WriteAllBytes(temp, LocalSecret.Protect(JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json)));
        File.Move(temp, PathName, true);
    }
}

public static class ClientApi
{
    private static readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly ConcurrentDictionary<string, HttpClient> boundHttp = new();

    private static HttpClient Transport(string localAddress)
    {
        if (!IPAddress.TryParse(localAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork)
            return http;
        return boundHttp.GetOrAdd(localAddress, _ => new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        }) { Timeout = TimeSpan.FromSeconds(3) });
    }
    public static async Task<T> Post<T>(ManagedClient client, string path, Command? command = null)
    {
        var key = Convert.FromBase64String(client.Key);
        var message = new ApiRequest { Command = command };
        var request = Wire.Encrypt(message, key);
        var host = client.Host.Trim();
        var uri = new UriBuilder("http", host, 48731, path).Uri;
        using var response = await Transport(client.LocalAddress).PostAsync(uri, new StringContent(request, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var decoded = Wire.Decrypt<ApiResponse<T>>(await response.Content.ReadAsStringAsync(), key);
        if (decoded.RequestId != message.RequestId || decoded.Data == null) throw new InvalidDataException("Response does not match request");
        return decoded.Data;
    }
}
