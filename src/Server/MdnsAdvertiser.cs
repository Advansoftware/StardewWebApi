using Makaretu.Dns;
using StardewWebApi.Game;

namespace StardewWebApi.Server;

/// <summary>
/// Anuncia a API na rede local via DNS-SD/mDNS, no serviço
/// <c>_stardewapi._tcp</c>. O app usa esse anúncio para encontrar o servidor
/// sem o jogador precisar digitar o IP.
/// </summary>
internal sealed class MdnsAdvertiser : IDisposable
{
    private const string ServiceType = "_stardewapi._tcp";

    private MulticastService? _multicast;
    private ServiceDiscovery? _discovery;

    public void Start(int port)
    {
        _multicast = new MulticastService();
        _discovery = new ServiceDiscovery(_multicast);

        var profile = new ServiceProfile(
            $"Stardew Web API ({Environment.MachineName})",
            ServiceType,
            (ushort)port);

        profile.AddProperty("path", "/");
        profile.AddProperty("version", "0.7.0");
        profile.AddProperty("game", "Stardew Valley");

        _discovery.Advertise(profile);
        _multicast.Start();

        SMAPIWrapper.LogInfo(
            $"Anúncio mDNS ativo: {ServiceType} na porta {port}");
    }

    public void Dispose()
    {
        try
        {
            _discovery?.Dispose();
        }
        catch
        {
            // Ignora erros ao encerrar.
        }

        try
        {
            _multicast?.Dispose();
        }
        catch
        {
            // Ignora erros ao encerrar.
        }

        _discovery = null;
        _multicast = null;
    }
}
