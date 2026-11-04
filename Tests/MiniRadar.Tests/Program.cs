using System;
using System.Linq;
using System.Numerics;
using System.Text;
using MiniRadar.Core;

// Petit lanceur sans dépendance : chaque test est une méthode, un échec fait échouer la CI.
var tests = new (string Name, Action Run)[]
{
    ("Centre de la carte à l'échelle 1", Tests.CenterAtScaleOne),
    ("Centre de la carte : tout suit l'échelle", Tests.CenterFollowsScale),
    ("Calibrage : le décalage suit l'échelle", Tests.CalibrationFollowsScale),
    ("Échelle : zone, fenêtre et zoom se multiplient", Tests.ScalesMultiply),
    ("Un joueur à l'est est à droite", Tests.EastIsRight),
    ("Un joueur au sud est en bas", Tests.SouthIsDown),
    ("Un joueur au nord est en haut", Tests.NorthIsUp),
    ("L'altitude ne compte pas", Tests.AltitudeIgnored),
    ("Rotation : quart de tour autour du centre", Tests.QuarterTurn),
    ("Rotation : même résultat que la formule de référence", Tests.MatchesReferenceFormula),
    ("La rotation garde la distance au centre", Tests.RotationKeepsDistance),
    ("Hors de la carte : ramené sur le bord", Tests.ClampsToEdge),
    ("Hors de la carte : position réelle sinon", Tests.NoClampKeepsPosition),
    ("Sur la carte : pas marqué hors bord", Tests.InsideIsNotBeyond),
    ("Soi-même : au centre, sans erreur", Tests.SelfIsAtCenter),
    ("Distance au sol", Tests.GroundDistance),
    ("Catégorie : l'ami passe avant la compagnie", Tests.FriendWins),
    ("Catégorie : la compagnie passe avant le groupe", Tests.CompanyBeatsParty),
    ("Catégorie : personne de connu", Tests.StrangerIsOthers),
    ("Affichage : ami masqué, couleur de la compagnie", Tests.DisabledFriendFallsBackToCompany),
    ("Affichage : ami masqué sans compagnie, autres joueurs", Tests.DisabledFriendFallsBackToOthers),
    ("Affichage : groupe masqué, ne retombe pas sur les autres", Tests.DisabledPartyStaysHidden),
    ("Affichage : ami du groupe gardé si les amis sont affichés", Tests.PartyFriendStaysFriend),
    ("Affichage : catégorie masquée", Tests.DisabledOthersHidden),
    ("Compagnie : même sigle et même monde", Tests.SameCompany),
    ("Compagnie : autre monde", Tests.OtherWorld),
    ("Compagnie : autre sigle", Tests.OtherTag),
    ("Compagnie : sans compagnie, personne ne correspond", Tests.NoTagNeverMatches),
    ("Compagnie : les octets après la fin du sigle sont ignorés", Tests.BytesAfterEndIgnored),
    ("Aperçu : toutes les catégories sont représentées", Tests.PreviewCoversCategories),
    ("Aperçu : distances annoncées exactes", Tests.PreviewDistancesAreRight),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  ÉCHEC {name} : {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests réussis");
return failed == 0 ? 0 : 1;

internal static class Tests
{
    private static readonly Vector2 Center = new(500f, 300f);
    private static MinimapGeometry Map(float rotation = 0f, float scale = 1f, float edge = 70f) => new(Center, scale, rotation, edge);

    private static void Check(bool condition, string message = "condition fausse")
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Near(float a, float b, float tolerance = 0.001f)
    {
        if (MathF.Abs(a - b) > tolerance)
            throw new Exception($"{a} au lieu de {b}");
    }

    private static void Near(Vector2 a, Vector2 b, float tolerance = 0.001f)
    {
        Near(a.X, b.X, tolerance);
        Near(a.Y, b.Y, tolerance);
    }

    public static void CenterAtScaleOne() => Near(MinimapProjection.MapCenter(new Vector2(100, 50), 1f, Vector2.Zero), new Vector2(209f, 154f));

    public static void CenterFollowsScale() => Near(MinimapProjection.MapCenter(new Vector2(100, 50), 2f, Vector2.Zero), new Vector2(318f, 258f));

    public static void CalibrationFollowsScale() => Near(MinimapProjection.MapCenter(Vector2.Zero, 2f, new Vector2(3, -1)) - MinimapProjection.MapCenter(Vector2.Zero, 2f, Vector2.Zero), new Vector2(6, -2));

    public static void ScalesMultiply() => Near(MinimapProjection.PixelsPerYalm(2f, 1.5f, 0.5f), 1.5f);

    public static void EastIsRight() => Near(MinimapProjection.Project(Map(), Vector3.Zero, new Vector3(10, 0, 0), true).Position, Center + new Vector2(10, 0));

    public static void SouthIsDown() => Near(MinimapProjection.Project(Map(), Vector3.Zero, new Vector3(0, 0, 10), true).Position, Center + new Vector2(0, 10));

    public static void NorthIsUp() => Near(MinimapProjection.Project(Map(), Vector3.Zero, new Vector3(0, 0, -10), true).Position, Center + new Vector2(0, -10));

    public static void AltitudeIgnored() => Near(MinimapProjection.Project(Map(), Vector3.Zero, new Vector3(5, 300, 0), true).Position, Center + new Vector2(5, 0));

    public static void QuarterTurn() => Near(MinimapProjection.Project(Map(MathF.PI / 2f), Vector3.Zero, new Vector3(10, 0, 0), true).Position, Center + new Vector2(0, 10));

    /// <summary>La formule utilisée depuis des années par les plugins de ce genre, écrite telle quelle.</summary>
    public static void MatchesReferenceFormula()
    {
        var self = new Vector3(120f, 5f, -40f);
        var other = new Vector3(133f, 9f, -22f);
        const float k = 1.3f;
        const float angle = 0.8f;

        var relative = new Vector2(self.X - other.X, self.Z - other.Z) * k;
        var before = Center - relative;
        var expected = new Vector2(
            MathF.Cos(angle) * (before.X - Center.X) - MathF.Sin(angle) * (before.Y - Center.Y) + Center.X,
            MathF.Sin(angle) * (before.X - Center.X) + MathF.Cos(angle) * (before.Y - Center.Y) + Center.Y);

        Near(MinimapProjection.Project(Map(angle, k), self, other, false).Position, expected);
    }

    public static void RotationKeepsDistance()
    {
        foreach (var angle in new[] { 0.3f, 1.7f, -2.4f, 6f })
            Near(Vector2.Distance(MinimapProjection.Project(Map(angle), Vector3.Zero, new Vector3(30, 0, 40), false).Position, Center), 50f);
    }

    public static void ClampsToEdge()
    {
        var point = MinimapProjection.Project(Map(edge: 70f), Vector3.Zero, new Vector3(300, 0, 400), true);
        Check(point.BeyondEdge);
        Near(Vector2.Distance(point.Position, Center), 70f);
        Near(point.Position, Center + new Vector2(42, 56));
    }

    public static void NoClampKeepsPosition()
    {
        var point = MinimapProjection.Project(Map(edge: 70f), Vector3.Zero, new Vector3(300, 0, 400), false);
        Check(point.BeyondEdge);
        Near(point.Position, Center + new Vector2(300, 400));
    }

    public static void InsideIsNotBeyond() => Check(!MinimapProjection.Project(Map(edge: 70f), Vector3.Zero, new Vector3(30, 0, 40), true).BeyondEdge);

    public static void SelfIsAtCenter()
    {
        var point = MinimapProjection.Project(Map(1f), new Vector3(7, 8, 9), new Vector3(7, 8, 9), true);
        Near(point.Position, Center);
        Check(!point.BeyondEdge);
    }

    public static void GroundDistance() => Near(MinimapProjection.GroundDistance(new Vector3(0, 100, 0), new Vector3(3, -50, 4)), 5f);

    public static void FriendWins() => Check(PlayerClassifier.Classify(true, true, true, true) == RadarCategory.Friend);

    public static void CompanyBeatsParty() => Check(PlayerClassifier.Classify(false, true, true, false) == RadarCategory.FreeCompany);

    public static void StrangerIsOthers() => Check(PlayerClassifier.Classify(false, false, false, false) == RadarCategory.Others);

    private static Func<RadarCategory, bool> AllExcept(params RadarCategory[] off) => c => !off.Contains(c);

    public static void DisabledFriendFallsBackToCompany() =>
        Check(PlayerClassifier.Resolve(true, true, false, false, AllExcept(RadarCategory.Friend)) == RadarCategory.FreeCompany);

    public static void DisabledFriendFallsBackToOthers() =>
        Check(PlayerClassifier.Resolve(true, false, false, false, AllExcept(RadarCategory.Friend)) == RadarCategory.Others);

    public static void DisabledPartyStaysHidden() =>
        Check(PlayerClassifier.Resolve(false, false, true, false, AllExcept(RadarCategory.Party)) == null);

    public static void PartyFriendStaysFriend() =>
        Check(PlayerClassifier.Resolve(true, false, true, false, AllExcept(RadarCategory.Party)) == RadarCategory.Friend);

    public static void DisabledOthersHidden() =>
        Check(PlayerClassifier.Resolve(false, false, false, false, AllExcept(RadarCategory.Others)) == null);

    private static byte[] Tag(string text, int size = 7)
    {
        var bytes = new byte[size];
        Encoding.UTF8.GetBytes(text).CopyTo(bytes, 0);
        return bytes;
    }

    public static void SameCompany() => Check(PlayerClassifier.SameCompany(Tag("ABC"), 40, Tag("ABC"), 40));

    public static void OtherWorld() => Check(!PlayerClassifier.SameCompany(Tag("ABC"), 40, Tag("ABC"), 41));

    public static void OtherTag() => Check(!PlayerClassifier.SameCompany(Tag("ABC"), 40, Tag("ABD"), 40));

    public static void NoTagNeverMatches() => Check(!PlayerClassifier.SameCompany(Tag(string.Empty), 40, Tag(string.Empty), 40));

    public static void BytesAfterEndIgnored()
    {
        var other = Tag("ABC");
        other[5] = 99;
        Check(PlayerClassifier.SameCompany(Tag("ABC"), 40, other, 40));
    }

    public static void PreviewCoversCategories()
    {
        var categories = PreviewPlayers.Around(Vector3.Zero).Select(p => p.Category).Distinct().Count();
        Check(categories == PlayerClassifier.CategoryCount, $"{categories} catégories");
    }

    public static void PreviewDistancesAreRight()
    {
        var self = new Vector3(10, 20, 30);
        foreach (var player in PreviewPlayers.Around(self))
            Near(MinimapProjection.GroundDistance(self, player.Position), player.Distance, 0.01f);
    }
}
