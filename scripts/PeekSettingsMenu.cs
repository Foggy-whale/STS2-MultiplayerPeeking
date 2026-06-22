using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace MultiplayerPeeking;

// 把本 mod 的选项作为自建 Godot 控件行注入游戏原生「设置」界面的 General 标签页。
// NSettingsTabManager._Ready 子节点先于父节点触发，所以此时 %GeneralSettings 面板的
// _Ready 已跑完、Content（VBoxContainer）已就绪。失败时 try/catch 优雅降级（仅没菜单）。
[HarmonyPatch(typeof(NSettingsTabManager), "_Ready")]
public static class PeekSettingsMenuPatch
{
    public static void Postfix(NSettingsTabManager __instance)
    {
        try { Inject(__instance); }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] settings menu injection failed: {e}"); }
    }

    private static void Inject(NSettingsTabManager mgr)
    {
        var panel = mgr.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        if (panel == null) { Log.Warn("[MultiplayerPeeking] %GeneralSettings not found"); return; }

        VBoxContainer content = panel.Content;
        if (content == null) return;
        if (content.HasNode("PeekHeader")) return; // 防重复注入

        var refreshers    = new List<Action>();   // 把当前值回填进控件（用于「恢复默认」）
        var thumbControls = new List<Control>();   // 缩略图子项控件（随父开关启用/禁用）
        var thumbRows     = new List<Control>();   // 对应的行容器（随父开关变暗）

        void Commit() { PeekSettings.Save(); PeekSettings.NotifyChanged(); }

        void UpdateThumbEnabled()
        {
            bool on = PeekSettings.ThumbnailsEnabled;
            for (int i = 0; i < thumbControls.Count; i++)
                SetRowEnabled(thumbControls[i], thumbRows[i], on);
        }

        (CheckBox, HBoxContainer) AddToggle(string key, Func<bool> get, Action<bool> set, bool indent = false)
        {
            var cb = new CheckBox { ButtonPressed = get(), FocusMode = Control.FocusModeEnum.None };
            cb.Toggled += pressed => { set(pressed); Commit(); };
            refreshers.Add(() => cb.SetPressedNoSignal(get()));
            var row = NewRow(indent);
            row.AddChild(MakeLabel(PeekLoc.T(key), indent));
            row.AddChild(cb);
            content.AddChild(row);
            return (cb, row);
        }

        (HSlider, HBoxContainer) AddSlider(string key, int min, int max, Func<int> get, Action<int> set, string suffix, bool indent = false)
        {
            var slider = new HSlider
            {
                MinValue = min, MaxValue = max, Step = 1, Value = get(),
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(200, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            var val = new Label
            {
                Text = get() + suffix,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(86, 40),
            };
            // 拖动时实时预览（NotifyChanged），仅在拖动结束时落盘（避免每帧写磁盘）
            slider.ValueChanged += v => { int iv = (int)v; set(iv); val.Text = iv + suffix; PeekSettings.NotifyChanged(); };
            slider.DragEnded += changed => { if (changed) PeekSettings.Save(); };
            refreshers.Add(() => { slider.SetValueNoSignal(get()); val.Text = get() + suffix; });
            var row = NewRow(indent);
            row.AddChild(MakeLabel(PeekLoc.T(key), indent));
            row.AddChild(slider);
            row.AddChild(val);
            content.AddChild(row);
            return (slider, row);
        }

        (ColorPickerButton, HBoxContainer) AddColor(string key, Func<Color> get, Action<Color> set, PeekColorRole role)
        {
            var btn = new ColorPickerButton
            {
                Color = get(),
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(90, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            var warn = new Label
            {
                Visible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Modulate = new Color(1f, 0.55f, 0.2f),
                CustomMinimumSize = new Vector2(0, 40),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            void Recheck(Color c) { string? m = PeekColorConflict.Check(c, role); warn.Text = m ?? ""; warn.Visible = m != null; }
            btn.ColorChanged += c => { set(c); Recheck(c); Commit(); };
            refreshers.Add(() => { btn.Color = get(); Recheck(get()); });
            Recheck(get());
            var row = NewRow(false);
            row.AddChild(MakeLabel(PeekLoc.T(key), false));
            row.AddChild(btn);
            row.AddChild(warn);
            content.AddChild(row);
            return (btn, row);
        }

        void AddThumbSlider(string key, int min, int max, Func<int> get, Action<int> set, string suffix)
        {
            var (ctl, row) = AddSlider(key, min, max, get, set, suffix, indent: true);
            thumbControls.Add(ctl); thumbRows.Add(row);
        }

        // —— 标题 ——
        var header = new Label
        {
            Name = "PeekHeader",
            Text = PeekLoc.T("header"),
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 52),
        };
        header.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(header);

        // —— 通用 ——
        AddToggle("master",      () => PeekSettings.MasterEnabled,     v => PeekSettings.MasterEnabled = v);
        AddSlider("cardScale",   10, 100, () => PeekSettings.CardScalePct,  v => PeekSettings.CardScalePct = v,  "%");
        AddSlider("hideDelay",   0, 2000, () => PeekSettings.HideDelayMs,   v => PeekSettings.HideDelayMs = v,   " ms");
        AddSlider("cardSpacing", 0, 40,   () => PeekSettings.CardSpacingPx, v => PeekSettings.CardSpacingPx = v, " px");
        AddSlider("overlayGap",  0, 120,  () => PeekSettings.OverlayGapPx,  v => PeekSettings.OverlayGapPx = v,  " px");
        AddToggle("purpleGlow",  () => PeekSettings.PurpleGlowEnabled, v => PeekSettings.PurpleGlowEnabled = v);
        AddColor("warningColor",  () => PeekSettings.WarningColor,  v => PeekSettings.WarningColor = v,  PeekColorRole.CardHighlight);
        AddColor("bodyGlowColor", () => PeekSettings.BodyGlowColor, v => PeekSettings.BodyGlowColor = v, PeekColorRole.BodyGlow);

        // —— 缩略图（父开关 + 子项） ——
        AddToggle("thumbsEnabled",
            () => PeekSettings.ThumbnailsEnabled,
            v => { PeekSettings.ThumbnailsEnabled = v; UpdateThumbEnabled(); });
        AddThumbSlider("thumbHeight",  40, 100, () => PeekSettings.ThumbnailHeightPct,  v => PeekSettings.ThumbnailHeightPct = v,  "%");
        AddThumbSlider("thumbSpacing", -30, 20, () => PeekSettings.ThumbnailSpacingPx,  v => PeekSettings.ThumbnailSpacingPx = v,  " px");
        AddThumbSlider("thumbOpacity", 20, 100, () => PeekSettings.ThumbnailOpacityPct, v => PeekSettings.ThumbnailOpacityPct = v, "%");
        {
            var (ctl, row) = AddToggle("thumbGlow",
                () => PeekSettings.ThumbnailShowGlow, v => PeekSettings.ThumbnailShowGlow = v, indent: true);
            thumbControls.Add(ctl); thumbRows.Add(row);
        }

        // —— 恢复默认 ——
        var resetBtn = new Button
        {
            Text = PeekLoc.T("reset"),
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        resetBtn.Pressed += () =>
        {
            PeekSettings.ResetToDefaults();
            foreach (Action r in refreshers) r();
            UpdateThumbEnabled();
            Commit();
        };
        content.AddChild(resetBtn);

        UpdateThumbEnabled();
        // RefreshSize 为私有方法：经 Godot 方法分发调用（不依赖 C# 可见性），延后到布局完成后执行
        panel.CallDeferred(NSettingsPanel.MethodName.RefreshSize);
    }

    private static Label MakeLabel(string text, bool indent) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        AutowrapMode = TextServer.AutowrapMode.Off,
        CustomMinimumSize = new Vector2(indent ? 332 : 360, 40),
    };

    private static HBoxContainer NewRow(bool indent)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 44) };
        row.AddThemeConstantOverride("separation", 12);
        if (indent) row.AddChild(new Control { CustomMinimumSize = new Vector2(28, 0) });
        return row;
    }

    private static void SetRowEnabled(Control inner, Control row, bool on)
    {
        switch (inner)
        {
            case BaseButton bb: bb.Disabled = !on; break;
            case Slider sl:     sl.Editable = on;  break;
        }
        row.Modulate = on ? Colors.White : new Color(1f, 1f, 1f, 0.4f);
    }
}
