using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BetterKingdom
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        public override void Load()
        {
            // 将自定义 MonoBehaviour 注册进 IL2CPP 域，之后才能 AddComponent
            ClassInjector.RegisterTypeInIl2Cpp<MapOverlayBehaviour>();
            ClassInjector.RegisterTypeInIl2Cpp<ModWindowBehaviour>();
            ClassInjector.RegisterTypeInIl2Cpp<FeatureBehaviour>();

            // 绑定并读取功能开关的持久化配置（退出游戏后保留设置）
            ModWindowBehaviour.InitConfig(Config);

            var go = new GameObject("BetterKingdom.MapOverlay");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MapOverlayBehaviour>();
            go.AddComponent<ModWindowBehaviour>();
            go.AddComponent<FeatureBehaviour>();

            // 应用 IMGUI 中文字体补丁（移植自 KingdomMod.DevTools，移植后可安全删除该插件）
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(TextGeneratorValidatedSettingsPatch));
            // 模组窗口打开期间拦截游戏 ESC 暂停菜单
            harmony.PatchAll(typeof(GameTryShowMenuPatch));
            harmony.PatchAll(typeof(GameShowMenuIfInGamePatch));
            // 模组窗口打开期间屏蔽玩家输入（移动/坐骑技能/支付等游戏内交互）
            harmony.PatchAll(typeof(PlayerReceiveInputPatch));
            // 游玩态补写光标显示（游戏 CursorSystem 会隐藏系统光标，MOD 需要其常显）
            harmony.PatchAll(typeof(CursorSystemUpdatePatch));
            // 功能实现相关补丁：摇铃立即上船、传送门摧毁后的工人分配
            harmony.PatchAll(typeof(EmbarkeeInstantBoardPatch));
            harmony.PatchAll(typeof(PortalWorkerDistributionPatch));
            harmony.PatchAll(typeof(PortalDestroyedPatch));
        }
    }

    /// <summary>
    /// 游戏在 CursorSystem.Update 里决定系统光标的显隐（游玩态隐藏、菜单/解析界面强制显示）。
    /// MOD 的地图覆盖层需要游玩时光标常显，因此在它自己的 Update 之后再补写一次：
    /// 同一帧内 MOD 是最后写入者，不会与游戏争抢同一个状态，从根上避免光标频闪。
    /// （此前在 OnGUI 里写 Cursor 属 IMGUI 渲染阶段，Unity 应用不可靠，正是频闪的来源。）
    /// </summary>
    [HarmonyPatch(typeof(CursorSystem), "Update")]
    public static class CursorSystemUpdatePatch
    {
        public static void Postfix()
        {
            if (!MapOverlayBehaviour.CursorWanted) return;

            Cursor.visible = true;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        }
    }

    /// <summary>
    /// MOD 窗口打开期间拦截 Player 的输入接收（Rewired 输入路由入口），
    /// 屏蔽移动、坐骑技能、支付等一切游戏内玩家交互；窗口关闭后自动恢复。
    /// </summary>
    [HarmonyPatch(typeof(Player), "IControllable_ReceiveInput")]
    public static class PlayerReceiveInputPatch
    {
        private static bool Prefix() => !ModWindowBehaviour.IsOpen;
    }

    /// <summary>
    /// 修复 IL2CPP 下 IMGUI 动态字体渲染失败的补丁：
    /// fontSize 为 0 时 TextGenerator 无法生成文本顶点，中文等文本不显示。
    /// 此时用字体默认字号兜底并重置字形样式，让 IMGUI 中文标签正常渲染。
    /// </summary>
    [HarmonyPatch(typeof(TextGenerator), "ValidatedSettings")]
    public static class TextGeneratorValidatedSettingsPatch
    {
        public static bool Prefix(TextGenerationSettings settings)
        {
            Font font = settings.font;
            if (font == null || !font.dynamic) return true;
            if (settings.fontSize == 0)
            {
                settings.fontSize = font.fontSize > 0 ? font.fontSize : 14;
                settings.fontStyle = FontStyle.Normal;
            }
            return true;
        }
    }
}
