using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MiniRadar.Core;
using MiniRadar.Radar;
using MiniRadar.Windows;

namespace MiniRadar;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/radar";
    private const string LongCommandName = "/miniradar";
    private const string ChatPrefix = "[MiniRadar] ";

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("MiniRadar");

    public PlayerScanner Scanner { get; }

    private readonly MinimapReader minimapReader = new();
    private readonly MinimapOverlay overlay;
    private readonly ConfigWindow configWindow;
    private readonly Dictionary<uint, string> worldNames = new();
    private readonly List<RadarPlayer> previewPlayers = new();

    /// <summary>Faux joueurs sur la minimap (réglages), pour choisir les couleurs sans personne autour.</summary>
    public bool PreviewMode { get; set; }

    /// <summary>Cercle et croix de calibrage sur la minimap (réglages).</summary>
    public bool CalibrationGuide { get; set; }

    public MinimapState Minimap { get; private set; }

    /// <summary>Pourquoi les points ne sont pas affichés, ou null s'ils le sont.</summary>
    public string? HiddenReason { get; private set; }

    /// <summary>Les points sont dessinés sur la minimap à cette image.</summary>
    public bool OverlayActive => HiddenReason == null;

    /// <summary>Les joueurs affichés : les vrais, ou les faux pendant l'aperçu.</summary>
    public IReadOnlyList<RadarPlayer> DisplayedPlayers => PreviewMode ? previewPlayers : Scanner.Players;

    public string LocalName => PlayerState.IsLoaded && !string.IsNullOrEmpty(PlayerState.CharacterName) ? PlayerState.CharacterName : "Toi";

    public static ulong CurrentCharacterId => ClientState.IsLoggedIn && PlayerState.IsLoaded ? PlayerState.ContentId : 0;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Infobulles : même style que les autres plugins, avec la couleur d'accent choisie.
        Tip.AccentProvider = () => Configuration.AccentColor;

        Scanner = new PlayerScanner(Configuration);
        overlay = new MinimapOverlay(this);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Affiche/masque les points sur la minimap. « /radar config » : réglages. « /radar debug » : état de la minimap.",
        });
        CommandManager.AddHandler(LongCommandName, new CommandInfo(OnCommand) { HelpMessage = "Comme /radar.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += overlay.Draw;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= overlay.Draw;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LongCommandName);
    }

    // ---------------------------------------------------------------- Commande

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "debug":
                Print(DiagnosticLine());
                break;
            default:
                ToggleRadar();
                Print(Configuration.RadarVisible ? "Points affichés sur la minimap." : "Points masqués.");
                break;
        }
    }

    // ------------------------------------------------------- Événements du jeu

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51] || !ClientState.IsLoggedIn)
        {
            Scanner.Clear();
            Minimap = default;
            HiddenReason = "Écran de chargement";
            return;
        }

        // Jamais en JcJ : voir l'adversaire à travers les murs serait un avantage déloyal. Pas de réglage pour ça.
        // Rien n'est lu, pour que le radar des réglages reste vide lui aussi.
        if (ClientState.IsPvPExcludingDen)
        {
            Scanner.Clear();
            Minimap = default;
            HiddenReason = "Désactivé en JcJ";
            return;
        }

        Scanner.Scan();
        Minimap = minimapReader.Read();

        if (PreviewMode && Scanner.HasSelf)
        {
            previewPlayers.Clear();
            foreach (var sample in PreviewPlayers.Around(Scanner.SelfPosition))
            {
                if (Configuration.Style(sample.Category).Enabled)
                    previewPlayers.Add(sample);
            }
        }

        HiddenReason = ComputeHiddenReason();
    }

    private string? ComputeHiddenReason()
    {
        if (!Scanner.HasSelf)
            return "Personnage non chargé";
        if (!Minimap.Available)
            return "Minimap masquée";

        if (PreviewMode || CalibrationGuide)
            return null;

        if (!Configuration.RadarVisible)
            return "Points masqués";
        if (Configuration.HideInCombat && Condition[ConditionFlag.InCombat])
            return "Masqué en combat";
        if (Configuration.HideInDuty && Condition[ConditionFlag.BoundByDuty])
            return "Masqué en instance";
        return null;
    }

    private void OnTerritoryChanged(uint territory) => Scanner.ForgetNames();

    // ------------------------------------------------------------ Calculs

    /// <summary>La géométrie de la minimap, à partir de ce que le jeu indique et du calibrage choisi.</summary>
    public MinimapGeometry BuildGeometry(in MinimapState map, Vector2 viewportPos)
    {
        var calibration = new Vector2(Configuration.CalibrationX, Configuration.CalibrationY);
        return new MinimapGeometry(
            MinimapProjection.MapCenter(viewportPos + map.Position, map.Scale, calibration),
            MinimapProjection.PixelsPerYalm(map.ZoneScale, map.Scale, map.Zoom),
            map.NorthLocked ? 0f : map.Rotation,
            Configuration.EdgeRadius * map.Scale);
    }

    /// <summary>Nom du monde d'origine d'un joueur.</summary>
    public string WorldName(uint worldId)
    {
        if (worldId == 0)
            return string.Empty;
        if (worldNames.TryGetValue(worldId, out var known))
            return known;

        var name = DataManager.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
        worldNames[worldId] = name;
        return name;
    }

    /// <summary>Joueurs à mettre en avant dans les réglages : les plus proches d'abord, sans la foule.</summary>
    public IEnumerable<RadarPlayer> KnownPlayers() =>
        DisplayedPlayers.Where(p => p.Category != RadarCategory.Others)
                        .OrderByDescending(p => p.Category)
                        .ThenBy(p => p.Distance);

    public string DiagnosticLine() =>
        $"Minimap : {(Minimap.Available ? "visible" : "indisponible")}  |  position {Minimap.Position.X:0},{Minimap.Position.Y:0}  |  " +
        $"échelle {Minimap.Scale:0.00}  |  zoom {Minimap.Zoom:0.00}  |  rotation {Minimap.Rotation:0.00}{(Minimap.NorthLocked ? " (nord fixe)" : string.Empty)}  |  " +
        $"carte {Minimap.MapId}, échelle {Minimap.ZoneScale:0.00}  |  joueurs détectés : {Scanner.TotalDetected}";

    // ------------------------------------------------------------ Fenêtres

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleRadar()
    {
        Configuration.RadarVisible = !Configuration.RadarVisible;
        Configuration.Save();
    }

    public static void Print(string message) => ChatGui.Print(ChatPrefix + message);
}
