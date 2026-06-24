using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace MultiplayerPeeking;

// 【开发用】场景内实时调参面板：在游戏画面里直接拖滑条 / 调颜色，立刻看效果，
// 不必反复进「设置 → 模组配置」来回跳。它是 ModConfig 各项设置的「影子前端」——
// 改动优先经 ModConfig 的 SetValue 走【同一条 OnChanged 通路】（自动写 PeekSettings + 保存 + 实时重建），
// 因此两边始终同步；在 ModConfig 菜单里改了值，本面板下次显示也会自动刷新（订阅 PeekSettings.Changed）。
// 纯 Godot 控件自建（不依赖游戏场景控件），按 F8 在游戏内开/关，默认隐藏。
// 发布版把 Enabled 置 false 即可彻底关闭（连输入轮询都不挂）。
internal static class PeekDevPanel
{
    internal const bool Enabled = true;        // 发布时改为 false
    private const Key ToggleKey = Key.F8;

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static bool _built;
    private static bool _keyDown;
    private static bool _suppress;             // 程序化刷新控件时，抑制控件自身的变更回调，避免回灌/抖动
    private static bool _applying;             // 本面板正在下发自身改动时，跳过 RefreshWidgets，避免自回声
    private static readonly List<Action> _refreshers = new();

    private static bool Zh => LocManager.Instance?.Language == "zhs";

    internal static void Install()
    {
        if (!Enabled) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        tree.ProcessFrame += OnFrame;            // 每帧轮询 F8（边沿触发），无需自定义 Node._Input
        PeekSettings.Changed += RefreshWidgets;  // 外部（ModConfig 菜单/重置）改值时同步本面板控件
        Log.Info("[MultiplayerPeeking] dev panel armed (press F8 in-game)");
    }

    private static void OnFrame()
    {
        bool down = Input.IsKeyPressed(ToggleKey);
        if (down && !_keyDown && !IsTypingInTextField()) Toggle();   // 正在文本框打字时不抢 F8
        _keyDown = down;
    }

    private static bool IsTypingInTextField()
        => Engine.GetMainLoop() is SceneTree tree && tree.Root?.GuiGetFocusOwner() is LineEdit or TextEdit;

    private static void Toggle()
    {
        if (!_built) Build();
        if (_layer == null) return;
        _layer.Visible = !_layer.Visible;
        if (_layer.Visible) { Reposition(); RefreshWidgets(); }   // 显示时停靠当前窗口右上 + 拉取最新值
    }

