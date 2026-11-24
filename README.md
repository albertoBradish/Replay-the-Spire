# 回放尖塔 · Replay the Spire

在《杀戮尖塔 2》里重看自己的对局。按当时的抽牌顺序和随机结果复盘一场战斗，找出某次选择花了多久，或把整段经过导出成有声视频和贴吧直播贴图集。

## 开始使用

1. 在游戏主菜单查看版本号，选用对应版本的安装包。当前目标为 Windows 版 `0.111.1` 和 `0.113.1`。从源码安装时，可用下方的构建命令生成对应的 ZIP。
2. 将压缩包中的 `SpireReplay` 文件夹解压到游戏的 `mods` 目录，在游戏的模组菜单启用 **Replay the Spire / 回放尖塔**，按游戏提示重启。
3. 开始一局单人游戏。回到主菜单，点击 **回放尖塔**，选择对局与楼层。录制随游戏开始，平时无需操作。
4. 从时间线双击一条战斗记录，定位到对应节点。播放条可以暂停、单步、选择回合或楼层，支持 0.5×、1×、2× 和 4×。打开 **保留思考时间** 可以按录制时的决策节奏观看。

**全局卡牌统计** 按卡牌种类汇总抽到、手动打出、自动打出、实际扣除的敌方生命和获得的格挡。选中卡牌可以展开记录并跳回对应战斗。同名卡牌的多张副本合并统计。收益归属采用游戏报告的直接来源。

## 分享对局

| 想分享的内容 | 在回放库中的操作 | 得到的文件 |
| --- | --- | --- |
| 可在游戏中自由观看的一局 | **导出 → 整局回放文件** | `.spireplay`，接收者用 **导入回放** 打开 |
| 一层战斗或整局视频 | **本层视频** 或 **导出 → 整局视频** | 720p、30 fps、有游戏原声的 MP4 |
| 更清晰的视频 | **导出 → 高清视频 · 1080p** | 1080p、30 fps、有游戏原声的 MP4 |
| 贴吧直播贴 | **生成整局直播贴** | 按顺序排列的游戏截图、可复制的贴文、可离线打开的图集 |
| 只整理一场战斗 | 选择楼层生成截图或决策图集 | 本层图片与时间线 |

游戏完成一局后会自动生成可分享的回放文件。回放库和导出目录位于游戏用户目录内的 `ReplayTheSpire` 与 `ReplayTheSpire/exports`。安装文件夹通常只有模组本体。接收者要在相同游戏构建、相同模组版本与加载顺序下重演战斗；版本不同时仍可阅读时间线与统计。

有声 MP4 使用 Windows 的进程音频采集和 FFmpeg。Windows 10 2004（build 19041）及以上的 64 位系统会在导出时尝试采集游戏原声，已在 Windows 11 实机验证。首次导出时可以在游戏内安装编码器或选择自己的 `ffmpeg.exe`。普通游玩和原生回放无需安装编码器。导出期间会显示楼层和进度，可以取消。游戏已静音时，可选择按静音设置导出，或仅为本次视频以 25% 音量播放并导出原声。[Windows 进程音频采集说明](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/) · [OBS 的系统兼容说明](https://obsproject.com/kb/application-audio-capture-guide/) · [FFmpeg Windows 构建来源](https://ffmpeg.org/download.html)

## 构建

需要 .NET 9 SDK 与本地 Windows 版《杀戮尖塔 2》安装。PowerShell 中执行以下命令，生成可直接解压到游戏 `mods` 目录的安装包。

```powershell
./scripts/build.ps1 -GameDirectory 'D:/Games/Slay the Spire 2' -Package
dotnet run --project tests/Storage/StorageTests.csproj -c Release
```

安装包写入 `artifacts/<游戏版本>/`。开发流程、实机覆盖与性能测量见[开发基线](docs/development.md)和[验收记录](docs/validation.md)。

## English

Replay the Spire records single-player runs in Slay the Spire 2. Open **Replay the Spire** from the main menu to watch the original game simulate a recorded floor. Pause, step, jump to a decision, review per-card totals, or export a floor or run as a video or illustrated forum post. Share a `.spireplay` file with players on the same game build and mod set.

Extract `SpireReplay/` to the game's `mods/` directory, enable the mod in-game and restart. Sound MP4 export uses FFmpeg and Windows process audio capture. Source code is licensed under [GPL-3.0-only](LICENSE).
