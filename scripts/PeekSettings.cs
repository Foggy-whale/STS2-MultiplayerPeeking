using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace MultiplayerPeeking;

// 全部可调参数 + 默认值 + ConfigFile 读写 + 即时生效事件。
// 持久化到 user://multiplayer_peeking_settings.cfg（单个 [peek] 段）。
public static class PeekSettings
{
    private const string SavePath = "user://multiplayer_peeking_settings.cfg";
    private const string Section  = "peek";

    public static readonly Color DefaultWarningColor  = new(0.396f, 0.306f, 0.639f, 0.98f);
    public static readonly Color DefaultBodyGlowColor = new(0.55f, 0.35f, 1.0f, 1.0f);

    // —— 通用 ——
    public static bool  MasterEnabled       = true;
    public static int   CardScalePct        = 40;   // 10–100 ；悬停 overlay 缩放
    public static int   HideDelayMs         = 0;     // 0–2000
    public static int   CardSpacingPx       = 8;     // 0–40
    public static int   OverlayGapPx        = 24;    // 0–120
    public static bool  PurpleGlowEnabled   = true;
    public static Color WarningColor        = DefaultWarningColor;
    public static Color BodyGlowColor       = DefaultBodyGlowColor;

    // —— 缩略图 ——
    public static bool  ThumbnailsEnabled   = false;
    public static int   ThumbnailHeightPct  = 85;    // 40–100 ；卡高占状态栏高度的 %
    public static int   ThumbnailSpacingPx  = 2;     // -30–20 ；负值=重叠
    public static int   ThumbnailOpacityPct = 80;    // 20–100
    public static bool  ThumbnailShowGlow   = false;

    // —— 派生 ——
    public static float  CardScale        => CardScalePct / 100f;
    public static double HideDelaySec     => HideDelayMs / 1000.0;
    public static float  ThumbnailOpacity => ThumbnailOpacityPct / 100f;

    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    public static void ResetToDefaults()
    {
        MasterEnabled       = true;
        CardScalePct        = 40;
        HideDelayMs         = 0;
        CardSpacingPx       = 8;
        OverlayGapPx        = 24;
        PurpleGlowEnabled   = true;
        WarningColor        = DefaultWarningColor;
        BodyGlowColor       = DefaultBodyGlowColor;
        ThumbnailsEnabled   = false;
        ThumbnailHeightPct  = 85;
        ThumbnailSpacingPx  = 2;
        ThumbnailOpacityPct = 80;
        ThumbnailShowGlow   = false;
    }

    public static void Load()
    {
        try
        {
            var cfg = new ConfigFile();
            if (cfg.Load(SavePath) != Error.Ok)
            {
                Save(); // 首次运行：落盘默认值，便于手动编辑
                return;
            }

            MasterEnabled       = cfg.GetValue(Section, nameof(MasterEnabled),       MasterEnabled).AsBool();
            CardScalePct        = cfg.GetValue(Section, nameof(CardScalePct),        CardScalePct).AsInt32();
            HideDelayMs         = cfg.GetValue(Section, nameof(HideDelayMs),         HideDelayMs).AsInt32();
            CardSpacingPx       = cfg.GetValue(Section, nameof(CardSpacingPx),       CardSpacingPx).AsInt32();
            OverlayGapPx        = cfg.GetValue(Section, nameof(OverlayGapPx),        OverlayGapPx).AsInt32();
            PurpleGlowEnabled   = cfg.GetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled).AsBool();
            WarningColor        = cfg.GetValue(Section, nameof(WarningColor),        WarningColor).AsColor();
            BodyGlowColor       = cfg.GetValue(Section, nameof(BodyGlowColor),       BodyGlowColor).AsColor();
            ThumbnailsEnabled   = cfg.GetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled).AsBool();
            ThumbnailHeightPct  = cfg.GetValue(Section, nameof(ThumbnailHeightPct),  ThumbnailHeightPct).AsInt32();
            ThumbnailSpacingPx  = cfg.GetValue(Section, nameof(ThumbnailSpacingPx),  ThumbnailSpacingPx).AsInt32();
            ThumbnailOpacityPct = cfg.GetValue(Section, nameof(ThumbnailOpacityPct), ThumbnailOpacityPct).AsInt32();
            ThumbnailShowGlow   = cfg.GetValue(Section, nameof(ThumbnailShowGlow),   ThumbnailShowGlow).AsBool();
        }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] settings load failed, using defaults: {e}");
            ResetToDefaults();
        }
    }

    public static void Save()
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.SetValue(Section, nameof(MasterEnabled),       MasterEnabled);
            cfg.SetValue(Section, nameof(CardScalePct),        CardScalePct);
            cfg.SetValue(Section, nameof(HideDelayMs),         HideDelayMs);
            cfg.SetValue(Section, nameof(CardSpacingPx),       CardSpacingPx);
            cfg.SetValue(Section, nameof(OverlayGapPx),        OverlayGapPx);
            cfg.SetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled);
            cfg.SetValue(Section, nameof(WarningColor),        WarningColor);
            cfg.SetValue(Section, nameof(BodyGlowColor),       BodyGlowColor);
            cfg.SetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled);
            cfg.SetValue(Section, nameof(ThumbnailHeightPct),  ThumbnailHeightPct);
            cfg.SetValue(Section, nameof(ThumbnailSpacingPx),  ThumbnailSpacingPx);
            cfg.SetValue(Section, nameof(ThumbnailOpacityPct), ThumbnailOpacityPct);
            cfg.SetValue(Section, nameof(ThumbnailShowGlow),   ThumbnailShowGlow);

            if (cfg.Save(SavePath) != Error.Ok)
                Log.Warn("[MultiplayerPeeking] settings save returned an error");
        }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] settings save failed: {e}");
        }
    }
}

public enum PeekColorRole { CardHighlight, BodyGlow }

// 颜色冲突检测：当所选颜色与「保留色」或「无效色」过近时给出本地化提示（不阻塞）。
public static class PeekColorConflict
{
    private const float Threshold = 0.20f; // RGB 欧氏距离（忽略 alpha）

    private static float Dist(Color a, Color b)
    {
        float dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static string? Check(Color c, PeekColorRole role)
    {
        if (role == PeekColorRole.CardHighlight)
        {
            if (Dist(c, NCardHighlight.red) < Threshold
             || Dist(c, NCardHighlight.gold) < Threshold
             || Dist(c, NCardHighlight.playableColor) < Threshold)
                return PeekLoc.T("conflict.cardHighlight");
        }
        else // BodyGlow
        {
            if (Dist(c, Colors.White) < Threshold) return PeekLoc.T("conflict.bodyWhite");
            if (c.A < 0.30f)                       return PeekLoc.T("conflict.bodyAlpha");
        }
        return null;
    }
}