    // 每次显示时按当前窗口尺寸重新停靠右上（窗口可能在两次显示间改过分辨率/大小）
    private static void Reposition()
    {
        if (_panel == null || Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
        _panel.Position = new Vector2(Mathf.Max(8f, (float)tree.Root.Size.X - 372f), 12f);
    }

    private static void Build()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return; // 没场景树就下次再试
        _built = true;
        try { BuildInternal(tree); }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] dev panel build failed: {e}");
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
            _layer = null; _panel = null; _built = false;   // 半成品清掉，允许下次 F8 重试
        }
    }

    private static void BuildInternal(SceneTree tree)
    {
        // 顶层独立画布，跨场景常驻、绘制在最上层；ProcessMode=Always 让暂停时也能调
        _layer = new CanvasLayer { Layer = 128, ProcessMode = Node.ProcessModeEnum.Always };

        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        var panel = _panel;
        panel.Position = new Vector2(Mathf.Max(8f, (float)tree.Root.Size.X - 372f), 12f); // 停靠右上，避开左侧队友状态栏

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(344, Mathf.Min(720f, (float)tree.Root.Size.Y * 0.85f)),
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);

        vbox.AddChild(new Label { Text = Zh ? "队友手牌透视 · 开发面板 (F8 开关)" : "Peek · Dev Panel (F8)" });
        vbox.AddChild(new HSeparator());

        AddToggle(vbox, Zh ? "总开关" : "Master enable",
            () => PeekSettings.MasterEnabled, "MasterEnabled", v => PeekSettings.MasterEnabled = v);
        AddSlider(vbox, Zh ? "悬停卡牌大小" : "Hover size", "%", 10, 100, 1,
            () => PeekSettings.CardScalePct, "CardScalePct", v => PeekSettings.CardScalePct = v);
        AddSlider(vbox, Zh ? "大图延迟消失" : "Hide delay", "ms", 0, 2000, 50,
            () => PeekSettings.HideDelayMs, "HideDelayMs", v => PeekSettings.HideDelayMs = v);
        AddSlider(vbox, Zh ? "悬停卡牌间隔" : "Card spacing", "px", 0, 40, 1,
            () => PeekSettings.CardSpacingPx, "CardSpacingPx", v => PeekSettings.CardSpacingPx = v);
        AddSlider(vbox, Zh ? "与状态栏间距" : "Bar gap", "px", 0, 120, 1,
            () => PeekSettings.OverlayGapPx, "OverlayGapPx", v => PeekSettings.OverlayGapPx = v);

        vbox.AddChild(new HSeparator());
        AddToggle(vbox, Zh ? "卡牌高光" : "Card highlight",
            () => PeekSettings.CardGlowEnabled, "CardGlowEnabled", v => PeekSettings.CardGlowEnabled = v);
        AddColor(vbox, Zh ? "卡牌高光色" : "Card glow color",
            () => PeekSettings.WarningColor, "WarningColor", c => PeekSettings.WarningColor = c);
        AddToggle(vbox, Zh ? "身体警示光晕" : "Body glow",
            () => PeekSettings.PurpleGlowEnabled, "PurpleGlowEnabled", v => PeekSettings.PurpleGlowEnabled = v);
        AddColor(vbox, Zh ? "身体光晕色" : "Body glow color",
            () => PeekSettings.BodyGlowColor, "BodyGlowColor", c => PeekSettings.BodyGlowColor = c);

        vbox.AddChild(new HSeparator());
        AddToggle(vbox, Zh ? "常态缩略图" : "Thumbnails",
            () => PeekSettings.ThumbnailsEnabled, "ThumbnailsEnabled", v => PeekSettings.ThumbnailsEnabled = v);
        AddSlider(vbox, Zh ? "缩略图大小" : "Thumb size", "%", 5, 40, 1,
            () => PeekSettings.ThumbnailScalePct, "ThumbnailScalePct", v => PeekSettings.ThumbnailScalePct = v);
        AddSlider(vbox, Zh ? "缩略图透明度" : "Thumb opacity", "%", 20, 100, 1,
            () => PeekSettings.ThumbnailOpacityPct, "ThumbnailOpacityPct", v => PeekSettings.ThumbnailOpacityPct = v);
        AddToggle(vbox, Zh ? "缩略图显示高光" : "Thumb highlight",
            () => PeekSettings.ThumbnailShowGlow, "ThumbnailShowGlow", v => PeekSettings.ThumbnailShowGlow = v);

        vbox.AddChild(new HSeparator());
        var reset = new Button { Text = Zh ? "恢复默认" : "Reset to defaults" };
        reset.Pressed += ResetAll;
        vbox.AddChild(reset);

        scroll.AddChild(vbox);
        margin.AddChild(scroll);
        panel.AddChild(margin);
        _layer.AddChild(panel);
        tree.Root.AddChild(_layer);              // 挂在 Root（current_scene 的兄弟）→ 跨场景常驻
        _layer.Visible = false;
    }

    /* ---- 行构造器：每个控件改值时优先经 ModConfig 下发（否则直接写字段），并登记一个「按当前值刷新自己」的回调 ---- */

    private static void AddToggle(VBoxContainer parent, string label, Func<bool> get, string key, Action<bool> setField)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var cb = new CheckBox { ButtonPressed = get() };
        cb.Toggled += on => { if (!_suppress) Apply(key, on, () => setField(on)); };
        row.AddChild(cb);
        parent.AddChild(row);
        _refreshers.Add(() => cb.ButtonPressed = get());
    }

    private static void AddSlider(VBoxContainer parent, string label, string suffix,
                                  int min, int max, int step, Func<int> get, string key, Action<int> setField)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(116, 0) });
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = get(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(110, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var val = new Label
        {
            Text = get() + suffix, CustomMinimumSize = new Vector2(58, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        slider.ValueChanged += d =>
        {
            int v = (int)Math.Round(d);
            val.Text = v + suffix;
            if (!_suppress) Apply(key, (float)v, () => setField(v));
        };
        row.AddChild(slider);
        row.AddChild(val);
        parent.AddChild(row);
        _refreshers.Add(() => { slider.Value = get(); val.Text = get() + suffix; });
    }

    private static void AddColor(VBoxContainer parent, string label, Func<Color> get, string key, Action<Color> setField)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var picker = new ColorPickerButton
        {
            Color = get(), EditAlpha = true, CustomMinimumSize = new Vector2(116, 26),
        };
        picker.ColorChanged += c => { if (!_suppress) Apply(key, c.ToHtml(true), () => setField(c)); };
        row.AddChild(picker);
        parent.AddChild(row);
        _refreshers.Add(() => picker.Color = get());
    }

    // 优先经 ModConfig 的 SetValue 走同一条 OnChanged 通路（写 PeekSettings + 保存 + 实时重建 + 持久化）；
    // 未装 ModConfig 时回退为直接写字段 + 保存 + 通知。
    private static void Apply(string key, object mcValue, Action directSet)
    {
        _applying = true;   // 本面板自身改动：跳过随之而来的 RefreshWidgets 自回声（否则取色器会被写回量化值而抖动）
        try
        {
            if (PeekModConfigBridge.TrySetValue(key, mcValue)) return;
            directSet();
            PeekSettings.ScheduleSave();
            PeekSettings.NotifyChanged();
        }
        finally { _applying = false; }
    }

    private static void ResetAll()
    {
        // 优先触发 ModConfig 自身的「恢复默认」（含其菜单/落盘一致）；没装则手动还原字段
        if (!PeekModConfigBridge.TryReset())
        {
            PeekSettings.MasterEnabled       = PeekSettings.DefaultMasterEnabled;
            PeekSettings.CardScalePct        = PeekSettings.DefaultCardScalePct;
            PeekSettings.HideDelayMs         = PeekSettings.DefaultHideDelayMs;
            PeekSettings.CardSpacingPx       = PeekSettings.DefaultCardSpacingPx;
            PeekSettings.OverlayGapPx        = PeekSettings.DefaultOverlayGapPx;
            PeekSettings.CardGlowEnabled     = PeekSettings.DefaultCardGlowEnabled;
            PeekSettings.PurpleGlowEnabled   = PeekSettings.DefaultPurpleGlowEnabled;
            PeekSettings.WarningColor        = PeekSettings.DefaultWarningColor;
            PeekSettings.BodyGlowColor       = PeekSettings.DefaultBodyGlowColor;
            PeekSettings.ThumbnailsEnabled   = PeekSettings.DefaultThumbnailsEnabled;
            PeekSettings.ThumbnailScalePct   = PeekSettings.DefaultThumbnailScalePct;
            PeekSettings.ThumbnailOpacityPct = PeekSettings.DefaultThumbnailOpacityPct;
            PeekSettings.ThumbnailShowGlow   = PeekSettings.DefaultThumbnailShowGlow;
            PeekSettings.Save();
            PeekSettings.NotifyChanged();
        }
        RefreshWidgets();
    }

    // 订阅 PeekSettings.Changed：外部改值时把控件刷到最新（仅在面板可见时，省开销）。
    // _suppress 防止刷新过程中控件回调再次下发，造成回灌/抖动。
    private static void RefreshWidgets()
    {
        if (_applying || _layer is not { Visible: true }) return;   // _applying：只对【外部】改动(ModConfig 菜单/重置)刷新，不回声自身编辑
        _suppress = true;
        foreach (Action r in _refreshers)
        {
            try { r(); } catch (Exception e) { Log.Warn($"[MultiplayerPeeking] dev refresh: {e.Message}"); }
        }
        _suppress = false;
    }
}
