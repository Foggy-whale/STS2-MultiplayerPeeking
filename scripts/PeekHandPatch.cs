using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace MultiplayerPeeking;

// 战斗开始：直接订阅 Hand.ContentsChanged，任何手牌变化都会触发
[HarmonyPatch(typeof(NMultiplayerPlayerState), "OnCombatSetUp")]
public static class PeekHandCombatSetUpPatch
{
    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        PeekHandPatch.SubscribeHandEvents(__instance);
        PeekHandPatch.UpdatePurpleGlow(__instance);
        PeekHandPatch.RebuildThumbnail(__instance);
    }
}

// 战斗结束前：取消订阅 + 清理 overlay/缩略图（Prefix 确保 PlayerCombatState 还未清空）
[HarmonyPatch(typeof(NMultiplayerPlayerState), "OnCombatEnded")]
public static class PeekHandCombatEndedPatch
{
    public static void Prefix(NMultiplayerPlayerState __instance)
    {
        PeekHandPatch.ClearState(__instance);
        PeekHandPatch.RestoreAllPrismatic();   // 兜底清扫残留棱彩卡
    }
}

// 退回主菜单/断线退出也会销毁状态栏，但不一定触发 CombatEnded。
[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._ExitTree))]
public static class PeekHandStateExitedPatch
{
    public static void Prefix(NMultiplayerPlayerState __instance) => PeekHandPatch.ClearState(__instance);
}

// 局内选牌界面（生存者/工具箱/选择悖论/牌堆查看等）的「窥视」按钮：按下时选牌网格被藏起、
// 战斗视图重新可见，但选牌屏仍在 NOverlayStack 上（GetCurrentScreen 仍返回它）。而且「开始窥视」
// 不会触发 ActiveScreenContext.Update（只有取消窥视才触发）→ 我们的门控收不到通知。
// 故在此对 SetPeeking 补一发刷新，让缩略图/预览随窥视开合而显隐
//（IsCombatViewActive 已把「正在窥视的选牌屏」视作战斗视图可见）。
[HarmonyPatch(typeof(NPeekButton), nameof(NPeekButton.SetPeeking), new[] { typeof(bool) })]
public static class PeekPeekButtonPatch
{
    public static void Postfix() => PeekHandPatch.OnScreenChanged();
}

[HarmonyPatch(typeof(NMultiplayerPlayerState), "UpdateHighlightedState")]
public static class PeekHandPatch
{
    // NCard.defaultSize = (300, 422)
    private const float CardNaturalWidth  = 300f;
    private const float CardNaturalHeight = 422f;

    // 状态栏几何（来自场景 ui/multiplayer_player_state.tscn）：行高 64px。
    private const float BarHeight = 64f;

    // 内容右缘（卡条锚点）改为【动态测量】：常规角色 ≈261，但储君会显示「辉星」(StarCount) 那一栏
    // → TopInfoContainer 更宽，固定值会盖住手牌数(CardCount)。取所有可见信息栏右缘的最大值，
    // 容器未就绪(布局未完成)时回退到此常量。ContentRightMargin = 原 267-261 的余量。
    private const float BarContentRightFallback = 267f;
    private const float ContentRightMargin      = 6f;
    private static readonly string[] _infoColumns =
        { "TopInfoContainer", "%EnergyCountContainer", "%StarCountContainer", "%CardCountContainer" };

    // 所有可调参数现由 PeekSettings 提供（在使用处直接读取，不缓存，以便即时生效）

    private static readonly Dictionary<NMultiplayerPlayerState, Control> _overlays = [];

    // 常态缩略图：每个远程队友一条，独立于 hover 的 _overlays
    private static readonly Dictionary<NMultiplayerPlayerState, Control> _thumbnails = [];

    // 每个 state 最多一个挂起的隐藏计时器
    private static readonly Dictionary<NMultiplayerPlayerState, SceneTreeTimer> _hideTimers = [];

    // 鼠标当前是否停在 overlay 上（阻止计时器触发隐藏）
    private static readonly HashSet<NMultiplayerPlayerState> _overlayHovered = [];

    // 当前被悬停（高亮）的队友集合；只要非空，就隐藏【所有】缩略图（不止被悬停那一个）
    private static readonly HashSet<NMultiplayerPlayerState> _highlightedStates = [];

    // 长按中键临时隐藏：反映中键此刻是否按住（按住=隐藏所有缩略图，松开=恢复）。并入可见性判据。
    private static bool _hideHeld;

    // 对角色 Body 施加紫色光效
    private static readonly Dictionary<NMultiplayerPlayerState, Node2D> _tintedBodies = [];

