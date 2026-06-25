using System.Linq;

namespace CairnAccessories;

/// <summary>Which body-diagram column a region sits in.</summary>
public enum Side { Left, Center, Right }

/// <summary>An anatomical region. Aava's ~243 bones each map to exactly one, so the picker can
/// show a body-shaped grid of regions instead of one giant list. Anything unmatched is Misc.</summary>
public enum Cat
{
    Head, Face, Neck, Spine, Pelvis, Root,
    ArmL, HandL, LegL, FootL,
    ArmR, HandR, LegR, FootR,
    Gear, Misc,
}

/// <summary>
/// Pure name-based classification of rig bones into <see cref="Cat"/> regions, plus the body-
/// diagram layout. The rig names every bone bn_[l_|r_]Region..., so classification is
/// deterministic and side-effect free — no Unity, no collections, just string → region.
/// </summary>
public static class BoneRegions
{
    /// <summary>Where a region's button sits in the head→feet, left|center|right body grid.</summary>
    public readonly struct Cell
    {
        public readonly Cat Cat;
        public readonly Side Side;
        public readonly int Row;     // 0 = top (head); increases downward
        public readonly string Label;
        public Cell(Cat cat, Side side, int row, string label) { Cat = cat; Side = side; Row = row; Label = label; }
    }

    public static readonly Cell[] Layout =
    {
        new(Cat.Head,   Side.Center, 0, "Head"),
        new(Cat.Face,   Side.Center, 1, "Face"),
        new(Cat.Neck,   Side.Center, 2, "Neck"),
        new(Cat.ArmL,   Side.Left,   3, "L Arm"),
        new(Cat.Spine,  Side.Center, 3, "Spine"),
        new(Cat.ArmR,   Side.Right,  3, "R Arm"),
        new(Cat.HandL,  Side.Left,   4, "L Hand"),
        new(Cat.Pelvis, Side.Center, 4, "Pelvis"),
        new(Cat.HandR,  Side.Right,  4, "R Hand"),
        new(Cat.LegL,   Side.Left,   5, "L Leg"),
        new(Cat.Root,   Side.Center, 5, "Root"),
        new(Cat.LegR,   Side.Right,  5, "R Leg"),
        new(Cat.FootL,  Side.Left,   6, "L Foot"),
        new(Cat.FootR,  Side.Right,  6, "R Foot"),
        new(Cat.Gear,   Side.Center, 7, "Gear"),
        new(Cat.Misc,   Side.Center, 8, "Misc"),
    };

    // Order matters: face/hand sub-parts are tested before the broader Arm/Head buckets so e.g.
    // bn_l_Index_01 lands in HandL, not ArmL.
    public static Cat Classify(string boneName)
    {
        var side = SideOf(boneName);
        string body = Strip(boneName);          // name minus the bn_ / l_ / r_ prefixes

        if (IsGear(body)) return Cat.Gear;
        if (IsFace(body)) return Cat.Face;

        if (IsHand(body)) return side == Side.Right ? Cat.HandR : Cat.HandL;
        if (IsFoot(body)) return side == Side.Right ? Cat.FootR : Cat.FootL;
        if (IsArm(body))  return side == Side.Right ? Cat.ArmR : Cat.ArmL;
        if (IsLeg(body))  return side == Side.Right ? Cat.LegR : Cat.LegL;

        if (Has(body, "Head", "Hood", "Hair", "Skull")) return Cat.Head;
        if (Has(body, "Neck", "Scarf"))                 return Cat.Neck;
        if (Has(body, "Spine"))                         return Cat.Spine;
        if (Has(body, "Hip", "Pelvis", "Sacrum"))       return Cat.Pelvis;
        if (Has(body, "Root"))                          return Cat.Root;

        return Cat.Misc;
    }

    private static Side SideOf(string n) =>
        n.StartsWith("bn_l_") || n.StartsWith("bn__l_") ? Side.Left :
        n.StartsWith("bn_r_") || n.StartsWith("bn__r_") ? Side.Right : Side.Center;

    private static string Strip(string n)
    {
        foreach (var p in new[] { "bn__l_", "bn__r_", "bn_l_", "bn_r_", "bn__", "bn_" })
            if (n.StartsWith(p)) return n.Substring(p.Length);
        return n;
    }

    private static readonly string[] Fingers = { "Index", "Middle", "Ring", "Pinky", "Thumb" };

    private static bool IsHand(string b) => Has(b, "Wrist") || Fingers.Any(f => b.StartsWith(f));
    private static bool IsArm(string b)  => Has(b, "Shoulder", "Clavicle", "Elbow", "ArmBend", "Strap", "Attach");
    private static bool IsFoot(string b) => Has(b, "Ankle", "Toe", "Ball", "Bottom");
    private static bool IsLeg(string b)  => Has(b, "Knee", "LegBend");
    private static bool IsFace(string b) => Has(b, "Eye", "Eyelid", "Eyebrow", "Pupil", "Cheek", "Jaw",
                                                  "Lip", "Nose", "Teeth", "Tongue", "Mouth");
    private static bool IsGear(string b) => Has(b, "Bag", "Gourd", "Mug", "Piolet", "Stick", "Pocket",
                                                  "Degaine", "Pontet", "Rope", "Ropes", "Couching",
                                                  "LightStick", "AttachRot");

    private static bool Has(string s, params string[] terms) => terms.Any(s.Contains);
}
