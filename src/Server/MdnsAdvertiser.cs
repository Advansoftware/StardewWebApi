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
///
/// Funciona como um "responder" mDNS de verdade (RFC 6762/6763):
///  - escuta na porta 5353 (compartilhada com o mDNS do Windows via SO_REUSEADDR);
///  - responde às perguntas (queries) do celular;
///  - envia anúncios a partir da porta 5353 (clientes ignoram respostas de outra porta).
/// </summary>
internal sealed class MdnsAdvertiser : IDisposable
{
    // Grupo multicast mDNS (RFC 6762) e porta DNS padrão.
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private const string ServiceType = "_stardewapi._tcp.local.";
    private const string ServicesMeta = "_services._dns-sd._udp.local.";

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33, TypeAny = 255;
    private const ushort ClassIn = 1, CacheFlush = 0x8000;

    private readonly object _sendLock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _announceTask;

    private string _instanceName = "";
    private string _hostname = "";
    private int _port;
    private List<(IPAddress Address, IPAddress Mask)> _interfaces = new();

    public void Start(int port)
    {
        _port = port;
        var machine = Environment.MachineName;
        _instanceName = $"Stardew Web API ({machine}).{ServiceType}";
        _hostname = $"stardewwebapi-{SanitizeHost(machine)}.local.";
        _interfaces = GetLocalInterfaces();

        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

            // Evita que ICMP "port unreachable" derrube o ReceiveAsync no Windows.
            try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0, 0, 0, 0 }, null); } catch { }

            foreach (var (address, _) in _interfaces)
            {
                try { udp.JoinMulticastGroup(MdnsGroup, address); }
                catch (Exception ex) { SMAPIWrapper.LogDebug($"mDNS: falha ao entrar no grupo em {address}: {ex.Message}"); }
            }

            if (_interfaces.Count == 0)
            {
                udp.JoinMulticastGroup(MdnsGroup);
            }

            udp.Ttl = 255;
            udp.MulticastLoopback = true;
            _udp = udp;
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogWarn($"mDNS: não foi possível abrir a porta {MdnsPort}: {ex.Message}");
            _udp?.Dispose();
            _udp = null;
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _receiveTask = Task.Run(() => ReceiveLoop(token), token);
        _announceTask = Task.Run(() => AnnounceLoop(token), token);

        var ips = _interfaces.Count > 0 ? string.Join(", ", _interfaces.Select(i => i.Address)) : "?";
        SMAPIWrapper.LogInfo($"Anúncio mDNS ativo: {ServiceType} na porta {port} (IP: {ips})");
    }

    public void Dispose()
    {
        // "Goodbye" (TTL 0) para o celular remover o servidor da lista imediatamente.
        try
        {
            foreach (var (address, _) in _interfaces)
            {
                SendMulticast(BuildResponse(address, 0, legacy: false, goodbye: true), address);
            }
        }
        catch { }

        _cts?.Cancel();
        try { _udp?.Dispose(); } catch { }
        try { Task.WaitAll(new[] { _receiveTask, _announceTask }.Where(t => t is not null).ToArray()!, 2000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _udp = null;
        _receiveTask = null;
        _announceTask = null;
    }

    // -------------------------------------------------------------------------
    // Loops

    private async Task AnnounceLoop(CancellationToken token)
    {
        try
        {
            // 3 anúncios iniciais em intervalos curtos (RFC 6762 §8.3).
            for (int i = 0; i < 3 && !token.IsCancellationRequested; i++)
            {
                AnnounceAll();
                await Task.Delay(1000 * (1 << i), token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(60_000, token).ConfigureAwait(false);
                AnnounceAll();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void AnnounceAll()
    {
        if (_interfaces.Count == 0)
        {
            var ip = IPAddress.Loopback;
            SendMulticast(BuildResponse(ip, 0, legacy: false), null);
            return;
        }

        foreach (var (address, _) in _interfaces)
        {
            SendMulticast(BuildResponse(address, 0, legacy: false), address);
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try
            {
                HandlePacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                SMAPIWrapper.LogTrace($"mDNS: pacote inválido de {result.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tratamento de queries

    private void HandlePacket(byte[] buf, IPEndPoint remote)
    {
        if (buf.Length < 12) return;

        var id = (ushort)(buf[0] << 8 | buf[1]);
        var flags = (ushort)(buf[2] << 8 | buf[3]);
        if ((flags & 0x8000) != 0) return; // é resposta, não pergunta

        var qdCount = buf[4] << 8 | buf[5];
        var offset = 12;
        var shouldAnswer = false;
        var wantsUnicast = false;

        for (int i = 0; i < qdCount; i++)
        {
            var name = ReadName(buf, ref offset);
            if (offset + 4 > buf.Length) return;

            var qType = (ushort)(buf[offset] << 8 | buf[offset + 1]);
            var qClass = (ushort)(buf[offset + 2] << 8 | buf[offset + 3]);
            offset += 4;

            if ((qClass & 0x8000) != 0) wantsUnicast = true;
            if (Matches(name, qType)) shouldAnswer = true;
        }

        if (!shouldAnswer) return;

        // Query "legada" (não vem da porta 5353): responde unicast com o mesmo ID.
        var legacy = remote.Port != MdnsPort;
        var local = PickLocalAddress(remote.Address);
        var packet = BuildResponse(local, legacy ? id : (ushort)0, legacy);

        SMAPIWrapper.LogTrace($"mDNS: respondendo query de {remote} com {local}:{_port}");

        if (legacy || wantsUnicast)
        {
            SendTo(packet, remote);
        }
        else
        {
            SendMulticast(packet, local);
        }
    }

    private bool Matches(string name, ushort qType)
    {
        bool Is(string target) => string.Equals(name, target, StringComparison.OrdinalIgnoreCase);

        if (Is(ServiceType) || Is(ServicesMeta))
            return qType is TypePtr or TypeAny;

        if (Is(_instanceName))
            return qType is TypeSrv or TypeTxt or TypeAny;

        if (Is(_hostname))
            return qType is TypeA or TypeAny;

        return false;
    }

    // -------------------------------------------------------------------------
    // Envio

    private void SendMulticast(byte[] packet, IPAddress? viaInterface)
    {
        var udp = _udp;
        if (udp is null) return;

        lock (_sendLock)
        {
            try
            {
                if (viaInterface is not null && !IPAddress.IsLoopback(viaInterface))
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        viaInterface.GetAddressBytes());
                }

                udp.Send(packet, packet.Length, new IPEndPoint(MdnsGroup, MdnsPort));
            }
            catch (Exception ex)
            {
                SMAPIWrapper.LogTrace($"mDNS: erro ao enviar multicast via {viaInterface}: {ex.Message}");
            }
        }
    }

    private void SendTo(byte[] packet, IPEndPoint remote)
    {
        var udp = _udp;
        if (udp is null) return;

        lock (_sendLock)
        {
            try { udp.Send(packet, packet.Length, remote); }
            catch (Exception ex) { SMAPIWrapper.LogTrace($"mDNS: erro ao enviar para {remote}: {ex.Message}"); }
        }
    }

    // -------------------------------------------------------------------------
    // Construção do pacote DNS

    private byte[] BuildResponse(IPAddress ip, ushort id, bool legacy, bool goodbye = false)
    {
        // Respostas legadas não podem usar cache-flush e devem ter TTL curto.
        var flush = legacy ? (ushort)0 : CacheFlush;
        uint Ttl(uint normal) => goodbye ? 0u : legacy ? Math.Min(normal, 10u) : normal;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ---------- Header (RFC 1035 §4.1.1) ----------
        w.WriteUInt16BE(id);
        w.WriteUInt16BE(0x8400);     // QR=1 AA=1 (resposta autoritativa)
        w.WriteUInt16BE(0);          // QDCOUNT
        w.WriteUInt16BE(5);          // ANCOUNT (meta PTR + PTR + SRV + TXT + A)
        w.WriteUInt16BE(0);          // NSCOUNT
        w.WriteUInt16BE(0);          // ARCOUNT

        // ---------- PTR meta: _services._dns-sd._udp.local. → _stardewapi._tcp.local. ----------
        WriteRecord(w, ServicesMeta, TypePtr, ClassIn, Ttl(4500), EncodeDnsName(ServiceType));

        // ---------- PTR: _stardewapi._tcp.local. → <instância> (registro compartilhado, sem cache-flush) ----------
        WriteRecord(w, ServiceType, TypePtr, ClassIn, Ttl(4500), EncodeDnsName(_instanceName));

        // ---------- SRV: <instância> → hostname:porta ----------
        using (var srv = new MemoryStream())
        using (var sw = new BinaryWriter(srv))
        {
            sw.WriteUInt16BE(0);              // prioridade
            sw.WriteUInt16BE(0);              // peso
            sw.WriteUInt16BE((ushort)_port);
            sw.Write(EncodeDnsName(_hostname));
            sw.Flush();
            WriteRecord(w, _instanceName, TypeSrv, (ushort)(ClassIn | flush), Ttl(120), srv.ToArray());
        }

        // ---------- TXT: propriedades ----------
        using (var txt = new MemoryStream())
        {
            foreach (var kv in new[] { "path=/", "version=0.7.1", "game=Stardew Valley" })
            {
                var b = System.Text.Encoding.UTF8.GetBytes(kv);
                txt.WriteByte((byte)b.Length);
                txt.Write(b, 0, b.Length);
            }

            WriteRecord(w, _instanceName, TypeTxt, (ushort)(ClassIn | flush), Ttl(4500), txt.ToArray());
        }

        // ---------- A: hostname → IP ----------
        WriteRecord(w, _hostname, TypeA, (ushort)(ClassIn | flush), Ttl(120), ip.GetAddressBytes());

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteRecord(BinaryWriter w, string name, ushort type, ushort cls, uint ttl, byte[] rdata)
    {
        w.Write(EncodeDnsName(name));
        w.WriteUInt16BE(type);
        w.WriteUInt16BE(cls);
        w.WriteUInt32BE(ttl);
        w.WriteUInt16BE((ushort)rdata.Length);
        w.Write(rdata);
    }

    // Codifica um nome DNS em labels (ex: "_tcp.local." → 4_tcp5local0).
    private static byte[] EncodeDnsName(string name)
    {
        using var buf = new MemoryStream();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label);
            buf.WriteByte((byte)Math.Min(bytes.Length, 63));
            buf.Write(bytes, 0, Math.Min(bytes.Length, 63));
        }
        buf.WriteByte(0); // label raiz
        return buf.ToArray();
    }

    // Lê um nome DNS, suportando ponteiros de compressão (RFC 1035 §4.1.4).
    private static string ReadName(byte[] buf, ref int offset)
    {
        var labels = new List<string>();
        var pos = offset;
        var jumped = false;
        var guard = 0;

        while (true)
        {
            if (pos >= buf.Length || ++guard > 128) throw new InvalidDataException("nome DNS inválido");

            var len = buf[pos];
            if (len == 0)
            {
                pos++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= buf.Length) throw new InvalidDataException("ponteiro DNS inválido");
                var target = (len & 0x3F) << 8 | buf[pos + 1];
                if (!jumped) offset = pos + 2;
                jumped = true;
                pos = target;
                continue;
            }

            pos++;
            if (pos + len > buf.Length) throw new InvalidDataException("label DNS inválido");
            labels.Add(System.Text.Encoding.UTF8.GetString(buf, pos, len));
            pos += len;
        }

        if (!jumped) offset = pos;
        return string.Join(".", labels) + ".";
    }

    // -------------------------------------------------------------------------
    // Interfaces de rede

    private IPAddress PickLocalAddress(IPAddress remote)
    {
        var r = remote.GetAddressBytes();
        foreach (var (address, mask) in _interfaces)
        {
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            if (r.Length != 4 || m.Length != 4) continue;

            var same = true;
            for (int i = 0; i < 4; i++)
            {
                if ((a[i] & m[i]) != (r[i] & m[i])) { same = false; break; }
            }

            if (same) return address;
        }

        return _interfaces.Count > 0 ? _interfaces[0].Address : IPAddress.Loopback;
    }

    private static List<(IPAddress, IPAddress)> GetLocalInterfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up
                    || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || !iface.SupportsMulticast)
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

                    result.Add((addr.Address, addr.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
                }
            }
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogWarn($"mDNS: não foi possível listar interfaces: {ex.Message}");
        }

        return result;
    }

    private static string SanitizeHost(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return string.IsNullOrEmpty(s) ? "pc" : s;
    }
}

// Extensões de escrita big-endian (exigido pelo protocolo DNS).
internal static class BinaryWriterExtensions
{
    public static void WriteUInt16BE(this BinaryWriter w, ushort value)
    {
        w.Write((byte)(value >> 8));
        w.Write((byte)(value & 0xFF));
    }

    public static void WriteUInt32BE(this BinaryWriter w, uint value)
    {
        w.Write((byte)(value >> 24));
        w.Write((byte)(value >> 16 & 0xFF));
        w.Write((byte)(value >> 8 & 0xFF));
        w.Write((byte)(value & 0xFF));
    }
}
