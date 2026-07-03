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
    public const  bool  DefaultPrismaticGlow       = true;   // 棱彩色（流动彩虹）高光：作为「卡牌高光色」的一个预设，默认开
    public const  int   DefaultOverlayGlowRadiusPct  = 15;   // 大图卡牌高光环宽（%，0–100）
    public const  int   DefaultOverlayGlowOpacityPct = 100;  // 大图卡牌高光不透明度（%，0–100）
    public const  bool  DefaultPurpleGlowEnabled   = true;
    public const  bool  DefaultBodyPrismaticGlow   = true;   // 身体光晕棱彩（随时间流动的彩虹），作为「身体光晕色」的预设，默认开
    public const  int   DefaultBodyGlowOpacityPct  = 100;   // 身体光晕不透明度（=染色强度：白 ↔ 光晕色 之间 lerp）
    public static readonly Color DefaultWarningColor  = new(0.396f, 0.306f, 0.639f, 0.98f);
    public static readonly Color DefaultBodyGlowColor = new(0.55f, 0.35f, 1.0f, 1.0f);
    public const  bool  DefaultThumbnailsEnabled   = true;
    public const  int   DefaultThumbnailScalePct   = 14;    // 5–40 ；≈59px 卡高，容于 64px 行内
    public const  int   DefaultThumbnailOpacityPct = 100;   // 20–100
    public const  bool  DefaultThumbnailShowGlow   = true;   // 默认开：重置后只需重开「常态缩略图」即恢复发光缩略图
    public const  int   DefaultThumbnailGlowRadiusPct  = 50;  // 缩略图卡牌高光环宽（%）
    public const  int   DefaultThumbnailGlowOpacityPct = 100; // 缩略图卡牌高光不透明度（%）
    public const  int   DefaultThumbnailSpacingPx  = 2;      // 0–40 ；缩略图卡牌间隔（绝对 px，独立于大图）
    public const  int   DefaultThumbnailGapPx      = 0;      // 0–120；缩略图与状态栏间距（独立于大图）
    public const  Key   DefaultPanelHotkey         = Key.F8;  // 设置面板开关快捷键
    public const  Key   DefaultHoldHideKey            = Key.None;   // 长按临时隐藏缩略图/大图；None=鼠标中键
    public const  Key   DefaultThumbnailToggleHotkey  = Key.Minus;  // 快速开关缩略图的快捷键
    public const  string DefaultLastSeenHintVersion   = "";         // 已看过的一次性更新提示版本（内部用）

    // —— 通用 ——
    public static bool  MasterEnabled       = DefaultMasterEnabled;
    public static int   CardScalePct        = DefaultCardScalePct;
    public static int   HideDelayMs         = DefaultHideDelayMs;
    public static int   CardSpacingPx       = DefaultCardSpacingPx;
    public static int   OverlayGapPx        = DefaultOverlayGapPx;
    public static bool  CardGlowEnabled     = DefaultCardGlowEnabled;
    public static bool  PrismaticGlow       = DefaultPrismaticGlow;
    public static int   OverlayGlowRadiusPct  = DefaultOverlayGlowRadiusPct;
    public static int   OverlayGlowOpacityPct = DefaultOverlayGlowOpacityPct;
    public static bool  PurpleGlowEnabled   = DefaultPurpleGlowEnabled;
    public static bool  BodyPrismaticGlow   = DefaultBodyPrismaticGlow;
    public static int   BodyGlowOpacityPct  = DefaultBodyGlowOpacityPct;
    public static Color WarningColor        = DefaultWarningColor;
    public static Color BodyGlowColor       = DefaultBodyGlowColor;

    // —— 缩略图 ——
    public static bool  ThumbnailsEnabled   = DefaultThumbnailsEnabled;
    public static int   ThumbnailScalePct   = DefaultThumbnailScalePct;
    public static int   ThumbnailOpacityPct = DefaultThumbnailOpacityPct;
    public static bool  ThumbnailShowGlow   = DefaultThumbnailShowGlow;
    public static int   ThumbnailGlowRadiusPct  = DefaultThumbnailGlowRadiusPct;
    public static int   ThumbnailGlowOpacityPct = DefaultThumbnailGlowOpacityPct;
    public static int   ThumbnailSpacingPx  = DefaultThumbnailSpacingPx;
    public static int   ThumbnailGapPx      = DefaultThumbnailGapPx;

    // —— 设置面板 / 快捷键 ——
    public static Key   PanelHotkey            = DefaultPanelHotkey;
    public static Key   HoldHideKey            = DefaultHoldHideKey;
    public static Key   ThumbnailToggleHotkey  = DefaultThumbnailToggleHotkey;

    // —— 内部标记（非玩家可调）——
    public static string LastSeenHintVersion   = DefaultLastSeenHintVersion;

    // —— 派生 ——
    public static float  CardScale        => CardScalePct / 100f;
    public static double HideDelaySec     => HideDelayMs / 1000.0;
    public static float  ThumbnailOpacity => ThumbnailOpacityPct / 100f;
    public static float  OverlayGlowRadius    => OverlayGlowRadiusPct    / 100f;
    public static float  OverlayGlowOpacity   => OverlayGlowOpacityPct   / 100f;
    public static float  ThumbnailGlowRadius  => ThumbnailGlowRadiusPct  / 100f;
    public static float  ThumbnailGlowOpacity => ThumbnailGlowOpacityPct / 100f;
    public static float  BodyGlowOpacity      => BodyGlowOpacityPct      / 100f;

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
            PrismaticGlow       = cfg.GetValue(Section, nameof(PrismaticGlow),       PrismaticGlow).AsBool();
            OverlayGlowRadiusPct  = cfg.GetValue(Section, nameof(OverlayGlowRadiusPct),  OverlayGlowRadiusPct).AsInt32();
            OverlayGlowOpacityPct = cfg.GetValue(Section, nameof(OverlayGlowOpacityPct), OverlayGlowOpacityPct).AsInt32();
            PurpleGlowEnabled   = cfg.GetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled).AsBool();
            BodyPrismaticGlow   = cfg.GetValue(Section, nameof(BodyPrismaticGlow),   BodyPrismaticGlow).AsBool();
            BodyGlowOpacityPct  = cfg.GetValue(Section, nameof(BodyGlowOpacityPct),  BodyGlowOpacityPct).AsInt32();
            WarningColor        = cfg.GetValue(Section, nameof(WarningColor),        WarningColor).AsColor();
            BodyGlowColor       = cfg.GetValue(Section, nameof(BodyGlowColor),       BodyGlowColor).AsColor();
            ThumbnailsEnabled   = cfg.GetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled).AsBool();
            ThumbnailScalePct   = cfg.GetValue(Section, nameof(ThumbnailScalePct),   ThumbnailScalePct).AsInt32();
            ThumbnailOpacityPct = cfg.GetValue(Section, nameof(ThumbnailOpacityPct), ThumbnailOpacityPct).AsInt32();
            ThumbnailShowGlow   = cfg.GetValue(Section, nameof(ThumbnailShowGlow),   ThumbnailShowGlow).AsBool();
            ThumbnailGlowRadiusPct  = cfg.GetValue(Section, nameof(ThumbnailGlowRadiusPct),  ThumbnailGlowRadiusPct).AsInt32();
            ThumbnailGlowOpacityPct = cfg.GetValue(Section, nameof(ThumbnailGlowOpacityPct), ThumbnailGlowOpacityPct).AsInt32();
            ThumbnailSpacingPx  = cfg.GetValue(Section, nameof(ThumbnailSpacingPx),  ThumbnailSpacingPx).AsInt32();
            ThumbnailGapPx      = cfg.GetValue(Section, nameof(ThumbnailGapPx),      ThumbnailGapPx).AsInt32();
            PanelHotkey         = (Key)cfg.GetValue(Section, nameof(PanelHotkey),    (int)PanelHotkey).AsInt32();
            HoldHideKey            = (Key)cfg.GetValue(Section, nameof(HoldHideKey),            (int)HoldHideKey).AsInt32();
            ThumbnailToggleHotkey  = (Key)cfg.GetValue(Section, nameof(ThumbnailToggleHotkey),  (int)ThumbnailToggleHotkey).AsInt32();
            LastSeenHintVersion = cfg.GetValue(Section, nameof(LastSeenHintVersion), LastSeenHintVersion).AsString();
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
            cfg.SetValue(Section, nameof(PrismaticGlow),       PrismaticGlow);
            cfg.SetValue(Section, nameof(OverlayGlowRadiusPct),  OverlayGlowRadiusPct);
            cfg.SetValue(Section, nameof(OverlayGlowOpacityPct), OverlayGlowOpacityPct);
            cfg.SetValue(Section, nameof(PurpleGlowEnabled),   PurpleGlowEnabled);
            cfg.SetValue(Section, nameof(BodyPrismaticGlow),   BodyPrismaticGlow);
            cfg.SetValue(Section, nameof(BodyGlowOpacityPct),  BodyGlowOpacityPct);
            cfg.SetValue(Section, nameof(WarningColor),        WarningColor);
            cfg.SetValue(Section, nameof(BodyGlowColor),       BodyGlowColor);
            cfg.SetValue(Section, nameof(ThumbnailsEnabled),   ThumbnailsEnabled);
            cfg.SetValue(Section, nameof(ThumbnailScalePct),   ThumbnailScalePct);
            cfg.SetValue(Section, nameof(ThumbnailOpacityPct), ThumbnailOpacityPct);
            cfg.SetValue(Section, nameof(ThumbnailShowGlow),   ThumbnailShowGlow);
            cfg.SetValue(Section, nameof(ThumbnailGlowRadiusPct),  ThumbnailGlowRadiusPct);
            cfg.SetValue(Section, nameof(ThumbnailGlowOpacityPct), ThumbnailGlowOpacityPct);
            cfg.SetValue(Section, nameof(ThumbnailSpacingPx),  ThumbnailSpacingPx);
            cfg.SetValue(Section, nameof(ThumbnailGapPx),      ThumbnailGapPx);
            cfg.SetValue(Section, nameof(PanelHotkey),         (int)PanelHotkey);
            cfg.SetValue(Section, nameof(HoldHideKey),            (int)HoldHideKey);
            cfg.SetValue(Section, nameof(ThumbnailToggleHotkey),  (int)ThumbnailToggleHotkey);
            cfg.SetValue(Section, nameof(LastSeenHintVersion), LastSeenHintVersion);

            if (cfg.Save(SavePath) != Error.Ok)
                Log.Warn("[MultiplayerPeeking] settings save returned an error");
        }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] settings save failed: {e}");
        }
    }
}
