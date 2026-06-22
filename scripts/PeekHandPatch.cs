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
        PeekHandPatch.UnsubscribeHandEvents(__instance);
        PeekHandPatch.HideOverlay(__instance);
        PeekHandPatch.DestroyThumbnail(__instance);
    }
}

[HarmonyPatch(typeof(NMultiplayerPlayerState), "UpdateHighlightedState")]
public static class PeekHandPatch
{
    // NCard.defaultSize = (300, 422)
    private const float CardNaturalWidth  = 300f;
    private const float CardNaturalHeight = 422f;

    // 所有可调参数现由 PeekSettings 提供（在使用处直接读取，不缓存，以便即时生效）

    private static readonly Dictionary<NMultiplayerPlayerState, Control> _overlays = [];

    // 常态缩略图：每个远程队友一条，独立于 hover 的 _overlays
    private static readonly Dictionary<NMultiplayerPlayerState, Control> _thumbnails = [];

    // 每个 state 最多一个挂起的隐藏计时器
    private static readonly Dictionary<NMultiplayerPlayerState, SceneTreeTimer> _hideTimers = [];

    // 鼠标当前是否停在 overlay 上（阻止计时器触发隐藏）
    private static readonly HashSet<NMultiplayerPlayerState> _overlayHovered = [];

    // 对角色 Body 施加紫色光效
    private static readonly Dictionary<NMultiplayerPlayerState, Node2D> _tintedBodies = [];

    // 直接订阅 Hand.ContentsChanged，存储 handler 以便精确取消订阅
    private static readonly Dictionary<NMultiplayerPlayerState, Action> _handHandlers = [];

    // 紫色警示色（卡牌高光）与身体光晕色现由 PeekSettings.WarningColor / BodyGlowColor 提供，
    // 与游戏内置的 gold/red/playableColor 共用同一套 NCardHighlight shader 机制

    // 通过 DynamicVars 检测卡牌是否包含 PowerVar<WeakPower> / PowerVar<VulnerablePower>
    // 或含 key "StrengthLoss"（减少敌人力量，如尖啸）
    private static readonly Type _weakVarType       = typeof(PowerVar<WeakPower>);
    private static readonly Type _vulnerableVarType = typeof(PowerVar<VulnerablePower>);
    private const string StrengthLossKey = "StrengthLoss";

    private static readonly FieldInfo _isHighlightedField =
        AccessTools.Field(typeof(NMultiplayerPlayerState), "_isHighlighted");

    // Harmony Postfix：在 UpdateHighlightedState 执行完毕后运行
    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        if (!PeekSettings.MasterEnabled) return;

        bool highlighted = _isHighlightedField.GetValue(__instance) is true;

        if (highlighted)
        {
            // 取消挂起的隐藏计时器
            _hideTimers.Remove(__instance);

            if (!_overlays.ContainsKey(__instance))
                ShowOverlay(__instance);
        }
        else if (_overlays.ContainsKey(__instance))
        {
            // 鼠标已移到 overlay 上，不隐藏
            if (_overlayHovered.Contains(__instance)) return;

            // 已有计时器则不重复创建
            if (_hideTimers.ContainsKey(__instance)) return;

            var tree = __instance.GetTree();
            if (tree == null) { HideOverlay(__instance); return; }

            // 延迟隐藏：给鼠标从状态栏移向右侧卡牌的时间
            var timer = tree.CreateTimer(PeekSettings.HideDelaySec);
            _hideTimers[__instance] = timer;
            timer.Timeout += () =>
            {
                _hideTimers.Remove(__instance);
                // 计时器到期时再次确认：若鼠标在 overlay 上则仍不隐藏
                bool stillHighlighted = _isHighlightedField.GetValue(__instance) is true;
                if (!stillHighlighted && !_overlayHovered.Contains(__instance))
                    HideOverlay(__instance);
            };
        }

