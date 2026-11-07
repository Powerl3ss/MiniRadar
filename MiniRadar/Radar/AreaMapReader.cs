using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MiniRadar.Core;

namespace MiniRadar.Radar;

/// <summary>État de la grande carte du jeu à cette image. Positions en pixels d'écran, sans le décalage de la fenêtre ImGui.</summary>
public readonly record struct AreaMapState(
    bool Available,
    Vector2 Self,
    Vector2 ClipMin,
    Vector2 ClipMax,
    Vector2 WindowMin,
    Vector2 WindowMax,
    float PixelsPerYalm,
    float Scale,
    float Alpha,
    float MapScale,
    uint MapId,
    string Status);

/// <summary>
/// Lit la grande carte du jeu (AreaMap). La carte n'a pas de rotation : seule compte la position de l'icône de
/// ton personnage, à partir de laquelle les autres joueurs sont placés. Aucun pointeur n'est gardé d'une image à l'autre.
/// </summary>
public sealed unsafe class AreaMapReader
{
    // Texture de l'icône de ton personnage sur les cartes (la version _hr1 est celle des textures haute résolution).
    private const string PlayerIcon = "060443.tex";
    private const string PlayerIconHr = "060443_hr1.tex";

    // Les premiers éléments de la carte sont le fond et les cadres, pas des icônes.
    private const int FirstMarkerNode = 6;
    private const int IconImageIndex = 4;

    // Position de l'icône trouvée à l'image précédente : on la vérifie en premier.
    private int lastPinIndex = -1;

    public AreaMapState Read()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon == null || !addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded || addon->Scale <= 0f)
            return default;

        var agent = AgentMap.Instance();
        if (agent == null || agent->CurrentMapId == 0)
            return Unavailable("Carte inconnue");

        // La grande carte peut montrer une autre zone que celle où tu te trouves : rien à afficher dans ce cas.
        if (agent->SelectedMapId != agent->CurrentMapId)
            return Unavailable("Autre carte affichée");

        var component = addon->AreaMap.ComponentMap != null ? addon->AreaMap.ComponentMap : addon->ComponentMap;
        if (component == null || component->OwnerNode == null || component->UldManager.LoadedState != AtkLoadState.Loaded)
            return Unavailable("Carte non chargée");

        var scale = addon->Scale;
        var windowPos = new Vector2(addon->X, addon->Y);
        var area = &component->OwnerNode->AtkResNode;
        var areaPos = new Vector2(area->X, area->Y);
        var areaSize = new Vector2(area->Width, area->Height);
        if (areaSize.X <= 0f || areaSize.Y <= 0f)
            return Unavailable("Carte non chargée");

        var pin = FindPlayerPin((AtkComponentBase*)component, addon);
        if (pin == null)
            return Unavailable("Icône du personnage introuvable");

        var self = AreaMapProjection.PinCenter(windowPos, scale, areaPos,
                                               new Vector2(pin->X, pin->Y), new Vector2(pin->OriginX, pin->OriginY));
        var sizeFactor = agent->SelectedMapSizeFactor > 0 ? agent->SelectedMapSizeFactor : (short)100;
        var mapScale = addon->AreaMap.MapScale;
        if (!float.IsFinite(mapScale) || mapScale <= 0f || !float.IsFinite(self.X) || !float.IsFinite(self.Y))
            return Unavailable("Échelle de la carte illisible");

        var root = addon->RootNode;
        var windowSize = root != null ? new Vector2(root->Width, root->Height) : areaPos + areaSize;

        return new AreaMapState(
            true,
            self,
            windowPos + areaPos * scale,
            windowPos + (areaPos + areaSize) * scale,
            windowPos,
            windowPos + windowSize * scale,
            AreaMapProjection.PixelsPerYalm(sizeFactor, mapScale, scale),
            scale,
            addon->Alpha / 255f,
            mapScale,
            agent->CurrentMapId,
            "Visible");
    }

    private static AreaMapState Unavailable(string status) => default(AreaMapState) with { Status = status };

    /// <summary>L'icône de ton personnage : reconnue à sa texture, sinon celle indiquée par le jeu.</summary>
    private AtkResNode* FindPlayerPin(AtkComponentBase* component, AddonAreaMap* addon)
    {
        ref var manager = ref component->UldManager;
        var count = (int)manager.NodeListCount;

        if (lastPinIndex >= FirstMarkerNode && lastPinIndex < count && IsPlayerPin(manager.NodeList[lastPinIndex]))
            return manager.NodeList[lastPinIndex];

        for (var i = FirstMarkerNode; i < count; i++)
        {
            if (!IsPlayerPin(manager.NodeList[i]))
                continue;
            lastPinIndex = i;
            return manager.NodeList[i];
        }

        lastPinIndex = -1;
        var fallback = (AtkResNode*)addon->AreaMap.PlayerPin;
        return fallback != null && fallback->IsVisible() ? fallback : null;
    }

    private static bool IsPlayerPin(AtkResNode* node)
    {
        if (node == null || !node->IsVisible() || (int)node->Type < 1000)
            return false;

        var marker = ((AtkComponentNode*)node)->Component;
        if (marker == null || marker->UldManager.NodeListCount <= IconImageIndex)
            return false;

        var image = (AtkImageNode*)marker->UldManager.NodeList[IconImageIndex];
        if (image == null || image->Type != NodeType.Image)
            return false;

        var parts = image->PartsList;
        if (parts == null || parts->Parts == null || image->PartId >= parts->PartCount)
            return false;

        var asset = parts->Parts[image->PartId].UldAsset;
        if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource)
            return false;

        var resource = asset->AtkTexture.Resource;
        if (resource == null || resource->TexFileResourceHandle == null)
            return false;

        var path = resource->TexFileResourceHandle->ResourceHandle.FileName.ToString();
        return path.EndsWith(PlayerIcon, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(PlayerIconHr, StringComparison.OrdinalIgnoreCase);
    }
}
