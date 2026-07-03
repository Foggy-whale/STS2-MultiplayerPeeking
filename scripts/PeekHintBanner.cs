using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace MultiplayerPeeking;

// 进入联机后弹一次性【确认提示框】，向老/新用户广播功能与快捷键。
// 主入口挂在 NMultiplayerSubmenu.OnSubmenuShown（玩家点「联机」进入的第一个界面，含 Host/Load/Join，新开局/读档都必经、单机不触发）；
// 兜底挂 NCharacterSelectScreen（fastmp 直连绕过主菜单时仍能触发）。弹窗节点挂在触发屏幕下。
// 用 PeekSettings.LastSeenHintVersion 标记：!= CurrentHintVersion 才弹，弹出即记录并落盘 → 只弹一次。以后 bump 版本可再弹。
// 半透明遮罩模态、点「知道了」确认后才消失。键位按当前绑定动态显示（用符号，不用 Period 这类英文名）。
internal static class PeekHintBanner
{
    private const string CurrentHintVersion = "1.1.1";   // 快捷提示内容的版本标记；v1.1.2 未改提示内容，不重复弹窗
    private const bool   AlwaysShow = false;   // 正常：只弹一次（LastSeenHintVersion 标记）；调试期可临时置 true 每次都弹

    private static CanvasLayer? _layer;

    private static bool Zh => LocManager.Instance?.Language == "zhs";

    // 由 Harmony Postfix 在联机选角色屏幕初始化后调用；parent = 选角色屏幕节点
    internal static void ShowOn(Node parent)
    {
        try
        {
            if (!AlwaysShow && PeekSettings.LastSeenHintVersion == CurrentHintVersion) return;   // 已看过 → 不弹
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;                    // 已在显示
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return;

            Build(parent);
            if (!AlwaysShow)   // 调试期不写标记，不污染 cfg
            {
                PeekSettings.LastSeenHintVersion = CurrentHintVersion;
                PeekSettings.ScheduleSave();
            }
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] hint dialog failed: {e}"); }
    }

    // 键位 → 直观符号（避免 OS.GetKeycodeString 返回 "Period"/"Minus" 这类玩家看不懂的英文名）
    private static string KeyLabel(Key k)
    {
        if (k == Key.None) return Zh ? "未设" : "None";
        return k switch
        {
            Key.Minus      => "-",
            Key.Equal      => "=",
            Key.Semicolon  => ";",
            Key.Apostrophe => "'",
            Key.Quoteleft  => "`",
            Key.Comma      => ",",
            Key.Period     => ".",
            Key.Slash      => "/",
            Key.Backslash  => "\\",
            Key.Bracketleft  => "[",
            Key.Bracketright => "]",
            _ => OS.GetKeycodeString(k)
        };
    }

