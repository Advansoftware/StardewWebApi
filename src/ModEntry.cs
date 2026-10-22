using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewWebApi.Game;
using StardewWebApi.Game.Events;
using StardewWebApi.Server;

namespace StardewWebApi;

public class ModEntry : Mod
{
    private EventManager? _eventManager;
    private MdnsAdvertiser? _mdns;

    public override void Entry(IModHelper helper)
    {
        SMAPIWrapper.Instance.Initialize(Monitor, helper);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        SMAPIWrapper.LogDebug("Starting web server");
        WebServer.Instance.StartWebServer();

        try
        {
            _mdns = new MdnsAdvertiser();
            _mdns.Start(7882);
        }
        catch (Exception ex)
        {
            SMAPIWrapper.LogWarn($"Não foi possível anunciar a API via mDNS: {ex.Message}");
            _mdns?.Dispose();
            _mdns = null;
        }

        _eventManager = new EventManager();
    }
}