using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace MultiplayerPeeking;

// 通过反射对接 ModConfig（软依赖，参考 MultiplayerQuickSL 的做法）：把本 mod 的所有设置
// 注册到游戏「设置」页的「模组配置」标签下。ModConfig 不存在时静默跳过，mod 仍按
// PeekSettings 的 cfg / 默认值正常工作。值变更经 OnChanged 同步进 PeekSettings 并即时生效。
//
// ModConfig 控件类型无「颜色」，故两个颜色用十六进制 TextInput（RRGGBBAA）。
internal static class PeekModConfigBridge
{
    private const string ModId = "MultiplayerPeeking";

    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configTypeEnum;

    internal static void DeferredRegister()
    {
        // 延后一帧：等所有 mod（含 ModConfig）加载完，其类型才可被反射发现
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame += OnNextFrame;
    }

    private static void OnNextFrame()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame -= OnNextFrame;
        if (Detect())
            Register();
    }

    private static bool Detect()
    {
        try
        {
            Type[] types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .ToArray();
            _apiType        = types.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType      = types.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configTypeEnum = types.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            return _apiType != null && _entryType != null && _configTypeEnum != null;
        }
        catch { return false; }
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            Array entries = BuildEntries();
            var names = new Dictionary<string, string> { ["en"] = "Multiplayer Peeking", ["zhs"] = "队友手牌透视" };

            MethodInfo reg = _apiType!.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == "Register")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

            object?[] args = reg.GetParameters().Length == 4
                ? new object?[] { ModId, names["en"], names, entries }
                : new object?[] { ModId, names["en"], entries };
            reg.Invoke(null, args);
            Log.Info("[MultiplayerPeeking] registered config with ModConfig");

            // 注入「可预览色块矩阵」到本 mod 区块（Harmony patch ModConfig 的面板构建）
            PeekColorGrid.TryPatch();
        }
        catch (Exception e)
        {
            Log.Warn($"[MultiplayerPeeking] ModConfig register failed: {e}");
        }
    }

    // 每次控件变更：写回 PeekSettings 的 cfg（合并到下一帧）+ 触发实时重建（ModConfig 另行持久化它自己的 json）
    private static void Commit() { PeekSettings.ScheduleSave(); PeekSettings.NotifyChanged(); }

    private static MethodInfo? _setValueMethod;

    // 供开发面板用：把值经 ModConfig 的 SetValue 推下去（= 在控件里改值，会触发该词条 OnChanged →
    // 写 PeekSettings + Commit + ModConfig 持久化）。返回 false 表示未装 ModConfig，调用方应回退为直接写 PeekSettings。
    internal static bool TrySetValue(string key, object value)
    {
        if (_apiType == null) return false;
        try
        {
            _setValueMethod ??= _apiType.GetMethod("SetValue", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string), typeof(string), typeof(object) }, null);
            if (_setValueMethod == null) return false;
            _setValueMethod.Invoke(null, new object[] { ModId, key, value });
            RefreshModConfigUi();   // 若 ModConfig 菜单恰好开着，让其控件重绘到最新值（关闭时为安全空操作）
            return true;
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] dev SetValue failed: {e}"); return false; }
    }

    private static Type? _injectorType;
    private static MethodInfo? _refreshUiMethod;

    // 让已打开的 ModConfig 设置页重绘到最新值。SetValue/ResetToDefaults 自身不会调 RefreshUI，
    // 故开发面板改值/重置后需手动触发。菜单关闭时 RefreshUI 只会修剪失效 WeakRef、不重建任何容器，安全。
    private static void RefreshModConfigUi()
    {
        try
        {
            _injectorType ??= AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.FullName == "ModConfig.SettingsTabInjector");
            _refreshUiMethod ??= _injectorType?.GetMethod("RefreshUI",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            _refreshUiMethod?.Invoke(null, null);
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] RefreshUI failed: {e}"); }
    }

    // 供开发面板用：触发 ModConfig 自身的「恢复默认」（与设置页里点重置等效）。返回 false 表示未装 ModConfig。
    internal static bool TryReset()
    {
        if (_apiType == null) return false;
        try
        {
            Type? mgr = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.FullName == "ModConfig.ModConfigManager");
            MethodInfo? m = mgr?.GetMethod("ResetToDefaults",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            if (m == null) return false;
            m.Invoke(null, new object[] { ModId });
            RefreshModConfigUi();   // ModConfig 自身的重置按钮也是在 ResetToDefaults 后另调 RefreshUI
            return true;
        }
        catch (Exception e) { Log.Warn($"[MultiplayerPeeking] dev reset failed: {e}"); return false; }
    }

    private static Array BuildEntries()
    {
        var list = new List<object>
        {
            // 注意：DefaultValue 传【出厂默认】（PeekSettings.DefaultXxx），这样 ModConfig 的「恢复默认」
            // 才会还原到真正的默认值；显示值仍取 ModConfig 已存的 json（=上次保存值）。
            Toggle("MasterEnabled", "Enable Multiplayer Peeking", "启用队友手牌透视（主开关）",
                PeekSettings.DefaultMasterEnabled, v => { PeekSettings.MasterEnabled = v; Commit(); PeekColorGrid.SetMasterFold(v); }),

            // 设置面板开关快捷键（玩家可改）；改后做冲突检测并把警告写到本行下方
            KeyBind("PanelHotkey", "Settings panel hotkey", "设置面板快捷键",
                PeekSettings.DefaultPanelHotkey,
                k => { PeekSettings.PanelHotkey = k; Commit(); PeekColorGrid.SetHotkeyWarning(PeekDevPanel.HotkeyConflict(k)); }),

            Separator(),
            Header("Hover overlay", "悬停大图"),
            Slider("CardScalePct", "Hover card size (%)", "悬停卡牌大小 (%)",
                PeekSettings.DefaultCardScalePct, 10, 100, 1, v => { PeekSettings.CardScalePct = v; Commit(); }),
            Slider("HideDelayMs", "Preview hide delay (ms)", "悬停预览延迟消失 (ms)",
                PeekSettings.DefaultHideDelayMs, 0, 2000, 50, v => { PeekSettings.HideDelayMs = v; Commit(); }),
            Slider("CardSpacingPx", "Hover card spacing (px)", "悬停卡牌间隔 (px)",
                PeekSettings.DefaultCardSpacingPx, 0, 40, 1, v => { PeekSettings.CardSpacingPx = v; Commit(); }),
            Slider("OverlayGapPx", "Gap from status bar (px)", "与状态栏间距 (px)",
                PeekSettings.DefaultOverlayGapPx, 0, 120, 1, v => { PeekSettings.OverlayGapPx = v; Commit(); }),

            Separator(),
            Header("Card highlight", "卡牌高光"),
            // 卡牌高光开关：关 → 不显示卡牌高光；同时让其下方「颜色 hex 行 + 调色盘」折叠隐藏
            Toggle("CardGlowEnabled", "Card highlight", "启用卡牌高光",
                PeekSettings.DefaultCardGlowEnabled,
                v => { PeekSettings.CardGlowEnabled = v; Commit(); PeekColorGrid.SetCardFold(v); }),
            // 颜色：保留 hex 精调；其下方由 PeekColorGrid 注入「可折叠的色块矩阵」（点击即用、预览、强绑定 hex）
            // 颜色：hex 精调；其下方由 PeekColorGrid 注入「可折叠色块矩阵」，含一个【🌈棱彩】预设（选它=开棱彩、选具体色=关棱彩）
            ColorHex("WarningColor", "Card highlight color (hex)", "卡牌高光颜色 (hex)",
                PeekSettings.DefaultWarningColor, () => PeekSettings.WarningColor, c => { PeekSettings.WarningColor = c; Commit(); }),
            Slider("OverlayGlowRadiusPct", "Hover glow width (%)", "悬停预览光晕宽度 (%)",
                PeekSettings.DefaultOverlayGlowRadiusPct, 0, 100, 1, v => { PeekSettings.OverlayGlowRadiusPct = v; Commit(); }),
            Slider("OverlayGlowOpacityPct", "Hover glow opacity (%)", "悬停预览光晕不透明度 (%)",
                PeekSettings.DefaultOverlayGlowOpacityPct, 0, 100, 1, v => { PeekSettings.OverlayGlowOpacityPct = v; Commit(); }),

            Separator(),
            Header("Body glow", "身体光晕"),
            // 身体光晕开关：关 → 不显示身体光晕；同时让其下方颜色行折叠隐藏
            Toggle("PurpleGlowEnabled", "Body warning glow", "启用身体光晕",
                PeekSettings.DefaultPurpleGlowEnabled,
                v => { PeekSettings.PurpleGlowEnabled = v; Commit(); PeekColorGrid.SetBodyFold(v); }),
            ColorHex("BodyGlowColor", "Body glow color (hex)", "身体光晕颜色 (hex)",
                PeekSettings.DefaultBodyGlowColor, () => PeekSettings.BodyGlowColor, c => { PeekSettings.BodyGlowColor = c; Commit(); }),
            Slider("BodyGlowOpacityPct", "Body glow opacity (%)", "身体光晕不透明度 (%)",
                PeekSettings.DefaultBodyGlowOpacityPct, 0, 100, 1, v => { PeekSettings.BodyGlowOpacityPct = v; Commit(); }),

            Separator(),
            Header("Always-on thumbnails", "常态缩略图"),

            Toggle("ThumbnailsEnabled", "Always-on thumbnails", "常态缩略图显示",
                PeekSettings.DefaultThumbnailsEnabled,
                v => { PeekSettings.ThumbnailsEnabled = v; Commit(); PeekColorGrid.SetThumbnailRowsVisible(v); }),
            Slider("ThumbnailScalePct", "Thumbnail size (%)", "缩略图大小 (%)",
                PeekSettings.DefaultThumbnailScalePct, 5, 40, 1, v => { PeekSettings.ThumbnailScalePct = v; Commit(); }),
            Slider("ThumbnailSpacingPx", "Thumbnail card spacing (px)", "缩略图卡牌间隔 (px)",
                PeekSettings.DefaultThumbnailSpacingPx, 0, 40, 1, v => { PeekSettings.ThumbnailSpacingPx = v; Commit(); }),
            Slider("ThumbnailGapPx", "Thumbnail bar gap (px)", "缩略图与状态栏间距 (px)",
                PeekSettings.DefaultThumbnailGapPx, 0, 120, 1, v => { PeekSettings.ThumbnailGapPx = v; Commit(); }),
            Slider("ThumbnailOpacityPct", "Thumbnail opacity (%)", "缩略图不透明度 (%)",
                PeekSettings.DefaultThumbnailOpacityPct, 20, 100, 1, v => { PeekSettings.ThumbnailOpacityPct = v; Commit(); }),
            Separator(),
            Toggle("ThumbnailShowGlow", "Highlight on thumbnails", "缩略图显示高光",
                PeekSettings.DefaultThumbnailShowGlow,
                v => { PeekSettings.ThumbnailShowGlow = v; Commit(); PeekColorGrid.SetThumbGlowFold(v); }),
            Slider("ThumbnailGlowRadiusPct", "Thumb glow width (%)", "缩略图光晕宽度 (%)",
                PeekSettings.DefaultThumbnailGlowRadiusPct, 0, 100, 1, v => { PeekSettings.ThumbnailGlowRadiusPct = v; Commit(); }),
            Slider("ThumbnailGlowOpacityPct", "Thumb glow opacity (%)", "缩略图光晕不透明度 (%)",
                PeekSettings.DefaultThumbnailGlowOpacityPct, 0, 100, 1, v => { PeekSettings.ThumbnailGlowOpacityPct = v; Commit(); }),
        };

        Array arr = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
        return arr;
    }

    /* ---- entry builders (reflection over ModConfig.ConfigEntry) ---- */

    private static object Toggle(string key, string en, string zh, bool def, Action<bool> onChanged) => Entry(e =>
    {
        Set(e, "Key", key); Set(e, "Label", en); Set(e, "Labels", L(en, zh));
        Set(e, "Type", EnumVal("Toggle"));
        Set(e, "DefaultValue", def);
        Set(e, "OnChanged", (Action<object>)(v => onChanged(Convert.ToBoolean(v))));
    });

    private static object Slider(string key, string en, string zh, int def, int min, int max, int step, Action<int> onChanged) => Entry(e =>
    {
        Set(e, "Key", key); Set(e, "Label", en); Set(e, "Labels", L(en, zh));
        Set(e, "Type", EnumVal("Slider"));
        Set(e, "DefaultValue", (float)def);
        Set(e, "Min", (float)min); Set(e, "Max", (float)max); Set(e, "Step", (float)step);
        Set(e, "Format", "F0");
        Set(e, "OnChanged", (Action<object>)(v => onChanged((int)Math.Round(Convert.ToSingle(v)))));
    });

    private static object ColorHex(string key, string en, string zh, Color def,
                                   Func<Color> get, Action<Color> set) => Entry(e =>
    {
        Set(e, "Key", key); Set(e, "Label", en); Set(e, "Labels", L(en, zh));
        Set(e, "Type", EnumVal("TextInput"));
        Set(e, "DefaultValue", def.ToHtml(true));    // 出厂默认色（恢复默认用）
        Set(e, "MaxLength", 9);
        Set(e, "Placeholder", get().ToHtml(true));
        Set(e, "OnChanged", (Action<object>)(v =>
        {
            string s = (v?.ToString() ?? "").Trim().TrimStart('#');
            if (Color.HtmlIsValid(s)) set(Color.FromHtml(s));
            else Log.Warn($"[MultiplayerPeeking] invalid hex for {key}: '{s}'");
        }));
    });

    private static object KeyBind(string key, string en, string zh, Key def, Action<Key> onChanged) => Entry(e =>
    {
        Set(e, "Key", key); Set(e, "Label", en); Set(e, "Labels", L(en, zh));
        Set(e, "Type", EnumVal("KeyBind"));
        Set(e, "DefaultValue", (long)def);     // ModConfig 用 long 存键码
        Set(e, "OnChanged", (Action<object>)(v => onChanged((Key)Convert.ToInt64(v))));
    });

    private static object Header(string en, string zh) => Entry(e =>
    {
        Set(e, "Label", en); Set(e, "Labels", L(en, zh));
        Set(e, "Type", EnumVal("Header"));
    });

    private static object Separator() => Entry(e => Set(e, "Type", EnumVal("Separator")));

    /* ---- reflection helpers ---- */

    private static object Entry(Action<object> configure)
    {
        object e = Activator.CreateInstance(_entryType!)!;
        configure(e);
        return e;
    }

    private static void Set(object o, string name, object? value)
        => o.GetType().GetProperty(name)?.SetValue(o, value);

    private static Dictionary<string, string> L(string en, string zh)
        => new() { ["en"] = en, ["zhs"] = zh };

    private static object EnumVal(string name) => Enum.Parse(_configTypeEnum!, name);
}
