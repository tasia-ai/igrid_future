/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Wearable types with the wire values every viewer and OpenSim use (the same numbers as
/// <c>OpenMetaverse.WearableType</c>; kept local so the library does not depend on the client library).
/// </summary>
public enum WearableKind
{
    /// <summary>Avatar shape wearable.</summary>
    Shape = 0,
    /// <summary>Avatar skin wearable.</summary>
    Skin = 1,
    /// <summary>Avatar hair wearable.</summary>
    Hair = 2,
    /// <summary>Avatar eyes wearable.</summary>
    Eyes = 3,
    /// <summary>Shirt wearable.</summary>
    Shirt = 4,
    /// <summary>Pants wearable.</summary>
    Pants = 5,
    /// <summary>Shoes wearable.</summary>
    Shoes = 6,
    /// <summary>Socks wearable.</summary>
    Socks = 7,
    /// <summary>Jacket wearable.</summary>
    Jacket = 8,
    /// <summary>Gloves wearable.</summary>
    Gloves = 9,
    /// <summary>Undershirt wearable.</summary>
    Undershirt = 10,
    /// <summary>Underpants wearable.</summary>
    Underpants = 11,
    /// <summary>Skirt wearable.</summary>
    Skirt = 12,
    /// <summary>Alpha-mask wearable.</summary>
    Alpha = 13,
    /// <summary>Tattoo wearable.</summary>
    Tattoo = 14,
    /// <summary>Physics wearable.</summary>
    Physics = 15,
    /// <summary>Universal wearable.</summary>
    Universal = 16,
    /// <summary>Not a valid wearable kind.</summary>
    Invalid = 255,
}

/// <summary>
/// Avatar texture slots (the viewer's <c>ETextureIndex</c>, the same numbers as <c>OpenMetaverse.AvatarTextureIndex</c>).
/// Wearable assets key their textures by these numbers; bakes land in the <c>*Baked</c> slots.
/// </summary>
public enum TextureSlot
{
    /// <summary>Unknown or unmapped texture slot.</summary>
    Unknown = -1,
    /// <summary>Head body-paint texture.</summary>
    HeadBodypaint = 0,
    /// <summary>Upper-body shirt texture.</summary>
    UpperShirt = 1,
    /// <summary>Lower-body pants texture.</summary>
    LowerPants = 2,
    /// <summary>Eye iris texture.</summary>
    EyesIris = 3,
    /// <summary>Hair texture.</summary>
    Hair = 4,
    /// <summary>Upper-body paint texture.</summary>
    UpperBodypaint = 5,
    /// <summary>Lower-body paint texture.</summary>
    LowerBodypaint = 6,
    /// <summary>Lower-body shoe texture.</summary>
    LowerShoes = 7,
    /// <summary>Baked head texture.</summary>
    HeadBaked = 8,
    /// <summary>Baked upper-body texture.</summary>
    UpperBaked = 9,
    /// <summary>Baked lower-body texture.</summary>
    LowerBaked = 10,
    /// <summary>Baked eyes texture.</summary>
    EyesBaked = 11,
    /// <summary>Lower-body socks texture.</summary>
    LowerSocks = 12,
    /// <summary>Upper-body jacket texture.</summary>
    UpperJacket = 13,
    /// <summary>Lower-body jacket texture.</summary>
    LowerJacket = 14,
    /// <summary>Upper-body gloves texture.</summary>
    UpperGloves = 15,
    /// <summary>Upper-body undershirt texture.</summary>
    UpperUndershirt = 16,
    /// <summary>Lower-body underpants texture.</summary>
    LowerUnderpants = 17,
    /// <summary>Skirt texture.</summary>
    Skirt = 18,
    /// <summary>Baked skirt texture.</summary>
    SkirtBaked = 19,
    /// <summary>Baked hair texture.</summary>
    HairBaked = 20,
    /// <summary>Lower-body alpha mask.</summary>
    LowerAlpha = 21,
    /// <summary>Upper-body alpha mask.</summary>
    UpperAlpha = 22,
    /// <summary>Head alpha mask.</summary>
    HeadAlpha = 23,
    /// <summary>Eye alpha mask.</summary>
    EyesAlpha = 24,
    /// <summary>Hair alpha mask.</summary>
    HairAlpha = 25,
    /// <summary>Head tattoo texture.</summary>
    HeadTattoo = 26,
    /// <summary>Upper-body tattoo texture.</summary>
    UpperTattoo = 27,
    /// <summary>Lower-body tattoo texture.</summary>
    LowerTattoo = 28,
    /// <summary>Head universal tattoo texture.</summary>
    HeadUniversalTattoo = 29,
    /// <summary>Upper-body universal tattoo texture.</summary>
    UpperUniversalTattoo = 30,
    /// <summary>Lower-body universal tattoo texture.</summary>
    LowerUniversalTattoo = 31,
    /// <summary>Skirt universal tattoo texture.</summary>
    SkirtTattoo = 32,
    /// <summary>Hair universal tattoo texture.</summary>
    HairTattoo = 33,
    /// <summary>Eye universal tattoo texture.</summary>
    EyesTattoo = 34,
    /// <summary>Left-arm tattoo texture.</summary>
    LeftArmTattoo = 35,
    /// <summary>Left-leg tattoo texture.</summary>
    LeftLegTattoo = 36,
    /// <summary>First auxiliary tattoo texture.</summary>
    Aux1Tattoo = 37,
    /// <summary>Second auxiliary tattoo texture.</summary>
    Aux2Tattoo = 38,
    /// <summary>Third auxiliary tattoo texture.</summary>
    Aux3Tattoo = 39,
    /// <summary>Baked left-arm texture.</summary>
    LeftArmBaked = 40,
    /// <summary>Baked left-leg texture.</summary>
    LeftLegBaked = 41,
    /// <summary>Baked first auxiliary texture.</summary>
    Aux1Baked = 42,
    /// <summary>Baked second auxiliary texture.</summary>
    Aux2Baked = 43,
    /// <summary>Baked third auxiliary texture.</summary>
    Aux3Baked = 44,
}

/// <summary>Well-known texture ids the bake pipeline treats specially.</summary>
public static class BakeConstants
{
    /// <summary>The "no texture" placeholder a wearable carries for a slot it does not paint (<c>IMG_DEFAULT_AVATAR</c>).</summary>
    public static readonly OpenMetaverse.UUID DefaultAvatarTexture = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    /// <summary>An alpha wearable carrying this texture hides the whole region (<c>IMG_INVISIBLE</c>).</summary>
    public static readonly OpenMetaverse.UUID InvisibleTexture = new("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");

    /// <summary>The TextureEntry face each bake channel is written to.</summary>
    public static TextureSlot BakedSlotOf(BakeChannel ch) => ch switch
    {
        BakeChannel.Head => TextureSlot.HeadBaked, BakeChannel.Upper => TextureSlot.UpperBaked, BakeChannel.Lower => TextureSlot.LowerBaked,
        BakeChannel.Eyes => TextureSlot.EyesBaked, BakeChannel.Skirt => TextureSlot.SkirtBaked, BakeChannel.Hair => TextureSlot.HairBaked,
        BakeChannel.LeftArm => TextureSlot.LeftArmBaked, BakeChannel.LeftLeg => TextureSlot.LeftLegBaked,
        BakeChannel.Aux1 => TextureSlot.Aux1Baked, BakeChannel.Aux2 => TextureSlot.Aux2Baked, BakeChannel.Aux3 => TextureSlot.Aux3Baked,
        _ => TextureSlot.Unknown,
    };
}

/// <summary>Conversions and classifications for wearable types.</summary>
public static class WearableKinds
{
    /// <summary>The wearable type name avatar_lad.xml uses in <c>wearable=</c> attributes and LLWearable files.</summary>
    public static string TypeName(WearableKind t) => t switch
    {
        WearableKind.Shape => "shape", WearableKind.Skin => "skin", WearableKind.Hair => "hair", WearableKind.Eyes => "eyes", WearableKind.Shirt => "shirt",
        WearableKind.Pants => "pants", WearableKind.Shoes => "shoes", WearableKind.Socks => "socks", WearableKind.Jacket => "jacket", WearableKind.Gloves => "gloves",
        WearableKind.Undershirt => "undershirt", WearableKind.Underpants => "underpants", WearableKind.Skirt => "skirt", WearableKind.Alpha => "alpha",
        WearableKind.Tattoo => "tattoo", WearableKind.Physics => "physics", WearableKind.Universal => "universal", _ => "",
    };

    /// <summary>Parses a wearable name from avatar_lad.xml or LLWearable data.</summary>
    public static WearableKind? FromName(string name)
    {
        foreach (WearableKind t in Enum.GetValues<WearableKind>())
            if (t != WearableKind.Invalid && string.Equals(TypeName(t), name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>Whether the wearable is one of the unique avatar body-part types.</summary>
    public static bool IsBodyPart(WearableKind t) => t is WearableKind.Shape or WearableKind.Skin or WearableKind.Hair or WearableKind.Eyes;
}
