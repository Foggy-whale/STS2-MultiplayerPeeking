using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace MultiplayerPeeking;

// 往 ModConfig 的「模组配置 → 队友手牌透视」区块里注入一个【可预览的色块矩阵】——
// ModConfig 内置控件没有取色器，所以用 Harmony 给它的面板构建方法 PopulateInto 打 postfix，
// 在本 mod 的 Entries_MultiplayerPeeking 容器里加一组「点击即用」的彩色按钮（按钮底色=实际颜色）。
// ModConfig 的 RefreshUI 会清空并重建容器，PopulateInto 每次都会被调用，所以注入自动随之恢复。
internal static class PeekColorGrid
{
    private const string ModId = "MultiplayerPeeking";
    private static bool _patched;

    private static bool Zh => LocManager.Instance?.Language == "zhs";

    // 色块矩阵（24 色，Columns=12 → 2 行）：覆盖紫/品红/粉/红/蓝/青/绿/橙 + 几个中性色
    internal static readonly string[] Palette =
    {
        "8a5cf6", "a855f7", "c026d3", "e040fb", "ec4899", "f43f5e",
        "ff4081", "ff5252", "ff6d00", "ffab00", "ffd600", "aeea00",
        "64dd17", "00e676", "1de9b6", "00b0ff", "2979ff", "5c6bc0",
        "7e57c2", "26c6da", "ffffff", "b0bec5", "78909c", "455a64",
    };

    internal static void TryPatch()
    {
        if (_patched) return;
        try
        {
            Type? t = AccessTools.TypeByName("ModConfig.SettingsTabInjector");
            MethodInfo? m = t == null ? null : AccessTools.Method(t, "PopulateInto");
            if (m == null) { Log.Info("[MultiplayerPeeking] ModConfig PopulateInto not found; swatch grid skipped"); return; }

            new Harmony("MultiplayerPeeking.colorgrid")
                .Patch(m, postfix: new HarmonyMethod(typeof(PeekColorGrid), nameof(Postfix)));
            _patched = true;
            Log.Info("[MultiplayerPeeking] color swatch grid patch applied");
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] swatch grid patch failed: {e}"); }
    }

    // PopulateInto(VBoxContainer target) 之后：往本 mod 的 entries 容器追加色块矩阵
    private static void Postfix(VBoxContainer __0)
    {
        try
        {
            VBoxContainer? entries = FindFreshEntries(__0);
            if (entries == null) return;
            InjectGrids(entries);
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] swatch grid inject failed: {e}"); }
    }

    // Entries_<modId> 嵌在 PopulateInto 新建的 ScrollContainer→VBox 里（不是 __0 的直接子节点），
    // 所以要【递归】查找；并跳过 RefreshUI 待删的旧子树，只取新建的那个。
    private static VBoxContainer? FindFreshEntries(Node node)
    {
        string want = $"Entries_{ModId}";
        foreach (Node child in node.GetChildren())
        {
            if (child.IsQueuedForDeletion()) continue;
            if (child is VBoxContainer vb && vb.Name.ToString() == want) return vb;
            VBoxContainer? found = FindFreshEntries(child);
            if (found != null) return found;
        }
        return null;
    }

    // key → (该颜色的 hex 行, 注入的调色盘块)。供开关「折叠/展开」用；每次 PopulateInto 都重新登记，引用始终最新
    private static readonly Dictionary<string, (Control? row, Control block)> _colorUi = new();

    // 把每个颜色的「可折叠色块矩阵」插到对应 hex 行的【正下方】；选色块时实时改上面的 hex 输入框。
    // 每个颜色行受其上方开关控制：开关关 → hex 行 + 调色盘一起折叠隐藏（CardGlowEnabled/PurpleGlowEnabled）。
    private static void InjectGrids(VBoxContainer entries)
    {
        InsertCollapsibleAfter(entries,
            Zh ? "卡牌高光颜色 (hex)" : "Card highlight color (hex)",
            "WarningColor", () => PeekSettings.WarningColor, () => PeekSettings.CardGlowEnabled,
            () => PeekSettings.PrismaticGlow, v => PeekSettings.PrismaticGlow = v);
        InsertCollapsibleAfter(entries,
            Zh ? "身体光晕颜色 (hex)" : "Body glow color (hex)",
            "BodyGlowColor", () => PeekSettings.BodyGlowColor, () => PeekSettings.PurpleGlowEnabled,
            () => PeekSettings.BodyPrismaticGlow, v => PeekSettings.BodyPrismaticGlow = v);

        RegisterThumbnailRows(entries);   // 缩略图子设置随「常态缩略图」开关折叠
        InjectHotkeyWarning(entries);     // 设置面板快捷键的冲突警告

        // 记录容器与主开关行，供「关主开关折叠全部」直接改可见性用（不重建面板 → 不跳焦点）
        _entries = entries;
        _masterIdx = FindRowIndex(entries, Zh ? "启用队友手牌透视（主开关）" : "Enable Multiplayer Peeking");
        SetMasterFold(PeekSettings.MasterEnabled);   // 应用初始折叠状态
    }

