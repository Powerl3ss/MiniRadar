using System;
using System.Collections.Generic;
using System.Numerics;

namespace MiniRadar.Core;

/// <summary>Catégorie d'un joueur, de la moins prioritaire à la plus prioritaire.</summary>
public enum RadarCategory
{
    Others = 0,
    Alliance = 1,
    Party = 2,
    FreeCompany = 3,
    Friend = 4,
}

/// <summary>Un joueur détecté autour de soi.</summary>
public readonly record struct RadarPlayer(ulong Id, string Name, RadarCategory Category, Vector3 Position, float Distance, ushort HomeWorld);

public static class PlayerClassifier
{
    public const int CategoryCount = 5;

    /// <summary>
    /// La catégorie d'un joueur, quels que soient les réglages : un ami de ta compagnie reste un ami.
    /// </summary>
    public static RadarCategory Classify(bool isFriend, bool isFreeCompany, bool isParty, bool isAlliance)
    {
        if (isFriend)
            return RadarCategory.Friend;
        if (isFreeCompany)
            return RadarCategory.FreeCompany;
        if (isParty)
            return RadarCategory.Party;
        if (isAlliance)
            return RadarCategory.Alliance;
        return RadarCategory.Others;
    }

    /// <summary>
    /// La catégorie à afficher en tenant compte de celles qui sont désactivées : si les amis sont masqués,
    /// un ami de ta compagnie apparaît avec la couleur de la compagnie. Renvoie null si le joueur ne doit pas apparaître.
    /// Un membre du groupe ne retombe jamais sur « Autres joueurs » : le jeu l'affiche déjà sur la minimap.
    /// </summary>
    public static RadarCategory? Resolve(bool isFriend, bool isFreeCompany, bool isParty, bool isAlliance, Func<RadarCategory, bool> enabled)
    {
        if (isFriend && enabled(RadarCategory.Friend))
            return RadarCategory.Friend;
        if (isFreeCompany && enabled(RadarCategory.FreeCompany))
            return RadarCategory.FreeCompany;
        if (isParty)
            return enabled(RadarCategory.Party) ? RadarCategory.Party : null;
        if (isAlliance)
            return enabled(RadarCategory.Alliance) ? RadarCategory.Alliance : null;
        return enabled(RadarCategory.Others) ? RadarCategory.Others : null;
    }

    /// <summary>
    /// Même compagnie : même sigle, et même monde d'origine (une compagnie n'existe que sur un monde).
    /// Le jeu ne donne pas l'identifiant de la compagnie des autres joueurs : le sigle est la seule preuve,
    /// et une autre compagnie du même monde qui aurait le même sigle serait confondue avec la tienne.
    /// </summary>
    public static bool SameCompany(ReadOnlySpan<byte> localTag, ushort localWorld, ReadOnlySpan<byte> otherTag, ushort otherWorld)
    {
        var local = Trim(localTag);
        return local.Length > 0 && localWorld == otherWorld && local.SequenceEqual(Trim(otherTag));
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> tag)
    {
        var end = tag.IndexOf((byte)0);
        return end < 0 ? tag : tag[..end];
    }
}

/// <summary>Ce qu'il faut savoir de la minimap pour y placer un point. Tout est en pixels d'écran.</summary>
public readonly record struct MinimapGeometry(
    Vector2 Center,
    float PixelsPerYalm,
    float Rotation,
    float EdgeRadius);

/// <summary>Position d'un point sur la minimap.</summary>
public readonly record struct ProjectedPoint(Vector2 Position, bool BeyondEdge);

public static class MinimapProjection
{
    // Taille de la fenêtre _NaviMap du jeu, sans mise à l'échelle.
    public const float AddonSize = 218f;

    // Le centre de la carte est un peu au-dessus du centre de la fenêtre (les coordonnées sont en dessous).
    public const float CenterLift = 5f;

    // Rayon de la partie visible de la carte, sans mise à l'échelle.
    public const float DefaultEdgeRadius = 68.7f;

    /// <summary>Centre de la carte à l'écran, à partir de la position et de l'échelle de la fenêtre du jeu.</summary>
    public static Vector2 MapCenter(Vector2 addonPosition, float addonScale, Vector2 calibration) =>
        addonPosition + new Vector2(AddonSize / 2f, AddonSize / 2f - CenterLift) * addonScale + calibration * addonScale;

    /// <summary>Pixels de minimap pour un yalm : dépend de la zone, de l'échelle de la fenêtre et du zoom.</summary>
    public static float PixelsPerYalm(float zoneScale, float addonScale, float zoom) => zoneScale * addonScale * zoom;

    /// <summary>
    /// Place un joueur sur la minimap. Le jeu travaille sur le plan X (est) et Z (sud) : l'altitude ne compte pas.
    /// Quand la minimap tourne avec la caméra, les points tournent autour de son centre, où se trouve le joueur.
    /// </summary>
    public static ProjectedPoint Project(in MinimapGeometry map, Vector3 self, Vector3 target, bool clampToEdge)
    {
        var offset = new Vector2(target.X - self.X, target.Z - self.Z) * map.PixelsPerYalm;

        if (map.Rotation != 0f)
        {
            var cos = MathF.Cos(map.Rotation);
            var sin = MathF.Sin(map.Rotation);
            offset = new Vector2(cos * offset.X - sin * offset.Y, sin * offset.X + cos * offset.Y);
        }

        var length = offset.Length();
        var beyond = length > map.EdgeRadius;
        if (beyond && clampToEdge)
            offset *= map.EdgeRadius / length;

        return new ProjectedPoint(map.Center + offset, beyond);
    }

    /// <summary>Distance au sol, en yalms.</summary>
    public static float GroundDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}

/// <summary>Faux joueurs de l'aperçu : un peu partout autour de soi, un par catégorie au moins.</summary>
public static class PreviewPlayers
{
    private static readonly (string Name, RadarCategory Category, float Angle, float Distance)[] Samples =
    {
        ("Ami proche", RadarCategory.Friend, 40f, 14f),
        ("Ami au loin", RadarCategory.Friend, 200f, 34f),
        ("Membre de ta compagnie", RadarCategory.FreeCompany, 110f, 20f),
        ("Membre de ta compagnie", RadarCategory.FreeCompany, 290f, 28f),
        ("Membre de ton groupe", RadarCategory.Party, 340f, 12f),
        ("Membre de ton alliance", RadarCategory.Alliance, 250f, 18f),
        ("Autre joueur", RadarCategory.Others, 15f, 30f),
        ("Autre joueur", RadarCategory.Others, 75f, 8f),
        ("Autre joueur", RadarCategory.Others, 150f, 26f),
        ("Autre joueur", RadarCategory.Others, 320f, 38f),
        ("Autre joueur", RadarCategory.Others, 185f, 200f),
    };

    public static IEnumerable<RadarPlayer> Around(Vector3 self)
    {
        for (var i = 0; i < Samples.Length; i++)
        {
            var (name, category, angle, distance) = Samples[i];
            var radians = angle * MathF.PI / 180f;
            var position = self + new Vector3(MathF.Sin(radians), 0f, -MathF.Cos(radians)) * distance;
            yield return new RadarPlayer((ulong)i + 1, name, category, position, distance, 0);
        }
    }
}