    // 行内多色文本（BBCode）：凸显键位/强调提示。默认文字色调亮，适配深色面板。
    private static RichTextLabel Rich(string bbcode)
    {
        var r = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(620, 0),
        };
        r.AddThemeColorOverride("default_color", new Color(0.92f, 0.93f, 0.95f));
        r.AddThemeFontSizeOverride("normal_font_size", 18);
        r.AddThemeFontSizeOverride("bold_font_size", 18);
        r.Text = bbcode;
        return r;
    }

    private static void Build(Node parent)
    {
        _layer = new CanvasLayer { Layer = 200, ProcessMode = Node.ProcessModeEnum.Always };

        // 半透明背景遮罩：拦截点击 + 轻微压暗 → 成为"必须点确认"的模态框
        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.5f), MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(backdrop);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        var title = new Label { Text = Zh ? "队友手牌透视 · 快捷操作" : "Multiplayer Peeking · Controls" };
        title.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
        title.AddThemeFontSizeOverride("font_size", 24);
        vbox.AddChild(title);

        string hold   = PeekSettings.HoldHideKey == Key.None
            ? (Zh ? "鼠标中键" : "middle mouse") : KeyLabel(PeekSettings.HoldHideKey);
        string toggle = KeyLabel(PeekSettings.ThumbnailToggleHotkey);
        string panelK = KeyLabel(PeekSettings.PanelHotkey);
        const string KY = "#ffe9a8";   // 淡黄：键位
        const string OR = "#ffc48a";   // 浅橙：操作短语
        const string RD = "#ff9c9c";   // 淡红：提示
        string K(string key)  => $"[color={KY}][lb]{key}[rb][/color]";   // 方括号 + 淡黄（不加粗）
        string A(string t)    => $"[color={OR}]{t}[/color]";             // 浅橙：操作短语
        string Hint(string t) => $"[color={RD}]{t}[/color]";             // 淡红：提示

        vbox.AddChild(Rich(Zh
            ? $"1. 按 {K(panelK)} 键{A("打开设置面板")}（调整卡牌显示/光晕显示）"
            : $"1. Press {K(panelK)} to {A("open the settings panel")} (card display / glow)"));
        vbox.AddChild(Rich(Zh
            ? $"2. 按 {K(toggle)} 键{A("快速开关缩略图")}"
            : $"2. Press {K(toggle)} to {A("toggle thumbnails on/off")}"));
        vbox.AddChild(Rich(Zh
            ? $"3. 按住 {K(hold)} 可{A("临时隐藏缩略图/预览图")}，松开恢复{Hint("（在出现遮挡问题时会很有帮助！）")}"
            : $"3. Hold {K(hold)} to {A("briefly hide thumbnails/preview")}, release to restore{Hint(" (great when something is covered!)")}"));

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });   // 与按钮的间距

        var btn = new Button { Text = Zh ? "确认" : "OK", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(220, 56) };
        StyleBoxFlat BtnBox(Color c) => new StyleBoxFlat
        {
            BgColor = c,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginTop = 14, ContentMarginBottom = 14, ContentMarginLeft = 40, ContentMarginRight = 40,
        };
        btn.AddThemeStyleboxOverride("normal",  BtnBox(new Color(0.24f, 0.44f, 0.72f)));
        btn.AddThemeStyleboxOverride("hover",   BtnBox(new Color(0.32f, 0.54f, 0.84f)));
        btn.AddThemeStyleboxOverride("pressed", BtnBox(new Color(0.18f, 0.34f, 0.58f)));
        btn.AddThemeStyleboxOverride("focus",   BtnBox(new Color(0.30f, 0.52f, 0.82f)));
        btn.AddThemeColorOverride("font_color", Colors.White);
        btn.AddThemeFontSizeOverride("font_size", 24);
        btn.Pressed += DismissNow;
        vbox.AddChild(btn);

        margin.AddChild(vbox);
        panel.AddChild(margin);
        _layer.AddChild(panel);
        parent.AddChild(_layer);   // 挂在选角色屏幕节点下：随该屏幕生灭

        // 居中（延后一帧拿到 panel.Size 再定位）+ 焦点给「确认」（可用 Enter/手柄直接确认）
        Callable.From(() =>
        {
            if (!panel.IsInsideTree()) return;
            Vector2 vp = panel.GetViewportRect().Size;
            panel.Position = new Vector2(Mathf.Max(8f, (vp.X - panel.Size.X) / 2f),
                                         Mathf.Max(8f, (vp.Y - panel.Size.Y) / 2f - 40f));
            if (GodotObject.IsInstanceValid(btn)) btn.GrabFocus();
        }).CallDeferred();
    }

    private static void DismissNow()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
    }
}

// 主入口：玩家点「联机」进入的第一个界面（含 Host/Load/Join）——新开局和读档都必经、单机不触发。
// OnSubmenuShown 每次显示都触发（子菜单被缓存复用，_Ready 只首建触发一次，故用 Shown 更稳）。
[HarmonyPatch(typeof(NMultiplayerSubmenu), "OnSubmenuShown")]
public static class PeekHintMultiplayerSubmenuPatch
{
    public static void Postfix(NMultiplayerSubmenu __instance) => PeekHintBanner.ShowOn(__instance);
}

// 兜底：联机选角色屏幕（新开局才走；用于 fastmp 直连等绕过主菜单的情形）。已弹/已看过则不重复。
[HarmonyPatch(typeof(NCharacterSelectScreen), "InitializeMultiplayerAsHost")]
public static class PeekHintHostPatch
{
    public static void Postfix(NCharacterSelectScreen __instance) => PeekHintBanner.ShowOn(__instance);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "InitializeMultiplayerAsClient")]
public static class PeekHintClientPatch
{
    public static void Postfix(NCharacterSelectScreen __instance) => PeekHintBanner.ShowOn(__instance);
}
