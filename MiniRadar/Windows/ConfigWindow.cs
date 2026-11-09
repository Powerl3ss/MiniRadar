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

    // La navigation, rangée par thème : chaque groupe a son petit titre dans la barre latérale.
    private static readonly (string Group, (Page Page, FontAwesomeIcon Icon, string Label)[] Items)[] Navigation =
    {
        ("Radar", new[]
        {
            (Page.Home, FontAwesomeIcon.Home, "Accueil"),
            (Page.Players, FontAwesomeIcon.Users, "Joueurs"),
        }),
        ("Réglages", new[]
        {
            (Page.Display, FontAwesomeIcon.Palette, "Affichage"),
        }),
        ("Plus", new[]
        {
            (Page.About, FontAwesomeIcon.InfoCircle, "À propos"),
        }),
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
        Size = new Vector2(1000, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedConstraints;

        // Vraies polices de grande taille (au lieu d'agrandir le texte, ce qui le rend flou).
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        // La police par défaut est dessinée à des tailles fixes : toute autre taille est étirée et devient floue.
        // 14 et 18 points (soit 18,67 et 24 pixels) sont des tailles exactes.
        titleFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(14f * 4f / 3f)));
        heroFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f)));
        // 36 points (48 pixels) : les grands chiffres de l'accueil.
        numberFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(36f * 4f / 3f)));
    }

    private readonly IFontHandle titleFont;
    private readonly IFontHandle heroFont;
    private readonly IFontHandle numberFont;

    // Tailles en pixels "non mis à l'échelle" (Dalamud applique l'échelle de l'interface).
    private const float HeaderHeight = 44f;
    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(860, 600),
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
                            .Push(ImGuiStyleVar.WindowRounding, 22f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg with { W = Math.Clamp(Cfg.SettingsOpacity, 0f, 1f) })
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
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f * S)
                                .Push(ImGuiStyleVar.GrabRounding, 10f * S)
                                .Push(ImGuiStyleVar.PopupRounding, 10f * S)
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
                                 .Push(ImGuiCol.PopupBg, new Vector4(0.110f, 0.106f, 0.129f, 0.99f))
                                 .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                                 .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                                 .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f))
                                 .Push(ImGuiCol.ScrollbarGrabActive, AccentBorder)
                                 .Push(ImGuiCol.Separator, Line);

        var size = ImGui.GetWindowSize();
        lastSize = size;
        var origin = ImGui.GetWindowPos();
        var margin = PanelGap;
        var sidebarW = 216f * S;
        var drawList = ImGui.GetWindowDrawList();

        DrawAnimatedBackground(origin, size);

        // La barre latérale est un panneau à part, posé sur le fond comme les autres.
        var sidebarMin = origin + new Vector2(margin, margin);
        var sidebarMax = origin + new Vector2(margin + sidebarW, size.Y - margin);
        drawList.AddRectFilled(sidebarMin, sidebarMax, Col(CardBg), PanelRounding);
        drawList.AddRect(sidebarMin, sidebarMax, Col(Line), PanelRounding);
        DrawSidebar(sidebarMin, sidebarMax);

        // Les pages défilent entre les mêmes marges que la barre latérale : leurs panneaux s'alignent sur elle en haut
        // et en bas.
        var pageX = margin + sidebarW;
        ImGui.SetCursorPos(new Vector2(pageX, margin));
        var noPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using (var child = ImRaii.Child("##page", new Vector2(size.X - pageX, size.Y - margin * 2f), false))
        {
            noPadding.Dispose();
            if (child.Success)
                DrawPage();
        }
    }

    private void DrawSidebar(Vector2 min, Vector2 max)
    {
        var pad = 14f * S;
        var origin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();
        var width = max.X - min.X - pad * 2;
        var x = min.X - origin.X + pad;
        var y = min.Y - origin.Y + pad + 4f * S;

        // Le logo, le nom, et la croix pour fermer.
        var logoSize = 30f * S;
        ImGui.SetCursorPos(new Vector2(x, y));
        DrawLogo(logoSize);
        using (titleFont.Push())
        {
            ImGui.SetCursorPos(new Vector2(x + logoSize + 10f * S, y + MathF.Round((logoSize - ImGui.GetTextLineHeight()) / 2f)));
            ImGui.TextUnformatted("BetterChat");
        }

        ImGui.SetCursorPos(new Vector2(x + width - FrameH, y + (logoSize - FrameH) / 2f));
        if (IconButton("##close", FontAwesomeIcon.Times))
            IsOpen = false;

        y += logoSize + 22f * S;

        // Les pages, par groupe. Le surlignage de la page active glisse jusqu'à la nouvelle.
        var itemH = 33f * S;
        var activeY = y;
        var positions = new List<(Page Page, FontAwesomeIcon Icon, string Label, float Y)>();
        foreach (var (group, items) in Navigation)
        {
            ImGui.SetCursorPos(new Vector2(x + 8f * S, y));
            ImGui.TextColored(TextFaint, group.ToUpperInvariant());
            y += ImGui.GetTextLineHeight() + 4f * S;

            foreach (var (target, icon, label) in items)
            {
                positions.Add((target, icon, label, y));
                if (target == page)
                    activeY = y;
                y += itemH + 1f * S;
            }

            y += 8f * S;
        }

        var slide = Animate(ImGui.GetID("##navHighlight"), activeY, 18f);
        var highlightMin = origin + new Vector2(x, slide);
        drawList.AddRectFilled(highlightMin, highlightMin + new Vector2(width, itemH), Col(InputBg), 11f * S);

        foreach (var (target, icon, label, itemY) in positions)
        {
            ImGui.SetCursorPos(new Vector2(x, itemY));
            var itemMin = ImGui.GetCursorScreenPos();
            var itemMax = itemMin + new Vector2(width, itemH);
            if (ImGui.InvisibleButton($"##nav{target}", itemMax - itemMin))
                page = target;

            var hovered = ImGui.IsItemHovered();
            var active = page == target;
            if (!active && hovered)
                drawList.AddRectFilled(itemMin, itemMax, Col(HoverOverlay), 11f * S);

            var iconBox = new Vector2(itemH, itemH);
            DrawIconCentered(icon, itemMin + new Vector2(4f * S, 0f), itemMin + new Vector2(4f * S, 0f) + iconBox, active ? Accent : hovered ? TextMain : TextDim);
            // L'icône et les majuscules du texte sont centrées sur le milieu de la ligne, chacune sur son dessin.
            var textY = MathF.Round(itemMin.Y + itemH / 2f - CapMiddle());
            drawList.AddText(new Vector2(itemMin.X + itemH + 4f * S, textY), Col(active || hovered ? TextMain : TextDim), label);
        }

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
        var pad = PanelGap;
        // La barre de défilement passe dans la marge de droite.
        pageWidth = ImGui.GetContentRegionAvail().X - pad - ImGui.GetStyle().ScrollbarSize;
        cardInner = pageWidth;

        // Transition : la nouvelle page apparaît en fondu en remontant légèrement.
        var pageBefore = lastPage;
        var appear = PageAppear();
        if (pageBefore != page)
            ImGui.SetScrollY(0);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Max(0.02f, appear) * ImGui.GetStyle().Alpha);

        ImGui.SetCursorPos(new Vector2(pad, (1f - appear) * 16f * S));
        ImGui.BeginGroup();
        switch (page)
        {
            case Page.Home: DrawHomePage(); break;
            case Page.Players: DrawPlayersPage(); break;
            case Page.Display: DrawDisplayPage(); break;
            case Page.About: DrawAboutPage(); break;
        }
        // La page s'arrête au bas de son dernier élément, sans marge en plus.
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, ImGui.GetItemRectMax().Y));
        ImGui.EndGroup();
    }

    // =================================================================== Accueil

    private void DrawHomePage()
    {
        var scanner = plugin.Scanner;
        var total = scanner.TotalDetected;

        CardRow("##stats1", 2, (i, width) =>
        {
            if (i == 0)
                RowCard(width, () => StatCard(FontAwesomeIcon.UserFriends, "Amis", scanner.Count(RadarCategory.Friend), Cfg.Friends));
            else
                RowCard(width, () => StatCard(FontAwesomeIcon.Home, "Compagnie", scanner.Count(RadarCategory.FreeCompany), Cfg.FreeCompany));
        });
        CardRow("##stats2", 2, (i, width) =>
        {
            if (i == 0)
                RowCard(width, () => StatCard(FontAwesomeIcon.Users, "Groupe et alliance", scanner.Count(RadarCategory.Party) + scanner.Count(RadarCategory.Alliance), Cfg.Party));
            else
                RowCard(width, () => StatCard(FontAwesomeIcon.Globe, "Autres joueurs", scanner.Count(RadarCategory.Others), Cfg.Others), glow: true);
        });

        CardRow("##homeRadar", 2, (i, width) =>
        {
            if (i == 0)
            {
                SectionTitle("Autour de toi", FontAwesomeIcon.Crosshairs, total == 0 ? "Personne" : $"{total} joueur{(total > 1 ? "s" : string.Empty)}");
                RowCard(width, DrawRadarCard);
            }
            else
            {
                SectionTitle("Tes proches", FontAwesomeIcon.UserFriends);
                RowCard(width, DrawKnownPlayersCard);
            }
        });

        var changed = false;
        SectionTitle("Minimap", FontAwesomeIcon.MapMarkedAlt, Cfg.RadarVisible ? "Points affichés" : "Points masqués");
        Card(() =>
        {
            if (plugin.HiddenReason is { } reason)
                TextDimmed(reason);
            changed |= ToggleRow("Afficher les points sur la minimap", "Raccourci : /radar", Cfg.RadarVisible, v => Cfg.RadarVisible = v);
            ToggleRow("Aperçu avec de faux joueurs", "Pour régler les couleurs sans personne autour de toi. S'arrête à la fermeture des réglages.", plugin.PreviewMode, v => plugin.PreviewMode = v);
        });

        if (changed)
            SaveSoon();
    }

    /// <summary>Un grand chiffre : pastille de la couleur de la catégorie et titre, le chiffre, puis son état.</summary>
    private void StatCard(FontAwesomeIcon icon, string title, int count, CategoryStyle style)
    {
        var start = ImGui.GetCursorPos();
        var badge = BadgeSize;
        IconBadge(icon, badge, style.Enabled ? style.Color : TextDim);
        ImGui.SetCursorPos(new Vector2(start.X + badge + 10f * S, start.Y + MathF.Round((badge - ImGui.GetTextLineHeight()) / 2f)));
        ImGui.TextUnformatted(title);

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + badge + 14f * S));
        using (numberFont.Push())
            ImGui.TextUnformatted(count.ToString());

        ImGui.Dummy(new Vector2(0f, 2f * S));
        TextDimmed(style.Enabled ? "Affichés sur la minimap" : "Masqués");
    }

    /// <summary>La même chose que la minimap, en plus grand : le nord en haut, toi au centre.</summary>
    private void DrawRadarCard()
    {
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
        SectionTitle("Aperçu", FontAwesomeIcon.Eye);
        Card(() =>
        {
            TextDimmed("Une couleur et une forme par catégorie. Un joueur n'a qu'une catégorie : un ami de ta compagnie reste un ami.");
            ToggleRow("Aperçu avec de faux joueurs", "Pour voir tes réglages sur la minimap sans personne autour de toi.", plugin.PreviewMode, v => plugin.PreviewMode = v);
        });

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

        SectionTitle(title, icon, style.Enabled ? "Affichés" : "Masqués", style.Enabled ? style.Color : TextDim);
        Card(() =>
        {
            changed |= ToggleRow("Afficher sur la minimap", description, style.Enabled, v => style.Enabled = v);

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
        var changed = false;

        SectionTitle("Points", FontAwesomeIcon.DotCircle);
        Card(() =>
        {
            changed |= ToggleRow("Afficher les points sur la minimap", "Raccourci : /radar", Cfg.RadarVisible, v => Cfg.RadarVisible = v);
            changed |= SliderRow("Opacité", null, Cfg.Opacity, 0.1f, 1f, "%.2f", v => Cfg.Opacity = v);
            changed |= ToggleRow("Suivre la taille de la minimap", "Les points grandissent avec la minimap quand tu l'agrandis dans la disposition de l'interface.", Cfg.ScaleWithMinimap, v => Cfg.ScaleWithMinimap = v);
            changed |= ToggleRow("Infobulle au survol", "Le nom et la distance du joueur sous la souris.", Cfg.ShowTooltips, v => Cfg.ShowTooltips = v);
        });

        SectionTitle("Minimap et portée", FontAwesomeIcon.MapMarkedAlt);
        Card(() =>
        {

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

        SectionTitle("Grande carte", FontAwesomeIcon.Map);
        Card(() =>
        {
            changed |= ToggleRow("Afficher aussi sur la grande carte", "Quand tu ouvres la carte de la zone où tu te trouves. Rien n'apparaît si elle montre une autre zone.", Cfg.ShowOnAreaMap, v => Cfg.ShowOnAreaMap = v);
            using (ImRaii.Disabled(!Cfg.ShowOnAreaMap))
                changed |= SliderRow("Taille des points", "Par rapport à la minimap.", Cfg.AreaMapDotScale, 0.5f, 3f, "%.1f x", v => Cfg.AreaMapDotScale = v);
        });

        SectionTitle("Masquer automatiquement", FontAwesomeIcon.EyeSlash);
        Card(() =>
        {
            changed |= ToggleRow("En combat", null, Cfg.HideInCombat, v => Cfg.HideInCombat = v);
            changed |= ToggleRow("En instance", "Donjons, raids, défis...", Cfg.HideInDuty, v => Cfg.HideInDuty = v);
            TextDimmed("Les points disparaissent aussi en JcJ, sans réglage, pendant les cinématiques et quand la minimap ou la grande carte est masquée.");
        });

        SectionTitle("Calibrage", FontAwesomeIcon.Crosshairs);
        Card(() =>
        {
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

        SectionTitle("Fenêtre de réglages", FontAwesomeIcon.Cog);
        Card(() =>
        {
            changed |= ColorRow("Couleur d'accent", null, Cfg.AccentColor, v => Cfg.AccentColor = v);
            changed |= ToggleRow("Fond animé", "Halos lumineux et particules derrière les réglages.", Cfg.AnimatedSettingsBackground, v => Cfg.AnimatedSettingsBackground = v);
            changed |= SliderRow("Opacité du fond", "Derrière les panneaux. 0 : le jeu se voit entièrement.", Cfg.SettingsOpacity, 0f, 1f, "%.2f", v => Cfg.SettingsOpacity = v);
        });

        if (changed)
            SaveSoon();
    }

    // =================================================================== À propos

    private void DrawAboutPage()
    {
        var version = Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();
        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            DrawLogo(64f * S);
            ImGui.SameLine(0, 16f * S);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(start.Y + 6f * S);
            using (heroFont.Push())
                ImGui.TextUnformatted("MiniRadar");
            ImGui.SameLine(0, 10f * S);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2f * S);
            Chip($"Version {version}");
            TextDimmed("Le plugin lit seulement ce que ton jeu connaît déjà : il n'envoie rien, et rien n'est enregistré sur les joueurs croisés. Il ne voit que les joueurs chargés autour de toi, pas toute la zone.");
            ImGui.Dummy(new Vector2(0f, 2f * S));
            // Les deux lignes des crédits se suivent de près.
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * S)))
                ImGui.TextUnformatted("Créé par Powerl3ss.");
            TextDimmed("Claude, l'IA d'Anthropic, a grandement participé à son développement.");
            ImGui.EndGroup();
        });

        SectionTitle("Commandes", FontAwesomeIcon.Terminal);
        Card(() =>
        {
            CommandRow("/radar", "Affiche ou masque les points sur la minimap");
            CommandRow("/radar config", "Ouvre ces réglages");
            CommandRow("/radar debug", "Affiche l'état de la minimap dans le chat");
        });

        SectionTitle("Diagnostic", FontAwesomeIcon.Stethoscope);
        Card(() =>
        {
            var map = plugin.Minimap;
            var state = map.Available ? "Visible" : "Indisponible";
            Row(() => ImGui.TextUnformatted("Minimap du jeu"), PillWidth(state, dot: true), () =>
                Pill(state, map.Available ? Success : Danger, dot: true, centerInFrame: true));
            TextDimmed($"Position : {map.Position.X:0}, {map.Position.Y:0}  |  échelle : {map.Scale:0.00}  |  zoom : {map.Zoom:0.00}");
            TextDimmed($"Rotation : {map.Rotation:0.00}{(map.NorthLocked ? " (nord fixe)" : string.Empty)}  |  carte : {map.MapId}  |  échelle de la carte : {map.ZoneScale:0.00}");
            TextDimmed($"Forme : {(Cfg.MapShape == MinimapShape.Square ? "carrée" : "ronde")}  |  zone de la carte : {map.AreaHalfSize:0.0}  |  bord utilisé : {plugin.EdgeSize(map):0.0}");
            var area = plugin.AreaMap;
            var areaState = area.Available ? "Visible" : string.IsNullOrEmpty(area.Status) ? "Fermée" : area.Status;
            Row(() => ImGui.TextUnformatted("Grande carte"), PillWidth(areaState, dot: true), () =>
                Pill(areaState, area.Available ? Success : TextDim, dot: true, centerInFrame: true));
            if (area.Available)
                TextDimmed($"Zoom : {area.MapScale:0.00}  |  {area.PixelsPerYalm:0.00} px par yalm  |  ton icône : {area.Self.X:0}, {area.Self.Y:0}");
            TextDimmed($"Joueurs détectés : {plugin.Scanner.TotalDetected}  |  affichés : {plugin.DisplayedPlayers.Count}");
        });

        if (FancyButton("##repo", "Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt, ButtonSize("Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt), primary: false))
            Util.OpenLink(RepoUrl);
    }

    /// <summary>Une commande et ce qu'elle fait, sur une ligne simple : les commandes se suivent de près.</summary>
    private void CommandRow(string command, string description)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(16f * S, 4f * S)))
        {
            ImGui.TextColored(Accent, command);
            ImGui.SameLine();
            ImGui.TextColored(TextDim, description);
        }
    }
}
