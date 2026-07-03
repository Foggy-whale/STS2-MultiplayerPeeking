using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace MultiplayerPeeking;

// 场景内实时【设置面板】：在游戏画面里直接拖滑条 / 选色，立刻看效果。它是 ModConfig 各项设置的「影子前端」——
// 改动经 ModConfig 的 SetValue 走同一条 OnChanged 通路（写 PeekSettings + 保存 + 实时重建），两边始终同步。
// 颜色用与 ModConfig 一致的预设色板（含 🌈 棱彩预设）；快捷键由 ModConfig 配置；默认关闭、由快捷键唤出。
// 纯 Godot 控件自建。发布版把 Enabled 置 false 即彻底关闭。
internal static class PeekDevPanel
{
    internal const bool Enabled = true;        // 发布时改为 false
    internal const bool DefaultOpen = false;   // 默认关闭，由快捷键唤出

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static bool _built;
    private static bool _autoOpened;
    private static bool _wantOpen;             // 用户「想打开」意图；实际可见性 = _wantOpen && 战斗视图未被遮挡
    private static bool _keyDown;
    private static bool _toggleDown;           // 快速开关缩略图快捷键的边沿跟踪
    private static bool _suppress;             // 程序化刷新控件时抑制控件自身回调
    private static bool _applying;             // 本面板自身改动时跳过 RefreshWidgets，避免自回声
    private static bool _userMoved;            // 用户拖动过 → 不再自动重新停靠
    private static bool _dragging;
    private static Vector2 _grabOffset;
    private static readonly List<Action> _refreshers = new();
    private static readonly List<(Func<bool> on, List<Control> rows)> _foldGroups = new();

    private static bool Zh => LocManager.Instance?.Language == "zhs";

    internal static void Install()
    {
        if (!Enabled) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        tree.ProcessFrame += OnFrame;            // 每帧轮询快捷键
        PeekSettings.Changed += RefreshWidgets;  // 外部（ModConfig 菜单/重置）改值时同步本面板控件
        tree.Root.SizeChanged += Reposition;     // 窗口尺寸变化（如最大化）时实时重定位，避免被算到屏外
        Log.Info("[MultiplayerPeeking] settings panel armed (hotkey)");
    }

    private static void OnFrame()
    {
        // 默认打开：首帧 Root 可能还没就绪 → 每帧重试，构建后置「想开」（只自动开一次，实际显示仍受战斗视图门控）
        if (DefaultOpen && !_autoOpened)
        {
            if (!_built) Build();
            if (_layer != null) { _wantOpen = true; _autoOpened = true; }
        }

        // 拖动标题：按下时记抓取偏移，之后每帧跟随鼠标（即使鼠标移出标题也跟手）
        if (_dragging && _panel != null)
        {
            if (Input.IsMouseButtonPressed(MouseButton.Left))
            {
                _panel.Position = MousePos() - _grabOffset;
                _userMoved = true;
                ClampToViewport();
            }
            else _dragging = false;
        }

        Key hotkey = PeekSettings.PanelHotkey;
        bool down = hotkey != Key.None && Input.IsKeyPressed(hotkey);
        if (down && !_keyDown)   // 边沿触发
        {
            if (IsTypingInTextField())
                Log.Info($"[MultiplayerPeeking] panel hotkey suppressed (focused text field: {FocusName()})");
            else if (PeekHandPatch.IsCombatViewActive())
                Toggle();
            else
                Log.Info("[MultiplayerPeeking] panel hotkey ignored (not in combat view)");
        }
        _keyDown = down;

        // 快速开关缩略图：ThumbnailToggleHotkey（默认 -）。边沿触发，切换 ThumbnailsEnabled 并即时重建。
        Key tkey = PeekSettings.ThumbnailToggleHotkey;
        bool tdown = tkey != Key.None && Input.IsKeyPressed(tkey);
        if (tdown && !_toggleDown && !IsTypingInTextField())
        {
            PeekSettings.ThumbnailsEnabled = !PeekSettings.ThumbnailsEnabled;
            PeekSettings.ScheduleSave();
            PeekSettings.NotifyChanged();
            Log.Info($"[MultiplayerPeeking] thumbnails toggled → {PeekSettings.ThumbnailsEnabled}");
        }
        _toggleDown = tdown;

        // 只在「战斗视图」(未被地图/卡组/抽牌堆/弃牌堆等遮挡)显示；其余场景自动隐藏，但记住开关意图。
        // 关闭遮挡屏幕回到战斗时自动重现（_wantOpen 仍为真）。
        if (_built && _layer != null)
        {
            bool show = _wantOpen && PeekHandPatch.IsCombatViewActive();
            if (show != _layer.Visible)
            {
                _layer.Visible = show;
                if (show) { Reposition(); RefreshWidgets(); }
            }
        }
    }

