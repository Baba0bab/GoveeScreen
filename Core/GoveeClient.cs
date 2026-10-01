using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GoveeScreenV2.Core;

public record GoveeDevice(string Ip, string Model, string DeviceId)
{
    public override string ToString() => $"{Model} ({Ip})";
}

public record GoveeStatus(bool IsOn, int Brightness, byte R, byte G, byte B);

/// <summary>
/// Client UDP pour le protocole LAN Govee (port 4003 commandes, 4001 multicast découverte, 4002 réponses).
/// </summary>
public sealed class GoveeClient : IDisposable
{
    private const int CommandPort = 4003;
    private const int DiscoveryPort = 4001;
    private const int ResponsePort = 4002;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");

    private UdpClient? _udp;
    private readonly object _lock = new();

    public string DeviceIp { get; set; }

    public GoveeClient(string deviceIp)
    {
        DeviceIp = deviceIp;
    }

    private void Send(object payload)
    {
        if (string.IsNullOrWhiteSpace(DeviceIp))
            return;

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
        lock (_lock)
        {
            try
            {
                _udp ??= new UdpClient();
                _udp.Send(data, data.Length, DeviceIp, CommandPort);
            }
            catch
            {
                _udp?.Dispose();
                _udp = null;
            }
        }
    }

    public void SetColor(byte r, byte g, byte b) => Send(new
    {
        msg = new
        {
            cmd = "colorwc",
            data = new
            {
                color = new { r, g, b },
                colorTemInKelvin = 0
            }
        }
    });

    public void SetBrightness(int percent) => Send(new
    {
        msg = new
        {
            cmd = "brightness",
            data = new { value = Math.Clamp(percent, 0, 100) }
        }
    });

    public void TurnOn() => Send(new { msg = new { cmd = "turn", data = new { value = 1 } } });

    public void TurnOff() => Send(new { msg = new { cmd = "turn", data = new { value = 0 } } });

    /// <summary>
    /// Découverte des appareils Govee sur le réseau local (multicast "scan").
    /// </summary>
    public static async Task<List<GoveeDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var devices = new List<GoveeDevice>();
        var seen = new HashSet<string>();

        using var listener = new UdpClient();
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, ResponsePort));

        byte[] scanMsg = Encoding.UTF8.GetBytes(
            "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");

        using var sender = new UdpClient();
        await sender.SendAsync(scanMsg, scanMsg.Length, new IPEndPoint(MulticastAddress, DiscoveryPort));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await listener.ReceiveAsync(cts.Token);
                try
                {
                    using var doc = JsonDocument.Parse(result.Buffer);
                    var data = doc.RootElement.GetProperty("msg").GetProperty("data");
                    string ip = data.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : result.RemoteEndPoint.Address.ToString();
                    string sku = data.TryGetProperty("sku", out var skuEl) ? skuEl.GetString() ?? "Govee" : "Govee";
                    string id = data.TryGetProperty("device", out var devEl) ? devEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(ip) && seen.Add(ip))
                        devices.Add(new GoveeDevice(ip, sku, id));
                }
                catch (JsonException)
                {
                    // Réponse non-JSON ignorée
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fin du délai de découverte
        }

        return devices;
    }

    /// <summary>
    /// Interroge l'état de la lampe ("devStatus") : null si elle ne répond pas dans le délai.
    /// La requête part du port de réponse 4002, la réponse revient donc sur le même socket.
    /// </summary>
    public static async Task<GoveeStatus?> QueryStatusAsync(string ip, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return null;

        using var socket = new UdpClient();
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, ResponsePort));

        byte[] query = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}");
        await socket.SendAsync(query, query.Length, new IPEndPoint(address, CommandPort));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(cts.Token);
                if (!result.RemoteEndPoint.Address.Equals(address))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(result.Buffer);
                    var msg = doc.RootElement.GetProperty("msg");
                    if (msg.GetProperty("cmd").GetString() != "devStatus")
                        continue;

                    var data = msg.GetProperty("data");
                    bool isOn = data.TryGetProperty("onOff", out var onOff) && onOff.GetInt32() == 1;
                    int brightness = data.TryGetProperty("brightness", out var b) ? b.GetInt32() : 0;
                    byte r = 0, g = 0, bl = 0;
                    if (data.TryGetProperty("color", out var color))
                    {
                        r = color.TryGetProperty("r", out var cr) ? cr.GetByte() : (byte)0;
                        g = color.TryGetProperty("g", out var cg) ? cg.GetByte() : (byte)0;
                        bl = color.TryGetProperty("b", out var cb) ? cb.GetByte() : (byte)0;
                    }
                    return new GoveeStatus(isOn, brightness, r, g, bl);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    // Réponse inattendue ignorée
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _udp?.Dispose();
            _udp = null;
        }
    }
}
