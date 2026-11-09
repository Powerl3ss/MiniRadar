using System;
using System.Numerics;
using Dalamud.Configuration;
using MiniRadar.Core;

namespace MiniRadar;

public enum DotShape
{
    Circle,
    Diamond,
    Square,
    Triangle,
}

/// <summary>Que faire d'un joueur plus loin que le bord de la minimap.</summary>
public enum EdgeBehavior
{
    KeepOnEdge,
    Hide,
}

/// <summary>Apparence des points d'une catégorie de joueurs.</summary>
[Serializable]
public class CategoryStyle
{
    public bool Enabled { get; set; } = true;
    public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);
    public float Size { get; set; } = 3f;
    public DotShape Shape { get; set; } = DotShape.Circle;
    public bool Outline { get; set; } = true;
    public bool ShowName { get; set; } = false;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Catégories ---
    public CategoryStyle Friends { get; set; } = new() { Color = new(1.00f, 0.69f, 0.18f, 1f), Size = 3.5f };
    public CategoryStyle FreeCompany { get; set; } = new() { Color = new(0.28f, 0.88f, 0.54f, 1f), Size = 3.5f };

    // Le jeu affiche déjà le groupe et l'alliance sur la minimap : désactivés au départ.
    public CategoryStyle Party { get; set; } = new() { Enabled = false, Color = new(0.39f, 0.71f, 1.00f, 1f), Size = 3f };
    public CategoryStyle Alliance { get; set; } = new() { Enabled = false, Color = new(0.68f, 0.56f, 1.00f, 1f), Size = 3f };
    public CategoryStyle Others { get; set; } = new() { Color = new(0.82f, 0.86f, 0.92f, 0.85f), Size = 2.5f };

    // --- Affichage ---
    public bool RadarVisible { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public bool ScaleWithMinimap { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;

    /// <summary>Distance maximale en yalms (0 = tous les joueurs que le jeu connaît).</summary>
    public int MaxDistance { get; set; } = 0;
    public MinimapShape MapShape { get; set; } = MinimapShape.Circle;
    public EdgeBehavior Edge { get; set; } = EdgeBehavior.KeepOnEdge;
    public float EdgeOpacity { get; set; } = 0.55f;

    // --- Grande carte ---
    public bool ShowOnAreaMap { get; set; } = true;

    /// <summary>Taille des points sur la grande carte, par rapport à la minimap.</summary>
    public float AreaMapDotScale { get; set; } = 1.2f;

    // --- Masquage automatique ---
    public bool HideInCombat { get; set; } = false;
    public bool HideInDuty { get; set; } = false;

    // --- Calibrage ---
    public float CalibrationX { get; set; } = 0f;
    public float CalibrationY { get; set; } = 0f;
    public float EdgeRadius { get; set; } = MinimapProjection.DefaultEdgeRadius;

    /// <summary>Moitié du côté d'une minimap carrée (0 = taille lue dans le jeu).</summary>
    public float SquareHalfSize { get; set; } = 0f;

    // --- Fenêtre de réglages ---
    public Vector4 AccentColor { get; set; } = new(0.30f, 0.65f, 1.00f, 1.00f);
    public bool AnimatedSettingsBackground { get; set; } = true;
    public float SettingsOpacity { get; set; } = 0.80f; // fond de la fenêtre des réglages (les panneaux restent pleins)

    public CategoryStyle Style(RadarCategory category) => category switch
    {
        RadarCategory.Friend => Friends,
        RadarCategory.FreeCompany => FreeCompany,
        RadarCategory.Party => Party,
        RadarCategory.Alliance => Alliance,
        _ => Others,
    };

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