    // 保存实际订阅的牌堆：退出/重连时 PlayerCombatState 可能已被替换或清空。
    private static readonly Dictionary<NMultiplayerPlayerState, (CardPile Hand, Action Handler)> _handHandlers = [];

    // 棱彩高光：被换成共享彩虹材质的卡 → 其原始(每卡唯一)材质。归还对象池前据此还原，
    // 否则池子复用到真实手牌会让真手牌变彩虹（NCard 池可能与游戏共用）。
    private static readonly Dictionary<NCard, Material> _origHighlightMat = [];

    // 紫色警示色（卡牌高光）与身体光晕色现由 PeekSettings.WarningColor / BodyGlowColor 提供，
    // 与游戏内置的 gold/red/playableColor 共用同一套 NCardHighlight shader 机制

    // 通过 DynamicVars 检测卡牌是否包含 PowerVar<WeakPower> / PowerVar<VulnerablePower>
    private static readonly Type _weakVarType       = typeof(PowerVar<WeakPower>);
    private static readonly Type _vulnerableVarType = typeof(PowerVar<VulnerablePower>);
    // 「减少敌人力量」的 DynamicVar key（不同卡不同）：尖啸/裂伤=StrengthLoss；命运同担=EnemyStrengthLoss。
    // 只算减敌；命运同担的 PlayerStrengthLoss（削自己）不计入。
    private static readonly string[] _enemyStrengthLossKeys = { "StrengthLoss", "EnemyStrengthLoss" };

    // 警示卡判定（身体光晕 UpdatePurpleGlow 与卡牌紫光 ApplyHighlight 共用同一标准）：
    //   联机专属卡 / 减敌力量 / 施加虚弱或易伤
    internal static bool IsWarningCard(CardModel card)
        => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly
        || _enemyStrengthLossKeys.Any(k => card.DynamicVars.ContainsKey(k))
        || card.DynamicVars.Values.Any(v => v.GetType() == _weakVarType
                                         || v.GetType() == _vulnerableVarType);

    private static readonly FieldInfo _isHighlightedField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_isHighlighted");

    // Harmony Postfix：在 UpdateHighlightedState 执行完毕后运行
    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        if (!PeekSettings.MasterEnabled) return;

        bool highlighted = _isHighlightedField.GetValue(__instance) is true;

        // 「自己」（本地玩家）的状态栏也在容器里：悬停自己时不应隐藏队友缩略图，
        // 只有悬停【远程队友】才触发隐藏。
        bool isLocal = __instance.Player != null && __instance.Player.NetId == LocalContext.NetId;

        if (highlighted)
        {
            _hideTimers.Remove(__instance);
            if (!isLocal)
            {
                _highlightedStates.Add(__instance);
                SetAllThumbnailsVisible(false);    // 悬停远程队友 → 隐藏所有缩略图

                // 互斥：同一时间只显示一个大图 —— 立即隐藏其它所有大图，
                // 优先级高于其「延迟消失」计时器（切换队友时上一个立刻消失，不与新的共存）
                foreach (var other in _overlays.Keys.ToList())
                    if (other != __instance) HideOverlay(other);
            }
            if (!_overlays.ContainsKey(__instance))
                ShowOverlay(__instance);           // ShowOverlay 内部已对本地玩家提前返回
        }
        else
        {
            _highlightedStates.Remove(__instance);

            // 延迟隐藏大图（鼠标在 overlay 上 / 已有计时器时不重复处理）
            if (_overlays.ContainsKey(__instance)
                && !_overlayHovered.Contains(__instance)
                && !_hideTimers.ContainsKey(__instance))
            {
                ScheduleHide(__instance);
            }

            // 仅当无人被悬停【且没有大图还在显示】时才恢复缩略图。
            // 延迟消失期间大图仍在 _overlays 里 → 缩略图保持隐藏，避免两者同时渲染。
            MaybeRestoreThumbnails();
        }

