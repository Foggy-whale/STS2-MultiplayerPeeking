# 双人实战回归测试

在仓库根目录运行：

```sh
python3 tests/multiplayer/run.py
```

需要 macOS、已安装的 Slay the Spire 2 v0.113.0、ModConfig 0.1.3、Godot .NET 4.5.1、
可构建 `net9.0` 的 .NET SDK 和 Python 3。可用 `--game`、`--godot` 指定安装位置。
本地 UDP 33771 必须空闲。默认打开两个 1280×720 游戏窗口，通常约一分钟完成。

启动器每次编译当前 mod 和独立测试 DLL、导出 PCK，将游戏可执行文件复制到临时 app，
只挂载游戏原资源及三份测试需要的 mod。两端使用独立的全新存档，不复制个人进度，
不修改正式游戏的 mods 目录。结束后关闭本次启动的进程，删除临时 app 和测试存档，
将诊断副本保留在结果目录。测试代码通过 csproj 和 `.gdignore` 排除，不进入发布 DLL/PCK。

`Probe.cs` 借助游戏原有 `IBootstrapSettings`，让两端以固定种子进入 SlimesWeak 战斗。
每人使用 Bash、Neutralize、SharedFate、DefendIronclad、StrikeIronclad 五张牌，
实际加载角色、卡牌和怪物节点。战斗运行在普通游戏模式，没有启用游戏的 `TestMode`。
文件屏障只协调测试步骤；玩家准备、出牌、敌方行动和抽牌通过真实 ENet 与游戏行动队列同步。
悬停调用游戏角色悬停入口，快捷键通过 Godot 输入事件触发；断言读取 mod 持有的真实 UI 节点。

每端 47 项断言覆盖：

- 真实主机/客户端连接、远端五张手牌缩略图、不重复显示本地手牌。
- 警示卡棱彩材质、队友身体染色、悬停大图及缩略图互斥。
- 两端真实打出防御牌后，大图和缩略图同步更新为四张牌。
- 网格选牌、三选一、牌组选择三个实际界面的窥视开关和悬停预览。
- 中键按住/松开、减号快捷键、总开关、对象池材质还原。
- 旧隐藏计时器不影响新悬停，当前计时器按期隐藏。
- 双方结束回合、怪物行动、第二回合抽牌后的预览更新。
- 挂着隐藏计时器返回主菜单，确认计时器在原状态节点销毁后才到期；随后订阅、节点、材质、染色和悬停集合全部清空。

启动器还逐项比较双方开局、出牌后、第二回合的玩家 ID、血量、格挡、能量、回合数、
手牌顺序和弃牌，并检查进程退出码及日志。断言、快照不一致或超时均返回非零退出码。
这验证的是本机双人联机；不覆盖 Steam 远程网络、四人局、所有角色、完整战斗胜利或断线重连。

输出目录包含 `summary.json`、双方 `*.result.json`、构建/导出/游戏日志、14 张实际渲染截图。
`engine-notices.json` 单独记录已观察到的 ModConfig `size_changed` 断开告警、
MegaDot `Invalid Task ID`、无头模式 dummy renderer 的空纹理诊断以及退出后的资源回收告警。这些不会被当成功能通过的依据；
其余错误会令测试失败，因此“通过”不代表底层引擎日志完全没有告警。

无需图形截图时可运行 `python3 tests/multiplayer/run.py --headless`。
验证测试能否捕获本次适配回归：

```sh
python3 tests/multiplayer/run.py --headless --disable-peek-patch
```

这个负向对照只在隔离进程内禁用 mod 对“选牌界面正在窥视”的识别，模拟适配前的战斗视图门控，
应在 `grid peek restores thumbnails` 断言处失败并返回非零；不要把该命令的失败当成正常版本失败。
