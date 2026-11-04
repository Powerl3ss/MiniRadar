using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MiniRadar.Radar;

/// <summary>État de la minimap du jeu à cette image.</summary>
public readonly record struct MinimapState(
    bool Available,
    Vector2 Position,
    float Scale,
    float Zoom,
    float Rotation,
    bool NorthLocked,
    float ZoneScale,
    uint MapId,
    float AreaHalfSize);

/// <summary>
/// Lit la fenêtre de la minimap du jeu (_NaviMap). Les pointeurs ne sont jamais gardés d'une image
/// à l'autre : la fenêtre est recréée à certains moments, et une erreur de pointeur fermerait le jeu.
/// Si une valeur manque, la dernière valeur connue est gardée.
/// </summary>
public sealed unsafe class MinimapReader
{
    // Identifiants des éléments dans la fenêtre du jeu, stables depuis des années.
    private const uint RotatingNodeId = 8;
    private const uint MapComponentNodeId = 18;
    private const uint MapImageNodeId = 6;

    private float zoom = 1f;
    private float rotation;
    private bool northLocked;
    private uint mapId;
    private float zoneScale = 1f;

    public MinimapState Read()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonNaviMap>("_NaviMap");
        if (addon == null || addon->UldManager.LoadedState != AtkLoadState.Loaded)
            return default;

        ReadZoom(addon);
        ReadRotation(addon);
        ReadZone();

        var available = addon->IsVisible && addon->VisibilityFlags == 0 && addon->Scale > 0f;
        return new MinimapState(available, new Vector2(addon->X, addon->Y), addon->Scale, zoom, rotation, northLocked, zoneScale, mapId, ReadAreaHalfSize(addon));
    }

    /// <summary>
    /// Moitié de la zone de la carte, avant mise à l'échelle : c'est le carré dans lequel la carte ronde est inscrite,
    /// et le bord d'une carte rendue carrée. Renvoie 0 si la valeur manque ou n'a pas de sens.
    /// </summary>
    private static float ReadAreaHalfSize(AddonNaviMap* addon)
    {
        var area = addon->MainCollision;
        if (area == null)
            return 0f;

        var half = MathF.Max(area->Width, area->Height) / 2f;
        return half is >= 30f and <= 150f ? half : 0f;
    }

    private void ReadZoom(AddonNaviMap* addon)
    {
        var image = addon->MapImage;
        if (image == null)
        {
            var node = addon->GetNodeById(MapComponentNodeId);
            if (node != null && (int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null && component->UldManager.LoadedState == AtkLoadState.Loaded)
                    image = component->GetImageNodeById(MapImageNodeId);
            }
        }

        if (image != null && image->ScaleX > 0f)
            zoom = image->ScaleX;
    }

    private void ReadRotation(AddonNaviMap* addon)
    {
        var node = addon->GetNodeById(RotatingNodeId);
        if (node == null)
            node = addon->NaviMap.CompassFrame;
        if (node != null)
            rotation = node->Rotation;

        // La carte verrouillée au nord ne tourne pas, quelle que soit la valeur lue ci-dessus.
        var checkbox = addon->LockNorthCheckbox;
        northLocked = checkbox != null ? checkbox->IsChecked : addon->NaviMap.NorthLockedUp;
    }

    /// <summary>L'échelle change d'une carte à l'autre, y compris dans une même zone (étages, quartiers).</summary>
    private void ReadZone()
    {
        var agent = AgentMap.Instance();
        if (agent == null || agent->CurrentMapId == 0 || agent->CurrentMapId == mapId)
            return;

        mapId = agent->CurrentMapId;
        var row = Plugin.DataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId);
        zoneScale = row is { SizeFactor: > 0 } map ? map.SizeFactor / 100f : 1f;
    }
}