        // 每次 hover 状态变化时同步角色轮廓光晕（独立于 hover，常驻显示）
        UpdatePurpleGlow(__instance);
    }

    /* == 手牌事件订阅 == */

    internal static void SubscribeHandEvents(NMultiplayerPlayerState state)
    {
        UnsubscribeHandEvents(state);
        var player = state.Player;
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        void handler() => OnHandChanged(state);
        var hand = player.PlayerCombatState.Hand;
        hand.ContentsChanged += handler;
        _handHandlers[state] = (hand, handler);
    }

    internal static void UnsubscribeHandEvents(NMultiplayerPlayerState state)
    {
        if (_handHandlers.Remove(state, out var subscription))
            subscription.Hand.ContentsChanged -= subscription.Handler;
    }

    internal static void ClearState(NMultiplayerPlayerState state)
    {
        UnsubscribeHandEvents(state);
        _hideTimers.Remove(state);
        _overlayHovered.Remove(state);
        _highlightedStates.Remove(state);
        HideOverlay(state);
        DestroyThumbnail(state);
        RemovePurpleGlow(state);
    }

    private static void ScheduleHide(NMultiplayerPlayerState state)
    {
        var tree = state.GetTree();
        if (tree == null) { HideOverlay(state); return; }

        var timer = tree.CreateTimer(PeekSettings.HideDelaySec);
        _hideTimers[state] = timer;
        timer.Timeout += () =>
        {
            // 移出字典即取消；旧计时器不能清除后来的计时器，也不能访问已释放的状态栏。
            if (!_hideTimers.TryGetValue(state, out var current) || current != timer) return;
            _hideTimers.Remove(state);
            if (!GodotObject.IsInstanceValid(state) || !state.IsInsideTree()) return;
            if (_isHighlightedField.GetValue(state) is not true && !_overlayHovered.Contains(state))
                HideOverlay(state);
        };
    }

    /* == 紫色光效 == */

    private static bool HasPurpleCards(NMultiplayerPlayerState state)
    {
        var player = state.Player;
        if (player?.PlayerCombatState == null) return false;
        if (player.NetId == LocalContext.NetId) return false;
        return player.PlayerCombatState.Hand.Cards.Any(IsWarningCard);
    }

    internal static void UpdatePurpleGlow(NMultiplayerPlayerState state)
    {
        bool want = PeekSettings.MasterEnabled && PeekSettings.PurpleGlowEnabled && HasPurpleCards(state);
        if (want && !_tintedBodies.ContainsKey(state))
            ShowPurpleGlow(state);
        else if (!want && _tintedBodies.ContainsKey(state))
            RemovePurpleGlow(state);
        else if (want && _tintedBodies.TryGetValue(state, out var body) && GodotObject.IsInstanceValid(body))
        {
            if (!PeekSettings.BodyPrismaticGlow) body.Modulate = BodyTint(PeekSettings.BodyGlowColor); // 颜色/不透明度可能已变（棱彩时由 tick 接管）
        }
    }

    // 身体染色强度：不透明度 = 白 ↔ 光晕色 的 lerp（alpha 保持 1，身体不透明）
    private static Color BodyTint(Color glow)
        => Colors.White.Lerp(new Color(glow.R, glow.G, glow.B, 1f), PeekSettings.BodyGlowOpacity);

    // 身体光晕棱彩：随时间把所有被染色身体的 Modulate 在彩虹色相间流动（身体是 sprite，无环 shader，故用时间循环）
    internal static void BodyPrismaticTick()
    {
        if (!PeekSettings.MasterEnabled || !PeekSettings.PurpleGlowEnabled || !PeekSettings.BodyPrismaticGlow) return;
        if (_tintedBodies.Count == 0) return;
        float hue = ((float)Time.GetTicksMsec() / 1000f * 0.25f) % 1f;
        Color c = BodyTint(Color.FromHsv(hue, 0.85f, 1f));
        foreach (var body in _tintedBodies.Values)
            if (GodotObject.IsInstanceValid(body)) body.Modulate = c;
    }

    private static void ShowPurpleGlow(NMultiplayerPlayerState state)
    {
        NCreature? nCreature = NCombatRoom.Instance?.GetCreatureNode(state.Player?.Creature);
        if (nCreature == null) return;

        Node2D body = nCreature.Body;
        body.Modulate = BodyTint(PeekSettings.BodyGlowColor);

        nCreature.TreeExited += () =>
        {
            if (_tintedBodies.Remove(state, out var b) && GodotObject.IsInstanceValid(b))
                b.Modulate = Colors.White;
        };

        _tintedBodies[state] = body;
    }

    private static void RemovePurpleGlow(NMultiplayerPlayerState state)
    {
        if (_tintedBodies.Remove(state, out var body) && GodotObject.IsInstanceValid(body))
            body.Modulate = Colors.White;
    }

    // 状态栏右缘（state 局部 X）：完全对齐游戏自身 UpdateSelectionReticleWidth 的算法 ——
    // max(最右可见信息栏右缘, 血条 HpBarContainer 右缘) + 6。
    // 关键：血条宽度按 HP 比例定（UpdateHealthBarWidth），战士等高 HP 角色【血条比 3/5 信息栏更宽】，
    // 其状态栏真实右缘是血条而非信息栏；只量信息栏会让卡条没跟上宽血条。储君/低 HP 角色信息栏更宽，max 仍取信息栏、不变。
    // 容器布局未完成时（Size 仍为 0）回退到固定值，由后续重建/延后一帧校正。
    private static float ContentRightFor(NMultiplayerPlayerState state)
    {
        float right = 0f;
        foreach (string name in _infoColumns)
        {
            var c = state.GetNodeOrNull<Control>(name);
            if (c != null && GodotObject.IsInstanceValid(c) && c.Visible && c.Size.X > 1f)
            {
                float edge = c.GlobalPosition.X + c.Size.X - state.GlobalPosition.X;   // → state 局部右缘
                if (edge > right) right = edge;
            }
        }
        // 并入血条右缘（与游戏算法一致）
        var hp = state.GetNodeOrNull<NHealthBar>("%HealthBar");
        Control? bar = hp != null && GodotObject.IsInstanceValid(hp) ? hp.HpBarContainer : null;
        if (bar != null && GodotObject.IsInstanceValid(bar) && bar.Size.X > 1f)
        {
            float edge = bar.GlobalPosition.X + bar.Size.X - state.GlobalPosition.X;
            if (edge > right) right = edge;
        }
        return right > 1f ? right + ContentRightMargin : BarContentRightFallback;
    }

    private static void ShowOverlay(NMultiplayerPlayerState state)
    {
        if (!PeekSettings.MasterEnabled) return;
        if (_hideHeld) return;              // 长按临时隐藏期间：不弹大图（松开时若仍在悬停会由 HoldHideTick 补弹）
        if (!IsCombatViewActive()) return;  // 只在战斗视图弹大图（防补弹/直调把大图画到地图/牌堆之上）

        Player player = state.Player;

        // 仅在战斗中、且只对远程玩家生效
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        var handCards = player.PlayerCombatState.Hand.Cards;
        if (handCards.Count == 0) return;

        var overlay = new Control();
        overlay.Name = "PeekHandOverlay";
        overlay.MouseFilter = Control.MouseFilterEnum.Pass;

        float scale = PeekSettings.CardScale;
        var cardNodes = new List<NCard>();
        float scaledHalfWidth = CardNaturalWidth * scale / 2f;
        float xOffset = scaledHalfWidth;
        foreach (CardModel card in handCards)
        {
            NCard? cardNode = NCard.Create(card);
            if (cardNode == null) continue;

            cardNode.Scale    = new Vector2(scale, scale);
            cardNode.Position = new Vector2(xOffset, 0f);
            cardNode.MouseFilter = Control.MouseFilterEnum.Ignore;
            overlay.AddChild(cardNode);
            cardNodes.Add(cardNode);

            xOffset += CardNaturalWidth * scale + PeekSettings.CardSpacingPx;
        }

        // 鼠标进入 overlay 区域时取消挂起的隐藏计时器
        overlay.MouseEntered += () =>
        {
            _overlayHovered.Add(state);
            _hideTimers.Remove(state);
        };
        // 鼠标离开 overlay 区域时启动隐藏计时器
        overlay.MouseExited += () =>
        {
            _overlayHovered.Remove(state);
            bool highlighted = _isHighlightedField.GetValue(state) is true;
            if (!highlighted && _overlays.ContainsKey(state) && !_hideTimers.ContainsKey(state))
            {
                ScheduleHide(state);
            }
        };

        // 先挂载到场景树，使卡牌节点进入 Ready 状态
        Node uiParent = (Node?)NRun.Instance?.GlobalUi ?? state.GetParent();
        uiParent.AddChild(overlay);

        // 节点 Ready 后再调用 UpdateVisuals，否则内部 IsNodeReady() 检查会直接跳过
        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            if (PeekSettings.CardGlowEnabled) ApplyHighlight(cardNode);
            else cardNode.CardHighlight.AnimHideInstantly();
        }

        // 延后一帧设置位置（此时状态栏布局已完成 → ContentRightFor 能读到真实内容宽度，含储君的辉星栏）
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(overlay) || !overlay.IsInsideTree()
                || !GodotObject.IsInstanceValid(state) || !state.IsInsideTree()) return;
            overlay.GlobalPosition = new Vector2(
                state.GlobalPosition.X + ContentRightFor(state) + PeekSettings.OverlayGapPx,
                state.GlobalPosition.Y + BarHeight / 2f   // 卡牌 y=0 → 从状态栏中线向下展开（大图保持原样）
            );
        }).CallDeferred();

        _overlays[state] = overlay;
    }

    // 优先级（高→低）：紫 > 红 > 金
    //   紫：见 IsWarningCard —— 施加虚弱/易伤 + 减敌力量 + 联机专属卡
    //   红：游戏原生 ShouldGlowRed（Osty 角色相关、Enthralled 诅咒等）
    //   金：游戏原生 ShouldGlowGold（特殊触发条件满足，仅可打出时显示）
    private static void ApplyHighlight(NCard cardNode, bool thumbnail = false)
    {
        // 防御：高光节点未就绪/脱离场景树时，CreateTween 会失败、留下空高光；就绪后由重建/手牌变化再套用。
        if (cardNode.CardHighlight == null || !cardNode.IsNodeReady()) return;

        var model = cardNode.Model;
        if (model == null) return;

        bool isPurple = IsWarningCard(model);
        bool isRed    = model.ShouldGlowRed;
        bool isGold   = model.CanPlay() && model.ShouldGlowGold;

        if (isRed || isPurple || isGold)
        {
            // 光晕半径(环宽) + 不透明度：大图 / 缩略图各自独立
            float radius  = thumbnail ? PeekSettings.ThumbnailGlowRadius  : PeekSettings.OverlayGlowRadius;
            float opacity = thumbnail ? PeekSettings.ThumbnailGlowOpacity : PeekSettings.OverlayGlowOpacity;

            // 棱彩色：仅替代【紫色警示光】（作为「卡牌高光色」的彩虹预设；红/金保留原生指示）
            if (isPurple && PeekSettings.PrismaticGlow)
            {
                ApplyPrismatic(cardNode, radius, opacity);
                return;
            }

            Color c = isPurple ? PeekSettings.WarningColor : isRed ? NCardHighlight.red : NCardHighlight.gold;
            // 不透明度：缩放 rgb（blend_add 下=调亮度），alpha 保留以维持环形 SDF 形状
            cardNode.CardHighlight.Modulate = new Color(c.R * opacity, c.G * opacity, c.B * opacity, c.A);
            if (cardNode.CardHighlight.Material is ShaderMaterial sm)
                sm.SetShaderParameter("width", radius);   // 光晕半径（环宽）；ripple 仍由 TIME 持续动
        }
        else cardNode.CardHighlight.AnimHideInstantly();
    }

    /* == 棱彩色（流动彩虹）高光 == */

    // 只取卡牌【轮廓那一圈】（SDF 内缘细环），环上颜色按绕中心角度上色、随时间顺时针流动。blend_add 与原高光一致。
    // width=光晕半径(环宽)，glow_alpha=不透明度（大图/缩略图各自从设置传入）。
    private const string PrismaticShaderCode = @"
