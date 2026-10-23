using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StardewWebApi.Game;

namespace StardewWebApi.Server;

/// <summary>
/// Anuncia a API na rede local via DNS-SD/mDNS, no serviço
/// <c>_stardewapi._tcp</c>. O app usa esse anúncio para encontrar o servidor
/// sem o jogador precisar digitar o IP.
///
/// Implementado com sockets UDP puros (sem dependências externas) para
/// compatibilidade com o carregador de assemblies do SMAPI.
/// </summary>
internal sealed class MdnsAdvertiser : IDisposable
{
    // Grupo multicast mDNS (RFC 6762) e porta DNS padrão.
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private const string ServiceType = "_stardewapi._tcp.local.";

    private CancellationTokenSource? _cts;
    private Task? _announceTask;

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var instanceName = $"Stardew Web API ({Environment.MachineName}).{ServiceType}";

        _announceTask = Task.Run(async () =>
        {
            // Anuncia em cada interface de rede local ativa.
            var sockets = CreateSockets();

            try
            {
                // Envia 3 anúncios iniciais em intervalos curtos (padrão mDNS).
                for (int i = 0; i < 3 && !token.IsCancellationRequested; i++)
                {
                    SendAnnouncement(sockets, instanceName, port);
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                // Re-anuncia periodicamente a cada 60 s (RFC 6762 §11.3).
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(60_000, token).ConfigureAwait(false);
                    SendAnnouncement(sockets, instanceName, port);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var s in sockets)
                    s.Dispose();
            }
        }, token);

        SMAPIWrapper.LogInfo($"Anúncio mDNS ativo: {ServiceType} na porta {port}");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _announceTask?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _announceTask = null;
    }

    // -------------------------------------------------------------------------
    // Sockets

    private static List<UdpClient> CreateSockets()
    {
        var result = new List<UdpClient>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up
                    || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(addr.Address)
                        || addr.Address.ToString().StartsWith("169.254."))
                    {
                        continue;
                    }

                    try
                    {
                        var client = new UdpClient(AddressFamily.InterNetwork);
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        client.Client.Bind(new IPEndPoint(addr.Address, 0));
                        client.JoinMulticastGroup(MdnsGroup, addr.Address);
                        client.Ttl = 255;
                        result.Add(client);
                    }
                    catch
                    {
                        // Interface não suporta multicast – ignora.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogWarn($"mDNS: não foi possível listar interfaces: {ex.Message}");
        }

        // Fallback: socket não vinculado a interface específica.
        if (result.Count == 0)
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Ttl = 255;
                result.Add(client);
            }
            catch { }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Construção e envio do pacote DNS

    private static void SendAnnouncement(List<UdpClient> sockets, string instanceName, int port)
    {
        try
        {
            // Obtém o primeiro IP local para o registro A.
            var localIp = GetFirstLocalIp() ?? IPAddress.Loopback;
            var hostname = Environment.MachineName.ToLowerInvariant() + ".local.";

            var packet = BuildDnsResponsePacket(instanceName, port, hostname, localIp);
            var endpoint = new IPEndPoint(MdnsGroup, MdnsPort);

            foreach (var socket in sockets)
            {
                try { socket.Send(packet, packet.Length, endpoint); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogDebug($"mDNS: erro ao enviar anúncio: {ex.Message}");
        }
    }

    private static byte[] BuildDnsResponsePacket(
        string instanceName, int port, string hostname, IPAddress localIp)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms);

        // ---------- Header (RFC 1035 §4.1.1) ----------
        w.WriteUInt16BE(0);          // ID = 0 (mDNS)
        w.WriteUInt16BE(0x8400);     // QR=1 AA=1 (response, authoritative)
        w.WriteUInt16BE(0);          // QDCOUNT
        w.WriteUInt16BE(4);          // ANCOUNT  (PTR + SRV + TXT + A)
        w.WriteUInt16BE(0);          // NSCOUNT
        w.WriteUInt16BE(0);          // ARCOUNT

        // ---------- PTR: _stardewapi._tcp.local. → <instance> ----------
        WriteDnsName(w, ServiceType);
        w.WriteUInt16BE(12);         // TYPE  = PTR
        w.WriteUInt16BE(0x8001);     // CLASS = IN + cache-flush
        w.WriteUInt32BE(4500);       // TTL   = 75 min

        var ptrRdata = EncodeDnsName(instanceName);
        w.WriteUInt16BE((ushort)ptrRdata.Length);
        w.Write(ptrRdata);

        // ---------- SRV: <instance> → hostname:port ----------
        WriteDnsName(w, instanceName);
        w.WriteUInt16BE(33);         // TYPE = SRV
        w.WriteUInt16BE(0x8001);
        w.WriteUInt32BE(120);        // TTL = 2 min
        var srvHost = EncodeDnsName(hostname);
        w.WriteUInt16BE((ushort)(6 + srvHost.Length)); // RDLENGTH
        w.WriteUInt16BE(0);          // priority
        w.WriteUInt16BE(0);          // weight
        w.WriteUInt16BE((ushort)port);
        w.Write(srvHost);

        // ---------- TXT: propriedades ----------
        WriteDnsName(w, instanceName);
        w.WriteUInt16BE(16);         // TYPE = TXT
        w.WriteUInt16BE(0x8001);
        w.WriteUInt32BE(4500);

        var txtPairs = new[]
        {
            "path=/",
            "version=0.7.0",
            "game=Stardew Valley"
        };

        using var txtBuf = new System.IO.MemoryStream();
        foreach (var kv in txtPairs)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(kv);
            txtBuf.WriteByte((byte)b.Length);
            txtBuf.Write(b, 0, b.Length);
        }

        var txtData = txtBuf.ToArray();
        w.WriteUInt16BE((ushort)txtData.Length);
        w.Write(txtData);

        // ---------- A: hostname → IP ----------
        WriteDnsName(w, hostname);
        w.WriteUInt16BE(1);          // TYPE = A
        w.WriteUInt16BE(0x8001);
        w.WriteUInt32BE(120);
        w.WriteUInt16BE(4);          // RDLENGTH
        w.Write(localIp.GetAddressBytes());

        return ms.ToArray();
    }

    // Escreve um nome DNS no formato label (ex: "_tcp.local." → 4_tcp5local0).
    private static void WriteDnsName(System.IO.BinaryWriter w, string name)
        => w.Write(EncodeDnsName(name));

    private static byte[] EncodeDnsName(string name)
    {
        using var buf = new System.IO.MemoryStream();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label);
            buf.WriteByte((byte)bytes.Length);
            buf.Write(bytes, 0, bytes.Length);
        }
        buf.WriteByte(0); // root label
        return buf.ToArray();
    }

    private static IPAddress? GetFirstLocalIp()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up
                    || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address)
                        && !addr.Address.ToString().StartsWith("169.254."))
                    {
                        return addr.Address;
                    }
                }
            }
        }
        catch { }

        return null;
    }
}

// Extensões de escrita big-endian (exigido pelo protocolo DNS).
internal static class BinaryWriterExtensions
{
    public static void WriteUInt16BE(this System.IO.BinaryWriter w, ushort value)
    {
        w.Write((byte)(value >> 8));
        w.Write((byte)(value & 0xFF));
    }

    public static void WriteUInt32BE(this System.IO.BinaryWriter w, uint value)
    {
        w.Write((byte)(value >> 24));
        w.Write((byte)(value >> 16 & 0xFF));
        w.Write((byte)(value >> 8 & 0xFF));
        w.Write((byte)(value & 0xFF));
    }
}
