using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MiniRadar.Core;

namespace MiniRadar.Windows;

/// <summary>
/// Dessine les points par-dessus la minimap du jeu. Rien n'est ajouté dans la fenêtre du jeu :
/// le dessin se fait sur le fond de l'écran, derrière les fenêtres des plugins, et il ne capte aucun clic.
/// </summary>
public sealed class MinimapOverlay
{
    private const float HoverMargin = 3f;
    private const int MaxTooltipLines = 10;

    private readonly Plugin plugin;
    private readonly List<Dot> dots = new();
    private readonly List<Dot> hovered = new();

    private readonly record struct Dot(RadarPlayer Player, Vector2 Position, float Radius, bool OnEdge);

    public MinimapOverlay(Plugin plugin) => this.plugin = plugin;

    private Configuration Cfg => plugin.Configuration;

    public void Draw()
    {
        if (!plugin.OverlayActive)
            return;

        var map = plugin.Minimap;
        var viewport = ImGui.GetMainViewport();
        var geometry = plugin.BuildGeometry(map, viewport.Pos);
        var drawList = ImGui.GetBackgroundDrawList();
        var dotScale = Cfg.ScaleWithMinimap ? map.Scale : ImGuiHelpers.GlobalScale;

        if (plugin.CalibrationGuide)
            DrawCalibrationGuide(drawList, geometry);

        CollectDots(geometry, dotScale);

        // Du moins prioritaire au plus prioritaire : les amis restent au-dessus de la foule.
        for (var rank = 0; rank < PlayerClassifier.CategoryCount; rank++)
        {
            var category = (RadarCategory)rank;
            var style = Cfg.Style(category);
            foreach (var dot in dots)
            {
                if (dot.Player.Category == category)
                    DrawDot(drawList, dot, style, dotScale);
            }
        }

        if (Cfg.ShowTooltips)
            DrawTooltip(geometry);
    }

    private void CollectDots(in MinimapGeometry geometry, float dotScale)
    {
        dots.Clear();
        var clamp = Cfg.Edge == EdgeBehavior.KeepOnEdge;
        var maxDistance = Cfg.MaxDistance;
        var self = plugin.Scanner.SelfPosition;

        foreach (var player in plugin.DisplayedPlayers)
        {
            if (maxDistance > 0 && player.Distance > maxDistance)
                continue;

            var point = MinimapProjection.Project(geometry, self, player.Position, clamp);
            if (point.BeyondEdge && !clamp)
                continue;

            dots.Add(new Dot(player, point.Position, Cfg.Style(player.Category).Size * dotScale, point.BeyondEdge));
        }
    }

    private void DrawDot(ImDrawListPtr drawList, in Dot dot, CategoryStyle style, float dotScale)
    {
        var alpha = style.Color.W * Cfg.Opacity * (dot.OnEdge ? Cfg.EdgeOpacity : 1f);
        var fill = ImGui.ColorConvertFloat4ToU32(style.Color with { W = alpha });
        var outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.80f * alpha));
        var thickness = MathF.Max(1f, dotScale);
        DrawShape(drawList, style.Shape, dot.Position, dot.Radius, fill, style.Outline ? outline : 0u, thickness);

        if (!style.ShowName || dot.Player.Name.Length == 0)
            return;

        var height = ImGui.GetTextLineHeight();
        var text = dot.Position + new Vector2(dot.Radius + 3f * dotScale, -height / 2f);
        drawList.AddText(text + Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, alpha)), dot.Player.Name);
        drawList.AddText(text, fill, dot.Player.Name);
    }

    /// <summary>Dessine une forme de point. Sert aussi aux exemples dans les réglages.</summary>
    public static void DrawShape(ImDrawListPtr drawList, DotShape shape, Vector2 center, float radius, uint fill, uint outline, float thickness)
    {
        switch (shape)
        {
            case DotShape.Diamond:
            {
                var r = radius * 1.25f;
                var (a, b, c, d) = (center + new Vector2(0, -r), center + new Vector2(r, 0), center + new Vector2(0, r), center + new Vector2(-r, 0));
                drawList.AddQuadFilled(a, b, c, d, fill);
                if (outline != 0)
                    drawList.AddQuad(a, b, c, d, outline, thickness);
                break;
            }
            case DotShape.Square:
            {
                var half = new Vector2(radius * 0.92f);
                drawList.AddRectFilled(center - half, center + half, fill);
                if (outline != 0)
                    drawList.AddRect(center - half, center + half, outline, 0f, ImDrawFlags.None, thickness);
                break;
            }
            case DotShape.Triangle:
            {
                var r = radius * 1.3f;
                var (a, b, c) = (center + new Vector2(0, -r), center + new Vector2(r * 0.87f, r * 0.5f), center + new Vector2(-r * 0.87f, r * 0.5f));
                drawList.AddTriangleFilled(a, b, c, fill);
                if (outline != 0)
                    drawList.AddTriangle(a, b, c, outline, thickness);
                break;
            }
            default:
                drawList.AddCircleFilled(center, radius, fill, 16);
                if (outline != 0)
                    drawList.AddCircle(center, radius, outline, 16, thickness);
                break;
        }
    }

    private static void DrawCalibrationGuide(ImDrawListPtr drawList, in MinimapGeometry geometry)
    {
        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.25f, 0.85f, 0.95f));
        var arm = geometry.EdgeRadius * 0.14f;
        if (geometry.Shape == MinimapShape.Square)
            drawList.AddRect(geometry.Center - new Vector2(geometry.EdgeRadius), geometry.Center + new Vector2(geometry.EdgeRadius), color, 0f, ImDrawFlags.None, 1.5f);
        else
            drawList.AddCircle(geometry.Center, geometry.EdgeRadius, color, 96, 1.5f);
        drawList.AddLine(geometry.Center - new Vector2(arm, 0), geometry.Center + new Vector2(arm, 0), color, 1.5f);
        drawList.AddLine(geometry.Center - new Vector2(0, arm), geometry.Center + new Vector2(0, arm), color, 1.5f);
    }

    private void DrawTooltip(in MinimapGeometry geometry)
    {
        var mouse = ImGui.GetMousePos();
        if (!MinimapProjection.IsInside(geometry, mouse, 10f) || ImGui.GetIO().WantCaptureMouse)
            return;

        hovered.Clear();
        foreach (var dot in dots)
        {
            if (Vector2.Distance(mouse, dot.Position) <= dot.Radius + HoverMargin)
                hovered.Add(dot);
        }

        if (hovered.Count == 0)
            return;

        using (Tip.Begin())
        {
            foreach (var dot in hovered.OrderByDescending(d => d.Player.Category).ThenBy(d => d.Player.Distance).Take(MaxTooltipLines))
            {
                Tip.DotLine(Cfg.Style(dot.Player.Category).Color with { W = 1f }, dot.Player.Name);
                ImGui.SameLine(0, 8f * ImGuiHelpers.GlobalScale);
                Tip.Note($"{dot.Player.Distance:0} yalms");
            }

            if (hovered.Count > MaxTooltipLines)
                Tip.Note($"et {hovered.Count - MaxTooltipLines} autre(s)");
        }
    }
}
