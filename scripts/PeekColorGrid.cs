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
    private static readonly string[] Palette =
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
            "WarningColor", () => PeekSettings.WarningColor, () => PeekSettings.CardGlowEnabled);
        InsertCollapsibleAfter(entries,
            Zh ? "身体光晕颜色 (hex)" : "Body glow color (hex)",
            "BodyGlowColor", () => PeekSettings.BodyGlowColor, () => PeekSettings.PurpleGlowEnabled);
    }

    private static void InsertCollapsibleAfter(VBoxContainer entries, string rowLabel, string key,
                                               Func<Color> getCurrent, Func<bool> isEnabled)
    {
        int idx = FindRowIndex(entries, rowLabel);
        Control? row = idx >= 0 ? entries.GetChild(idx) as Control : null;
        LineEdit? hexEdit = row != null ? FindLineEdit(row) : null;   // 该 hex 行里的输入框
        Control block = BuildCollapsible(key, hexEdit, getCurrent);
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

    private static Control BuildCollapsible(string key, LineEdit? hexEdit, Func<Color> getCurrent)
    {
        var box = new VBoxContainer();

        var grid = new GridContainer { Columns = 12, Visible = false };   // 默认收起
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);

        var swatches = new List<(Button btn, Color c, string hex)>();

        // 把选中标记（白边）移到匹配 selHex 的色块上
        void Restyle(string? selHex)
        {
            foreach (var (b, col, hx) in swatches)
                b.AddThemeStyleboxOverride("normal",
                    Swatch(col, border: string.Equals(hx, selHex, StringComparison.OrdinalIgnoreCase)));
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
            btn.AddThemeStyleboxOverride("normal",  Swatch(c, border: false));
            btn.AddThemeStyleboxOverride("hover",   Swatch(c.Lightened(0.18f), border: true));
            btn.AddThemeStyleboxOverride("pressed", Swatch(c, border: true));
            btn.Pressed += () =>
            {
                string h = c.ToHtml(true);
                if (hexEdit != null) hexEdit.Text = h;   // ← 上方 hex 输入框实时跟着变（强绑定）
                SetModConfigValue(key, h);                // 等同于在 hex 框输入：触发该词条 OnChanged（写 PeekSettings+Commit）并持久化
                Restyle(hex);                             // 选中标记移到当前色块
            };
            swatches.Add((btn, c, hex));
            grid.AddChild(btn);
        }

        Restyle(getCurrent().ToHtml(false));   // 初始按当前颜色标记选中色块

        string label = Zh ? "调色盘（点击展开/收起）" : "Palette (click to expand)";
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

    private static StyleBoxFlat Swatch(Color c, bool border)
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
