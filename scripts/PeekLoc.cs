using MegaCrit.Sts2.Core.Localization;

namespace MultiplayerPeeking;

// 极简内联本地化：按当前游戏语言在中/英之间二选一。
// 不改 PCK 字符串表（风险最低），覆盖设置菜单所需的全部文案。
public static class PeekLoc
{
    // LocManager 把简体中文映射为 "zhs"；其它语言一律回退英文。
    private static bool Zh => LocManager.Instance?.Language == "zhs";

    private static readonly Dictionary<string, (string En, string Zh)> Map = new()
    {
        ["header"]        = ("— Multiplayer Peeking —", "— 队友手牌透视 —"),
        ["master"]        = ("Enable Multiplayer Peeking", "启用队友手牌透视（总开关）"),
        ["cardScale"]     = ("Hover card size", "悬停卡牌大小"),
        ["hideDelay"]     = ("Overlay hide delay", "悬停大图延迟消失"),
        ["cardSpacing"]   = ("Hover card spacing", "悬停卡牌间隔"),
        ["overlayGap"]    = ("Gap from status bar", "与状态栏水平间距"),
        ["purpleGlow"]    = ("Body warning glow", "身体警示光晕"),
        ["warningColor"]  = ("Card highlight color", "卡牌高光颜色"),
        ["bodyGlowColor"] = ("Body glow color", "身体光晕颜色"),
        ["thumbsEnabled"] = ("Always-on thumbnails", "常态缩略图显示"),
        ["thumbHeight"]   = ("Thumbnail size", "缩略图大小"),
        ["thumbSpacing"]  = ("Thumbnail spacing", "缩略图间隔"),
        ["thumbOpacity"]  = ("Thumbnail opacity", "缩略图透明度"),
        ["thumbGlow"]     = ("Highlight on thumbnails", "缩略图显示高光"),
        ["reset"]         = ("Reset to defaults", "恢复默认设置"),

        ["conflict.cardHighlight"] = ("Too close to the native red/gold/playable glow",
                                      "与原生红/金/可打出高光过于接近"),
        ["conflict.bodyWhite"]     = ("Near white — the glow will be nearly invisible",
                                      "颜色接近白色，光晕将几乎不可见"),
        ["conflict.bodyAlpha"]     = ("Too transparent — the glow will be faint",
                                      "过于透明，光晕将很微弱"),
    };

    public static string T(string key)
        => Map.TryGetValue(key, out var v) ? (Zh ? v.Zh : v.En) : key;
}
