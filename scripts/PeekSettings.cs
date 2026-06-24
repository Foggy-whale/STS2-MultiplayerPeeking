using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace MultiplayerPeeking;

// 全部可调参数 + 默认值 + ConfigFile 读写 + 即时生效事件。
// 行为层的真源：user://multiplayer_peeking_settings.cfg（即使没装 ModConfig 也能用）。
// 游戏内 UI 由 ModConfig 提供（见 PeekModConfigBridge）；其控件变更经 OnChanged 同步到这里。
public static class PeekSettings
{
    private const string SavePath = "user://multiplayer_peeking_settings.cfg";
    private const string Section  = "peek";

    // —— 出厂默认（ModConfig「恢复默认」用；与下面字段初值一致，互为单一来源）——
    public const  bool  DefaultMasterEnabled       = true;
    public const  int   DefaultCardScalePct        = 40;   // 10–100
    public const  int   DefaultHideDelayMs         = 0;     // 0–2000
    public const  int   DefaultCardSpacingPx       = 8;     // 0–40
    public const  int   DefaultOverlayGapPx        = 24;    // 0–120
    public const  bool  DefaultCardGlowEnabled     = true;
    public const  bool  DefaultPurpleGlowEnabled   = true;
    public static readonly Color DefaultWarningColor  = new(0.396f, 0.306f, 0.639f, 0.98f);
    public static readonly Color DefaultBodyGlowColor = new(0.55f, 0.35f, 1.0f, 1.0f);
    public const  bool  DefaultThumbnailsEnabled   = false;
    public const  int   DefaultThumbnailScalePct   = 14;    // 5–40 ；≈59px 卡高，容于 64px 行内
    public const  int   DefaultThumbnailOpacityPct = 80;    // 20–100
    public const  bool  DefaultThumbnailShowGlow   = true;   // 默认开：重置后只需重开「常态缩略图」即恢复发光缩略图

    // —— 通用 ——
    public static bool  MasterEnabled       = DefaultMasterEnabled;
    public static int   CardScalePct        = DefaultCardScalePct;
    public static int   HideDelayMs         = DefaultHideDelayMs;
    public static int   CardSpacingPx       = DefaultCardSpacingPx;
    public static int   OverlayGapPx        = DefaultOverlayGapPx;
    public static bool  CardGlowEnabled     = DefaultCardGlowEnabled;
    public static bool  PurpleGlowEnabled   = DefaultPurpleGlowEnabled;
    public static Color WarningColor        = DefaultWarningColor;
    public static Color BodyGlowColor       = DefaultBodyGlowColor;

    // —— 缩略图 ——
    public static bool  ThumbnailsEnabled   = DefaultThumbnailsEnabled;
    public static int   ThumbnailScalePct   = DefaultThumbnailScalePct;
    public static int   ThumbnailOpacityPct = DefaultThumbnailOpacityPct;
    public static bool  ThumbnailShowGlow   = DefaultThumbnailShowGlow;

    // —— 派生 ——
    public static float  CardScale        => CardScalePct / 100f;
    public static double HideDelaySec     => HideDelayMs / 1000.0;
    public static float  ThumbnailOpacity => ThumbnailOpacityPct / 100f;

    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    // 合并到下一帧只写一次盘：拖滑条/调色时 OnChanged 会逐帧触发，避免每帧同步 ConfigFile.Save 阻塞。
    private static bool _saveScheduled;
    public static void ScheduleSave()
    {
        if (_saveScheduled) return;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            _saveScheduled = true;
            tree.ProcessFrame += FlushSave;
        }
        else Save();
    }
    private static void FlushSave()
    {
        if (Engine.GetMainLoop() is SceneTree tree) tree.ProcessFrame -= FlushSave;
        _saveScheduled = false;
        Save();
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
            CardGlowEnabled     = cfg.GetValue(Section, nameof(CardGlowEnabled),     CardGlowEnabled).AsBool();
            PurpleGlowEnabled   = cfg.GetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled).AsBool();
            WarningColor        = cfg.GetValue(Section, nameof(WarningColor),        WarningColor).AsColor();
            BodyGlowColor       = cfg.GetValue(Section, nameof(BodyGlowColor),       BodyGlowColor).AsColor();
            ThumbnailsEnabled   = cfg.GetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled).AsBool();
            ThumbnailScalePct   = cfg.GetValue(Section, nameof(ThumbnailScalePct),   ThumbnailScalePct).AsInt32();
            ThumbnailOpacityPct = cfg.GetValue(Section, nameof(ThumbnailOpacityPct), ThumbnailOpacityPct).AsInt32();
            ThumbnailShowGlow   = cfg.GetValue(Section, nameof(ThumbnailShowGlow),   ThumbnailShowGlow).AsBool();
        }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] settings load failed, using defaults: {e}");
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
            cfg.SetValue(Section, nameof(CardGlowEnabled),     CardGlowEnabled);
            cfg.SetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled);
            cfg.SetValue(Section, nameof(WarningColor),        WarningColor);
            cfg.SetValue(Section, nameof(BodyGlowColor),       BodyGlowColor);
            cfg.SetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled);
            cfg.SetValue(Section, nameof(ThumbnailScalePct),   ThumbnailScalePct);
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