        // 每次 hover 状态变化时同步角色轮廓光晕（独立于 hover，常驻显示）
        UpdatePurpleGlow(__instance);
    }

    /* == 手牌事件订阅 == */

    internal static void SubscribeHandEvents(NMultiplayerPlayerState state)
    {
        if (_handHandlers.ContainsKey(state)) return;
        var player = state.Player;
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        void handler() => OnHandChanged(state);
        player.PlayerCombatState.Hand.ContentsChanged += handler;
        _handHandlers[state] = handler;
    }

    internal static void UnsubscribeHandEvents(NMultiplayerPlayerState state)
    {
        if (!_handHandlers.Remove(state, out var handler)) return;
        var player = state.Player;
        if (player?.PlayerCombatState != null)
            player.PlayerCombatState.Hand.ContentsChanged -= handler;
    }

    /* == 紫色光效 == */

    private static bool HasPurpleCards(NMultiplayerPlayerState state)
    {
        var player = state.Player;
        if (player?.PlayerCombatState == null) return false;
        if (player.NetId == LocalContext.NetId) return false;
        return player.PlayerCombatState.Hand.Cards.Any(card =>
            card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly
            || card.DynamicVars.ContainsKey(StrengthLossKey)
            || card.DynamicVars.Values.Any(v => v.GetType() == _weakVarType
                                             || v.GetType() == _vulnerableVarType));
    }

    internal static void UpdatePurpleGlow(NMultiplayerPlayerState state)
    {
        bool want = PeekSettings.MasterEnabled && PeekSettings.PurpleGlowEnabled && HasPurpleCards(state);
        if (want && !_tintedBodies.ContainsKey(state))
            ShowPurpleGlow(state);
        else if (!want && _tintedBodies.ContainsKey(state))
            RemovePurpleGlow(state);
        else if (want && _tintedBodies.TryGetValue(state, out var body) && GodotObject.IsInstanceValid(body))
            body.Modulate = PeekSettings.BodyGlowColor; // 颜色可能已变，重新应用
    }

    private static void ShowPurpleGlow(NMultiplayerPlayerState state)
    {
        NCreature? nCreature = NCombatRoom.Instance?.GetCreatureNode(state.Player?.Creature);
        if (nCreature == null) return;

        Node2D body = nCreature.Body;
        body.Modulate = PeekSettings.BodyGlowColor;

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

    private static void ShowOverlay(NMultiplayerPlayerState state)
    {
        if (!PeekSettings.MasterEnabled) return;

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
                var tree = state.GetTree();
                if (tree == null) { HideOverlay(state); return; }
                var timer = tree.CreateTimer(PeekSettings.HideDelaySec);
                _hideTimers[state] = timer;
                timer.Timeout += () =>
                {
                    _hideTimers.Remove(state);
                    if (_isHighlightedField.GetValue(state) is not true && !_overlayHovered.Contains(state))
                        HideOverlay(state);
                };
            }
        };

        // 先挂载到场景树，使卡牌节点进入 Ready 状态
        Node uiParent = (Node?)NRun.Instance?.GlobalUi ?? state.GetParent();
        uiParent.AddChild(overlay);

        // 节点 Ready 后再调用 UpdateVisuals，否则内部 IsNodeReady() 检查会直接跳过
        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            ApplyHighlight(cardNode);
        }

        // 延后一帧设置位置，确保 Hitbox 布局已完成，Size 已填充
        Callable.From(() =>
        {
            if (!overlay.IsInsideTree()) return;
           
            var hitbox = state.Hitbox;
            overlay.GlobalPosition = new Vector2(
                hitbox.GlobalPosition.X + hitbox.Size.X + PeekSettings.OverlayGapPx,
                hitbox.GlobalPosition.Y + hitbox.Size.Y / 2f
            );
        }).CallDeferred();

        _overlays[state] = overlay;

        // 显示大图时隐藏该队友的缩略图
        SetThumbnailVisible(state, false);
    }

    // 优先级（高→低）：紫 > 红 > 金
    //   紫：施加虚弱/易伤的卡牌（DynamicVars 含 PowerVar<WeakPower/VulnerablePower>）
    //       + 降低敌人力量 (ContainsKey(StrengthLossKey))
    //       + 多人专属卡（MultiplayerOnly)
    //   红：游戏原生 ShouldGlowRed（Osty 角色相关、Enthralled 诅咒等）
    //   金：游戏原生 ShouldGlowGold（特殊触发条件满足，仅可打出时显示）
    private static void ApplyHighlight(NCard cardNode)
    {
        var model = cardNode.Model;
        if (model == null) return;

        bool isPurple = model.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly
                     || model.DynamicVars.ContainsKey(StrengthLossKey)
                     || model.DynamicVars.Values.Any(v => v.GetType() == _weakVarType
                                                       || v.GetType() == _vulnerableVarType);
        bool isRed    = model.ShouldGlowRed;
        bool isGold   = model.CanPlay() && model.ShouldGlowGold;

        if (isRed || isPurple || isGold)
        {
            cardNode.CardHighlight.Modulate = isPurple ? PeekSettings.WarningColor
                                            : isRed ? NCardHighlight.red
                                            : NCardHighlight.gold;
            cardNode.CardHighlight.AnimShow();
        }
        else cardNode.CardHighlight.AnimHideInstantly();
    }

    internal static void HideOverlay(NMultiplayerPlayerState state)
    {
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
            overlay.RemoveChild(card);
            NodePool.Free(card);
        }

        overlay.QueueFree();

        // 隐藏大图后恢复该队友的缩略图
        SetThumbnailVisible(state, true);
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

    // 卡高略小于状态栏高度：scale = (ThumbnailHeightPct% * 状态栏高) / 422
    internal static void RebuildThumbnail(NMultiplayerPlayerState state)
    {
        DestroyThumbnail(state);

        if (!PeekSettings.MasterEnabled || !PeekSettings.ThumbnailsEnabled) return;

        Player player = state.Player;
        if (player?.PlayerCombatState == null) return;
        if (player.NetId == LocalContext.NetId) return;

        var handCards = player.PlayerCombatState.Hand.Cards;
        if (handCards.Count == 0) return;

        float statusBarHeight = state.Size.Y;
        if (statusBarHeight <= 0f) statusBarHeight = state.Hitbox?.Size.Y ?? 0f;
        if (statusBarHeight <= 0f) return;

        float cardHeight = PeekSettings.ThumbnailHeightPct / 100f * statusBarHeight;
        float scale      = cardHeight / CardNaturalHeight;

        var strip = new Control { Name = "PeekThumbnail", MouseFilter = Control.MouseFilterEnum.Ignore };
        strip.Modulate = new Color(1f, 1f, 1f, PeekSettings.ThumbnailOpacity);

        var cardNodes = new List<NCard>();
        float xOffset = CardNaturalWidth * scale / 2f;
        foreach (CardModel card in handCards)
        {
            NCard? cardNode = NCard.Create(card);
            if (cardNode == null) continue;

            cardNode.Scale    = new Vector2(scale, scale);
            cardNode.Position = new Vector2(xOffset, 0f);
            cardNode.MouseFilter = Control.MouseFilterEnum.Ignore;
            strip.AddChild(cardNode);
            cardNodes.Add(cardNode);

            xOffset += CardNaturalWidth * scale + PeekSettings.ThumbnailSpacingPx;
        }

        // 绘制层级高于状态栏；回退到 state 父节点
        Node uiParent = (Node?)NRun.Instance?.GlobalUi?.AboveTopBarVfxContainer ?? state.GetParent();
        uiParent.AddChild(strip);

        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            if (PeekSettings.ThumbnailShowGlow) ApplyHighlight(cardNode);
            else cardNode.CardHighlight.AnimHideInstantly();
        }

        // 延后一帧定位：垂直居中于状态栏、置于其右侧
        Callable.From(() =>
        {
            if (!strip.IsInsideTree()) return;
            var hitbox = state.Hitbox;
            if (hitbox == null) return;
            strip.GlobalPosition = new Vector2(
                hitbox.GlobalPosition.X + hitbox.Size.X + PeekSettings.OverlayGapPx,
                hitbox.GlobalPosition.Y + (hitbox.Size.Y - cardHeight) / 2f
            );
        }).CallDeferred();

        _thumbnails[state] = strip;

        // 若该队友当前被悬停，则保持缩略图隐藏（让大图显示）
        if (_isHighlightedField.GetValue(state) is true)
            strip.Visible = false;
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
            strip.RemoveChild(card);
            NodePool.Free(card);
        }
        strip.QueueFree();
    }

    private static void SetThumbnailVisible(NMultiplayerPlayerState state, bool visible)
    {
        if (PeekSettings.ThumbnailsEnabled
            && _thumbnails.TryGetValue(state, out var strip)
            && GodotObject.IsInstanceValid(strip))
            strip.Visible = visible;
    }

    /* == 设置即时生效 == */

    internal static void OnSettingsChanged()
    {
        // 拆除所有当前 overlay / 缩略图，随后按最新设置重建
        foreach (var s in _overlays.Keys.ToList()) HideOverlay(s);
        foreach (var s in _thumbnails.Keys.ToList()) DestroyThumbnail(s);

        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (container == null) return;

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