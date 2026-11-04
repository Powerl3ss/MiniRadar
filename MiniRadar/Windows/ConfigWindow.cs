using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using MiniRadar.Core;

namespace MiniRadar.Windows;

/// <summary>Fenêtre de réglages : en-tête, barre de navigation à gauche, pages, statut en bas.</summary>
public sealed partial class ConfigWindow : Window, IDisposable
{
    private enum Page { Home, Players, Display, About }

    private static readonly (Page Page, FontAwesomeIcon Icon, string Label)[] Pages =
    {
        (Page.Home, FontAwesomeIcon.Home, "Accueil"),
        (Page.Players, FontAwesomeIcon.Users, "Joueurs"),
        (Page.Display, FontAwesomeIcon.Palette, "Affichage"),
        (Page.About, FontAwesomeIcon.InfoCircle, "À propos"),
    };

    private const string RepoUrl = "https://github.com/Powerl3ss/MiniRadar";

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;

    private Page page = Page.Home;
    
    // Styles de fenêtre poussés avant Begin (PreDraw) et retirés après End (PostDraw).
    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    private Task<IDalamudTextureWrap?>? logoTask;

    public ConfigWindow(Plugin plugin)
        : base("MiniRadar | Réglages###MiniRadarSettings",
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(880, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedConstraints;

        // Vraies polices de grande taille (au lieu d'agrandir le texte, ce qui le rend flou).
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        // La police par défaut est dessinée à des tailles fixes : toute autre taille est étirée et devient floue.
        // 14 et 18 points (soit 18,67 et 24 pixels) sont des tailles exactes.
        titleFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(14f * 4f / 3f)));
        heroFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f)));
        numberFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(32f)));
    }

    private readonly IFontHandle titleFont;
    private readonly IFontHandle heroFont;
    private readonly IFontHandle numberFont;

    // Tailles en pixels "non mis à l'échelle" (Dalamud applique l'échelle de l'interface).
    private const float HeaderHeight = 44f;
    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(780, 560),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    private static readonly WindowSizeConstraints CollapsedConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, HeaderHeight),
    };
    private static readonly WindowSizeConstraints FoldingConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    // Fenêtre repliée : seul l'en-tête reste visible.
    private bool collapsed;
    private float expandedHeight;
    private Vector2 lastSize;

    private void ToggleWindowCollapsed()
    {
        if (folding)
            return;

        if (!collapsed)
        {
            expandedHeight = lastSize.Y;
            collapsed = true;
            StartFold(lastSize.Y, HeaderHeight * S);
        }
        else
        {
            collapsed = false;
            StartFold(lastSize.Y, Math.Max(expandedHeight, 560f * S));
        }
    }

    // --- Enregistrement différé des réglages
    private bool savePending;

    /// <summary>Demande l'enregistrement des réglages : il est fait dès que plus rien n'est en cours de modification.</summary>
    private void SaveSoon() => savePending = true;

    private void FlushSave()
    {
        if (!savePending)
            return;
        savePending = false;
        Cfg.Save();
    }

    public override void OnClose()
    {
        FlushSave();
        // L'aperçu et le calibrage ne doivent pas rester affichés une fois les réglages fermés.
        plugin.PreviewMode = false;
        plugin.CalibrationGuide = false;
    }

    public void Dispose()
    {
        FlushSave();
        titleFont.Dispose();
        heroFont.Dispose();
        numberFont.Dispose();
        if (logoTask is { IsCompletedSuccessfully: true })
            logoTask.Result?.Dispose();
    }

    // =================================================================== Cadre

    public override void PreDraw()
    {
        windowStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                            .Push(ImGuiStyleVar.WindowRounding, 14f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg)
                             .Push(ImGuiCol.Border, Line);

        // Repli / dépli animé de la fenêtre.
        ApplyFoldAnimation();
    }

    public override void PostDraw()
    {
        // Enregistré une fois le geste fini (curseur relâché, couleur choisie, texte validé),
        // et non à chaque image pendant qu'on fait glisser un curseur.
        if (savePending && !ImGui.IsAnyItemActive())
            FlushSave();

        windowColors?.Dispose();
        windowColors = null;
        windowStyle?.Dispose();
        windowStyle = null;
    }

    public override void Draw()
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f * S)
                                .Push(ImGuiStyleVar.GrabRounding, 7f * S)
                                .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarSize, 10f * S)
                                .Push(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f) * S)
                                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 8f) * S)
                                .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * S);
        using var colors = ImRaii.PushColor(ImGuiCol.Text, TextMain)
                                 .Push(ImGuiCol.TextDisabled, TextDim)
                                 .Push(ImGuiCol.ChildBg, Vector4.Zero)
                                 .Push(ImGuiCol.FrameBg, InputBg)
                                 .Push(ImGuiCol.FrameBgHovered, InputBgHover)
                                 .Push(ImGuiCol.FrameBgActive, InputBgHover)
                                 .Push(ImGuiCol.Button, InputBg)
                                 .Push(ImGuiCol.ButtonHovered, InputBgHover)
                                 .Push(ImGuiCol.ButtonActive, AccentSoft)
                                 .Push(ImGuiCol.Header, AccentSoft)
                                 .Push(ImGuiCol.HeaderHovered, AccentSoft)
                                 .Push(ImGuiCol.HeaderActive, AccentBorder)
                                 .Push(ImGuiCol.CheckMark, Accent)
                                 .Push(ImGuiCol.SliderGrab, Accent)
                                 .Push(ImGuiCol.SliderGrabActive, Vector4.Lerp(Accent, White, 0.2f))
                                 .Push(ImGuiCol.PopupBg, new Vector4(0.09f, 0.10f, 0.13f, 0.98f))
                                 .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                                 .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                                 .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f))
                                 .Push(ImGuiCol.ScrollbarGrabActive, AccentBorder)
                                 .Push(ImGuiCol.Separator, Line);

        var size = ImGui.GetWindowSize();
        lastSize = size;
        var origin = ImGui.GetWindowPos();
        var headerH = HeaderHeight * S;
        var footerH = 44f * S;
        var sidebarW = 64f * S;
        var drawList = ImGui.GetWindowDrawList();

        DrawAnimatedBackground(origin, size);

        // Repliée, ou pas encore assez haute pendant l'animation : seulement l'en-tête.
        if (collapsed || size.Y < headerH + footerH + 80f * S)
        {
            DrawTitleBar(size, headerH);
            return;
        }

        drawList.AddRectFilled(origin + new Vector2(0, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(SidebarBg));
        drawList.AddLine(origin + new Vector2(0, headerH), origin + new Vector2(size.X, headerH), Col(Line));
        drawList.AddLine(origin + new Vector2(sidebarW, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(Line));
        drawList.AddLine(origin + new Vector2(0, size.Y - footerH), origin + new Vector2(size.X, size.Y - footerH), Col(Line));

        DrawTitleBar(size, headerH);
        DrawSidebar(headerH, sidebarW);

        ImGui.SetCursorPos(new Vector2(sidebarW + 1, headerH + 1));
        using (var child = ImRaii.Child("##page", new Vector2(size.X - sidebarW - 1, size.Y - headerH - footerH - 2), false))
        {
            if (child.Success)
                DrawPage();
        }

        DrawFooter(size, footerH);
    }

    private void DrawTitleBar(Vector2 size, float headerH)
    {
        var pad = 18f * S;
        var logoSize = 24f * S;

        ImGui.SetCursorPos(new Vector2(pad, (headerH - logoSize) / 2f));
        DrawLogo(logoSize);

        ImGui.SameLine(0, 12f * S);
        using (titleFont.Push())
        {
            ImGui.SetCursorPosY((headerH - ImGui.GetTextLineHeight()) / 2f);
            ImGui.TextUnformatted("MiniRadar");
        }
        var titleMin = ImGui.GetItemRectMin();
        var titleMax = ImGui.GetItemRectMax();

        var loggedIn = Plugin.CurrentCharacterId != 0;
        ImGui.SameLine(0, 12f * S);
        ImGui.SetCursorPosY((headerH - FrameH) / 2f);
        if (loggedIn)
            Pill(plugin.LocalName, Success, dot: true, centerInFrame: true);
        else
            Pill("Hors ligne", TextDim, dot: true, centerInFrame: true);

        ImGui.SetWindowFontScale(0.95f); // soleil / lune un peu plus petits que le titre

        // Soleil la journée, lune le soir (heure réelle du PC), à la taille du titre
        // et centré verticalement sur lui.
        ImGui.SameLine(0, 10f * S);
        var dayIcon = IsDaytime ? FontAwesomeIcon.Sun : FontAwesomeIcon.Moon;
        var box = titleMax.Y - titleMin.Y;
        var boxMin = new Vector2(ImGui.GetCursorScreenPos().X, (titleMin.Y + titleMax.Y) / 2f - box / 2f);
        ImGui.SetCursorScreenPos(boxMin);
        ImGui.Dummy(new Vector2(box, box));
        if (ImGui.IsItemHovered())
            Tip.Show(IsDaytime ? "Journée (8h00 - 20h00)" : "Soirée (20h00 - 8h00)");
        DrawIconCentered(dayIcon, boxMin, boxMin + new Vector2(box, box), IsDaytime ? SunColor : MoonColor);
        ImGui.SetWindowFontScale(1f);

        var buttonY = (headerH - FrameH) / 2f;
        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH, buttonY));
        if (IconButton("##close", FontAwesomeIcon.Times, "Fermer"))
            IsOpen = false;

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 2 - 6f * S, buttonY));
        if (IconButton("##fold", collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                       collapsed ? "Déplier les réglages (ou double-clic sur l'en-tête)" : "Replier les réglages (ou double-clic sur l'en-tête)"))
            ToggleWindowCollapsed();

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 3 - 12f * S, buttonY));
        if (IconButton("##radar", Cfg.RadarVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                       Cfg.RadarVisible ? "Masquer les points sur la minimap" : "Afficher les points sur la minimap"))
            plugin.ToggleRadar();

        // Double-clic sur l'en-tête (hors boutons) : replie ou déplie la fenêtre.
        var origin = ImGui.GetWindowPos();
        var mouse = ImGui.GetMousePos();
        var inHeader = mouse.X >= origin.X && mouse.X < origin.X + size.X &&
                       mouse.Y >= origin.Y && mouse.Y < origin.Y + headerH;
        if (inHeader && ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() &&
            ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            ToggleWindowCollapsed();
    }

    private void DrawSidebar(float headerH, float sidebarW)
    {
        var button = 40f * S;
        var gap = 8f * S;
        var x = (sidebarW - button) / 2f;
        var origin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();

        // Surlignage de la page active : il glisse jusqu'à la nouvelle page.
        var activeIndex = Array.FindIndex(Pages, p => p.Page == page);
        var slide = Animate(ImGui.GetID("##navHighlight"), activeIndex, 18f);
        var highlightMin = origin + new Vector2(x, headerH + 14f * S + slide * (button + gap));
        var highlightMax = highlightMin + new Vector2(button, button);
        drawList.AddRectFilled(highlightMin, highlightMax, Col(AccentSoft), 10f * S);
        drawList.AddRect(highlightMin, highlightMax, Col(AccentBorder), 10f * S);
        drawList.AddRectFilled(new Vector2(origin.X, highlightMin.Y + 9f * S), new Vector2(origin.X + 3f * S, highlightMax.Y - 9f * S), Col(Accent), 2f * S);

        for (var i = 0; i < Pages.Length; i++)
        {
            var (target, icon, label) = Pages[i];
            ImGui.SetCursorPos(new Vector2(x, headerH + 14f * S + i * (button + gap)));
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(button, button);
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(button, button)))
                page = target;

            var hovered = ImGui.IsItemHovered();
            var active = page == target;
            if (!active && hovered)
                drawList.AddRectFilled(min, max, Col(HoverOverlay), 10f * S);

            DrawIconCentered(icon, min, max, active ? Accent : hovered ? TextMain : TextDim);
            if (hovered)
                Tip.Show(label);
        }
    }

    private void DrawFooter(Vector2 size, float footerH)
    {
        var pad = 18f * S;
        var status = FooterStatus();
        var statusSize = ImGui.CalcTextSize(status);
        ImGui.SetCursorPos(new Vector2(size.X - pad - statusSize.X, size.Y - footerH + (footerH - statusSize.Y) / 2f));
        ImGui.TextColored(TextDim, status);
    }

    private string FooterStatus()
    {
        if (Plugin.CurrentCharacterId == 0)
            return "Connecte-toi en jeu pour voir les joueurs autour de toi";
        if (plugin.HiddenReason is { } reason)
            return reason;

        var total = plugin.Scanner.TotalDetected;
        return plugin.PreviewMode ? "Aperçu sur la minimap"
            : total == 0 ? "Personne d'autre autour de toi"
            : $"{total} joueur{(total > 1 ? "s" : string.Empty)} détecté{(total > 1 ? "s" : string.Empty)}";
    }

    private void DrawLogo(float size)
    {
        logoTask ??= LoadLogoAsync();
        if (logoTask is { IsCompletedSuccessfully: true, Result: { } logo })
        {
            ImGui.Image(logo.Handle, new Vector2(size, size));
            return;
        }

        // Le temps que le logo charge : pastille avec une icône de graphique.
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size, size);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, Col(Accent), 8f * S);
        DrawIconCentered(FontAwesomeIcon.Crosshairs, min, max, OnAccent);
        ImGui.Dummy(new Vector2(size, size));
    }

    private static async Task<IDalamudTextureWrap?> LoadLogoAsync()
    {
        try
        {
            await using var stream = typeof(ConfigWindow).Assembly.GetManifestResourceStream("MiniRadar.icon.png");
            if (stream == null)
                return null;
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return await Plugin.TextureProvider.CreateFromImageAsync(memory.ToArray());
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Logo introuvable");
            return null;
        }
    }

    private void DrawPage()
    {
        var pad = 24f * S;
        pageWidth = ImGui.GetContentRegionAvail().X - pad * 2;
        cardInner = pageWidth;

        // Transition : la nouvelle page apparaît en fondu en remontant légèrement.
        var pageBefore = lastPage;
        var appear = PageAppear();
        if (pageBefore != page)
            ImGui.SetScrollY(0);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Max(0.02f, appear) * ImGui.GetStyle().Alpha);

        ImGui.SetCursorPos(new Vector2(pad, pad * 0.8f + (1f - appear) * 16f * S));
        ImGui.BeginGroup();
        switch (page)
        {
            case Page.Home: DrawHomePage(); break;
            case Page.Players: DrawPlayersPage(); break;
            case Page.Display: DrawDisplayPage(); break;
            case Page.About: DrawAboutPage(); break;
        }
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, pad));
    }

    // =================================================================== Accueil

    private void DrawHomePage()
    {
        var scanner = plugin.Scanner;
        var total = scanner.TotalDetected;
        Hero(total == 0 ? "Personne autour de toi." : $"{total} joueur{(total > 1 ? "s" : string.Empty)} autour de toi.",
             plugin.HiddenReason ?? "Les points sont affichés sur ta minimap.");

        CardsRow(
            () => StatCard(FontAwesomeIcon.UserFriends, "Amis", scanner.Count(RadarCategory.Friend), Cfg.Friends),
            () => StatCard(FontAwesomeIcon.Home, "Compagnie", scanner.Count(RadarCategory.FreeCompany), Cfg.FreeCompany),
            () => StatCard(FontAwesomeIcon.Users, "Groupe et alliance", scanner.Count(RadarCategory.Party) + scanner.Count(RadarCategory.Alliance), Cfg.Party),
            () => StatCard(FontAwesomeIcon.Globe, "Autres joueurs", scanner.Count(RadarCategory.Others), Cfg.Others));

        CardsRow(DrawRadarCard, DrawKnownPlayersCard);

        var changed = false;
        Card(() =>
        {
            changed |= ToggleRow("Afficher les points sur la minimap", "Raccourci : /radar", Cfg.RadarVisible, v => Cfg.RadarVisible = v);
            ToggleRow("Aperçu avec de faux joueurs", "Pour régler les couleurs sans personne autour de toi. S'arrête à la fermeture des réglages.", plugin.PreviewMode, v => plugin.PreviewMode = v);
        });

        if (changed)
            SaveSoon();
    }

    private void StatCard(FontAwesomeIcon icon, string title, int count, CategoryStyle style)
    {
        IconText(icon, style.Enabled ? style.Color with { W = 1f } : TextDim);
        ImGui.SameLine();
        ImGui.TextColored(TextDim, title);
        using (numberFont.Push())
            ImGui.TextUnformatted(count.ToString());
        TextDimmed(style.Enabled ? "Affichés" : "Masqués");
    }

    /// <summary>La même chose que la minimap, en plus grand : le nord en haut, toi au centre.</summary>
    private void DrawRadarCard()
    {
        CardLabel("Autour de toi");
        var size = cardInner;
        var radius = size / 2f - 2f * S;
        var min = ImGui.GetCursorScreenPos();
        var center = min + new Vector2(size / 2f, size / 2f);
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(size, size));

        var players = plugin.DisplayedPlayers;
        var farthest = 0f;
        foreach (var player in players)
            farthest = Math.Max(farthest, player.Distance);
        var range = Math.Clamp(farthest * 1.1f, 30f, 150f);

        var corner = new Vector2(radius, radius);
        if (Cfg.MapShape == MinimapShape.Square)
        {
            drawList.AddRectFilled(center - corner, center + corner, Col(new Vector4(0f, 0f, 0f, 0.25f)));
            drawList.AddRect(center - corner, center + corner, Col(TextDim with { W = 0.45f }), 0f, ImDrawFlags.None, 1.5f * S);
            drawList.AddRect(center - corner / 2f, center + corner / 2f, Col(Line));
        }
        else
        {
            drawList.AddCircleFilled(center, radius, Col(new Vector4(0f, 0f, 0f, 0.25f)), 64);
            drawList.AddCircle(center, radius, Col(TextDim with { W = 0.45f }), 64, 1.5f * S);
            drawList.AddCircle(center, radius / 2f, Col(Line), 64);
        }

        drawList.AddLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), Col(Line));
        drawList.AddLine(center - new Vector2(0, radius), center + new Vector2(0, radius), Col(Line));

        var north = "N";
        drawList.AddText(center + new Vector2(-ImGui.CalcTextSize(north).X / 2f, -radius + 4f * S), Col(TextDim), north);
        var legend = $"{range:0} yalms";
        drawList.AddText(new Vector2(center.X + radius - ImGui.CalcTextSize(legend).X, min.Y + size - ImGui.GetTextLineHeight()), Col(TextDim), legend);

        var geometry = new MinimapGeometry(center, radius / range, 0f, radius - 4f * S, Cfg.MapShape);
        var self = plugin.Scanner.SelfPosition;
        foreach (var rank in Enumerable.Range(0, PlayerClassifier.CategoryCount))
        {
            var style = Cfg.Style((RadarCategory)rank);
            foreach (var player in players)
            {
                if ((int)player.Category != rank)
                    continue;
                var point = MinimapProjection.Project(geometry, self, player.Position, clampToEdge: true);
                MinimapOverlay.DrawShape(drawList, style.Shape, point.Position, style.Size * 1.3f * S, Col(style.Color), style.Outline ? Col(new Vector4(0f, 0f, 0f, 0.8f)) : 0u, 1f);
            }
        }

        drawList.AddCircleFilled(center, 3.5f * S, Col(White), 16);
    }

    private void DrawKnownPlayersCard()
    {
        const int maxLines = 14;
        CardLabel("Tes proches");

        var shown = 0;
        var total = 0;
        foreach (var player in plugin.KnownPlayers())
        {
            total++;
            if (shown >= maxLines)
                continue;
            shown++;

            var style = Cfg.Style(player.Category);
            var p = ImGui.GetCursorScreenPos();
            var lineH = ImGui.GetTextLineHeight();
            MinimapOverlay.DrawShape(ImGui.GetWindowDrawList(), style.Shape, p + new Vector2(6f * S, lineH / 2f), 4f * S, Col(style.Color with { W = 1f }), 0u, 1f);
            ImGui.Dummy(new Vector2(14f * S, lineH));
            ImGui.SameLine();
            ImGui.TextUnformatted(player.Name);
            var world = plugin.WorldName(player.HomeWorld);
            var detail = world.Length > 0 ? $"{world}  |  {player.Distance:0} y" : $"{player.Distance:0} y";
            ImGui.SameLine(p.X - ImGui.GetWindowPos().X + ImGui.GetScrollX() + cardInner - TextWidth(detail));
            ImGui.TextColored(TextDim, detail);
        }

        if (total == 0)
            TextDimmed("Aucun ami ni membre de ta compagnie autour de toi pour le moment.");
        else if (total > shown)
            TextDimmed($"et {total - shown} autre(s)");
    }

    // =================================================================== Joueurs

    private void DrawPlayersPage()
    {
        Hero("Joueurs", "Une couleur et une forme par catégorie. Un joueur n'a qu'une catégorie : un ami de ta compagnie reste un ami.");

        Card(() => ToggleRow("Aperçu avec de faux joueurs", "Pour voir tes réglages sur la minimap sans personne autour de toi.", plugin.PreviewMode, v => plugin.PreviewMode = v));

        var changed = false;
        changed |= CategoryCard(RadarCategory.Friend, FontAwesomeIcon.UserFriends, "Amis", "Les joueurs de ta liste d'amis.");
        changed |= CategoryCard(RadarCategory.FreeCompany, FontAwesomeIcon.Home, "Compagnie", "Même sigle de compagnie et même monde d'origine que toi. Le jeu ne donne pas plus d'information sur les autres joueurs.");
        changed |= CategoryCard(RadarCategory.Others, FontAwesomeIcon.Globe, "Autres joueurs", "Tous les autres joueurs que le jeu connaît autour de toi.");
        changed |= CategoryCard(RadarCategory.Party, FontAwesomeIcon.Users, "Groupe", "Désactivé au départ : le jeu les affiche déjà sur la minimap.");
        changed |= CategoryCard(RadarCategory.Alliance, FontAwesomeIcon.UsersCog, "Alliance", "Désactivé au départ : le jeu les affiche déjà sur la minimap.");

        if (FancyButton("##resetStyles", "Catégories par défaut", FontAwesomeIcon.Undo, ButtonSize("Catégories par défaut", FontAwesomeIcon.Undo), primary: false))
        {
            var defaults = new Configuration();
            Cfg.Friends = defaults.Friends;
            Cfg.FreeCompany = defaults.FreeCompany;
            Cfg.Party = defaults.Party;
            Cfg.Alliance = defaults.Alliance;
            Cfg.Others = defaults.Others;
            changed = true;
        }

        if (changed)
            SaveSoon();
    }

    private static readonly (DotShape Shape, string Label)[] Shapes =
    {
        (DotShape.Circle, "Rond"),
        (DotShape.Diamond, "Losange"),
        (DotShape.Square, "Carré"),
        (DotShape.Triangle, "Triangle"),
    };

    private bool CategoryCard(RadarCategory category, FontAwesomeIcon icon, string title, string description)
    {
        var style = Cfg.Style(category);
        var changed = false;
        using var id = ImRaii.PushId(title);

        Card(() =>
        {
            Row(() =>
            {
                IconText(icon, style.Enabled ? style.Color with { W = 1f } : TextDim);
                ImGui.SameLine();
                ImGui.TextUnformatted(title);
                TextDimmed(description);
            }, SwitchSize.X, () =>
            {
                var enabled = style.Enabled;
                if (Switch("##enabled", ref enabled))
                {
                    style.Enabled = enabled;
                    changed = true;
                }
            });

            if (!style.Enabled)
                return;

            Divider();
            changed |= ColorRow("Couleur", null, style.Color, v => style.Color = v);
            changed |= SliderRow("Taille", null, style.Size, 1f, 8f, "%.1f", v => style.Size = v);

            var shapeWidth = 340f * S;
            SettingRow("Forme", null, shapeWidth, () =>
            {
                var items = new (string Label, FontAwesomeIcon Icon)[]
                {
                    (Shapes[0].Label, FontAwesomeIcon.Circle),
                    (Shapes[1].Label, FontAwesomeIcon.Gem),
                    (Shapes[2].Label, FontAwesomeIcon.Square),
                    (Shapes[3].Label, FontAwesomeIcon.CaretUp),
                };
                var chosen = Segmented("##shape", items, (int)style.Shape, shapeWidth);
                if (chosen != (int)style.Shape)
                {
                    style.Shape = (DotShape)chosen;
                    changed = true;
                }
            }, FrameH + 20f * S);

            changed |= ToggleRow("Contour sombre", "Garde le point visible sur les zones claires de la carte.", style.Outline, v => style.Outline = v);
            changed |= ToggleRow("Nom à côté du point", "À réserver aux catégories peu nombreuses.", style.ShowName, v => style.ShowName = v);
        });

        return changed;
    }

    // =================================================================== Affichage

    private void DrawDisplayPage()
    {
        Hero("Affichage", "Comment et quand les points apparaissent sur la minimap.",
             Cfg.RadarVisible ? "Points affichés" : "Points masqués");

        var changed = false;

        Card(() =>
        {
            CardLabel("Points");
            changed |= ToggleRow("Afficher les points sur la minimap", "Raccourci : /radar", Cfg.RadarVisible, v => Cfg.RadarVisible = v);
            changed |= SliderRow("Opacité", null, Cfg.Opacity, 0.1f, 1f, "%.2f", v => Cfg.Opacity = v);
            changed |= ToggleRow("Suivre la taille de la minimap", "Les points grandissent avec la minimap quand tu l'agrandis dans la disposition de l'interface.", Cfg.ScaleWithMinimap, v => Cfg.ScaleWithMinimap = v);
            changed |= ToggleRow("Infobulle au survol", "Le nom et la distance du joueur sous la souris.", Cfg.ShowTooltips, v => Cfg.ShowTooltips = v);
        });

        Card(() =>
        {
            CardLabel("Minimap et portée");

            var shapeWidth = 340f * S;
            SettingRow("Forme de la minimap", "Carrée si un autre plugin a changé sa forme : les joueurs au loin se placent alors sur le bord du carré.", shapeWidth, () =>
            {
                var chosen = Segmented("##mapShape", new[]
                {
                    ("Ronde", FontAwesomeIcon.Circle),
                    ("Carrée", FontAwesomeIcon.Square),
                }, (int)Cfg.MapShape, shapeWidth);
                if (chosen != (int)Cfg.MapShape)
                {
                    Cfg.MapShape = (MinimapShape)chosen;
                    changed = true;
                }
            }, FrameH + 20f * S);

            changed |= SliderIntRow("Distance maximale", Cfg.MaxDistance == 0 ? "Sans limite : tous les joueurs que le jeu connaît autour de toi." : "En yalms. Les joueurs plus loin ne sont pas affichés. 0 = sans limite.", Cfg.MaxDistance, 0, 150, v => Cfg.MaxDistance = v);

            var edgeWidth = 340f * S;
            SettingRow("Joueurs hors de la minimap", null, edgeWidth, () =>
            {
                var chosen = Segmented("##edge", new[]
                {
                    ("Sur le bord", FontAwesomeIcon.DotCircle),
                    ("Masqués", FontAwesomeIcon.EyeSlash),
                }, (int)Cfg.Edge, edgeWidth);
                if (chosen != (int)Cfg.Edge)
                {
                    Cfg.Edge = (EdgeBehavior)chosen;
                    changed = true;
                }
            }, FrameH + 20f * S);

            using (ImRaii.Disabled(Cfg.Edge != EdgeBehavior.KeepOnEdge))
                changed |= SliderRow("Opacité sur le bord", "Pour distinguer les joueurs au loin de ceux qui sont vraiment sur la carte.", Cfg.EdgeOpacity, 0.1f, 1f, "%.2f", v => Cfg.EdgeOpacity = v);
        });

        Card(() =>
        {
            CardLabel("Masquer automatiquement");
            changed |= ToggleRow("En combat", null, Cfg.HideInCombat, v => Cfg.HideInCombat = v);
            changed |= ToggleRow("En instance", "Donjons, raids, défis...", Cfg.HideInDuty, v => Cfg.HideInDuty = v);
            TextDimmed("Les points disparaissent aussi en JcJ, sans réglage, pendant les cinématiques et quand la minimap est masquée.");
        });

        Card(() =>
        {
            CardLabel("Calibrage");
            TextDimmed("À toucher seulement si les points sont décalés. Pour vérifier : active la catégorie Groupe avec une autre personne, son point doit tomber sur l'icône du jeu.");
            ToggleRow("Repère sur la minimap", "Une croix au centre et une ligne sur le bord de la carte. S'arrête à la fermeture des réglages.", plugin.CalibrationGuide, v => plugin.CalibrationGuide = v);
            changed |= SliderRow("Décalage horizontal", null, Cfg.CalibrationX, -20f, 20f, "%.1f", v => Cfg.CalibrationX = v);
            changed |= SliderRow("Décalage vertical", null, Cfg.CalibrationY, -20f, 20f, "%.1f", v => Cfg.CalibrationY = v);
            if (Cfg.MapShape == MinimapShape.Square)
                changed |= SliderRow("Taille du carré", Cfg.SquareHalfSize > 0f ? "Moitié du côté. 0 = taille lue dans le jeu." : $"Taille lue dans le jeu : {plugin.EdgeSize(plugin.Minimap):0.0}. Monte le curseur pour la remplacer.", Cfg.SquareHalfSize, 0f, 150f, "%.1f", v => Cfg.SquareHalfSize = v);
            else
                changed |= SliderRow("Rayon du bord", null, Cfg.EdgeRadius, 40f, 110f, "%.1f", v => Cfg.EdgeRadius = v);
            if (FancyButton("##resetCalibration", "Valeurs par défaut", FontAwesomeIcon.Undo, ButtonSize("Valeurs par défaut", FontAwesomeIcon.Undo), primary: false))
            {
                Cfg.CalibrationX = 0f;
                Cfg.CalibrationY = 0f;
                Cfg.EdgeRadius = MinimapProjection.DefaultEdgeRadius;
                Cfg.SquareHalfSize = 0f;
                changed = true;
            }
        });

        Card(() =>
        {
            CardLabel("Fenêtre de réglages");
            changed |= ColorRow("Couleur d'accent", null, Cfg.AccentColor, v => Cfg.AccentColor = v);
            changed |= ToggleRow("Fond animé", "Halos lumineux et particules derrière les réglages.", Cfg.AnimatedSettingsBackground, v => Cfg.AnimatedSettingsBackground = v);
        });

        if (changed)
            SaveSoon();
    }

    // =================================================================== À propos

    private void DrawAboutPage()
    {
        var version = Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();
        Hero("À propos", "Les joueurs autour de toi, sur ta minimap.", $"Version {version}", "par Powerl3ss");

        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            DrawLogo(64f * S);
            ImGui.SameLine(0, 16f * S);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(start.Y + 6f * S);
            using (heroFont.Push())
                ImGui.TextUnformatted("MiniRadar");
            TextDimmed("Le plugin lit seulement ce que ton jeu connaît déjà : il n'envoie rien, et rien n'est enregistré sur les joueurs croisés. Il ne voit que les joueurs chargés autour de toi, pas toute la zone.");
            ImGui.EndGroup();
        });

        Card(() =>
        {
            CardLabel("Commandes");
            CommandRow("/radar", "Affiche ou masque les points sur la minimap");
            CommandRow("/radar config", "Ouvre ces réglages");
            CommandRow("/radar debug", "Affiche l'état de la minimap dans le chat");
        });

        Card(() =>
        {
            CardLabel("Diagnostic");
            var map = plugin.Minimap;
            var state = map.Available ? "Visible" : "Indisponible";
            Row(() => ImGui.TextUnformatted("Minimap du jeu"), PillWidth(state, dot: true), () =>
                Pill(state, map.Available ? Success : Danger, dot: true, centerInFrame: true));
            TextDimmed($"Position : {map.Position.X:0}, {map.Position.Y:0}  |  échelle : {map.Scale:0.00}  |  zoom : {map.Zoom:0.00}");
            TextDimmed($"Rotation : {map.Rotation:0.00}{(map.NorthLocked ? " (nord fixe)" : string.Empty)}  |  carte : {map.MapId}  |  échelle de la carte : {map.ZoneScale:0.00}");
            TextDimmed($"Forme : {(Cfg.MapShape == MinimapShape.Square ? "carrée" : "ronde")}  |  zone de la carte : {map.AreaHalfSize:0.0}  |  bord utilisé : {plugin.EdgeSize(map):0.0}");
            TextDimmed($"Joueurs détectés : {plugin.Scanner.TotalDetected}  |  affichés : {plugin.DisplayedPlayers.Count}");
        });

        if (FancyButton("##repo", "Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt, ButtonSize("Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt), primary: false))
            Util.OpenLink(RepoUrl);
    }

    private void CommandRow(string command, string description)
    {
        Row(() =>
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Accent))
                ImGui.TextUnformatted(command);
            ImGui.SameLine(0, 16f * S);
            ImGui.TextColored(TextDim, description);
        }, 0f, null);
    }
}
