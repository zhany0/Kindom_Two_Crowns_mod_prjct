using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace BetterKingdom
{
    /// <summary>
    /// 快捷键修改实现：解析/捕获按键、持久化配置，并把改绑结果写入游戏 Rewired 键盘映射。
    /// 设置窗口只负责显示行与按钮交互，具体的改键规则、运行时绑定与应用逻辑集中在本文件。
    /// </summary>
    public static class HotkeyBindings
    {
        /// <summary>打开窗口快捷键的默认值（恢复默认与初始配置共用）。</summary>
        public const string DefaultOpenWindowKey = "F3+F4";

        private static ConfigEntry<string> _cfgOpenWindowKey;   // 打开窗口快捷键（如 "F3+F4"）
        private static KeyCode _openKey1 = KeyCode.F3;    // 解析后的主键
        private static KeyCode _openKey2 = KeyCode.F4;    // 解析后的组合键（None=单键）

        /// <summary>游戏按键绑定定义（Rewired 运行时改绑 + BepInEx 配置持久化）。</summary>
        public sealed class Binding
        {
            public string Id;                 // 内部标识
            public string Label;              // 行显示名
            public string Desc;               // 行说明文字
            public string[] ActionNames;      // 候选 Rewired 动作名（不区分大小写）
            public KeyCode DefaultKey;        // 默认键（回退识别 + 恢复默认）
            public bool IsAxisPart;           // 是否移动轴分量（左/右移动为同一动作的两个分量）
            public Rewired.Pole Contribution; // 轴分量方向（仅 IsAxisPart 时有意义）
            public int PlayerIndex;           // 所属 Rewired 玩家（0=玩家1，1=分屏合作玩家2）
            public int ActionId = -1;         // 运行时解析出的动作 id
            public ConfigEntry<string> Entry; // 持久化（"A" / "Q+LeftShift"）
            public bool Applied;              // 已保存的键位是否已写入游戏
        }

        /// <summary>
        /// 游戏按键绑定表（玩家 1 的 5 项 + 分屏合作玩家 2 的 5 项）：
        /// 候选动作名覆盖可能的命名差异；解析失败时按默认键在对应玩家的键盘映射中回退识别。
        /// </summary>
        private static readonly Binding[] _gameBindings = BuildGameBindings();

        public static int BindingCount => _gameBindings.Length;
        public static Binding GetBinding(int index) => _gameBindings[index];

        /// <summary>
        /// 构建绑定表：玩家 1 默认 A/D/S/W/左Shift；玩家 2 复用同样的动作与说明，
        /// 但改绑作用于玩家 2 的键盘映射（默认 H/K/J/U/G，顺序对应 左移/右移/支付/能力/冲刺）。
        /// </summary>
        private static Binding[] BuildGameBindings()
        {
            var p1 = new List<Binding>
            {
                new Binding { Id = "MoveLeft", Label = "向左移动", Desc = "",
                    ActionNames = new[] { "ControlMove", "Move", "Move Horizontal", "Horizontal", "Walk" },
                    DefaultKey = KeyCode.A, IsAxisPart = true, Contribution = Rewired.Pole.Negative },
                new Binding { Id = "MoveRight", Label = "向右移动", Desc = "",
                    ActionNames = new[] { "ControlMove", "Move", "Move Horizontal", "Horizontal", "Walk" },
                    DefaultKey = KeyCode.D, IsAxisPart = true, Contribution = Rewired.Pole.Positive },
                new Binding { Id = "Pay", Label = "支付/使用金币", Desc = "长按可连续支付",
                    ActionNames = new[] { "ControlPay", "Pay", "Action", "Coin", "PayKey" },
                    DefaultKey = KeyCode.S },
                new Binding { Id = "Ability", Label = "特殊能力", Desc = "",
                    ActionNames = new[] { "ControlAbility", "Ability", "Special", "Ruler Ability", "Monarch Ability" },
                    DefaultKey = KeyCode.W },
                new Binding { Id = "Sprint", Label = "冲刺", Desc = "按住冲刺键 + 移动键即冲刺",
                    ActionNames = new[] { "ControlSprint", "Sprint", "Gallop", "Run", "Dash" },
                    DefaultKey = KeyCode.LeftShift },
            };

            var all = new List<Binding>(p1);
            KeyCode[] p2Defaults = { KeyCode.H, KeyCode.K, KeyCode.J, KeyCode.U, KeyCode.G };
            for (int i = 0; i < p1.Count; i++)
            {
                var src = p1[i];
                all.Add(new Binding
                {
                    Id = "P2" + src.Id,
                    Label = src.Label + "（玩家2）",
                    Desc = src.Desc,
                    ActionNames = src.ActionNames,
                    DefaultKey = p2Defaults[i],
                    IsAxisPart = src.IsAxisPart,
                    Contribution = src.Contribution,
                    PlayerIndex = 1,
                });
            }
            return all.ToArray();
        }

        private static bool _rewiredReady;      // 全部绑定是否已解析并应用
        private static float _rewiredRetryAt;   // 下次重试解析的时刻
        private static int _rewiredRetryLeft = 20;  // 剩余重试次数（等玩家 2 稍后加入时有限重试）

        private static readonly List<KeyCode> _capturedKeys = new();   // 捕获到的按键（最多两个）

        /// <summary>绑定并读取快捷键配置（打开窗口键 + 全部游戏按键改绑）。</summary>
        public static void InitConfig(ConfigFile config)
        {
            _cfgOpenWindowKey = config.Bind("快捷键设置", "打开BetterKingdom MOD设置窗口", DefaultOpenWindowKey,
                "打开/关闭 MOD 设置窗口的按键（可用 + 组合两个键，如 F3+F4）");
            ParseOpenKey(_cfgOpenWindowKey.Value);
            // 游戏按键改绑配置（Rewired 就绪后由 EnsureRewiredBindings 应用到游戏）
            foreach (var b in _gameBindings)
                b.Entry = config.Bind("快捷键设置", b.Label, b.DefaultKey.ToString(),
                    b.Desc + "（可用 + 组合修饰键，如 Q+LeftShift）");
        }

        /// <summary>当前打开窗口快捷键的显示文本（供屏幕提示等模块读取）。</summary>
        public static string OpenKeyDisplay => _cfgOpenWindowKey != null ? _cfgOpenWindowKey.Value : DefaultOpenWindowKey;

        /// <summary>某一游戏绑定当前键位的显示文本。</summary>
        public static string GetDisplay(Binding binding)
            => binding != null && binding.Entry != null ? binding.Entry.Value : binding != null ? binding.DefaultKey.ToString() : string.Empty;

        /// <summary>定时重试：等 Rewired 就绪后解析游戏动作并应用已保存的改绑。</summary>
        public static void Tick()
        {
            if (!_rewiredReady && _rewiredRetryLeft > 0 && Time.unscaledTime >= _rewiredRetryAt)
            {
                _rewiredRetryAt = Time.unscaledTime + 2f;
                _rewiredRetryLeft--;
                EnsureRewiredBindings();
            }
        }

        /// <summary>检测打开/关闭窗口快捷键：组合键时按住其一再按另一个（两个方向都支持），单键时直接按下触发。</summary>
        public static bool OpenTogglePressed()
        {
            if (_openKey1 == KeyCode.None) return false;
            return _openKey2 == KeyCode.None
                ? Input.GetKeyDown(_openKey1)
                : (Input.GetKey(_openKey1) && Input.GetKeyDown(_openKey2))
                  || (Input.GetKey(_openKey2) && Input.GetKeyDown(_openKey1));
        }

        /// <summary>解析快捷键配置字符串（"F3+F4"）为 KeyCode；无效时回退 F3+F4。</summary>
        public static void ParseOpenKey(string s)
        {
            _openKey1 = KeyCode.F3;
            _openKey2 = KeyCode.F4;
            if (string.IsNullOrEmpty(s)) return;
            var parts = s.Split('+');
            if (parts.Length >= 1 && Enum.TryParse(parts[0].Trim(), true, out KeyCode k1))
            {
                _openKey1 = k1;
                _openKey2 = KeyCode.None;
            }
            if (parts.Length >= 2 && Enum.TryParse(parts[1].Trim(), true, out KeyCode k2))
                _openKey2 = k2;
        }

        /// <summary>将捕获的按键列表格式化为配置字符串（"F3+F4"）。</summary>
        public static string FormatKeys(List<KeyCode> keys)
        {
            var names = new List<string>();
            foreach (var k in keys) names.Add(k.ToString());
            return string.Join("+", names.ToArray());
        }

        // ---- 按键捕获（设置窗口等待输入状态调用） ----

        public static bool CaptureDone { get; private set; }

        /// <summary>开始捕获：清空按键记录，等待玩家按键。</summary>
        public static void BeginCapture()
        {
            _capturedKeys.Clear();
            CaptureDone = false;
        }

        /// <summary>
        /// 每帧捕获键盘输入：记录按下的键（最多两个，支持组合键），
        /// 全部按键松开后视为输入完成；鼠标/手柄键忽略。
        /// 返回当前按键显示文本（尚未捕获到按键时返回 null）。
        /// </summary>
        public static string UpdateCapture()
        {
            if (CaptureDone) return _capturedKeys.Count > 0 ? FormatKeys(_capturedKeys) : null;
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                string ks = k.ToString();
                if (ks.StartsWith("Mouse") || ks.StartsWith("Joystick")) continue;   // 鼠标/手柄键不作为快捷键
                if (Input.GetKeyDown(k) && !_capturedKeys.Contains(k))
                {
                    if (_capturedKeys.Count < 2) _capturedKeys.Add(k);
                    break;
                }
            }
            if (_capturedKeys.Count > 0)
            {
                if (!Input.anyKey) CaptureDone = true;   // 全部松开→输入完成，等待确定/取消
                return FormatKeys(_capturedKeys);
            }
            return null;
        }

        /// <summary>
        /// 提交捕获的按键：binding 为 null 时写入打开窗口快捷键，否则写入对应游戏绑定并立即应用；
        /// 没有捕获到按键时返回 null（调用方恢复原显示）。
        /// </summary>
        public static string CommitCapture(Binding binding)
        {
            if (_capturedKeys.Count == 0) return null;
            string s = FormatKeys(_capturedKeys);
            if (binding != null)
            {
                binding.Entry.Value = s;   // ConfigEntry 赋值自动写盘
                ApplyBinding(binding);     // 同步应用到游戏 Rewired 键盘映射（即时生效）
            }
            else
            {
                _cfgOpenWindowKey.Value = s;   // ConfigEntry 赋值自动写盘
                ParseOpenKey(s);
            }
            return s;
        }

        /// <summary>清空捕获状态（结束等待按键时调用）。</summary>
        public static void ClearCapture()
        {
            _capturedKeys.Clear();
            CaptureDone = false;
        }

        /// <summary>恢复默认：打开窗口键与全部游戏按键改绑都回到默认值并立即应用。</summary>
        public static void ResetAll()
        {
            if (_cfgOpenWindowKey != null) _cfgOpenWindowKey.Value = DefaultOpenWindowKey;   // ConfigEntry 赋值自动写盘
            ParseOpenKey(DefaultOpenWindowKey);
            foreach (var b in _gameBindings)
            {
                if (b.Entry == null) continue;
                b.Entry.Value = b.DefaultKey.ToString();
                ApplyBinding(b);
            }
        }

        // ==================== 游戏按键改绑（Rewired 键盘映射） ====================

        /// <summary>
        /// 解析游戏动作并应用已保存的改绑：已解析的绑定不再重复处理，未解析的留待下次重试；
        /// 玩家 1 与分屏合作玩家 2 的动作相同，但改绑分别作用于各自的键盘映射。
        /// </summary>
        private static void EnsureRewiredBindings()
        {
            try
            {
                if (!Rewired.ReInput.isReady) return;
                bool allResolved = true;
                foreach (var b in _gameBindings)
                {
                    if (b.ActionId < 0) ResolveActionId(b);
                    if (b.ActionId >= 0 && !b.Applied)
                    {
                        ApplyBinding(b);
                        b.Applied = true;
                    }
                    if (b.ActionId < 0) allResolved = false;
                }
                _rewiredReady = allResolved;
            }
            catch { /* Rewired 未就绪时留待下次重试 */ }
        }

        /// <summary>取 Rewired 玩家（0=玩家1，1=分屏合作玩家2）；未就绪或该玩家不存在时返回 null。</summary>
        private static Rewired.Player GetRewiredPlayer(int index)
        {
            try
            {
                if (!Rewired.ReInput.isReady) return null;
                if (index >= Rewired.ReInput.players.playerCount) return null;
                return Rewired.ReInput.players.GetPlayer(index);
            }
            catch { return null; }
        }

        /// <summary>解析动作 id：先按候选动作名匹配（轴类动作左/右移动同名，解析结果共享），失败则按默认键在对应玩家的键盘映射中回退识别。</summary>
        private static void ResolveActionId(Binding b)
        {
            Rewired.Player player = GetRewiredPlayer(b.PlayerIndex);
            if (player == null) return;   // 玩家 2 需进入分屏合作模式后才存在
            // 按候选名直查 id（Il2Cpp 泛型集合接口无托管枚举/索引，用按名查询绕开）
            foreach (var name in b.ActionNames)
            {
                int id = Rewired.ReInput.mapping.GetActionId(name);
                if (id >= 0) { b.ActionId = id; break; }
            }
            // 回退：扫描动作 id 范围，按默认键识别（游戏动作命名与候选名不符时兜底）
            if (b.ActionId < 0)
            {
                for (int actionId = 0; actionId < 200; actionId++)
                {
                    if (Rewired.ReInput.mapping.GetAction(actionId) == null) continue;
                    var elements = GetKeyboardElements(player, actionId);
                    for (int j = 0; j < elements.Count; j++)
                    {
                        var aem = elements[j];
                        if (aem.keyCode != b.DefaultKey) continue;
                        if (b.IsAxisPart && aem.axisContribution != b.Contribution) continue;
                        b.ActionId = actionId;
                        break;
                    }
                    if (b.ActionId >= 0) break;
                }
            }
        }

        /// <summary>取玩家键盘映射中某动作的全部元素（Il2Cpp List 输出参数重载，索引遍历）。</summary>
        private static Il2CppSystem.Collections.Generic.List<Rewired.ActionElementMap> GetKeyboardElements(Rewired.Player player, int actionId)
        {
            var list = new Il2CppSystem.Collections.Generic.List<Rewired.ActionElementMap>();
            player.controllers.maps.GetElementMapsWithAction(Rewired.ControllerType.Keyboard, actionId, true, list);
            return list;
        }

        /// <summary>将某绑定已保存的键位应用到游戏对应玩家（改键/恢复默认时调用）。</summary>
        private static void ApplyBinding(Binding b)
        {
            if (b.ActionId < 0) return;
            Rewired.Player player = GetRewiredPlayer(b.PlayerIndex);
            if (player != null) ApplyBinding(b, player);
        }

        /// <summary>将绑定键位写入玩家键盘映射中对应动作的元素（轴分量只改对应方向），Rewired 每帧读取映射故即时生效。</summary>
        private static void ApplyBinding(Binding b, Rewired.Player player)
        {
            ParseGameKey(b.Entry.Value, out KeyCode main, out Rewired.ModifierKey mod);
            var elements = GetKeyboardElements(player, b.ActionId);
            for (int i = 0; i < elements.Count; i++)
            {
                var aem = elements[i];
                if (b.IsAxisPart && aem.axisContribution != b.Contribution) continue;
                aem.keyCode = main;
                aem.modifierKey1 = mod;   // modifierKeyFlags 只读（由 mod1/2/3 合成），改绑最多一个修饰键
                aem.modifierKey2 = Rewired.ModifierKey.None;
                aem.modifierKey3 = Rewired.ModifierKey.None;
            }
        }

        /// <summary>
        /// 解析游戏键配置字符串（"Q+LeftShift"）：Shift/Ctrl/Alt 归为修饰键（不区分左右），其余取最后一个按下的为主键；
        /// 只按了修饰键时（如 Sprint 单绑 Shift）主键=该修饰键、无修饰键。
        /// </summary>
        private static void ParseGameKey(string s, out KeyCode main, out Rewired.ModifierKey mod)
        {
            main = KeyCode.None;
            mod = Rewired.ModifierKey.None;
            if (string.IsNullOrEmpty(s)) return;
            KeyCode firstModKey = KeyCode.None;
            foreach (var part in s.Split('+'))
            {
                if (!Enum.TryParse(part.Trim(), true, out KeyCode k)) continue;
                Rewired.ModifierKey m = KeyToModifier(k);
                if (m != Rewired.ModifierKey.None)
                {
                    if (mod == Rewired.ModifierKey.None) mod = m;
                    if (firstModKey == KeyCode.None) firstModKey = k;
                }
                else main = k;   // 多个非修饰键时最后按下的生效
            }
            if (main == KeyCode.None) { main = firstModKey; mod = Rewired.ModifierKey.None; }
        }

        /// <summary>KeyCode 转 Rewired 修饰键（左右合并，非修饰键返回 None）。</summary>
        private static Rewired.ModifierKey KeyToModifier(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return Rewired.ModifierKey.Shift;
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return Rewired.ModifierKey.Control;
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return Rewired.ModifierKey.Alt;
                default: return Rewired.ModifierKey.None;
            }
        }

        /// <summary>
        /// 检测指定玩家（0=玩家1，1=分屏玩家2）的"支付/使用金币"键当前是否按住。
        /// 读取改绑配置（含修饰键组合），供钱袋国库联动的长按取款判定；配置未就绪时回退默认键。
        /// </summary>
        public static bool IsPayKeyHeld(int playerIndex)
        {
            string id = playerIndex == 1 ? "P2Pay" : "Pay";
            foreach (var b in _gameBindings)
            {
                if (b.Id != id || b.Entry == null) continue;
                ParseGameKey(b.Entry.Value, out KeyCode main, out Rewired.ModifierKey mod);
                if (main == KeyCode.None || !Input.GetKey(main)) return false;
                switch (mod)
                {
                    case Rewired.ModifierKey.Shift:
                        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    case Rewired.ModifierKey.Control:
                        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    case Rewired.ModifierKey.Alt:
                        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                    default:
                        return true;
                }
            }
            return Input.GetKey(playerIndex == 1 ? KeyCode.J : KeyCode.S);
        }
    }
}
