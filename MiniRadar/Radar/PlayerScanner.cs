using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using MiniRadar.Core;

namespace MiniRadar.Radar;

/// <summary>Parcourt les joueurs que le jeu connaît et les range par catégorie. À appeler à chaque image.</summary>
public sealed unsafe class PlayerScanner
{
    private readonly Configuration configuration;
    private readonly Dictionary<ulong, string> names = new();
    private readonly Func<RadarCategory, bool> isEnabled;
    private readonly int[] counts = new int[PlayerClassifier.CategoryCount];

    public PlayerScanner(Configuration configuration)
    {
        this.configuration = configuration;
        isEnabled = category => configuration.Style(category).Enabled;
    }

    /// <summary>Joueurs à afficher, sans soi-même.</summary>
    public List<RadarPlayer> Players { get; } = new();

    /// <summary>Tous les joueurs détectés, sans soi-même, y compris ceux des catégories masquées.</summary>
    public int TotalDetected { get; private set; }

    public Vector3 SelfPosition { get; private set; }

    public bool HasSelf { get; private set; }

    /// <summary>Nombre de joueurs détectés dans une catégorie, même masquée.</summary>
    public int Count(RadarCategory category) => counts[(int)category];

    /// <summary>Les identifiants peuvent resservir : on oublie les noms à chaque changement de zone.</summary>
    public void ForgetNames() => names.Clear();

    public void Clear()
    {
        Players.Clear();
        Array.Clear(counts);
        TotalDetected = 0;
        HasSelf = false;
    }

    public void Scan()
    {
        Clear();

        var self = Plugin.ObjectTable.LocalPlayer;
        if (self == null)
            return;

        HasSelf = true;
        SelfPosition = self.Position;
        var selfId = self.GameObjectId;
        var selfCharacter = (Character*)self.Address;
        var selfTag = selfCharacter->FreeCompanyTag;
        var selfWorld = selfCharacter->HomeWorld;

        foreach (var obj in Plugin.ObjectTable.PlayerObjects)
        {
            if (obj.GameObjectId == selfId)
                continue;

            var character = (Character*)obj.Address;
            if (character == null)
                continue;

            var isFriend = character->IsFriend;
            var isCompany = PlayerClassifier.SameCompany(selfTag, selfWorld, character->FreeCompanyTag, character->HomeWorld);
            var isParty = character->IsPartyMember;
            var isAlliance = character->IsAllianceMember;

            TotalDetected++;
            counts[(int)PlayerClassifier.Classify(isFriend, isCompany, isParty, isAlliance)]++;

            if (PlayerClassifier.Resolve(isFriend, isCompany, isParty, isAlliance, isEnabled) is not { } category)
                continue;

            var position = obj.Position;
            Players.Add(new RadarPlayer(obj.GameObjectId, NameOf(obj.GameObjectId, obj), category, position,
                                        MinimapProjection.GroundDistance(SelfPosition, position), character->HomeWorld));
        }
    }

    private string NameOf(ulong id, Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        if (names.TryGetValue(id, out var known))
            return known;

        var name = obj.Name.TextValue;
        if (name.Length > 0)
            names[id] = name;
        return name;
    }
}
