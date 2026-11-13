# Multiplayer Peeking
《杀戮尖塔2》联机优化mod

它支持让你在战斗过程中查看队友手牌，并且在队友拥有特殊手牌（**虚弱**、**易伤**、**降低敌人力量**、**联机专属**等卡牌）时能给予紫色的高光提示

<img width="3840" height="2160" alt="e2c3376eb2c0fc96640a48dd569f8791" src="https://github.com/user-attachments/assets/46b51e5a-02f4-44ce-b91e-c57f56803b66" />

## 安装指南
直接在[release](https://github.com/Foggy-whale/STS2-MultiplayerPeeking/releases)界面下载最新版本的zip压缩包，解压到游戏根目录的mod目录下即可使用

## 版本适配与构建
当前开发版 **1.1.3** 面向游戏 **v0.113.0**，需要 **ModConfig ≥ 0.1.3**。
使用 Godot .NET 4.5.1 SDK、`net9.0` 目标框架，引用本机 Steam 游戏的程序集。

`dotnet build MultiplayerPeeking.csproj -c Release` 会自动复制 DLL 和清单到本机游戏的 mod 目录。
只编译、不安装时使用 `dotnet build MultiplayerPeeking.csproj -c Release -p:DeployMod=false`。
打包时将同一次构建的 DLL、最新清单和重新导出的 PCK 放在 `MultiplayerPeeking/` 目录中。

## 双人实战回归测试

macOS 上运行 `python3 tests/multiplayer/run.py`，自动编译并启动两个隔离的游戏实例，
使用真实 ENet 联机、出牌队列和敌方回合验证功能；每端执行 47 项断言，比较三个阶段的双方状态。
结果和战斗截图保存在 `dist/multiplayer-tests/<时间>/`。详细范围和要求见 [测试说明](tests/multiplayer/README.md)。

## 关于存档
杀戮尖塔2的存档在启用mod和不启用mod的情况下是不互通的。

如果你是第一次安装mod，你需要将 %AppData%\SlayTheSpire2\steam\\{一串id} 目录下的profile文件夹复制到modded目录下

## 最后
该模组还属于测试阶段，如果存在bug，希望你能通过issue进行反馈~