    // 仅当焦点确实落在【可见】文本框时才屏蔽快捷键。host 退出大厅后可能残留一个【隐藏】文本框仍占焦点，
    // 旧判断（只看是不是 LineEdit/TextEdit）会把快捷键整个屏蔽 → host 打不开面板。
    private static bool IsTypingInTextField()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return false;
        Control? f = tree.Root?.GuiGetFocusOwner();
        return (f is LineEdit le && le.IsVisibleInTree())
            || (f is TextEdit te && te.IsVisibleInTree());
    }

    private static string FocusName()
    {
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root?.GuiGetFocusOwner() is Control c)
            return $"{c.GetType().Name}/{c.Name}";
        return "none";
    }

    // 快捷键冲突检测：与游戏输入映射或常用保留键冲突时返回原因文案，否则 null
    internal static string? HotkeyConflict(Key key)
    {
        if (key == Key.None) return null;
        try
        {
            foreach (StringName action in InputMap.GetActions())
                foreach (InputEvent ev in InputMap.ActionGetEvents(action))
                    if (ev is InputEventKey k)
                    {
                        Key kc = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
                        if (kc == key)
                            return (Zh ? "与游戏按键冲突：" : "Conflicts with game input: ") + action;
                    }
        }
        catch { /* InputMap 不可用时跳过游戏键检测 */ }

        if (key is Key.Escape or Key.Enter or Key.Space or Key.Tab)
            return Zh ? "与常用保留键冲突" : "Reserved-key conflict";
        return null;
    }

    private static string HeaderText()
    {
        string key = PeekSettings.PanelHotkey == Key.None
            ? (Zh ? "未设" : "None") : OS.GetKeycodeString(PeekSettings.PanelHotkey);
        return Zh ? $"⠿ 队友手牌透视 · 设置面板  ({key} 开关)" : $"⠿ Peek Settings  ({key})";
    }

    private static void Toggle()
    {
        if (!_built) Build();
        if (_layer == null) { Log.Warn("[MultiplayerPeeking] panel build failed; can't show"); return; }
        _wantOpen = !_wantOpen;
        Log.Info($"[MultiplayerPeeking] panel toggled → wantOpen={_wantOpen}");
        // 实际可见性由 OnFrame 按战斗视图门控（_wantOpen && IsCombatViewActive）
    }

    // 内容坐标系尺寸（含内容缩放/Retina 时与窗口物理像素不同）。用它定位才不会被算到屏外。
    private static Vector2 ViewSize()
        => Engine.GetMainLoop() is SceneTree tree && tree.Root != null
            ? tree.Root.GetVisibleRect().Size : new Vector2(1280, 720);

    private static Vector2 MousePos()
        => Engine.GetMainLoop() is SceneTree tree && tree.Root != null ? tree.Root.GetMousePosition() : Vector2.Zero;

    // 仅在用户【未】拖动过时，自动停靠当前视图右上（拖过则尊重用户位置）
    private static void Reposition()
    {
        if (_userMoved || _panel == null) return;
        _panel.Position = new Vector2(Mathf.Max(8f, ViewSize().X - 372f), 12f);
    }

    private static void ClampToViewport()
    {
        if (_panel == null) return;
        Vector2 vp = ViewSize();
        Vector2 sz = _panel.Size;
        float x = Mathf.Clamp(_panel.Position.X, 0f, Mathf.Max(0f, vp.X - sz.X));
        float y = Mathf.Clamp(_panel.Position.Y, 0f, Mathf.Max(0f, vp.Y - 40f)); // 至少留标题在屏内
        _panel.Position = new Vector2(x, y);
    }

    private static void OnHeaderInput(InputEvent e)
    {
        if (_panel == null) return;
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            _dragging = true;
            _grabOffset = MousePos() - _panel.Position;   // 之后每帧 OnFrame 跟随鼠标
        }
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
            _layer = null; _panel = null; _built = false;   // 半成品清掉，允许下次重试
        }
    }

    private static void BuildInternal(SceneTree tree)
    {
        _refreshers.Clear(); _foldGroups.Clear();

        // 顶层独立画布，跨场景常驻、绘制在最上层；ProcessMode=Always 让暂停时也能调
        _layer = new CanvasLayer { Layer = 128, ProcessMode = Node.ProcessModeEnum.Always };

        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.Position = new Vector2(Mathf.Max(8f, ViewSize().X - 372f), 12f);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 4);

        var header = new Label
        {
            Text = HeaderText(),
            MouseFilter = Control.MouseFilterEnum.Stop,     // 接收拖动
            TooltipText = Zh ? "拖动此标题移动面板" : "Drag to move",
        };
        header.GuiInput += OnHeaderInput;
        outer.AddChild(header);
        outer.AddChild(new HSeparator());
        _refreshers.Add(() => header.Text = HeaderText());

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(344, Mathf.Min(680f, ViewSize().Y * 0.8f)),
        };
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);

        // 主开关只在 ModConfig 里（控制面板不放）。各分区用分区标题清晰分隔。

        // —— 悬停预览 ——
        AddSectionHeader(vbox, Zh ? "悬停预览" : "Hover preview");
        AddSlider(vbox, Zh ? "卡牌大小" : "Card size", "%", 10, 100, 1,
            () => PeekSettings.CardScalePct, "CardScalePct", v => PeekSettings.CardScalePct = v);
        AddSlider(vbox, Zh ? "延迟消失" : "Hide delay", "ms", 0, 2000, 50,
            () => PeekSettings.HideDelayMs, "HideDelayMs", v => PeekSettings.HideDelayMs = v);
        AddSlider(vbox, Zh ? "卡牌间隔" : "Card spacing", "px", 0, 40, 1,
            () => PeekSettings.CardSpacingPx, "CardSpacingPx", v => PeekSettings.CardSpacingPx = v);
        AddSlider(vbox, Zh ? "与状态栏间距" : "Bar gap", "px", 0, 120, 1,
            () => PeekSettings.OverlayGapPx, "OverlayGapPx", v => PeekSettings.OverlayGapPx = v);

        // —— 卡牌高光 ——
        AddSectionHeader(vbox, Zh ? "卡牌高光" : "Card highlight");
        AddToggle(vbox, Zh ? "启用" : "Enable",
            () => PeekSettings.CardGlowEnabled, "CardGlowEnabled", v => PeekSettings.CardGlowEnabled = v);
        Control warnColorRow = AddSwatchRow(vbox, Zh ? "颜色" : "Color",
            () => PeekSettings.WarningColor, "WarningColor", c => PeekSettings.WarningColor = c,
            () => PeekSettings.PrismaticGlow, v => PeekSettings.PrismaticGlow = v);
        Control ovRadius = AddSlider(vbox, Zh ? "光晕宽度" : "Glow width", "%", 0, 100, 1,
            () => PeekSettings.OverlayGlowRadiusPct, "OverlayGlowRadiusPct", v => PeekSettings.OverlayGlowRadiusPct = v);
        Control ovOpacity = AddSlider(vbox, Zh ? "光晕不透明度" : "Glow opacity", "%", 0, 100, 1,
            () => PeekSettings.OverlayGlowOpacityPct, "OverlayGlowOpacityPct", v => PeekSettings.OverlayGlowOpacityPct = v);

        // —— 身体光晕 ——
        AddSectionHeader(vbox, Zh ? "身体光晕" : "Body glow");
        AddToggle(vbox, Zh ? "启用" : "Enable",
            () => PeekSettings.PurpleGlowEnabled, "PurpleGlowEnabled", v => PeekSettings.PurpleGlowEnabled = v);
        Control bodyColorRow = AddSwatchRow(vbox, Zh ? "颜色" : "Color",
            () => PeekSettings.BodyGlowColor, "BodyGlowColor", c => PeekSettings.BodyGlowColor = c,
            () => PeekSettings.BodyPrismaticGlow, v => PeekSettings.BodyPrismaticGlow = v);
        Control bodyOpacity = AddSlider(vbox, Zh ? "不透明度" : "Opacity", "%", 0, 100, 1,
            () => PeekSettings.BodyGlowOpacityPct, "BodyGlowOpacityPct", v => PeekSettings.BodyGlowOpacityPct = v);

        // —— 常态缩略图 ——
        AddSectionHeader(vbox, Zh ? "常态缩略图" : "Thumbnails");
        AddToggle(vbox, Zh ? "启用" : "Enable",
            () => PeekSettings.ThumbnailsEnabled, "ThumbnailsEnabled", v => PeekSettings.ThumbnailsEnabled = v);
        Control thSize = AddSlider(vbox, Zh ? "大小" : "Size", "%", 5, 40, 1,
            () => PeekSettings.ThumbnailScalePct, "ThumbnailScalePct", v => PeekSettings.ThumbnailScalePct = v);
        Control thSpacing = AddSlider(vbox, Zh ? "卡牌间隔" : "Card spacing", "px", 0, 40, 1,
            () => PeekSettings.ThumbnailSpacingPx, "ThumbnailSpacingPx", v => PeekSettings.ThumbnailSpacingPx = v);
        Control thGap = AddSlider(vbox, Zh ? "与状态栏间距" : "Bar gap", "px", 0, 120, 1,
            () => PeekSettings.ThumbnailGapPx, "ThumbnailGapPx", v => PeekSettings.ThumbnailGapPx = v);
        Control thOpacity = AddSlider(vbox, Zh ? "不透明度" : "Opacity", "%", 20, 100, 1,
            () => PeekSettings.ThumbnailOpacityPct, "ThumbnailOpacityPct", v => PeekSettings.ThumbnailOpacityPct = v);
        Control thGlow = AddToggle(vbox, Zh ? "显示高光" : "Show highlight",
            () => PeekSettings.ThumbnailShowGlow, "ThumbnailShowGlow", v => PeekSettings.ThumbnailShowGlow = v);
        Control thGlowR = AddSlider(vbox, Zh ? "光晕宽度" : "Glow width", "%", 0, 100, 1,
            () => PeekSettings.ThumbnailGlowRadiusPct, "ThumbnailGlowRadiusPct", v => PeekSettings.ThumbnailGlowRadiusPct = v);
        Control thGlowO = AddSlider(vbox, Zh ? "光晕不透明度" : "Glow opacity", "%", 0, 100, 1,
            () => PeekSettings.ThumbnailGlowOpacityPct, "ThumbnailGlowOpacityPct", v => PeekSettings.ThumbnailGlowOpacityPct = v);

        vbox.AddChild(new HSeparator());
        var reset = new Button { Text = Zh ? "恢复默认" : "Reset to defaults" };
        reset.Pressed += ResetAll;
        vbox.AddChild(reset);

        // 子折叠（#5：开关关 → 颜色 + 光晕宽度/不透明度 整段隐藏）
        _foldGroups.Add((() => PeekSettings.CardGlowEnabled,   new List<Control> { warnColorRow, ovRadius, ovOpacity }));
        _foldGroups.Add((() => PeekSettings.PurpleGlowEnabled, new List<Control> { bodyColorRow, bodyOpacity }));
        _foldGroups.Add((() => PeekSettings.ThumbnailsEnabled, new List<Control> { thSize, thSpacing, thGap, thOpacity, thGlow }));
        // 嵌套：缩略图光晕宽度/不透明度 只在「常态缩略图」且「显示高光」都开时才显示
        _foldGroups.Add((() => PeekSettings.ThumbnailsEnabled && PeekSettings.ThumbnailShowGlow, new List<Control> { thGlowR, thGlowO }));

        scroll.AddChild(vbox);
        outer.AddChild(scroll);
        margin.AddChild(outer);
        _panel.AddChild(margin);
        _layer.AddChild(_panel);
        tree.Root.AddChild(_layer);              // 挂在 Root（current_scene 的兄弟）→ 跨场景常驻
        _layer.Visible = false;

        ApplyFolds();
    }

    /* ---- 行构造器：改值经 ModConfig 下发（否则直接写字段）；返回该行 Control 以便折叠；登记刷新回调 ---- */

    // 分区标题（#6）：上方分割线 + 带色标签，清晰区隔各分区
    private static void AddSectionHeader(VBoxContainer parent, string text)
    {
        parent.AddChild(new HSeparator());
        var lbl = new Label { Text = text };
        lbl.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
        parent.AddChild(lbl);
    }

    private static Control AddToggle(VBoxContainer parent, string label, Func<bool> get, string key, Action<bool> setField)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var cb = new CheckBox { ButtonPressed = get() };
        StyleCheckBox(cb);   // 关闭态也保持可见的方框轮廓（游戏主题里 unchecked 图标透明，用户找不到）
        cb.Toggled += on => { if (!_suppress) Apply(key, on, () => setField(on)); };
        row.AddChild(cb);
        parent.AddChild(row);
        _refreshers.Add(() => cb.ButtonPressed = get());
        return row;
    }

    // 游戏主题里勾选框「未勾选」图标完全透明 → 关闭态看不见框。覆盖 checked/unchecked 图标为自绘方框：
    // 未勾选=空心描边框，勾选=描边框+对勾✓，两态都有清晰轮廓。图标全局共用一份。
    private static ImageTexture? _iconOff, _iconOn;
    private static void StyleCheckBox(CheckBox cb)
    {
        _iconOff ??= MakeCheckIcon(false);
        _iconOn  ??= MakeCheckIcon(true);
        cb.AddThemeIconOverride("unchecked", _iconOff);
        cb.AddThemeIconOverride("unchecked_disabled", _iconOff);
        cb.AddThemeIconOverride("checked", _iconOn);
        cb.AddThemeIconOverride("checked_disabled", _iconOn);
    }

    private static ImageTexture MakeCheckIcon(bool on)
    {
        const int n = 24, b = 2;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        Color border = new Color(0.80f, 0.84f, 0.90f, 1f);   // 描边方框（始终可见）
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                if (x < b || x >= n - b || y < b || y >= n - b)
                    img.SetPixel(x, y, border);
        // 勾选 = 画对勾 ✓（两段线）；未勾选 = 仅空心框
        if (on)
        {
            Color check = new Color(0.45f, 0.85f, 1f, 1f);
            DrawSeg(img, new Vector2(6, 12), new Vector2(10, 16), check, n);
            DrawSeg(img, new Vector2(10, 16), new Vector2(18, 7), check, n);
        }
        return ImageTexture.CreateFromImage(img);
    }

    // 在图上沿 a→b 画一条粗线（3px 笔刷）：采样插值，每点涂一个 3×3 方块
    private static void DrawSeg(Image img, Vector2 a, Vector2 b, Color col, int n)
    {
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            int cx = Mathf.RoundToInt(Mathf.Lerp(a.X, b.X, t));
            int cy = Mathf.RoundToInt(Mathf.Lerp(a.Y, b.Y, t));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < n && y >= 0 && y < n) img.SetPixel(x, y, col);
                }
        }
    }

    private static Control AddSlider(VBoxContainer parent, string label, string suffix,
                                     int min, int max, int step, Func<int> get, string key, Action<int> setField)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(140, 0) });
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = get(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(96, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var val = new Label
        {
            Text = get() + suffix, CustomMinimumSize = new Vector2(56, 0),
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
        return row;
    }

    // 预设色板（与 ModConfig 一致）：标签 + 一格色块矩阵（含可选 🌈 棱彩预设）
    private static Control AddSwatchRow(VBoxContainer parent, string label, Func<Color> get, string key,
                                        Action<Color> setField, Func<bool>? prismGet = null, Action<bool>? prismSet = null)
    {
        bool withRainbow = prismGet != null && prismSet != null;

        var box = new VBoxContainer();

        // 标签 + hex 输入（与 ModConfig 一致，可精调）
        var top = new HBoxContainer();
        top.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var hexEdit = new LineEdit
        {
            Text = get().ToHtml(true), MaxLength = 9,
            CustomMinimumSize = new Vector2(92, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        top.AddChild(hexEdit);
        box.AddChild(top);

        var grid = new GridContainer { Columns = 12 };
        grid.AddThemeConstantOverride("h_separation", 3);
        grid.AddThemeConstantOverride("v_separation", 3);

        var swatches = new List<(Button btn, string hex)>();
        Button? rainbowBtn = null;

        void Restyle()
        {
            string sel = get().ToHtml(false);
            bool prism = withRainbow && prismGet!();
            foreach (var (b, hx) in swatches)
                b.AddThemeStyleboxOverride("normal",
                    PeekColorGrid.Swatch(Color.FromHtml(hx), !prism && string.Equals(hx, sel, StringComparison.OrdinalIgnoreCase)));
            if (rainbowBtn != null)
                rainbowBtn.AddThemeStyleboxOverride("normal", PeekColorGrid.Swatch(new Color(0f, 0f, 0f, 0f), prism));
        }

        void SetColor(Color c)
        {
            if (withRainbow) prismSet!(false);   // 选具体颜色=关棱彩
            hexEdit.Text = c.ToHtml(true);
            Apply(key, c.ToHtml(true), () => setField(c));
            Restyle();
        }

        hexEdit.TextSubmitted += s =>
        {
            string h = s.Trim().TrimStart('#');
            if (Color.HtmlIsValid(h)) SetColor(Color.FromHtml(h));
            else hexEdit.Text = get().ToHtml(true);   // 非法 → 还原
        };

        if (withRainbow)
        {
            rainbowBtn = new Button
            {
                TooltipText = Zh ? "棱彩渐变" : "Prismatic gradient",
                CustomMinimumSize = new Vector2(24, 18), FocusMode = Control.FocusModeEnum.None,
                Icon = PeekColorGrid.RainbowTex(), ExpandIcon = true,
            };
            rainbowBtn.AddThemeStyleboxOverride("hover",   PeekColorGrid.Swatch(new Color(0f, 0f, 0f, 0f), true));
            rainbowBtn.Pressed += () => { SetPrismatic(prismSet!, true); Restyle(); };
            grid.AddChild(rainbowBtn);
        }

        foreach (string hex in PeekColorGrid.Palette)
        {
            Color c = Color.FromHtml(hex);
            var btn = new Button { TooltipText = "#" + hex, CustomMinimumSize = new Vector2(24, 18), FocusMode = Control.FocusModeEnum.None };
            btn.AddThemeStyleboxOverride("hover", PeekColorGrid.Swatch(c.Lightened(0.18f), true));
            btn.Pressed += () => SetColor(c);
            swatches.Add((btn, hex));
            grid.AddChild(btn);
        }

        Restyle();
        box.AddChild(grid);
        parent.AddChild(box);
        _refreshers.Add(() => { hexEdit.Text = get().ToHtml(true); Restyle(); });
        return box;
    }

    // 棱彩作为「颜色」的预设（卡牌色→PrismaticGlow / 身体色→BodyPrismaticGlow），不是独立开关
    private static void SetPrismatic(Action<bool> set, bool on)
    {
        _applying = true;
        try { set(on); PeekSettings.ScheduleSave(); PeekSettings.NotifyChanged(); }
        finally { _applying = false; ApplyFolds(); }
    }

    // 优先经 ModConfig 的 SetValue 走同一条 OnChanged 通路（写 PeekSettings + 保存 + 实时重建 + 持久化）；
    // 未装 ModConfig 时回退为直接写字段 + 保存 + 通知。改完同步折叠状态。
    private static void Apply(string key, object mcValue, Action directSet)
    {
        _applying = true;   // 本面板自身改动：跳过随之而来的 RefreshWidgets 自回声
        try
        {
            if (PeekModConfigBridge.TrySetValue(key, mcValue)) return;
            directSet();
            PeekSettings.ScheduleSave();
            PeekSettings.NotifyChanged();
        }
        finally { _applying = false; ApplyFolds(); }
    }

    // 折叠：按各开关当前值显示/隐藏其下属行（主开关只在 ModConfig，本面板不做主折叠）
    private static void ApplyFolds()
    {
        foreach ((Func<bool> on, List<Control> rows) in _foldGroups)
        {
            bool vis = on();
            foreach (Control r in rows)
                if (GodotObject.IsInstanceValid(r)) r.Visible = vis;
        }
    }

    private static void ResetAll()
    {
        // 优先触发 ModConfig 自身的「恢复默认」（含其菜单/落盘一致）；没装则手动还原字段
        if (PeekModConfigBridge.TryReset())
        {
            // 棱彩开关已不是 ModConfig 词条（改为颜色的预设），其重置不含它们 → 单独还原
            PeekSettings.PrismaticGlow     = PeekSettings.DefaultPrismaticGlow;
            PeekSettings.BodyPrismaticGlow = PeekSettings.DefaultBodyPrismaticGlow;
            PeekSettings.ScheduleSave();
            PeekSettings.NotifyChanged();
        }
        else
        {
            PeekSettings.MasterEnabled         = PeekSettings.DefaultMasterEnabled;
            PeekSettings.CardScalePct          = PeekSettings.DefaultCardScalePct;
            PeekSettings.HideDelayMs           = PeekSettings.DefaultHideDelayMs;
            PeekSettings.CardSpacingPx         = PeekSettings.DefaultCardSpacingPx;
            PeekSettings.OverlayGapPx          = PeekSettings.DefaultOverlayGapPx;
            PeekSettings.CardGlowEnabled       = PeekSettings.DefaultCardGlowEnabled;
            PeekSettings.PrismaticGlow         = PeekSettings.DefaultPrismaticGlow;
            PeekSettings.BodyPrismaticGlow     = PeekSettings.DefaultBodyPrismaticGlow;
            PeekSettings.OverlayGlowRadiusPct  = PeekSettings.DefaultOverlayGlowRadiusPct;
            PeekSettings.OverlayGlowOpacityPct = PeekSettings.DefaultOverlayGlowOpacityPct;
            PeekSettings.PurpleGlowEnabled     = PeekSettings.DefaultPurpleGlowEnabled;
            PeekSettings.WarningColor          = PeekSettings.DefaultWarningColor;
            PeekSettings.BodyGlowColor         = PeekSettings.DefaultBodyGlowColor;
            PeekSettings.BodyGlowOpacityPct    = PeekSettings.DefaultBodyGlowOpacityPct;
            PeekSettings.ThumbnailsEnabled     = PeekSettings.DefaultThumbnailsEnabled;
            PeekSettings.ThumbnailScalePct     = PeekSettings.DefaultThumbnailScalePct;
            PeekSettings.ThumbnailGlowRadiusPct  = PeekSettings.DefaultThumbnailGlowRadiusPct;
            PeekSettings.ThumbnailGlowOpacityPct = PeekSettings.DefaultThumbnailGlowOpacityPct;
            PeekSettings.ThumbnailSpacingPx    = PeekSettings.DefaultThumbnailSpacingPx;
            PeekSettings.ThumbnailGapPx        = PeekSettings.DefaultThumbnailGapPx;
            PeekSettings.ThumbnailOpacityPct   = PeekSettings.DefaultThumbnailOpacityPct;
            PeekSettings.ThumbnailShowGlow     = PeekSettings.DefaultThumbnailShowGlow;
            PeekSettings.PanelHotkey           = PeekSettings.DefaultPanelHotkey;
            PeekSettings.HoldHideKey           = PeekSettings.DefaultHoldHideKey;
            PeekSettings.ThumbnailToggleHotkey = PeekSettings.DefaultThumbnailToggleHotkey;
            PeekSettings.Save();
            PeekSettings.NotifyChanged();
        }
        RefreshWidgets();
    }

    // 订阅 PeekSettings.Changed：外部改值时把控件刷到最新（仅在面板可见时，省开销）。
    private static void RefreshWidgets()
    {
        if (_applying || _layer is not { Visible: true }) return;
        _suppress = true;
        foreach (Action r in _refreshers)
        {
            try { r(); } catch (Exception e) { Log.Warn($"[MultiplayerPeeking] dev refresh: {e.Message}"); }
        }
        _suppress = false;
        ApplyFolds();
    }
}
