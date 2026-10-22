using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StardewWebApi.Game;

namespace StardewWebApi.Server;

/// <summary>
/// Encaminha conexões da rede local para o HttpListener, que continua
/// escutando apenas em localhost. Assim não é preciso configurar URLACL no
/// Windows nem executar o jogo como administrador.
/// </summary>
internal sealed class LanForwarder : IDisposable
{
    private const int Port = 7882;

    private readonly List<TcpListener> _listeners = new();
    private readonly CancellationTokenSource _stop = new();

    public void Start()
    {
        foreach (var address in GetLocalIPv4Addresses())
        {
            try
            {
                var listener = new TcpListener(address, Port);
                listener.Start();
                _listeners.Add(listener);
                _ = Task.Run(() => AcceptLoop(listener, _stop.Token));
                SMAPIWrapper.LogInfo($"API acessível na rede local em http://{address}:{Port}/");
            }
            catch (Exception ex)
            {
                SMAPIWrapper.LogWarn(
                    $"Não foi possível abrir a API em {address}:{Port}: {ex.Message}");
            }
        }
    }

    private static async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                SMAPIWrapper.LogError($"Erro ao aceitar conexão da rede local: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => Forward(client, token));
        }
    }

    private static async Task Forward(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, Port, token);

                using var clientStream = client.GetStream();
                using var upstreamStream = upstream.GetStream();

                var toServer = clientStream.CopyToAsync(upstreamStream, token);
                var toClient = upstreamStream.CopyToAsync(clientStream, token);
                await Task.WhenAny(toServer, toClient);
            }
            catch (OperationCanceledException)
            {
                // Encerrando o mod.
            }
            catch (Exception ex)
            {
                SMAPIWrapper.LogDebug($"Conexão da rede local encerrada: {ex.Message}");
            }
        }
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address)
                        || address.ToString().StartsWith("169.254."))
                    {
                        continue;
                    }

                    if (!addresses.Contains(address))
                    {
                        addresses.Add(address);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogWarn($"Não foi possível listar os endereços da rede: {ex.Message}");
        }

        return addresses;
    }

    public void Dispose()
    {
        _stop.Cancel();

        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Ignora erros ao encerrar.
            }
        }

        _listeners.Clear();
        _stop.Dispose();
    }
}
