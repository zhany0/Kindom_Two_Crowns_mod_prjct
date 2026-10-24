# BetterKingdom

《王国：两位君主》（Kingdom Two Crowns）的功能增强模组，基于 BepInEx 6（IL2CPP）与 Harmony 开发。

具体mod效果请移步隔壁项目https://github.com/zhany0/Kindom_Two_Crowns_mod


## 代码文件说明

| 文件 | 作用 |
| --- | --- |
| [Plugin.cs](file:///d:/KingdomMod/code2git/Plugin.cs) | 插件入口：注册组件、初始化配置、创建常驻对象、应用 Harmony 补丁 |
| [ModWindowBehaviour.cs](file:///d:/KingdomMod/code2git/ModWindowBehaviour.cs) | 模组设置窗口：显示/功能开关、快捷键改绑、配置持久化 |
| [MapOverlayBehaviour.cs](file:///d:/KingdomMod/code2git/MapOverlayBehaviour.cs) | 游戏内 HUD 覆盖层：小地图、怪物位置、金币/职业/时间季节、血条与进度条 |
| [SeasonClock.cs](file:///d:/KingdomMod/code2git/SeasonClock.cs) | 季节时钟：由累计天数推算当前季节与季节内天数 |

