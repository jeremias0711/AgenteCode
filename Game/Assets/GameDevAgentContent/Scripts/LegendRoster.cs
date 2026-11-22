/// <summary>
/// Definitive relation between the four defender types and the Colombian legends that represent them (phase 11).
/// Presentation only: the mechanics stay in the existing tower components (ProjectileTower, SlowdownTower, BombTower)
/// and the tower type is still detected from those components (TutorialContext.TryGetKind), never from a name.
/// </summary>
public enum LegendId
{
    Mohan = 0,       // Cannon tower: single-target projectile
    Madremonte = 1,  // Slow tower: slowing area
    Hojarasquin = 2, // Bomb tower: area damage
    Patasola = 3,    // Triple shot tower: three projectiles
}

public static class LegendRoster
{
    public static LegendId FromKind(TutorialContext.TowerKind kind)
    {
        switch (kind)
        {
            case TutorialContext.TowerKind.Slow: return LegendId.Madremonte;
            case TutorialContext.TowerKind.Bomb: return LegendId.Hojarasquin;
            case TutorialContext.TowerKind.Triple: return LegendId.Patasola;
            default: return LegendId.Mohan;
        }
    }

    public static bool TryGet(Tower tower, out LegendId legend)
    {
        legend = LegendId.Mohan;
        if (!TutorialContext.TryGetKind(tower, out TutorialContext.TowerKind kind))
        {
            return false;
        }
        legend = FromKind(kind);
        return true;
    }

    /// <summary>Full name, capitalised ("El Mohán"): tutorial, upgrade panel, collection sheet.</summary>
    public static string DisplayName(LegendId legend)
    {
        switch (legend)
        {
            case LegendId.Madremonte: return "La Madremonte";
            case LegendId.Hojarasquin: return "El Hojarasquín";
            case LegendId.Patasola: return "La Patasola";
            default: return "El Mohán";
        }
    }

    public static string DisplayName(TutorialContext.TowerKind kind) => DisplayName(FromKind(kind));

    /// <summary>Short lowercase name for the small HUD tray card, in the lowercase style of the v2 HUD ("mohán").</summary>
    public static string CardName(LegendId legend)
    {
        switch (legend)
        {
            case LegendId.Madremonte: return "madremonte";
            case LegendId.Hojarasquin: return "hojarasquín";
            case LegendId.Patasola: return "patasola";
            default: return "mohán";
        }
    }

    /// <summary>Object of "invocar" with the Spanish contraction: "al mohán", "a la madremonte".</summary>
    public static string InvokeObject(LegendId legend)
    {
        switch (legend)
        {
            case LegendId.Madremonte: return "a la madremonte";
            case LegendId.Hojarasquin: return "al hojarasquín";
            case LegendId.Patasola: return "a la patasola";
            default: return "al mohán";
        }
    }

    /// <summary>Stable id of the legend card in the Collection catalog (phase 10).</summary>
    public static string CollectionId(LegendId legend)
    {
        switch (legend)
        {
            case LegendId.Madremonte: return "leyenda_madremonte";
            case LegendId.Hojarasquin: return "leyenda_hojarasquin";
            case LegendId.Patasola: return "leyenda_patasola";
            default: return "leyenda_mohan";
        }
    }

    /// <summary>Card name for a tower prefab name ("Cannon Tower"...), used by editor tools that only have the name.</summary>
    public static LegendId FromTowerName(string towerName)
    {
        string n = (towerName ?? string.Empty).ToLowerInvariant();
        if (n.Contains("slow")) return LegendId.Madremonte;
        if (n.Contains("bomb")) return LegendId.Hojarasquin;
        if (n.Contains("triple")) return LegendId.Patasola;
        return LegendId.Mohan;
    }
}
