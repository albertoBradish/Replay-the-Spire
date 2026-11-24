# 回放尖塔 · Replay the Spire

在《杀戮尖塔 2》游玩时记录操作，局后在游戏中观看回放，查看决策用时和卡牌收益，导出选定楼层的图集。

## 开发

使用 .NET 9 SDK 和本地游戏目录构建。首版的验证目标为 0.111.1 和 0.113.1。

```powershell
./scripts/build.ps1 -GameDirectory 'D:/Games/Slay the Spire 2'
```

源代码使用 GPL-3.0-only。提交遵循 Conventional Commits。