    // 主开关：关 → 隐藏除「主开关」行外的所有设置行；开 → 全部显示，再应用各子折叠。
    // 直接改可见性（不调 RefreshUI 重建面板），所以焦点不会跳。
    private static VBoxContainer? _entries;
    private static int _masterIdx = -1;
    internal static void SetMasterFold(bool masterOn)
    {
        if (_entries == null || !GodotObject.IsInstanceValid(_entries)) return;
        var children = _entries.GetChildren();
        for (int i = 0; i < children.Count; i++)
            if (i != _masterIdx && children[i] is Control c)
                c.Visible = masterOn;
        if (masterOn)   // 重新应用各子折叠
        {
            SetCardFold(PeekSettings.CardGlowEnabled);
            SetBodyFold(PeekSettings.PurpleGlowEnabled);
            SetThumbnailRowsVisible(PeekSettings.ThumbnailsEnabled);
        }
    }

    // 卡牌高光折叠：颜色行+调色盘 + 悬停预览光晕宽度/不透明度整段随开关折叠
    internal static void SetCardFold(bool visible)
    {
        SetColorRowVisible("WarningColor", visible);
        SetRowVisibleByLabel(Zh ? "悬停预览光晕宽度 (%)"   : "Hover glow width (%)",   visible);
        SetRowVisibleByLabel(Zh ? "悬停预览光晕不透明度 (%)" : "Hover glow opacity (%)", visible);
    }

    // 身体光晕折叠：颜色行+调色盘 + 身体光晕不透明度
    internal static void SetBodyFold(bool visible)
    {
        SetColorRowVisible("BodyGlowColor", visible);
        SetRowVisibleByLabel(Zh ? "身体光晕不透明度 (%)" : "Body glow opacity (%)", visible);
    }

    private static void SetRowVisibleByLabel(string label, bool visible)
    {
        if (_entries == null || !GodotObject.IsInstanceValid(_entries)) return;
        int idx = FindRowIndex(_entries, label);
        if (idx >= 0 && _entries.GetChild(idx) is Control c) c.Visible = visible;
    }

    private static void InsertCollapsibleAfter(VBoxContainer entries, string rowLabel, string key,
                                               Func<Color> getCurrent, Func<bool> isEnabled,
                                               Func<bool>? prismGet = null, Action<bool>? prismSet = null)
    {
        int idx = FindRowIndex(entries, rowLabel);
        Control? row = idx >= 0 ? entries.GetChild(idx) as Control : null;
        LineEdit? hexEdit = row != null ? FindLineEdit(row) : null;   // 该 hex 行里的输入框
        Control block = BuildCollapsible(key, hexEdit, getCurrent, prismGet, prismSet);
        entries.AddChild(block);
        if (idx >= 0) entries.MoveChild(block, idx + 1);   // 该行下方；找不到则留在末尾

        // 登记引用 + 按开关设初始可见性（关 → hex 行与调色盘一起折叠隐藏）
        _colorUi[key] = (row, block);
        bool on = isEnabled();
        if (row != null) row.Visible = on;
        block.Visible = on;
    }

    // 开关切换时直接改可见性（无需重建整个面板）：折叠/展开该颜色的 hex 行 + 调色盘
    internal static void SetColorRowVisible(string key, bool visible)
    {
        if (!_colorUi.TryGetValue(key, out var ui)) return;
        if (ui.row != null && GodotObject.IsInstanceValid(ui.row)) ui.row.Visible = visible;
        if (GodotObject.IsInstanceValid(ui.block)) ui.block.Visible = visible;
    }

    // 缩略图子设置行（大小/透明度/显示高光）：随「常态缩略图」开关折叠。每次 PopulateInto 重新登记，引用最新。
    private static readonly List<Control> _thumbRows = new();

