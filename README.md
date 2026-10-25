**非二次开发人员勿下，普通玩家请下载<https://github.com/zhany0/Kindom_Two_Crowns_mod>中的现成mod**

# BetterKingdom

《王国：两位君主》（Kingdom Two Crowns）的功能增强模组，基于 BepInEx 6（IL2CPP）与 Harmony 开发。

具体mod效果请移步隔壁项目<https://github.com/zhany0/Kindom_Two_Crowns_mod>

### 各文件职责边界

- [Plugin.cs](file:///d:/KingdomMod/code2git/Plugin.cs)：注册并挂载三个常驻组件，集中应用所有 Harmony 补丁；负责窗口输入拦截、光标补写与 IMGUI 中文字体修复。
- [MapOverlayBehaviour.cs](file:///d:/KingdomMod/code2git/MapOverlayBehaviour.cs)：游戏内信息显示与刷新缓存，包含时间/季节推算与显示。
- [FeatureBehaviour.cs](file:///d:/KingdomMod/code2git/FeatureBehaviour.cs)：所有功能的具体实现及对应补丁（含传送门摧毁后的工人改派，兼容部署前已摧毁的存档）。
- [HotkeyBindings.cs](file:///d:/KingdomMod/code2git/HotkeyBindings.cs)：快捷键的解析、组合键捕获、配置读写与 Rewired 运行时绑定。
- [ModWindowBehaviour.cs](file:///d:/KingdomMod/code2git/ModWindowBehaviour.cs)：负责设置窗口的 UI 搭建、选项开关状态与交互。