shader_type canvas_item;
render_mode blend_add;

uniform float width = 0.075;
uniform float glow_alpha = 1.0;
uniform float hue_speed = 0.15;
uniform float hue_turns = 1.0;

vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

void fragment() {
    float a = smoothstep(1.0 - width, 1.0 + width, COLOR.a);   // 只取轮廓那一圈
    vec2 p = UV - vec2(0.5);
    float ang = atan(p.y, p.x);
    float hue = fract(ang / 6.2831853 * hue_turns - TIME * hue_speed);
    vec3 rainbow = hsv2rgb(vec3(hue, 1.0, 1.0));
    COLOR = vec4(rainbow * glow_alpha, a);   // glow_alpha=不透明度（blend_add 下调亮度）
}
";

    private static Shader? _prismaticShader;
    private static readonly Dictionary<(float r, float o), ShaderMaterial> _prismaticMats = new();
    private static ShaderMaterial PrismaticMaterial(float radius, float opacity)
    {
        _prismaticShader ??= new Shader { Code = PrismaticShaderCode };
        var key = (radius, opacity);
        if (!_prismaticMats.TryGetValue(key, out var mat))
        {
            mat = new ShaderMaterial { Shader = _prismaticShader };
            mat.SetShaderParameter("width", radius);
            mat.SetShaderParameter("glow_alpha", opacity);   // 同(半径,不透明度)多卡共用一份
            _prismaticMats[key] = mat;
        }
        return mat;
    }

    private static bool IsPrismaticMat(Material? m) => m is ShaderMaterial sm && _prismaticMats.ContainsValue(sm);

    // 注：换材质后 NCardHighlight._shaderMaterial 仍指向被换下的原始材质（已不生效），
    // 故 Anim*/SetShaderParameter 对彩虹无影响；棱彩卡只能经 RestoreHighlight+池回收 或整段重建退出。
    private static void ApplyPrismatic(NCard cardNode, float radius, float opacity)
    {
        var hl = cardNode.CardHighlight;
        var orig = hl.Material;
        // 防再次中毒：绝不把共享彩虹材质当成「原始材质」存表（否则还原变 no-op，彩虹永久卡住）
        if (!IsPrismaticMat(orig) && !_origHighlightMat.ContainsKey(cardNode))
            _origHighlightMat[cardNode] = orig;   // 记住原(每卡唯一)材质，归还池子前还原
        hl.Material  = PrismaticMaterial(radius, opacity);
        hl.Modulate  = Colors.White;                      // 形状由 shader 计算的环形 alpha 决定
    }

    // 归还对象池前还原被换过的高光材质（防止池子复用到真实手牌时把真手牌也变彩虹）。
    // 返回 true=该卡可安全归还对象池（已成功还原 或 本就非棱彩卡）；false=失效卡，调用方勿入池。
    private static bool RestoreHighlight(NCard cardNode)
    {
        if (!_origHighlightMat.TryGetValue(cardNode, out var mat)) return true;   // 非棱彩卡
        if (GodotObject.IsInstanceValid(cardNode) && cardNode.CardHighlight != null)
        {
            cardNode.CardHighlight.Material = mat;   // 先还原，成功后再移除（顺序很重要）
            _origHighlightMat.Remove(cardNode);
            return true;
        }
        _origHighlightMat.Remove(cardNode);          // 失效卡：清表；绝不归还池子
        return false;
    }

    // 兜底清扫：把残留的棱彩卡全部还原（用于整段重建 / 战斗结束，防止漏网或表内 stale key）
    internal static void RestoreAllPrismatic()
    {
        foreach (var card in _origHighlightMat.Keys.ToList())
            RestoreHighlight(card);
    }

    internal static void HideOverlay(NMultiplayerPlayerState state)
    {
        _hideTimers.Remove(state);
        _overlayHovered.Remove(state);
        if (!_overlays.Remove(state, out var overlay)) return;

        // 先把卡牌从 overlay 摘除，再归还对象池；
        // 否则 overlay.QueueFree() 会释放仍被 pool 引用的节点
        var cards = new List<NCard>();
        foreach (Node child in overlay.GetChildren())
        {
            if (child is NCard card)
                cards.Add(card);
        }
        foreach (NCard card in cards)
        {
            bool safe = RestoreHighlight(card);   // 还原棱彩材质；失败=失效卡，不入池
            if (!GodotObject.IsInstanceValid(card)) continue;
            overlay.RemoveChild(card);
            if (safe) NodePool.Free(card);
        }

        overlay.QueueFree();

        // 大图真正消失后，若已无人悬停、也无其它大图 → 才恢复缩略图
        MaybeRestoreThumbnails();
    }

    /* == 手牌变化 == */

    private static void OnHandChanged(NMultiplayerPlayerState state)
    {
        UpdatePurpleGlow(state);

        if (PeekSettings.ThumbnailsEnabled)
            RebuildThumbnail(state);

        // 若当前正显示该队友的大图，则一并重建（手牌已变）
        if (_overlays.ContainsKey(state))
        {
            HideOverlay(state);
            ShowOverlay(state);
        }
    }

    /* == 常态缩略图 == */

    // 缩略图 = 大图的等比缩小版：完全相同的布局/锚点，只是 scale = 大图缩放 × 相对比例
    internal static void RebuildThumbnail(NMultiplayerPlayerState state)
    {
        DestroyThumbnail(state);

        if (!PeekSettings.MasterEnabled || !PeekSettings.ThumbnailsEnabled) return;

        Player player = state.Player;
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        var handCards = player.PlayerCombatState.Hand.Cards;
        if (handCards.Count == 0) return;

        // 缩略图卡牌布局沿用大图的对齐方式（已验证）；唯一不同：挂在 state 下用局部坐标（#3 跳位的修法，不能回退）。
        // 间距已与大图【解耦】：卡牌间隔/与状态栏间距各用缩略图自己的设置（绝对 px）。
        float scale   = PeekSettings.ThumbnailScalePct / 100f;
        float spacing = PeekSettings.ThumbnailSpacingPx;      // 缩略图卡牌间隔（绝对 px，独立于大图）

        var strip = new Control { Name = "PeekThumbnail", MouseFilter = Control.MouseFilterEnum.Ignore };
        strip.Modulate = new Color(1f, 1f, 1f, PeekSettings.ThumbnailOpacity);

        var cardNodes = new List<NCard>();
        float scaledHalfWidth = CardNaturalWidth * scale / 2f;   // 与大图一致
        float xOffset = scaledHalfWidth;
        foreach (CardModel card in handCards)
        {
            NCard? cardNode = NCard.Create(card);
            if (cardNode == null) continue;

            cardNode.Scale    = new Vector2(scale, scale);
            cardNode.Position = new Vector2(xOffset, 0f);        // 与大图完全相同
            cardNode.MouseFilter = Control.MouseFilterEnum.Ignore;
            strip.AddChild(cardNode);
            cardNodes.Add(cardNode);

            xOffset += CardNaturalWidth * scale + spacing;       // 与大图完全相同
        }

        // 挂在 state 下，局部坐标 = 「内容右缘 + 间距」「状态栏中线」。内容右缘动态测量（含储君辉星栏）。
        state.AddChild(strip);
        strip.Position = new Vector2(ContentRightFor(state) + PeekSettings.ThumbnailGapPx, BarHeight / 2f);

        // 状态栏布局可能在本帧尚未完成（储君的辉星栏 Size 还是 0）→ 延后一帧按真实宽度再校正一次
        var capturedStrip = strip;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(capturedStrip) && capturedStrip.IsInsideTree()
                && _thumbnails.TryGetValue(state, out var cur) && cur == capturedStrip)
                capturedStrip.Position = new Vector2(ContentRightFor(state) + PeekSettings.ThumbnailGapPx, BarHeight / 2f);
        }).CallDeferred();

        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            // 缩略图高光只由「缩略图显示高光」自身决定（与大图「卡牌高光」解耦）。半径/不透明度走缩略图自己的设置。
            if (PeekSettings.ThumbnailShowGlow) ApplyHighlight(cardNode, thumbnail: true);
            else cardNode.CardHighlight.AnimHideInstantly();
        }

        _thumbnails[state] = strip;

        // 可见性单一真源：与 MaybeRestoreThumbnails 判据完全一致——有任一队友被悬停、
        // 或仍有大图存在、或战斗视图被地图/卡组/牌堆等遮挡时隐藏；否则显示。
        strip.Visible = ShouldThumbnailsShow();
    }

    internal static void DestroyThumbnail(NMultiplayerPlayerState state)
    {
        if (!_thumbnails.Remove(state, out var strip)) return;

        // 先把卡牌摘除并归还对象池，再 QueueFree（与 HideOverlay 一致）
        var cards = new List<NCard>();
        foreach (Node child in strip.GetChildren())
        {
            if (child is NCard card)
                cards.Add(card);
        }
        foreach (NCard card in cards)
        {
            bool safe = RestoreHighlight(card);   // 还原棱彩材质；失败=失效卡，不入池
            if (!GodotObject.IsInstanceValid(card)) continue;
            strip.RemoveChild(card);
            if (safe) NodePool.Free(card);
        }
        strip.QueueFree();
    }

    // 任一队友被悬停时隐藏【所有】缩略图；无人被悬停时全部恢复
    private static void SetAllThumbnailsVisible(bool visible)
    {
        if (!PeekSettings.ThumbnailsEnabled) return;
        foreach (var strip in _thumbnails.Values)
            if (GodotObject.IsInstanceValid(strip)) strip.Visible = visible;
    }

    // 仅当「无人被悬停」「没有大图还在显示」且「战斗视图未被遮挡」时才恢复缩略图
    // （延迟消失期间大图仍在 _overlays 里 → 不恢复，避免大图与缩略图同框）
    // 缩略图「应否显示」的单一真源：无人悬停、无大图、在战斗视图、且未长按隐藏。
    // RebuildThumbnail 与 MaybeRestoreThumbnails 共用，避免判据重复而将来改漏其中一条。
    private static bool ShouldThumbnailsShow()
        => _highlightedStates.Count == 0 && _overlays.Count == 0 && IsCombatViewActive() && !_hideHeld;

    private static void MaybeRestoreThumbnails()
    {
        if (ShouldThumbnailsShow())
            SetAllThumbnailsVisible(true);
    }

    /* == 活动屏幕门控：只在「战斗视图」显示，打开地图/卡组/抽牌堆/弃牌堆等时收起 == */

    // 战斗视图当前是否未被遮挡。ActiveScreenContext 是游戏权威的「当前活动屏幕」解析器，
    // 仅在 capstone/地图/牌堆视图/inspect 等都没打开时才返回 CombatRoom。
    // v0.113 的三类选牌场景都使用唯一节点名 %PeekButton；避免依赖各自的私有字段。
    private static bool IsPeekingSelectionScreen(object? screen)
    {
        if (screen is not (NCardGridSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen))
            return false;
        if (screen is not Control control || !GodotObject.IsInstanceValid(control) || !control.IsInsideTree())
            return false;
        return control.GetNodeOrNull<NPeekButton>("%PeekButton")?.IsPeeking ?? false;
    }

    internal static bool IsCombatViewActive()
    {
        try
        {
            if (NCombatRoom.Instance == null) return false;
            var screen = ActiveScreenContext.Instance?.GetCurrentScreen();
            if (screen is NCombatRoom) return true;
            // 局内选牌界面按下「窥视」按钮 → 选牌网格被藏起、战斗视图可见 → 允许预览队友手牌。
            // 未窥视（网格覆盖战斗）时仍视作被遮挡而收起，与查看牌堆/卡组的行为保持一致。
            return IsPeekingSelectionScreen(screen);
        }
        catch { return false; }
    }

    // 长按中键临时隐藏：每帧读中键的【实时按住状态】，状态翻转时才动作（非切换、非点击检测）。
    // 按住 → 隐藏所有缩略图；松开 → 恢复。_hideHeld 已并入可见性判据，按住期间的重建也保持隐藏。
    internal static void HoldHideTick()
    {
        if (!PeekSettings.MasterEnabled) return;
        // 绑定键：默认 None → 鼠标中键；绑了键就用键（读实时按住状态）
        Key k = PeekSettings.HoldHideKey;
        bool held = k == Key.None ? Input.IsMouseButtonPressed(MouseButton.Middle) : Input.IsKeyPressed(k);
        if (held == _hideHeld) return;   // 状态未变则不重复处理（避免每帧重设）
        _hideHeld = held;
        if (held)
        {
            SetAllThumbnailsVisible(false);
            foreach (var s in _overlays.Keys.ToList()) HideOverlay(s);   // 大图也收起（与"打开地图/牌堆时收起大图"语义一致）
        }
        else
        {
            MaybeRestoreThumbnails();
            // 松开时若仍在悬停某队友，重新弹出其大图（按住期间被收起了）
            foreach (var s in _highlightedStates.ToList())
                if (!_overlays.ContainsKey(s)) ShowOverlay(s);
        }
    }

    // 订阅 ActiveScreenContext.Updated：地图/卡组/牌堆等开关时被调用。
    // 被遮挡 → 收起所有缩略图与大图；回到战斗视图 → 按 hover 状态恢复缩略图。
    internal static void OnScreenChanged()
    {
        if (!PeekSettings.MasterEnabled) return;
        if (IsCombatViewActive())
            MaybeRestoreThumbnails();
        else
        {
            SetAllThumbnailsVisible(false);
            foreach (var s in _overlays.Keys.ToList()) HideOverlay(s);
        }
    }

    /* == 设置即时生效 == */

    // 设置变更 → 合并到下一帧只重建一次（debounce）。
    // 根因：ModConfig 的「恢复默认」会逐条触发 OnChanged，一帧内可连发十几次本方法；
    // 若每次都同步整段销毁重建，中间态会把高光动画/可见性搞乱（= reset 后再开总开关、
    // 缩略图高光不显示的同步 bug）。合并成「下一帧一次」后，只按最终设置干净地重建一遍。
    private static bool _rebuildScheduled;

    internal static void OnSettingsChanged()
    {
        if (_rebuildScheduled) return;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            _rebuildScheduled = true;
            tree.ProcessFrame += DebouncedRebuild;
        }
        else RebuildAll(); // 无场景树时退化为同步
    }

    private static void DebouncedRebuild()
    {
        if (Engine.GetMainLoop() is SceneTree tree) tree.ProcessFrame -= DebouncedRebuild;
        _rebuildScheduled = false;
        RebuildAll();
    }

    private static void RebuildAll()
    {
        // 拆除所有当前 overlay / 缩略图，随后按最新设置重建
        foreach (var s in _overlays.Keys.ToList()) HideOverlay(s);
        foreach (var s in _thumbnails.Keys.ToList()) DestroyThumbnail(s);
        RestoreAllPrismatic();   // 兜底：清掉任何漏网的残留棱彩卡（如关闭棱彩开关时）

        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (container == null) return;

        // 从真实游戏状态重新核对「被悬停」集合：避免遗留的 stale 高亮项把刚重建的
        // 缩略图/高光误判为「需隐藏」（状态同步——别信旧缓存，以场景实际状态为准）。
        _highlightedStates.Clear();
        for (int i = 0; i < container.GetChildCount(); i++)
        {
            var state = container.GetChildOrNull<NMultiplayerPlayerState>(i);
            if (state == null) continue;
            bool isLocal = state.Player?.NetId == LocalContext.NetId;
            if (!isLocal && _isHighlightedField.GetValue(state) is true)
                _highlightedStates.Add(state);
        }

        for (int i = 0; i < container.GetChildCount(); i++)
        {
            var state = container.GetChildOrNull<NMultiplayerPlayerState>(i);
            if (state == null) continue;

            UpdatePurpleGlow(state); // 遵循 Master / PurpleGlowEnabled / 新颜色

            if (PeekSettings.MasterEnabled && PeekSettings.ThumbnailsEnabled)
                RebuildThumbnail(state);

            // 为当前正被悬停的队友恢复大图
            if (PeekSettings.MasterEnabled && _isHighlightedField.GetValue(state) is true)
                ShowOverlay(state);
        }
    }
}