    private static void RegisterThumbnailRows(VBoxContainer entries)
    {
        // 缩略图区在末尾：登记「常态缩略图总开关」行之后的【所有】行（含其内部分割线），整体随开关折叠
        _thumbRows.Clear();
        int start = FindRowIndex(entries, Zh ? "常态缩略图显示" : "Always-on thumbnails");
        if (start < 0) return;
        var children = entries.GetChildren();
        for (int i = start + 1; i < children.Count; i++)
            if (children[i] is Control c) _thumbRows.Add(c);
        SetThumbnailRowsVisible(PeekSettings.ThumbnailsEnabled);
    }

    internal static void SetThumbnailRowsVisible(bool visible)
    {
        foreach (Control row in _thumbRows)
            if (GodotObject.IsInstanceValid(row)) row.Visible = visible;
        if (visible) SetThumbGlowFold(PeekSettings.ThumbnailShowGlow);   // 嵌套：再按「显示高光」折叠光晕宽度/不透明度
    }

    // 缩略图光晕子折叠：「显示高光」关 → 隐藏缩略图光晕宽度/不透明度（嵌套在缩略图分区内）
    internal static void SetThumbGlowFold(bool visible)
    {
        SetRowVisibleByLabel(Zh ? "缩略图光晕宽度 (%)"   : "Thumb glow width (%)",   visible);
        SetRowVisibleByLabel(Zh ? "缩略图光晕不透明度 (%)" : "Thumb glow opacity (%)", visible);
    }

    // 设置面板快捷键的冲突警告标签（注入到 KeyBind 行下方；改键时由 bridge 调 SetHotkeyWarning 更新）
    private static Label? _hotkeyWarn;

