# 更新日志 / Changelog

## v1.1.3

### 兼容 / Compatibility
- 针对游戏 **v0.113.0** 重新构建，声明最低游戏版本 `0.113.0`。
  Rebuilt for game **v0.113.0**, with a declared minimum game version of `0.113.0`.
- 资源包导出排除依赖缓存、历史会话、构建产物和外部清单，避免夹带旧版本文件。
  Resource exports exclude dependency caches, session history, build outputs, and the external manifest.
- 固定构建分析器版本并移除未使用的 BaseLib 引用；内联双语文案不再触发缺少本地化表的构建错误。
  Pin the build analyzer and remove the unused BaseLib reference; inline bilingual UI text no longer triggers a missing-localization-table build error.

### 修复 / Fixed
- 选牌界面点击「窥视」时恢复队友手牌预览，覆盖网格、单卡和卡牌组选择；通过场景的 `%PeekButton` 节点读取状态。
  Ally hand previews now return when peeking from grid, single-card, and card-bundle selection screens.
- 退出对局时取消原手牌堆的事件订阅并清理预览、缩略图和身体染色；取消的延迟隐藏回调不会再访问已销毁的状态栏或干扰新的预览。
  Leaving a run clears hand subscriptions and preview effects. Cancelled hide callbacks no longer access freed player UI or interfere with a newer preview.

## v1.1.2

### 修复 / Fixed
- **命运同担 / Shared Fate** 现在会触发警示高光。该卡使用 `EnemyStrengthLoss` 表示“敌人失去力量”，此前未被识别为减敌力量牌。
  **Shared Fate** now triggers the warning highlight. The card uses `EnemyStrengthLoss` for enemy Strength loss, which was previously not recognized as enemy-Strength reduction.

## v1.1.1

### 新增 / New
在部分战斗（如 Boss「帝皇蟹 / Kaiser Crab」）中，常态缩略图可能盖住关键战斗信息（如 Boss 的攻击意图）。新增两个快捷操作随时让开视线：
In some fights (e.g. the Kaiser Crab boss), the always-on thumbnails can cover important combat art (such as the boss's attack intent). Two new controls clear the view on demand:
- **长按临时隐藏 / Hold to hide**：按住 **鼠标中键**（可改键）临时隐藏手牌缩略图与悬停预览，看清其后的球 / 攻击意图，松开恢复。
  Hold **middle mouse** (rebindable) to briefly hide the hand thumbnails & hover preview so you can see the orbs / intent behind them; release to restore.
- **快速开关缩略图 / Toggle thumbnails**：按 **-**（可改键）一键开 / 关常态缩略图。
  Press **-** (rebindable) to toggle the always-on thumbnails on/off.
- 首次进入联机时弹出一次性快捷操作提示。/ A one-time controls hint on entering multiplayer.

### 修复 / Fixed
- 缩略图 / 预览位置改为动态贴合状态栏真实右缘——储君（多「辉星」栏）不再被盖住手牌数，高 HP 角色（血条更宽）也正确让开。
  Thumbnail/preview position now tracks the true right edge of the status bar — the Regent (extra Star column) no longer has its hand count covered, and high-HP characters (wider health bar) are handled correctly.
- 打开地图 / 卡组 / 抽牌堆 / 弃牌堆 / 查看卡牌时，缩略图·预览·设置面板自动收起，不再画在其上；关闭后恢复。
  Thumbnails, preview, and the settings panel now hide when the map / deck / draw pile / discard pile / card inspect is open, and return when closed.
- 设置面板勾选框在关闭态显示清晰方框轮廓（勾选显示对勾），不再「看不见」。
  Settings-panel checkboxes now show a clear box outline when off (checkmark when on) instead of being invisible.

### 其它 / Other
- **ModConfig 现为必需依赖。** / ModConfig is now a required dependency.

## v1.1

### 新增 / New
- **游戏内设置 / In-game settings**：所有选项移入「模组配置 (ModConfig)」标签；并新增场景内**设置面板**——默认按 **F8** 唤出，可拖动标题移动、按功能分区、快捷键可改并带冲突检测。
  All options now live under the **ModConfig** tab, plus a new in-scene **settings panel** — toggle with **F8** by default, draggable, grouped by section, rebindable hotkey with conflict detection.
- **常态缩略图 / Always-on thumbnails**：每个队友状态栏旁常驻显示其手牌缩略图（**默认开**）；悬停某队友时该处切换为放大的「悬停预览」。
  Each ally's hand shows as a small strip beside their status bar (**on by default**); hovering an ally swaps it for the enlarged hover preview.
- **棱彩高光 / Prismatic glow**：卡牌警示高光与身体光晕可选「流动彩虹」效果（**默认开**）——在调色板里选 🌈 预设即开，选具体颜色即关。
  Card warning highlight and body glow can use a flowing-rainbow look (**on by default**) — pick the 🌈 preset in the palette to enable, pick a solid color to disable.
- **可调项 / Tunables**：悬停预览的卡牌大小 / 间距 / 延迟消失；缩略图的大小 / 卡牌间隔 / 与状态栏间距 / 不透明度；卡牌高光与身体光晕的颜色 / 光晕宽度 / 不透明度（悬停预览与缩略图各自独立）。
  Hover-preview card size / spacing / hide-delay; thumbnail size / card spacing / bar gap / opacity; card & body glow color / width / opacity (preview and thumbnail are independent).
- **颜色 / Colors**：预设色板（点击即用、可预览）+ hex 精调输入，两个界面一致。
  Preset swatches (click-to-apply, previewable) plus hex input, consistent across both UIs.

### 改进 / Improved
- 主开关一键折叠 / 展开全部设置；各开关折叠其下属子项，界面分区清晰。
  The master toggle folds / unfolds all settings at once; each toggle folds its own sub-options; cleaner sectioning.
- 稳定性：设置变更合并到一帧重建、对象池安全（棱彩材质归还前必还原，真实手牌不受影响）、状态同步修复。
  Stability: settings changes coalesce into one rebuild, pool-safe prismatic materials (restored before pooling so the real hand is never affected), and state-sync fixes.

### 默认 / Defaults
- 开箱即用：缩略图开启、卡牌与身体光晕走流动棱彩。
  Out of the box: thumbnails on, with prismatic card & body glow.
