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
        PeekHandPatch.ForgetHighlight(__instance);
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

    // 状态栏几何（来自场景 ui/multiplayer_player_state.tscn）：行高 64px，
    // 内容右缘 ≈ TopInfoContainer.offset_right(261) + 余量。用作【稳定】右侧锚点，
    // 避免依赖只在 hover 时才赋值的 Hitbox.Size.X（非 hover 时为 0 → 会盖住状态栏）。
    private const float BarHeight       = 64f;
    private const float BarContentRight = 267f;

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

        // 「自己」（本地玩家）的状态栏也在容器里：悬停自己时不应隐藏队友缩略图，
        // 只有悬停【远程队友】才触发隐藏。
        bool isLocal = __instance.Player != null && __instance.Player.NetId == LocalContext.NetId;

        if (highlighted)
        {
            if (!isLocal)
            {
                _highlightedStates.Add(__instance);
                SetAllThumbnailsVisible(false);    // 悬停远程队友 → 隐藏所有缩略图
            }
            _hideTimers.Remove(__instance);
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
                var tree = __instance.GetTree();
                if (tree == null) HideOverlay(__instance);
                else
                {
                    var timer = tree.CreateTimer(PeekSettings.HideDelaySec);
                    _hideTimers[__instance] = timer;
                    timer.Timeout += () =>
                    {
                        _hideTimers.Remove(__instance);
                        bool stillHighlighted = _isHighlightedField.GetValue(__instance) is true;
                        if (!stillHighlighted && !_overlayHovered.Contains(__instance))
                            HideOverlay(__instance);
                    };
                }
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
            if (PeekSettings.CardGlowEnabled) ApplyHighlight(cardNode);
            else cardNode.CardHighlight.AnimHideInstantly();
        }

        // 延后一帧设置位置（用稳定的 state 锚点，而非 hover 才赋值的 Hitbox.Size.X）
        Callable.From(() =>
        {
            if (!overlay.IsInsideTree()) return;
            overlay.GlobalPosition = new Vector2(
                state.GlobalPosition.X + BarContentRight + PeekSettings.OverlayGapPx,
                state.GlobalPosition.Y + BarHeight / 2f   // 卡牌 y=0 → 从状态栏中线向下展开（大图保持原样）
            );
        }).CallDeferred();

        _overlays[state] = overlay;
    }

    // 优先级（高→低）：紫 > 红 > 金
    //   紫：施加虚弱/易伤的卡牌（DynamicVars 含 PowerVar<WeakPower/VulnerablePower>）
    //       + 降低敌人力量 (ContainsKey(StrengthLossKey))
    //       + 多人专属卡（MultiplayerOnly)
    //   红：游戏原生 ShouldGlowRed（Osty 角色相关、Enthralled 诅咒等）
    //   金：游戏原生 ShouldGlowGold（特殊触发条件满足，仅可打出时显示）
    private static void ApplyHighlight(NCard cardNode)
    {
        // 防御：高光节点未就绪/脱离场景树时，AnimShow 内部 CreateTween 会失败，
        // 留下半套用、宽度为 0 的空高光。就绪后由 RebuildAll/ContentsChanged 再次套用。
        if (cardNode.CardHighlight == null || !cardNode.IsNodeReady()) return;

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

        // 缩略图 = 大图模式的等比缩小版：卡牌布局【逐字照搬大图（ShowOverlay）】——已验证对齐，
        // 不再自己用锚点数学重算。唯一不同：挂在 state 下用局部坐标（这是 #3 跳位的修法，不能回退）。
        float scale   = PeekSettings.ThumbnailScalePct / 100f;
        float spacing = PeekSettings.CardSpacingPx * scale;   // 与大图同一间隔规则，按比例缩小

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

        // 挂在 state 下，局部坐标 = 大图「state 右侧 + 状态栏中线」在 state 局部空间的等价：
        //   局部(BarContentRight+gap, BarHeight/2)  ≡  全局(state.X+267+gap, state.Y+32)
        state.AddChild(strip);
        strip.Position = new Vector2(BarContentRight + PeekSettings.OverlayGapPx, BarHeight / 2f);

        foreach (NCard cardNode in cardNodes)
        {
            cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            // 缩略图高光 = 卡牌高光总开关 且 缩略图子开关 同时开启
            if (PeekSettings.CardGlowEnabled && PeekSettings.ThumbnailShowGlow) ApplyHighlight(cardNode);
            else cardNode.CardHighlight.AnimHideInstantly();
        }

        _thumbnails[state] = strip;

        // 可见性单一真源：与 MaybeRestoreThumbnails 判据完全一致——有任一队友被悬停、
        // 或仍有大图存在时隐藏；否则显示。（旧代码只看 _highlightedStates、漏看 _overlays：
        // 在悬停/大图存在时重建会把刚建好的缩略图永久藏到下次取消悬停才恢复。）
        strip.Visible = _highlightedStates.Count == 0 && _overlays.Count == 0;
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

    internal static void ForgetHighlight(NMultiplayerPlayerState state) => _highlightedStates.Remove(state);

    // 任一队友被悬停时隐藏【所有】缩略图；无人被悬停时全部恢复
    private static void SetAllThumbnailsVisible(bool visible)
    {
        if (!PeekSettings.ThumbnailsEnabled) return;
        foreach (var strip in _thumbnails.Values)
            if (GodotObject.IsInstanceValid(strip)) strip.Visible = visible;
    }

    // 仅当「无人被悬停」且「没有大图还在显示」时才恢复缩略图
    // （延迟消失期间大图仍在 _overlays 里 → 不恢复，避免大图与缩略图同框）
    private static void MaybeRestoreThumbnails()
    {
        if (_highlightedStates.Count == 0 && _overlays.Count == 0)
            SetAllThumbnailsVisible(true);
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