    private static void InjectHotkeyWarning(VBoxContainer entries)
    {
        _hotkeyWarn = null;
        int idx = FindRowIndex(entries, Zh ? "设置面板快捷键" : "Settings panel hotkey");
        if (idx < 0) return;
        var warn = new Label { Visible = false };
        warn.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.3f));
        entries.AddChild(warn);
        entries.MoveChild(warn, idx + 1);
        _hotkeyWarn = warn;
        SetHotkeyWarning(PeekDevPanel.HotkeyConflict(PeekSettings.PanelHotkey));   // 初始按当前键计算
    }

    internal static void SetHotkeyWarning(string? text)
    {
        if (_hotkeyWarn == null || !GodotObject.IsInstanceValid(_hotkeyWarn)) return;
        if (string.IsNullOrEmpty(text)) _hotkeyWarn.Visible = false;
        else { _hotkeyWarn.Text = "    ⚠ " + text; _hotkeyWarn.Visible = true; }
    }

    private static LineEdit? FindLineEdit(Node row)
    {
        foreach (Node c in row.GetChildren())
            if (c is LineEdit le) return le;
        return null;
    }

    // ModConfig 的 TextInput 行是 HBoxContainer，首个 Label.Text = "  " + 词条标签
    private static int FindRowIndex(VBoxContainer entries, string rowLabel)
    {
        Godot.Collections.Array<Node> children = entries.GetChildren();
        for (int i = 0; i < children.Count; i++)
            if (children[i] is HBoxContainer hb)
                foreach (Node c in hb.GetChildren())
                    if (c is Label lb && lb.Text.Trim() == rowLabel)
                        return i;
        return -1;
    }

    // 把某个棱彩开关与「颜色调色板」绑定：选 🌈 预设=开棱彩、选具体颜色=关棱彩（prismGet/Set 决定是卡牌还是身体）
    private static Control BuildCollapsible(string key, LineEdit? hexEdit, Func<Color> getCurrent,
                                            Func<bool>? prismGet, Action<bool>? prismSet)
    {
        bool withRainbow = prismGet != null && prismSet != null;

        var box = new VBoxContainer();

        var grid = new GridContainer { Columns = 12, Visible = false };   // 默认收起
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);

        var swatches = new List<(Button btn, Color c, string hex)>();
        Button? rainbowBtn = null;

        // 把选中标记（白边）移到当前选中项：棱彩开 → 标 🌈；否则标匹配 selHex 的色块
        void Restyle(string? selHex)
        {
            bool prism = withRainbow && prismGet!();
            foreach (var (b, col, hx) in swatches)
                b.AddThemeStyleboxOverride("normal",
                    Swatch(col, border: !prism && string.Equals(hx, selHex, StringComparison.OrdinalIgnoreCase)));
            if (rainbowBtn != null)
                rainbowBtn.AddThemeStyleboxOverride("normal", Swatch(new Color(0f, 0f, 0f, 0f), border: prism));
        }

        // 棱彩预设（仅卡牌高光色）：用彩虹渐变色块，风格与其它色块一致
        if (withRainbow)
        {
            rainbowBtn = new Button
            {
                TooltipText = Zh ? "棱彩渐变" : "Prismatic gradient",
                CustomMinimumSize = new Vector2(42, 24), FocusMode = Control.FocusModeEnum.None,
                Icon = RainbowTex(), ExpandIcon = true,
            };
            rainbowBtn.AddThemeStyleboxOverride("hover",   Swatch(new Color(0f, 0f, 0f, 0f), border: true));
            rainbowBtn.AddThemeStyleboxOverride("pressed", Swatch(new Color(0f, 0f, 0f, 0f), border: true));
            rainbowBtn.Pressed += () => { prismSet!(true); PeekSettings.ScheduleSave(); PeekSettings.NotifyChanged(); Restyle(null); };
            grid.AddChild(rainbowBtn);
        }

        foreach (string hex in Palette)
        {
            Color c = Color.FromHtml(hex);
            var btn = new Button
            {
                Text = "",
                TooltipText = "#" + hex,
                CustomMinimumSize = new Vector2(42, 24),
                FocusMode = Control.FocusModeEnum.None,
            };
            btn.AddThemeStyleboxOverride("hover",   Swatch(c.Lightened(0.18f), border: true));
            btn.AddThemeStyleboxOverride("pressed", Swatch(c, border: true));
            btn.Pressed += () =>
            {
                if (withRainbow) prismSet!(false);   // 选具体颜色=关棱彩（随下面 Commit 落盘+生效）
                string h = c.ToHtml(true);
                if (hexEdit != null) hexEdit.Text = h;   // ← 上方 hex 输入框实时跟着变（强绑定）
                SetModConfigValue(key, h);                // 触发该词条 OnChanged（写 PeekSettings+Commit）并持久化
                Restyle(hex);                             // 选中标记移到当前色块
            };
            swatches.Add((btn, c, hex));
            grid.AddChild(btn);
        }

        Restyle(getCurrent().ToHtml(false));   // 初始按当前状态标记选中（棱彩 → 🌈）

        string label = Zh ? "预设色板（点击展开/收起）" : "Preset swatches (click to expand)";
        var toggle = new Button
        {
            Text = "    ▸ " + label,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None,
        };
        toggle.Pressed += () =>
        {
            grid.Visible = !grid.Visible;
            toggle.Text = (grid.Visible ? "    ▾ " : "    ▸ ") + label;
        };
        box.AddChild(toggle);
        box.AddChild(grid);
        return box;
    }

    // 反射调用 ModConfig.ModConfigApi.SetValue（= 在 hex 框输入同样的值）：触发该词条 OnChanged + 持久化
    private static MethodInfo? _setValue;
    private static void SetModConfigValue(string key, object value)
    {
        try
        {
            if (_setValue == null)
            {
                Type? api = AccessTools.TypeByName("ModConfig.ModConfigApi");
                _setValue = api == null ? null
                    : AccessTools.Method(api, "SetValue", new[] { typeof(string), typeof(string), typeof(object) });
            }
            _setValue?.Invoke(null, new object[] { ModId, key, value });
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] ModConfig SetValue failed: {e}"); }
    }

    // 彩虹渐变纹理（横向七色），给「棱彩预设」当色块用，使其与其它纯色色块风格一致（而非 🌈 emoji）
    private static Texture2D? _rainbowTex;
    internal static Texture2D RainbowTex()
    {
        if (_rainbowTex != null) return _rainbowTex;
        var grad = new Gradient();
        var offs = new float[7];
        var cols = new Color[7];
        for (int i = 0; i < 7; i++) { offs[i] = i / 6f; cols[i] = Color.FromHsv(i / 6f * 0.85f, 0.9f, 1f); }
        grad.Offsets = offs;
        grad.Colors  = cols;
        _rainbowTex = new GradientTexture2D
        {
            Gradient = grad, Width = 48, Height = 24,
            Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 0),
        };
        return _rainbowTex;
    }

    internal static StyleBoxFlat Swatch(Color c, bool border)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = c,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        if (border)
        {
            sb.BorderColor = Colors.White;
            sb.BorderWidthTop = sb.BorderWidthBottom = sb.BorderWidthLeft = sb.BorderWidthRight = 2;
        }
        return sb;
    }
}
