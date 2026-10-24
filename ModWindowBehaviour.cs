using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Coatsink.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BetterKingdom
{
    /// <summary>
    /// F3+F4 组合键开关的模组窗口：打开时暂停游戏（timeScale=0），
    /// 窗口风格克隆自游戏暂停菜单按钮（Menu.continueButton 的九宫格像素精灵与中文像素字体），
    /// 底部"关闭"按钮常态黑色、光标悬停变红，点击或按 ESC 均可关闭窗口并恢复游戏。
    /// 窗口打开期间通过 Harmony 拦截 Game.TryShowMenu，阻止游戏自身 ESC 暂停菜单弹出。
    /// </summary>
    public class ModWindowBehaviour : MonoBehaviour
    {
        // 注：构建链为 .NET SDK 6（最高 C# 10），请勿使用主构造函数/集合表达式等 C# 12 语法
        public ModWindowBehaviour(IntPtr ptr) : base(ptr) { }

        /// <summary>窗口是否打开（静态供 Harmony 补丁查询）。</summary>
        public static bool IsOpen { get; private set; }

        // ---- 外观常量（基于 1920x1080 参考分辨率，由 CanvasScaler 适配） ----
        private const int CanvasSortOrder = 5000;          // 压过游戏全部 UI
        private static readonly Vector2 ReferenceResolution = new(1920f, 1080f);
        private const float PanelHeight = 480f;            // 面板高（较原 560 减约一行文字高度）
        private const float PanelMinWidth = 880f;          // 面板宽（整体加宽）
        private const float TabInnerPadding = 40f;         // 页签距面板左右内缘距离
        private const float ButtonSpacing = 28f;           // 底部两按钮间距
        private const float BottomButtonMargin = 28f;      // 底部按钮距面板左右边缘空白
        private const float BottomButtonHeight = 56f;      // 底部按钮高（宽度按面板宽自动适配平分）
        private const float BottomBarHeight = 88f;         // 底边框栏高（容纳底部两个按钮，上下各留 16）
        private const float TopBarHeight = 64f;            // 上边框栏高（容纳窗口标题）
        private const float SideBarWidth = 26f;            // 左右边框栏宽（右栏紧贴滚动条）
        private const float ButtonFontHeightRatio = 0.42f; // 按钮字号 = 按钮高度 × 该比例（对齐游戏按钮字高占比）
        private const float LabelRiseRatio = 0.10f;        // 文本上移量 = 控件高度 × 该比例（修正视觉偏下）
        private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.65f);
        private static readonly Color PanelColor = new(0.18f, 0.15f, 0.13f, 0.97f);
        private static readonly Color FrameColor = new(0.72f, 0.60f, 0.48f, 1f);      // 边框回退色（无精灵时用）
        private static readonly Color FrameTintColor = new(0.78f, 0.72f, 0.64f, 1f);  // 边框精灵乘法色（原色基础上加深）
        private static readonly Color ButtonTintColor = new(0.84f, 0.80f, 0.75f, 1f); // 按钮常态略变深
        private static readonly Color ButtonLabelColor = new(0.25f, 0.20f, 0.17f);  // 按钮文字深棕色（贴近游戏按钮）

        // ---- 分页（顶部活页索引） ----
        private const float TabBarTopOffset = 20f;         // 索引栏距面板顶（标题已移入上边框栏）
        private const float TabBarHeight = 50f;
        private const float TabBarBgPad = 14f;             // 分隔底条上下各超出页签的高度（形成"次级标题栏"横带）
        private const float TabSpacing = 16f;
        private const int TabFontSize = 22;
        private static readonly Color TabNormalColor = new(0.12f, 0.10f, 0.09f, 1f);   // 未选中页签（暗）
        private static readonly Color TabActiveColor = new(0.30f, 0.24f, 0.19f, 1f);   // 选中页签/悬停（亮）

        // ---- 功能开关（方形槽样式，含下方解释文字行） ----
        private const float ToggleRowHeight = 84f;         // 行高（下部为解释文字行区，其余为主标签区，留空不紧凑）
        private const float ToggleDescZoneHeight = 38f;    // 解释文字行区高（下留空增大但不超主文字上留空：D≤(行高-7)/2）
        private const float ToggleRowGap = 0f;             // 行间距（边框高度相接，行间不留缝）
        private const float ToggleRowWidth = PanelMinWidth;  // 行宽 = 面板满宽（选项边框左右与内窗口相接）
        private const float ToggleBoxSize = 34f;
        private const int ToggleLabelFontSize = 24;
        private const int ToggleDescFontSize = 17;
        private static readonly Color ToggleDescColor = new(1f, 1f, 1f, 0.45f);  // 解释文字（浅灰）
        private const float ScrollbarWidth = 10f;
        private const float ScrollbarGap = 6f;             // 滚动条与内容间距
        private static readonly Color ToggleOnColor = new(0.25f, 0.62f, 0.28f, 1f);    // 开启（绿）
        private static readonly Color ToggleOffColor = new(0.12f, 0.10f, 0.085f, 1f);  // 未开启（面板同色系更深）
        private static readonly Color TabBarBgColor = new(0.235f, 0.195f, 0.165f, 0.97f);  // 页签栏底色（比内窗口略浅，视觉分割）
        // ---- 选项行边框底色（两页共用：拍平按钮精灵九宫格，比面板略亮以区分选项边界） ----
        private const float RowPadding = 20f;              // 行内容与行边框的内边距（文字左留空/按钮右留空）
        private static readonly Color RowBgColor = new(0.50f, 0.45f, 0.40f, 1f);        // 选项框常态（浅色）
        private static readonly Color RowHoverColor = new(0.64f, 0.58f, 0.51f, 1f);     // 选项框悬停（平滑变亮）
        private static readonly Color RowHighlightColor = new(0.76f, 0.68f, 0.58f, 1f); // 等待按键时整框提亮
        private static Sprite _flatSlotSprite;  // 拍平后的方形槽精灵（共享缓存）
        private CanvasGroup _onlyBuiltRowGroup;  // "只显示已建造"行组（地图子选项之一，需父选项与地图总开关同时开启）
        private Image _onlyBuiltFill;            // "只显示已建造"状态块（自动取消时刷新视觉）
        private readonly List<CanvasGroup> _mapSubRows = new();  // 所有地图子选项行组（地图显示关闭时统一变淡禁交互）
        private ScrollRect _scrollRect;          // 显示设置页滚动视图（手动滚轮控制）
        private ScrollRect _funcScrollRect;      // 功能设置页滚动视图（手动滚轮控制）
        private const float WheelStep = 0.05f;    // 每格滚轮的滚动距离（以显示页可滚动范围为基准的比例，各页换算为同一像素步长）
        private float _wheelPixels;              // 每格滚轮的像素距离（按显示页换算，懒加载）

        // ---- 快捷键设置 ----
        private static ConfigEntry<string> _cfgOpenWindowKey;   // 打开窗口快捷键（如 "F3+F4"）
        private static KeyCode _openKey1 = KeyCode.F3;    // 解析后的主键
        private static KeyCode _openKey2 = KeyCode.F4;    // 解析后的组合键（None=单键）
        private Button _buttonTemplate;      // 游戏按钮模板（动态建按钮用）
        private Text _lastButtonLabel;       // BuildMenuButton 最近一次新建的按钮文本
        private ScrollRect _hotkeyScrollRect; // 快捷键页滚动视图（手动滚轮控制）
        private GameObject _toggleScrollbarGo, _funcScrollbarGo, _hotkeyScrollbarGo;  // 三页滚动条（随页切换显隐）

        /// <summary>快捷键行视觉引用（改键时按行操作）。</summary>
        private class HotkeyRow
        {
            public string Id;                 // "OpenWindow" 或游戏绑定 Id
            public GameKeyBinding Binding;    // 游戏按键绑定（打开窗口行为 null）
            public RectTransform Rt;          // 行（等待输入时提升到遮罩之上）
            public Image Bg;                  // 行边框底色（等待按键时整框提亮）
            public Text ButtonLabel;          // 快捷键按钮文本（显示当前键）
        }
        private readonly List<HotkeyRow> _hotkeyRows = new();
        private HotkeyRow _activeRow;         // 正在改键的行（等待输入状态作用对象）

        /// <summary>游戏按键绑定定义（Rewired 运行时改绑 + BepInEx 配置持久化）。</summary>
        private class GameKeyBinding
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
        private static readonly GameKeyBinding[] _gameBindings = BuildGameBindings();

        /// <summary>
        /// 构建绑定表：玩家 1 默认 A/D/S/W/左Shift；玩家 2 复用同样的动作与说明，
        /// 但改绑作用于玩家 2 的键盘映射（默认 H/K/J/U/G，顺序对应 左移/右移/支付/能力/冲刺）。
        /// </summary>
        private static GameKeyBinding[] BuildGameBindings()
        {
            var p1 = new List<GameKeyBinding>
            {
                new GameKeyBinding { Id = "MoveLeft", Label = "向左移动", Desc = "",
                    ActionNames = new[] { "ControlMove", "Move", "Move Horizontal", "Horizontal", "Walk" },
                    DefaultKey = KeyCode.A, IsAxisPart = true, Contribution = Rewired.Pole.Negative },
                new GameKeyBinding { Id = "MoveRight", Label = "向右移动", Desc = "",
                    ActionNames = new[] { "ControlMove", "Move", "Move Horizontal", "Horizontal", "Walk" },
                    DefaultKey = KeyCode.D, IsAxisPart = true, Contribution = Rewired.Pole.Positive },
                new GameKeyBinding { Id = "Pay", Label = "支付/使用金币", Desc = "长按可连续支付",
                    ActionNames = new[] { "ControlPay", "Pay", "Action", "Coin", "PayKey" },
                    DefaultKey = KeyCode.S },
                new GameKeyBinding { Id = "Ability", Label = "特殊能力", Desc = "",
                    ActionNames = new[] { "ControlAbility", "Ability", "Special", "Ruler Ability", "Monarch Ability" },
                    DefaultKey = KeyCode.W },
                new GameKeyBinding { Id = "Sprint", Label = "冲刺", Desc = "按住冲刺键 + 移动键即冲刺",
                    ActionNames = new[] { "ControlSprint", "Sprint", "Gallop", "Run", "Dash" },
                    DefaultKey = KeyCode.LeftShift },
            };

            var all = new List<GameKeyBinding>(p1);
            KeyCode[] p2Defaults = { KeyCode.H, KeyCode.K, KeyCode.J, KeyCode.U, KeyCode.G };
            for (int i = 0; i < p1.Count; i++)
            {
                var src = p1[i];
                all.Add(new GameKeyBinding
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

        // 游戏原生的光标系统（ui_cursor 软件光标 + forceVisibleCursor 开关），惰性获取后缓存
        private static CursorSystem _gameCursor;
        private static bool _gameCursorLookupDone;  // 是否已尝试获取
        private static bool _prevCursorForced;      // 开窗前的游戏光标强制显隐状态（关窗时还原）
        private static bool _ownCursorForced;       // 未获取到 CursorSystem 时，是否由我们直接强显系统光标

        private Transform _rowOrigParent;    // 行原父级（提升后归位用）
        private int _rowOrigIndex;
        private Vector2 _rowOrigPos;
        private GameObject _waitDimGo;       // 等待输入时的全屏变暗层
        private readonly List<GameObject> _waitButtonGos = new();  // 确定/取消按钮
        private bool _waitingForKey;         // 正在等待玩家按键
        private bool _captureDone;           // 捕获完成（按键全部松开），等待确定/取消
        private readonly List<KeyCode> _capturedKeys = new();      // 捕获到的按键（最多两个）

        /// <summary>地图子选项联动：地图显示关闭时全部子选项变淡且禁交互；"只显示已建造"还需父选项开启，父级取消时联动取消并持久化。</summary>
        private void UpdateMapSubRowStates()
        {
            bool onlyBuiltOn = ShowMap && ShowAllDefenses;
            // 父级被取消时联动取消子选项（含视觉与持久化）
            if (!onlyBuiltOn && ShowOnlyBuiltDefenses)
            {
                ShowOnlyBuiltDefenses = false;
                if (_onlyBuiltFill != null) _onlyBuiltFill.color = ToggleOffColor;
                SaveSettings();
            }
            foreach (CanvasGroup g in _mapSubRows)
            {
                if (g == null) continue;
                bool on = ReferenceEquals(g, _onlyBuiltRowGroup) ? onlyBuiltOn : ShowMap;
                g.alpha = on ? 1f : 0.45f;
                g.interactable = on;
                g.blocksRaycasts = on;
            }
        }

        // ---- 功能开关状态（供地图覆盖层等模块读取；默认关闭=保持游戏当前显示规则） ----
        public static bool ShowAllDefenses;   // 地图上显示所有箭塔与城墙
        public static bool ShowOnlyBuiltDefenses; // 只显示已建造的城墙与箭塔（过滤建造中/建造位）
        public static bool ShowAllCamps;      // 显示所有难民营（平民房+原始难民营，不含被摧毁）
        public static bool ShowStatues;       // 显示雕像位置
        public static bool ShowMountSpawns;   // 显示坐骑出生点位置
        public static bool ShowGreedHealth;   // 显示怪物血条
        public static bool ShowCropProgress;  // 显示作物生长进度条
        public static bool LinkPurseTreasury; // 钱袋与国库共通（溢出自动存入/空袋长按支付键取出）
        public static bool ShowChests;        // 显示宝箱（金币宝箱/宝石宝箱）
        public static bool ShowTechPoints;    // 显示科技解锁点（石器科技/铁器科技）
        public static bool ShowMap;           // 地图显示（总开关：关闭时整张地图与全部地图标记隐藏，子选项不生效）
        public static bool ShowMapEnemies;    // 地图显示怪物位置（紫色竖线）
        public static bool ShowClassStats;    // 职业显示
        public static bool ShowCoins;         // 金币数量显示
        public static bool ShowTimeSeason;    // 时间与当前季节天数显示
        public static bool BellInstantBoard;  // 摇铃后被召唤的职业立即传送上船（省去跑动，不影响玩家）

        // ---- 持久化配置项（Plugin.Load 时绑定并读取；ConfigEntry 赋值即自动写盘） ----
        private static ConfigEntry<bool> _cfgAllDefenses, _cfgOnlyBuilt, _cfgCamps, _cfgStatues, _cfgMounts, _cfgGreedHealth, _cfgCropProgress, _cfgPurseLink, _cfgChests, _cfgTechPoints, _cfgMap, _cfgMapEnemies, _cfgClassStats, _cfgCoins, _cfgTimeSeason, _cfgBellInstant;

        /// <summary>绑定并读取持久化配置，使退出游戏后再次进入时恢复上次的开关设置。</summary>
        public static void InitConfig(ConfigFile config)
        {
            // 描述文字只在此处定义：写入配置文件注释，并由设置界面各行的解释文字直接读取（ConfigEntry.Description）
            // 分类名与设置界面页签标题保持一致：显示设置 / 功能设置 / 快捷键设置
            _cfgMap = config.Bind("显示设置", "地图显示", false, "");
            _cfgAllDefenses = config.Bind("显示设置", "显示所有箭塔与城墙", false, "");
            _cfgOnlyBuilt = config.Bind("显示设置", "只显示已建造的城墙与箭塔", false, "");
            _cfgCamps = config.Bind("显示设置", "显示难民营与民房", false, "");
            _cfgStatues = config.Bind("显示设置", "显示雕像位置", false, "");
            _cfgMounts = config.Bind("显示设置", "显示坐骑出生点", false, "");
            _cfgChests = config.Bind("显示设置", "显示未开启的宝箱", false, "");
            _cfgTechPoints = config.Bind("显示设置", "显示科技解锁点", false, "在地图上显示石器科技与铁器科技的位置");
            _cfgMapEnemies = config.Bind("显示设置", "显示怪物位置", false, "");
            _cfgClassStats = config.Bind("显示设置", "职业统计", false, "");
            _cfgCoins = config.Bind("显示设置", "金币数量统计", false, "");
            _cfgTimeSeason = config.Bind("显示设置", "显示时间与季节", false, "");
            _cfgGreedHealth = config.Bind("显示设置", "显示怪物血条", false, "不会显示普通贪婪的血条");
            _cfgCropProgress = config.Bind("显示设置", "显示作物生长进度条", false, "不会显示没有农民耕种的农田的进度条");
            _cfgPurseLink = config.Bind("功能设置", "钱袋与国库共通", false, "钱袋金币数过多时将多余金币自动存入国库；钱袋为空时长按支付键可从国库中取出金币填满钱袋");
            _cfgBellInstant = config.Bind("功能设置", "摇铃后NPC立即上船", false, "");
            _cfgOpenWindowKey = config.Bind("快捷键设置", "打开BetterKingdom MOD设置窗口", DefaultOpenWindowKey,
                "打开/关闭 MOD 设置窗口的按键（可用 + 组合两个键，如 F3+F4）");
            ShowAllDefenses = _cfgAllDefenses.Value;
            ShowOnlyBuiltDefenses = _cfgOnlyBuilt.Value;
            ShowAllCamps = _cfgCamps.Value;
            ShowStatues = _cfgStatues.Value;
            ShowMountSpawns = _cfgMounts.Value;
            ShowGreedHealth = _cfgGreedHealth.Value;
            ShowCropProgress = _cfgCropProgress.Value;
            LinkPurseTreasury = _cfgPurseLink.Value;
            ShowChests = _cfgChests.Value;
            ShowTechPoints = _cfgTechPoints.Value;
            ShowMap = _cfgMap.Value;
            ShowMapEnemies = _cfgMapEnemies.Value;
            ShowClassStats = _cfgClassStats.Value;
            ShowCoins = _cfgCoins.Value;
            ShowTimeSeason = _cfgTimeSeason.Value;
            BellInstantBoard = _cfgBellInstant.Value;
            ParseOpenKey(_cfgOpenWindowKey.Value);
            // 游戏按键改绑配置（Rewired 就绪后由 EnsureRewiredBindings 应用到游戏）
            foreach (var b in _gameBindings)
                b.Entry = config.Bind("快捷键设置", b.Label, b.DefaultKey.ToString(),
                    b.Desc + "（可用 + 组合修饰键，如 Q+LeftShift）");
        }

        /// <summary>取配置项的描述文字（ConfigEntry.Description 为 ConfigDescription 类型，其 Description 属性才是文本）。</summary>
        private static string ConfigDesc(ConfigEntryBase entry)
        {
            if (entry == null) return string.Empty;
            ConfigDescription desc = entry.Description;
            return desc == null ? string.Empty : (desc.Description ?? string.Empty);
        }

        /// <summary>解析快捷键配置字符串（"F3+F4"）为 KeyCode；无效时回退 F3+F4。</summary>
        private static void ParseOpenKey(string s)
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
        private static string FormatKeys(List<KeyCode> keys)
        {
            var names = new List<string>();
            foreach (var k in keys) names.Add(k.ToString());
            return string.Join("+", names.ToArray());
        }

        /// <summary>将当前开关状态写回配置文件（退出游戏后保留设置）。</summary>
        private static void SaveSettings()
        {
            if (_cfgAllDefenses == null) return;
            _cfgAllDefenses.Value = ShowAllDefenses;
            _cfgOnlyBuilt.Value = ShowOnlyBuiltDefenses;
            _cfgCamps.Value = ShowAllCamps;
            _cfgStatues.Value = ShowStatues;
            _cfgMounts.Value = ShowMountSpawns;
            _cfgGreedHealth.Value = ShowGreedHealth;
            _cfgCropProgress.Value = ShowCropProgress;
            _cfgPurseLink.Value = LinkPurseTreasury;
            _cfgChests.Value = ShowChests;
            _cfgTechPoints.Value = ShowTechPoints;
            _cfgMap.Value = ShowMap;
            _cfgMapEnemies.Value = ShowMapEnemies;
            _cfgClassStats.Value = ShowClassStats;
            _cfgCoins.Value = ShowCoins;
            _cfgTimeSeason.Value = ShowTimeSeason;
            _cfgBellInstant.Value = BellInstantBoard;
        }

        /// <summary>开关项视觉引用（恢复默认时统一刷新颜色）。</summary>
        private class ToggleEntry { public Image Fill; }
        private readonly List<ToggleEntry> _toggles = new();

        private GameObject _canvasGo;
        private GameObject _eventSystemGo;
        private bool _eventSystemOurs;      // 事件系统是否由本模组创建（游戏自带的不可动）
        private float _prevTimeScale = 1f;
        private GameObject _page1, _page2, _page3;  // 三个分页内容（显示设置/功能设置/快捷键设置）
        private Button _tab1, _tab2, _tab3;         // 三个索引页签
        private int _pageIndex;             // 当前页（0=显示设置 1=功能设置 2=快捷键设置）
        private int _displayToggleCount;    // 显示页开关数（_toggles 前段属显示页、后段属功能页，恢复默认按页分别重置）

        private void Update()
        {
            // 定时重试：等 Rewired 就绪后解析游戏动作并应用已保存的改绑；
            // 有绑定始终解析不出（如玩家 2 还没加入分屏）时有限次数重试，避免每帧空转
            if (!_rewiredReady && _rewiredRetryLeft > 0 && Time.unscaledTime >= _rewiredRetryAt)
            {
                _rewiredRetryAt = Time.unscaledTime + 2f;
                _rewiredRetryLeft--;
                EnsureRewiredBindings();
            }

            // 等待按键输入状态：独占键盘，捕获新快捷键，屏蔽开关窗/ESC 等一切按键逻辑
            if (_waitingForKey)
            {
                CaptureKeyInput();
                return;
            }

            // 打开窗口快捷键：组合键时按住其一再按另一个（两个方向都支持），单键时直接按下触发
            bool toggle = _openKey2 == KeyCode.None
                ? Input.GetKeyDown(_openKey1)
                : (Input.GetKey(_openKey1) && Input.GetKeyDown(_openKey2))
                  || (Input.GetKey(_openKey2) && Input.GetKeyDown(_openKey1));
            if (toggle)
            {
                if (IsOpen) Close();
                else Open();
            }
            else if (IsOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                // 确认弹窗打开时 ESC 只关闭弹窗
                if (_confirmGo != null) CloseRestoreConfirm();
                else Close();
            }

            if (IsOpen)
            {
                // 光标显隐交还给游戏的 CursorSystem（Open 时已置其 forceVisibleCursor）；
                // 仅在未获取到该系统时兜底逐帧强显，且只在指针被锁定时才写 lockState，避免与游戏互相覆盖
                if (_ownCursorForced) Cursor.visible = true;
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                // 切换窗口/焦点后事件系统可能被游戏重建或顶替（窗口会因此短暂无响应），每帧兜底确保可用
                EventSystem cur = EventSystem.current;
                if (cur == null || !cur.isActiveAndEnabled)
                    EnsureEventSystem();
                // 手动滚轮控制内容滚动：不限指针位置、即滚即动（ScrollRect 自带滚轮已关闭，避免双重滚动）
                // 各页各有滚动视图，只滚动当前显示页；各页内容高度不同，按像素步长滚动——
                // 每格滚轮滚动的像素距离取显示页的（WheelStep × 显示页可滚动范围），避免行更多的页滚得过快
                ScrollRect sr = _pageIndex == 0 ? _scrollRect : (_pageIndex == 1 ? _funcScrollRect : _hotkeyScrollRect);
                if (sr != null)
                {
                    float wheel = Input.mouseScrollDelta.y;
                    if (wheel != 0f)
                    {
                        float scrollable = sr.content.rect.height - sr.viewport.rect.height;
                        if (scrollable > 0f)
                        {
                            if (_wheelPixels <= 0f) _wheelPixels = FeatureWheelPixels();
                            sr.verticalNormalizedPosition = Mathf.Clamp01(
                                sr.verticalNormalizedPosition + wheel * _wheelPixels / scrollable);
                        }
                    }
                }
            }
        }

        /// <summary>打开窗口并暂停游戏。</summary>
        public void Open()
        {
            if (IsOpen) return;
            if (!IsInGameplay()) return;
            if (_canvasGo == null && !TryBuildUi()) return;

            IsOpen = true;
            _prevTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            _canvasGo.SetActive(true);
            EnsureEventSystem();
            AcquireCursor();
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        }

        /// <summary>关闭窗口并恢复游戏。</summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Time.timeScale = _prevTimeScale;
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_eventSystemOurs && _eventSystemGo != null) _eventSystemGo.SetActive(false);
            ReleaseCursor();
        }

        /// <summary>
        /// 取游戏原生的 CursorSystem 单例（惰性获取并缓存）。游戏自带软件光标（ui_cursor）且游玩态隐藏系统光标，
        /// 光标的显隐统一由该系统管理，因此不要逐帧改写 Cursor.visible，而是走它自己的开关。
        /// </summary>
        private static CursorSystem GameCursor()
        {
            if (_gameCursorLookupDone) return _gameCursor;
            _gameCursorLookupDone = true;
            try
            {
                _gameCursor = Coatsink.Common.SingletonMonoBehaviour<CursorSystem>.Inst;
            }
            catch
            {
                // 取不到游戏组件时退回直接设置系统光标
                _gameCursor = null;
            }
            return _gameCursor;
        }

        /// <summary>窗口打开：请求显示光标（优先借用游戏自己的 forceVisibleCursor 开关）。</summary>
        private static void AcquireCursor()
        {
            CursorSystem cs = GameCursor();
            if (cs != null)
            {
                try
                {
                    _prevCursorForced = cs.forceVisibleCursor;
                    cs.SetForceVisibleCursor(true);
                    return;
                }
                catch
                {
                    // 调用游戏组件失败时退回直接设置系统光标
                }
            }
            _ownCursorForced = true;
            Cursor.visible = true;
        }

        /// <summary>窗口关闭：把光标显隐交还给游戏自身的逻辑。</summary>
        private static void ReleaseCursor()
        {
            CursorSystem cs = GameCursor();
            if (cs != null)
            {
                try
                {
                    cs.SetForceVisibleCursor(_prevCursorForced);
                    return;
                }
                catch
                {
                    // 还原失败时保持游戏自身状态，不再干预
                }
            }
            if (_ownCursorForced)
            {
                _ownCursorForced = false;
                Cursor.visible = false;
            }
        }

        /// <summary>仅在进行局内游戏（非主菜单、且游戏暂停菜单未打开）时允许开窗。</summary>
        private static bool IsInGameplay()
        {
            try
            {
                Managers m = SingletonMonoBehaviour<Managers>.Inst;
                if (m == null) return false;
                Game g = m.game;
                if (g == null) return false;
                var s = g.state;
                return (s & Game.State.Playing) != 0 && (s & Game.State.Menu) == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 构建窗口 UI（首次打开时执行一次）。
        /// 关键步骤：克隆游戏暂停菜单的"继续游戏"按钮，获得与截图一致的像素风边框精灵与中文字体；
        /// 克隆体上的 MenuButtonEventHandler / LocalizedText 必须移除，
        /// 否则悬停事件会回调游戏菜单系统、文本会被本地化组件覆盖。
        /// </summary>
        private bool TryBuildUi()
        {
            try
            {
                if (!TryGetNativeAssets(out Button srcButton, out Font gameFont)) return false;

                Image srcImage = srcButton.image;
                Sprite frameSprite = srcImage != null ? srcImage.sprite : null;

                // ---- Canvas（屏幕空间覆盖层，最高排序） ----
                _canvasGo = new GameObject("BetterKingdom.ModWindow.Canvas");
                _canvasGo.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
                var canvas = _canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = CanvasSortOrder;
                canvas.pixelPerfect = true;
                var scaler = _canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = ReferenceResolution;
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
                _canvasGo.AddComponent<GraphicRaycaster>();

                // ---- 全屏半透明背景（阻挡点击穿透到游戏） ----
                // IL2CPP 下 GameObject 构造器不接受 System.Type[]，故先建空对象再 AddComponent<RectTransform>（自动替换 Transform）
                var backdropGo = new GameObject("Backdrop");
                var backdropRt = backdropGo.AddComponent<RectTransform>();
                backdropGo.transform.SetParent(_canvasGo.transform, false);
                var backdrop = backdropGo.AddComponent<Image>();
                backdrop.color = BackdropColor;
                backdropRt.anchorMin = Vector2.zero;
                backdropRt.anchorMax = Vector2.one;
                backdropRt.offsetMin = Vector2.zero;
                backdropRt.offsetMax = Vector2.zero;

                // ---- 居中面板（复用暂停菜单按钮的九宫格像素边框，压暗作窗口底色） ----
                var panelGo = new GameObject("Panel");
                var panelRt = panelGo.AddComponent<RectTransform>();
                panelGo.transform.SetParent(_canvasGo.transform, false);
                var panel = panelGo.AddComponent<Image>();
                if (frameSprite != null)
                {
                    panel.sprite = frameSprite;
                    panel.type = frameSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
                }
                panel.color = PanelColor;
                panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
                panelRt.pivot = new Vector2(0.5f, 0.5f);
                float panelWidth = PanelMinWidth;
                panelRt.sizeDelta = new Vector2(panelWidth, PanelHeight);

                // ---- 窗口边框：游戏按钮精灵九宫格（与游戏同风格），原色显示（比纯色边框略浅），
                // 垫于面板背后（四边露出 FrameThickness，底部加宽 BottomBarHeight 放按钮） ----
                var frameGo = new GameObject("Frame");
                var frameRt = frameGo.AddComponent<RectTransform>();
                frameGo.transform.SetParent(_canvasGo.transform, false);
                var frameImg = frameGo.AddComponent<Image>();
                if (frameSprite != null)
                {
                    frameImg.sprite = GetFlatSlotSprite(frameSprite);
                    frameImg.type = Image.Type.Sliced;
                }
                frameImg.color = frameSprite != null ? FrameTintColor : FrameColor;   // 纯色为无精灵时的回退
                frameRt.anchorMin = frameRt.anchorMax = new Vector2(0.5f, 0.5f);
                frameRt.pivot = new Vector2(0.5f, 0.5f);
                // 四边均为加宽栏：左/右栏宽 SideBarWidth（右栏放滚动条），上/下栏高 Top/BottomBarHeight
                frameRt.sizeDelta = new Vector2(panelWidth + SideBarWidth * 2f,
                    PanelHeight + TopBarHeight + BottomBarHeight);
                frameRt.anchoredPosition = new Vector2(0f, (TopBarHeight - BottomBarHeight) * 0.5f);
                frameRt.SetSiblingIndex(panelRt.GetSiblingIndex());   // 插到面板之下（先渲染边框再渲染面板）

                // ---- 底部按钮：左"恢复默认"、右"确认"，固定在底边框栏中央；宽度按面板宽自动平分适配 ----
                float btnWidth = (panelWidth - BottomButtonMargin * 2f - ButtonSpacing) * 0.5f;
                var bottomButtonSize = new Vector2(btnWidth, BottomButtonHeight);
                Button restoreBtn = BuildMenuButton(panelGo.transform, srcButton, "恢复默认", ShowRestoreConfirm, gameFont, bottomButtonSize);
                Button confirmBtn = BuildMenuButton(panelGo.transform, srcButton, "确认", Close, gameFont, bottomButtonSize);
                var restoreRt = restoreBtn.GetComponent<RectTransform>();
                var confirmRt = confirmBtn.GetComponent<RectTransform>();
                float halfSpan = btnWidth * 0.5f + ButtonSpacing * 0.5f;
                float btnY = -(BottomBarHeight * 0.5f);   // 面板底缘之下的底栏中心
                restoreRt.anchoredPosition = new Vector2(-halfSpan, btnY);
                confirmRt.anchoredPosition = new Vector2(halfSpan, btnY);

                // ---- 标题（位于上边框栏中央，面板顶缘之上） ----
                var titleGo = new GameObject("Title");
                var titleRt = titleGo.AddComponent<RectTransform>();
                titleGo.transform.SetParent(panelGo.transform, false);
                var title = titleGo.AddComponent<Text>();
                title.font = gameFont;
                title.text = "BetterKingdom MOD 设置";
                title.fontSize = 44;
                title.alignment = TextAnchor.MiddleCenter;
                title.color = Color.white;
                titleRt.anchorMin = titleRt.anchorMax = new Vector2(0.5f, 1f);
                titleRt.pivot = new Vector2(0.5f, 0.5f);
                titleRt.sizeDelta = new Vector2(panelWidth - 40f, TopBarHeight);
                titleRt.anchoredPosition = new Vector2(0f, TopBarHeight * 0.5f);

                // ---- 顶部活页索引页签：显示设置 / 功能设置 / 快捷键设置 ----
                float tabWidth = (panelWidth - TabInnerPadding * 2f - TabSpacing * 2f) / 3f;
                float tabInnerPadding = TabInnerPadding;
                _tab1 = BuildTab(panelGo.transform, srcButton, "显示设置", gameFont, tabWidth, () => ShowPage(0));
                _tab2 = BuildTab(panelGo.transform, srcButton, "功能设置", gameFont, tabWidth, () => ShowPage(1));
                _tab3 = BuildTab(panelGo.transform, srcButton, "快捷键设置", gameFont, tabWidth, () => ShowPage(2));
                var tab1Rt = _tab1.GetComponent<RectTransform>();
                var tab2Rt = _tab2.GetComponent<RectTransform>();
                var tab3Rt = _tab3.GetComponent<RectTransform>();
                // 页签从面板左内边距开始排布，像书本活页索引一样贴在内容区顶缘
                float tabY = -(TabBarTopOffset + TabBarHeight * 0.5f);
                float tabX0 = -panelWidth * 0.5f + tabInnerPadding + tabWidth * 0.5f;
                tab1Rt.anchoredPosition = new Vector2(tabX0, tabY);
                tab2Rt.anchoredPosition = new Vector2(tabX0 + tabWidth + TabSpacing, tabY);
                tab3Rt.anchoredPosition = new Vector2(tabX0 + (tabWidth + TabSpacing) * 2f, tabY);

                // ---- 页签栏分隔底条（次级标题栏与窗口视觉分割，底色比内窗口略浅） ----
                var tabBarBgGo = new GameObject("TabBarBg");
                var tabBarBgRt = tabBarBgGo.AddComponent<RectTransform>();
                tabBarBgGo.transform.SetParent(panelGo.transform, false);
                var tabBarBg = tabBarBgGo.AddComponent<Image>();
                tabBarBg.color = TabBarBgColor;
                tabBarBg.raycastTarget = false;
                tabBarBgRt.anchorMin = tabBarBgRt.anchorMax = new Vector2(0.5f, 1f);
                tabBarBgRt.pivot = new Vector2(0.5f, 1f);
                // 顶缘贴合面板顶（与上边框栏无缝衔接），向下盖过页签并多出 TabBarBgPad
                tabBarBgRt.sizeDelta = new Vector2(panelWidth, TabBarTopOffset + TabBarHeight + TabBarBgPad);
                tabBarBgRt.anchoredPosition = Vector2.zero;
                tabBarBgRt.SetSiblingIndex(tab1Rt.GetSiblingIndex());   // 插到页签之下（同级靠后渲染在上）

                // ---- 分页内容区（页签栏底条下方至面板内底缘，三页叠放按页切换；按钮已移至底边框栏） ----
                float pageTop = TabBarTopOffset + TabBarHeight + TabBarBgPad + 6f;   // 避开底条底缘
                float pageBottom = 14f;
                _page1 = BuildPage(panelGo.transform, null, gameFont, pageTop, pageBottom);
                _page2 = BuildPage(panelGo.transform, null, gameFont, pageTop, pageBottom);
                _page3 = BuildPage(panelGo.transform, null, gameFont, pageTop, pageBottom);
                _buttonTemplate = srcButton;   // 留存模板：等待输入界面动态建确定/取消按钮用

                // ---- 快捷键设置页内容：可滚动视图 + 打开窗口键行 + 玩家 1 / 玩家 2 各 5 个游戏按键行，
                // 行样式与显示/功能页一致（主标签+解释文字+右侧键位按钮+行边框，各页行左右缘对齐） ----
                int hotkeyRowCount = 1 + _gameBindings.Length;
                float hotkeyContentHeight = 28f + hotkeyRowCount * ToggleRowHeight + (hotkeyRowCount - 1) * ToggleRowGap;
                _hotkeyScrollRect = CreateScrollContent(_page3.transform, hotkeyContentHeight, out RectTransform hotkeyContentRt);
                BuildHotkeyRow(hotkeyContentRt, srcButton, new HotkeyRow { Id = "OpenWindow" },
                    "打开BetterKingdom MOD设置窗口", "", gameFont, 0);
                for (int i = 0; i < _gameBindings.Length; i++)
                {
                    var binding = _gameBindings[i];
                    BuildHotkeyRow(hotkeyContentRt, srcButton, new HotkeyRow { Id = binding.Id, Binding = binding },
                        binding.Label, binding.Desc, gameFont, i + 1);
                }

                // ---- 显示设置页内容：可滚动视图 + 十四个开关行 + 右侧滚动条 ----
                float contentHeight = 28f + 14 * ToggleRowHeight + 13 * ToggleRowGap;
                _scrollRect = CreateScrollContent(_page1.transform, contentHeight, out RectTransform contentRt);
                // 解释文字直接读配置项描述（ConfigEntry.Description.Description），描述只写在 InitConfig 的 Bind 语句里
                BuildToggleRow(contentRt, srcButton, "地图显示", ConfigDesc(_cfgMap), 0, gameFont,
                    () => ShowMap, v => { ShowMap = v; UpdateMapSubRowStates(); });
                // 地图子选项：比父级"地图显示"多一级缩进（28f），二级子选项"只显示已建造"再多一级（56f）；
                // 地图显示关闭时整体变淡且禁交互
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示所有箭塔与城墙", ConfigDesc(_cfgAllDefenses), 1, gameFont,
                    () => ShowAllDefenses, v => { ShowAllDefenses = v; UpdateMapSubRowStates(); }, 28f));
                _onlyBuiltRowGroup = BuildToggleRow(contentRt, srcButton, "只显示已建造的城墙与箭塔", ConfigDesc(_cfgOnlyBuilt), 2, gameFont,
                    () => ShowOnlyBuiltDefenses, v => ShowOnlyBuiltDefenses = v, 56f);
                _onlyBuiltFill = _toggles[_toggles.Count - 1].Fill;
                _mapSubRows.Add(_onlyBuiltRowGroup);
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示难民营与民房", ConfigDesc(_cfgCamps), 3, gameFont,
                    () => ShowAllCamps, v => ShowAllCamps = v, 28f));
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示雕像位置", ConfigDesc(_cfgStatues), 4, gameFont,
                    () => ShowStatues, v => ShowStatues = v, 28f));
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示坐骑出生点", ConfigDesc(_cfgMounts), 5, gameFont,
                    () => ShowMountSpawns, v => ShowMountSpawns = v, 28f));
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示未开启的宝箱", ConfigDesc(_cfgChests), 6, gameFont,
                    () => ShowChests, v => ShowChests = v, 28f));
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示科技解锁点", ConfigDesc(_cfgTechPoints), 7, gameFont,
                    () => ShowTechPoints, v => ShowTechPoints = v, 28f));
                _mapSubRows.Add(BuildToggleRow(contentRt, srcButton, "显示怪物位置", ConfigDesc(_cfgMapEnemies), 8, gameFont,
                    () => ShowMapEnemies, v => ShowMapEnemies = v, 28f));
                BuildToggleRow(contentRt, srcButton, "职业统计", ConfigDesc(_cfgClassStats), 9, gameFont,
                    () => ShowClassStats, v => ShowClassStats = v);
                BuildToggleRow(contentRt, srcButton, "金币数量统计", ConfigDesc(_cfgCoins), 10, gameFont,
                    () => ShowCoins, v => ShowCoins = v);
                BuildToggleRow(contentRt, srcButton, "显示时间与季节", ConfigDesc(_cfgTimeSeason), 11, gameFont,
                    () => ShowTimeSeason, v => ShowTimeSeason = v);
                BuildToggleRow(contentRt, srcButton, "显示怪物血条", ConfigDesc(_cfgGreedHealth), 12, gameFont,
                    () => ShowGreedHealth, v => ShowGreedHealth = v);
                BuildToggleRow(contentRt, srcButton, "显示作物生长进度条", ConfigDesc(_cfgCropProgress), 13, gameFont,
                    () => ShowCropProgress, v => ShowCropProgress = v);
                _displayToggleCount = _toggles.Count;   // 显示页开关构建完毕，之后进入功能页

                // ---- 功能设置页内容：可滚动视图 + 两个开关行（行样式与显示页完全一致） ----
                float funcContentHeight = 28f + 2 * ToggleRowHeight + 1 * ToggleRowGap;
                _funcScrollRect = CreateScrollContent(_page2.transform, funcContentHeight, out RectTransform funcContentRt);
                BuildToggleRow(funcContentRt, srcButton, "钱袋与国库共通", ConfigDesc(_cfgPurseLink), 0, gameFont,
                    () => LinkPurseTreasury, v => LinkPurseTreasury = v);
                BuildToggleRow(funcContentRt, srcButton, "摇铃后NPC立即上船", ConfigDesc(_cfgBellInstant), 1, gameFont,
                    () => BellInstantBoard, v => BellInstantBoard = v);
                Sprite sbSprite = srcButton.image != null ? srcButton.image.sprite : null;
                _toggleScrollbarGo = AttachScrollbar(panelGo.transform, _scrollRect, sbSprite);
                _funcScrollbarGo = AttachScrollbar(panelGo.transform, _funcScrollRect, sbSprite);
                _hotkeyScrollbarGo = AttachScrollbar(panelGo.transform, _hotkeyScrollRect, sbSprite);
                UpdateMapSubRowStates();   // 初始：地图显示未开时子选项变淡且禁交互

                ShowPage(0);
                _canvasGo.SetActive(false);
                return true;
            }
            catch
            {
                if (_canvasGo != null)
                {
                    UnityEngine.Object.Destroy(_canvasGo);
                    _canvasGo = null;
                }
                return false;
            }
        }

        /// <summary>
        /// 克隆游戏暂停菜单按钮，完全保留原生样式（像素边框、悬停高亮过渡），常态颜色略变深；
        /// 文本替换为指定内容并使用给定字体，字号按按钮高度比例计算。
        /// 尺寸由调用方按面板宽计算传入（窗口改宽自动适配），锚点固定为面板底部居中（pivot 中心）。
        /// </summary>
        private Button BuildMenuButton(Transform parent, Button template, string label, Action onClick, Font uiFont, Vector2 size)
        {
            GameObject btnGo = UnityEngine.Object.Instantiate(template.gameObject);
            btnGo.name = label + "Button";
            btnGo.transform.SetParent(parent, false);

            // 移除回调游戏菜单系统的事件组件与本地化组件（否则文本会被覆盖、悬停/点击触发游戏菜单逻辑）
            foreach (var h in btnGo.GetComponentsInChildren<MenuButtonEventHandler>(true))
                UnityEngine.Object.Destroy(h);
            foreach (var l in btnGo.GetComponentsInChildren<LocalizedText>(true))
                UnityEngine.Object.Destroy(l);
            foreach (var le in btnGo.GetComponents<LayoutElement>())
                UnityEngine.Object.Destroy(le);

            var btn = btnGo.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();

            // 禁用手柄/键盘导航，避免与游戏菜单导航串扰；悬停/按下保持原生过渡
            Navigation nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;

            // 常态颜色略变深（与浅色边框区分层次）
            ColorBlock cb = btn.colors;
            cb.normalColor = ButtonTintColor;
            btn.colors = cb;

            // 布局：固定数值尺寸，面板底部居中锚定（pivot 中心，便于坐到面板外底边框栏上）
            // 注：IL2CPP 下 btnGo.transform 的托管包装为 Transform，强转 RectTransform 会抛异常，必须 GetComponent
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.sizeDelta = size;
            btnRt.anchorMin = btnRt.anchorMax = new Vector2(0.5f, 0f);
            btnRt.pivot = new Vector2(0.5f, 0.5f);
            btnRt.anchoredPosition = Vector2.zero;
            btnRt.localScale = Vector3.one;

            // 克隆按钮携带的文本子对象锚点固定且带裁剪（按钮缩放+字号调大后整行被裁掉不显示），
            // 且可能挂有游戏字体特效组件，直接删除并重建全参数自控的文本。
            // 注意：新建文本必须缓存到 _lastButtonLabel 供调用方取用——Destroy 延迟到帧末，
            // 此时 GetComponentInChildren 仍会取到待销毁的旧文本（其原生对象帧末销毁后 set_text 必抛 NRE）
            var oldTxt = btnGo.GetComponentInChildren<Text>(true);
            if (oldTxt != null) UnityEngine.Object.Destroy(oldTxt.gameObject);
            _lastButtonLabel = CreateLabel(btnGo.transform, label, uiFont,
                Mathf.Max(10, Mathf.RoundToInt(btnRt.sizeDelta.y * ButtonFontHeightRatio)), ButtonLabelColor,
                btnRt.sizeDelta.y * LabelRiseRatio);

            // IL2CPP 委托需经 DelegateSupport 转换后才能注册进原生 UnityEvent
            UnityAction action = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            btn.onClick.AddListener(action);
            return btn;
        }

        /// <summary>
        /// 为选项行挂整行按钮：悬停/按下时行边框平滑变亮（ColorTint 过渡动画）。
        /// 调用方自行决定是否挂 onClick（功能页整行可点；快捷键行仅作悬停动画载体）。
        /// </summary>
        private static Button AddRowButton(GameObject rowGo, Image rowBg)
        {
            var btn = rowGo.AddComponent<Button>();
            btn.targetGraphic = rowBg;
            btn.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = btn.colors;
            cb.normalColor = RowBgColor;
            cb.highlightedColor = RowHoverColor;
            cb.pressedColor = RowHighlightColor;
            cb.selectedColor = RowBgColor;
            cb.disabledColor = RowBgColor;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.12f;
            btn.colors = cb;
            Navigation nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;
            return btn;
        }

        /// <summary>当前打开窗口快捷键的显示文本（供屏幕提示等模块读取）。</summary>
        public static string OpenKeyDisplay => _cfgOpenWindowKey != null ? _cfgOpenWindowKey.Value : "F3+F4";

        /// <summary>
        /// 在指定父级下新建全填充、双方向溢出的文本（居中），参数完全自控。
        /// yOffset 将文本框整体上移，修正动态字体行框偏下造成的视觉下沉。
        /// </summary>
        private static Text CreateLabel(Transform parent, string content, Font uiFont, int fontSize, Color color, float yOffset = 0f)
        {
            var labelGo = new GameObject("Label");
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelGo.transform.SetParent(parent, false);
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(0f, yOffset);
            labelRt.offsetMax = new Vector2(0f, yOffset);
            var txt = labelGo.AddComponent<Text>();
            if (uiFont != null) txt.font = uiFont;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = color;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        /// <summary>
        /// 构建顶部活页索引页签（克隆游戏按钮缩小版）。
        /// 配色：未选中暗、悬停/选中亮；选中页签由 ShowPage 置为 interactable=false，
        /// 借 disabledColor 呈现常亮选中态。
        /// </summary>
        private Button BuildTab(Transform parent, Button template, string label, Font uiFont, float width, Action onClick)
        {
            GameObject tabGo = UnityEngine.Object.Instantiate(template.gameObject);
            tabGo.name = label + "Tab";
            tabGo.transform.SetParent(parent, false);

            foreach (var h in tabGo.GetComponentsInChildren<MenuButtonEventHandler>(true))
                UnityEngine.Object.Destroy(h);
            foreach (var l in tabGo.GetComponentsInChildren<LocalizedText>(true))
                UnityEngine.Object.Destroy(l);
            foreach (var le in tabGo.GetComponents<LayoutElement>())
                UnityEngine.Object.Destroy(le);

            var tab = tabGo.GetComponent<Button>();
            tab.onClick.RemoveAllListeners();

            Navigation nav = tab.navigation;
            nav.mode = Navigation.Mode.None;
            tab.navigation = nav;

            tab.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = tab.colors;
            cb.normalColor = TabNormalColor;
            cb.highlightedColor = TabActiveColor;
            cb.pressedColor = TabActiveColor;
            cb.selectedColor = TabActiveColor;
            cb.disabledColor = TabActiveColor;   // 选中态：interactable=false 时显示亮色
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            tab.colors = cb;

            var tabRt = tabGo.GetComponent<RectTransform>();
            tabRt.anchorMin = tabRt.anchorMax = new Vector2(0.5f, 1f);
            tabRt.pivot = new Vector2(0.5f, 0.5f);
            tabRt.sizeDelta = new Vector2(width, TabBarHeight);
            tabRt.localScale = Vector3.one;

            var oldTxt = tabGo.GetComponentInChildren<Text>(true);
            if (oldTxt != null) UnityEngine.Object.Destroy(oldTxt.gameObject);
            CreateLabel(tabGo.transform, label, uiFont, TabFontSize, Color.white, TabBarHeight * LabelRiseRatio);

            UnityAction action = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            tab.onClick.AddListener(action);
            return tab;
        }

        /// <summary>构建一个分页内容区（placeholder 非空时显示全填充占位文本，随 ShowPage 切换显隐）。</summary>
        private GameObject BuildPage(Transform parent, string placeholder, Font uiFont, float topOffset, float bottomOffset)
        {
            var pageGo = new GameObject("Page_" + (placeholder ?? "Content"));
            var pageRt = pageGo.AddComponent<RectTransform>();
            pageGo.transform.SetParent(parent, false);
            pageRt.anchorMin = Vector2.zero;
            pageRt.anchorMax = Vector2.one;
            pageRt.offsetMin = new Vector2(0f, bottomOffset);   // 左右无内缩（选项边框与内窗口左右相接）
            pageRt.offsetMax = new Vector2(0f, -topOffset);
            if (placeholder != null)
                CreateLabel(pageGo.transform, placeholder, uiFont, 28, new Color(1f, 1f, 1f, 0.85f));
            return pageGo;
        }

        /// <summary>
        /// 在功能开关页中构建一行开关项：说明文本在左（下方附浅灰解释文字），右侧为方形槽开关
        /// （克隆游戏按钮的像素边框样式，点击切换，状态块绿=开启、深棕=关闭），切换后通知地图覆盖层即时重刷新。
        /// rowIndex 决定纵向位置（从内容顶部往下排）。
        /// </summary>
        /// <returns>行 CanvasGroup（用于子选项的变淡/禁交互控制）</returns>
        private CanvasGroup BuildToggleRow(Transform parent, Button template, string label, string desc, int rowIndex, Font uiFont, Func<bool> getter, Action<bool> setter, float indent = 0f)
        {
            var rowGo = new GameObject("ToggleRow_" + label);
            var rowRt = rowGo.AddComponent<RectTransform>();
            rowGo.transform.SetParent(parent, false);
            rowRt.anchorMin = rowRt.anchorMax = new Vector2(0f, 1f);
            rowRt.pivot = new Vector2(0f, 1f);
            rowRt.sizeDelta = new Vector2(ToggleRowWidth, ToggleRowHeight);
            rowRt.anchoredPosition = new Vector2(0f, -(14f + rowIndex * (ToggleRowHeight + ToggleRowGap)));   // 左对齐内容区左缘

            // 行边框底色：拍平按钮精灵九宫格，比面板略亮，清楚区分不同选项的边界
            var rowBg = rowGo.AddComponent<Image>();
            Sprite rowSlotSprite = template.image != null ? template.image.sprite : null;
            if (rowSlotSprite != null)
            {
                rowBg.sprite = GetFlatSlotSprite(rowSlotSprite);
                rowBg.type = Image.Type.Sliced;
            }
            rowBg.color = RowBgColor;

            // 整行可点：点击选项框任意位置等效点击开关槽（事件沿父链上行至本按钮；
            // 点在开关槽上时由开关槽自身处理，不会重复触发）；悬停时行边框平滑变亮
            var rowBtn = AddRowButton(rowGo, rowBg);

            // 方形槽：克隆游戏按钮（保留像素边框与原生悬停过渡），行内右对齐
            GameObject boxGo = UnityEngine.Object.Instantiate(template.gameObject);
            boxGo.name = "Box";
            boxGo.transform.SetParent(rowGo.transform, false);
            foreach (var h in boxGo.GetComponentsInChildren<MenuButtonEventHandler>(true))
                UnityEngine.Object.Destroy(h);
            foreach (var l in boxGo.GetComponentsInChildren<LocalizedText>(true))
                UnityEngine.Object.Destroy(l);
            foreach (var le in boxGo.GetComponents<LayoutElement>())
                UnityEngine.Object.Destroy(le);
            var clonedTxt = boxGo.GetComponentInChildren<Text>(true);
            if (clonedTxt != null) UnityEngine.Object.Destroy(clonedTxt.gameObject);

            var boxBtn = boxGo.GetComponent<Button>();
            boxBtn.onClick.RemoveAllListeners();
            // 游戏按钮精灵下半部为暗色渐变，缩成方形槽后下侧过暗——换用拍平精灵（下侧=上侧颜色），
            // 颜色过渡保持克隆原值（原生悬停高亮）
            var boxImg = boxBtn.image;
            if (boxImg != null && boxImg.sprite != null)
                boxImg.sprite = GetFlatSlotSprite(boxImg.sprite);
            Navigation nav = boxBtn.navigation;
            nav.mode = Navigation.Mode.None;
            boxBtn.navigation = nav;

            var boxRt = boxGo.GetComponent<RectTransform>();
            boxRt.sizeDelta = new Vector2(ToggleBoxSize, ToggleBoxSize);
            boxRt.anchorMin = boxRt.anchorMax = new Vector2(1f, 0.5f);
            boxRt.pivot = new Vector2(1f, 0.5f);
            boxRt.anchoredPosition = new Vector2(-RowPadding, 0f);   // 行垂直中心（标题与解释文字之间），右缘留边框内边距
            boxRt.localScale = Vector3.one;

            // 状态块（内嵌于槽中央：开启绿 / 关闭深棕）
            var fillGo = new GameObject("Fill");
            var fillRt = fillGo.AddComponent<RectTransform>();
            fillGo.transform.SetParent(boxGo.transform, false);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = getter() ? ToggleOnColor : ToggleOffColor;
            fillRt.anchorMin = fillRt.anchorMax = new Vector2(0.5f, 0.5f);
            fillRt.sizeDelta = new Vector2(ToggleBoxSize - 10f, ToggleBoxSize - 10f);
            fillRt.anchoredPosition = Vector2.zero;

            UnityAction act = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction>((Action)(() =>
            {
                bool v = !getter();
                setter(v);
                fillImg.color = v ? ToggleOnColor : ToggleOffColor;
                MapOverlayBehaviour.RequestHeavyRefresh();
                SaveSettings();
            }));
            boxBtn.onClick.AddListener(act);
            rowBtn.onClick.AddListener(act);   // 选项框任意位置点击同样切换

            // 说明文本（行内左侧上部 44 高区域，右端让出开关位置）
            var labelGo = new GameObject("Text");
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelGo.transform.SetParent(rowGo.transform, false);
            var txt = labelGo.AddComponent<Text>();
            if (uiFont != null) txt.font = uiFont;
            txt.text = label;
            txt.fontSize = ToggleLabelFontSize;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = new Color(1f, 1f, 1f, 0.9f);
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            // 无解释文字时选项文字占满整行高度（上下居中），有解释文字时下部留出说明区
            float labelBottomInset = string.IsNullOrEmpty(desc) ? 0f : ToggleDescZoneHeight;
            labelRt.offsetMin = new Vector2(indent + RowPadding, labelBottomInset);
            labelRt.offsetMax = new Vector2(-(ToggleBoxSize + 14f + RowPadding), 0f);

            // 解释文字（行内左侧下部 22 高区域，浅灰色）
            var descGo = new GameObject("Desc");
            var descRt = descGo.AddComponent<RectTransform>();
            descGo.transform.SetParent(rowGo.transform, false);
            var descTxt = descGo.AddComponent<Text>();
            if (uiFont != null) descTxt.font = uiFont;
            descTxt.text = desc;
            descTxt.fontSize = ToggleDescFontSize;
            descTxt.alignment = TextAnchor.MiddleLeft;
            descTxt.color = ToggleDescColor;
            descTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
            descTxt.verticalOverflow = VerticalWrapMode.Overflow;
            descRt.anchorMin = Vector2.zero;
            descRt.anchorMax = Vector2.one;
            descRt.offsetMin = new Vector2(indent + RowPadding, 0f);
            // 注意 offsetMax 负值才向内：正值会把文本区顶到行顶之外（与主标签重叠）
            descRt.offsetMax = new Vector2(-(ToggleBoxSize + 14f + RowPadding), -(ToggleRowHeight - ToggleDescZoneHeight));

            _toggles.Add(new ToggleEntry { Fill = fillImg });
            return rowGo.AddComponent<CanvasGroup>();
        }

        /// <summary>按功能页可滚动范围换算每格滚轮的像素距离（WheelStep × 可滚动范围）；功能页不可滚动时回退 0.6 行高。</summary>
        private float FeatureWheelPixels()
        {
            if (_scrollRect == null || _scrollRect.viewport == null) return WheelStep * ToggleRowHeight;
            float scrollable = _scrollRect.content.rect.height - _scrollRect.viewport.rect.height;
            return scrollable > 0f ? WheelStep * scrollable : WheelStep * ToggleRowHeight;
        }

        /// <summary>
        /// 在分页中创建可滚动内容视图：RectMask2D 裁剪 + ScrollRect（自带滚轮与内容拖动），
        /// 右侧让出滚动条位置。返回 ScrollRect 并输出内容容器（行项加入其中）。
        /// </summary>
        private ScrollRect CreateScrollContent(Transform pageTransform, float contentHeight, out RectTransform contentRt)
        {
            var scrollGo = new GameObject("ScrollView");
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollGo.transform.SetParent(pageTransform, false);
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;   // 内容区占满页宽（滚动条已移至右边框栏）
            scrollGo.AddComponent<RectMask2D>();
            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 0f;   // 自带滚轮关闭（改由 Update 手动控制，不限指针位置）
            scrollRect.inertia = false;   // 关闭惯性，滚动跟手
            scrollRect.viewport = scrollRt;

            var contentGo = new GameObject("Content");
            contentRt = contentGo.AddComponent<RectTransform>();
            contentGo.transform.SetParent(scrollGo.transform, false);
            contentRt.anchorMin = contentRt.anchorMax = new Vector2(0.5f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = new Vector2(ToggleRowWidth, contentHeight);   // 与页区同宽（行边框与内窗口同宽）
            contentRt.anchoredPosition = Vector2.zero;
            scrollRect.content = contentRt;
            return scrollRect;
        }

        /// <summary>
        /// 为可滚动视图挂接滚动条：位于面板右缘外的右边框栏内（与边框紧贴、贯穿面板全高），
        /// 深色背景槽 + 拍平按钮精灵滑块（九宫格），支持拖动；滚轮由 Update 手动控制。
        /// 内容不超高时自动隐藏（AutoHide）。返回滚动条 GameObject（多页时按页切换显隐）。
        /// </summary>
        private GameObject AttachScrollbar(Transform panelTransform, ScrollRect scrollRect, Sprite slotSprite)
        {
            var sbGo = new GameObject("Scrollbar");
            var sbRt = sbGo.AddComponent<RectTransform>();
            sbGo.transform.SetParent(panelTransform, false);
            // 锚定面板右缘全高，向外偏移至右边框栏中央（offsetMin/Max.x 为距右缘的外伸量）
            sbRt.anchorMin = new Vector2(1f, 0f);
            sbRt.anchorMax = new Vector2(1f, 1f);
            sbRt.offsetMin = new Vector2(SideBarWidth * 0.5f - ScrollbarWidth * 0.5f, 0f);
            sbRt.offsetMax = new Vector2(SideBarWidth * 0.5f + ScrollbarWidth * 0.5f, 0f);
            var sbBg = sbGo.AddComponent<Image>();
            sbBg.color = new Color(0.09f, 0.075f, 0.065f, 1f);

            var areaGo = new GameObject("Sliding Area");
            var areaRt = areaGo.AddComponent<RectTransform>();
            areaGo.transform.SetParent(sbGo.transform, false);
            areaRt.anchorMin = Vector2.zero;
            areaRt.anchorMax = Vector2.one;
            areaRt.offsetMin = new Vector2(2f, 2f);
            areaRt.offsetMax = new Vector2(-2f, -2f);

            var handleGo = new GameObject("Handle");
            var handleRt = handleGo.AddComponent<RectTransform>();
            handleGo.transform.SetParent(areaGo.transform, false);
            // 标准 Scrollbar Handle 配置：相对滑动区全拉伸（Scrollbar 接管后会调整垂直部分）
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;
            var handleImg = handleGo.AddComponent<Image>();
            if (slotSprite != null)
            {
                handleImg.sprite = GetFlatSlotSprite(slotSprite);
                handleImg.type = Image.Type.Sliced;
            }
            handleImg.color = new Color(0.55f, 0.45f, 0.36f, 1f);

            var sb = sbGo.AddComponent<Scrollbar>();
            sb.handleRect = handleRt;
            sb.targetGraphic = handleImg;
            sb.direction = Scrollbar.Direction.BottomToTop;
            ColorBlock cb = sb.colors;
            cb.highlightedColor = new Color(0.68f, 0.56f, 0.45f, 1f);
            cb.pressedColor = new Color(0.78f, 0.65f, 0.52f, 1f);
            sb.colors = cb;
            Navigation nav = sb.navigation;
            nav.mode = Navigation.Mode.None;
            sb.navigation = nav;

            scrollRect.verticalScrollbar = sb;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return sbGo;
        }

        // ==================== 快捷键设置 ====================

        private const float HotkeyButtonWidth = 180f;
        private static readonly Vector2 WaitButtonSize = new(120f, 40f);   // 等待界面确定/取消按钮尺寸

        /// <summary>快捷键行的当前键位显示文本（打开窗口键读窗口配置；游戏键读各自改绑配置）。</summary>
        private static string GetRowKeyDisplay(HotkeyRow row)
            => row.Binding != null ? row.Binding.Entry.Value : _cfgOpenWindowKey.Value;

        /// <summary>
        /// 构建快捷键设置行：与功能页开关行同款布局（行高/行宽/行边框一致）——
        /// 左侧操作名（下方附浅灰解释文字），右侧快捷键按钮（显示当前键），点击按钮进入等待按键状态。
        /// rowIndex 决定纵向位置（从内容顶部往下排，与功能页行位一致）。
        /// </summary>
        private void BuildHotkeyRow(Transform parent, Button template, HotkeyRow row, string label, string desc, Font uiFont, int rowIndex)
        {
            var rowGo = new GameObject("HotkeyRow_" + label);
            row.Rt = rowGo.AddComponent<RectTransform>();
            rowGo.transform.SetParent(parent, false);
            row.Rt.anchorMin = row.Rt.anchorMax = new Vector2(0f, 1f);
            row.Rt.pivot = new Vector2(0f, 1f);
            row.Rt.sizeDelta = new Vector2(ToggleRowWidth, ToggleRowHeight);
            row.Rt.anchoredPosition = new Vector2(0f, -(14f + rowIndex * (ToggleRowHeight + ToggleRowGap)));

            // 行边框底色（与功能页一致；等待按键时整框提亮）
            row.Bg = rowGo.AddComponent<Image>();
            Sprite rowSlotSprite = template.image != null ? template.image.sprite : null;
            if (rowSlotSprite != null)
            {
                row.Bg.sprite = GetFlatSlotSprite(rowSlotSprite);
                row.Bg.type = Image.Type.Sliced;
            }
            row.Bg.color = RowBgColor;
            AddRowButton(rowGo, row.Bg);   // 悬停时行边框平滑变亮（与功能页一致，无点击动作）

            // 左：操作名（行内左侧上部 44 高区域，与功能页主标签同字体同色同位）
            var labelGo = new GameObject("Text");
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelGo.transform.SetParent(rowGo.transform, false);
            var txt = labelGo.AddComponent<Text>();
            if (uiFont != null) txt.font = uiFont;
            txt.text = label;
            txt.fontSize = ToggleLabelFontSize;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = new Color(1f, 1f, 1f, 0.9f);
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            // 无解释文字时选项文字占满整行高度（上下居中），有解释文字时下部留出说明区
            float labelBottomInset = string.IsNullOrEmpty(desc) ? 0f : ToggleDescZoneHeight;
            labelRt.offsetMin = new Vector2(RowPadding, labelBottomInset);
            labelRt.offsetMax = new Vector2(-(HotkeyButtonWidth + 14f + RowPadding), 0f);

            // 左下：解释文字（行内左侧下部 22 高区域，浅灰色，与功能页一致）
            var descGo = new GameObject("Desc");
            var descRt = descGo.AddComponent<RectTransform>();
            descGo.transform.SetParent(rowGo.transform, false);
            var descTxt = descGo.AddComponent<Text>();
            if (uiFont != null) descTxt.font = uiFont;
            descTxt.text = desc;
            descTxt.fontSize = ToggleDescFontSize;
            descTxt.alignment = TextAnchor.MiddleLeft;
            descTxt.color = ToggleDescColor;
            descTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
            descTxt.verticalOverflow = VerticalWrapMode.Overflow;
            descRt.anchorMin = Vector2.zero;
            descRt.anchorMax = Vector2.one;
            descRt.offsetMin = new Vector2(RowPadding, 0f);
            descRt.offsetMax = new Vector2(-(HotkeyButtonWidth + 14f + RowPadding), -(ToggleRowHeight - ToggleDescZoneHeight));

            // 右：快捷键按钮（显示当前键，点击进入等待输入；位置与功能页开关槽一致）
            HotkeyRow captured = row;   // IL2CPP 委托闭包持有行引用
            Button keyBtn = BuildMenuButton(rowGo.transform, template, GetRowKeyDisplay(row),
                () => BeginWaitKey(captured), uiFont, new Vector2(HotkeyButtonWidth, 40f));
            var keyBtnRt = keyBtn.GetComponent<RectTransform>();
            keyBtnRt.anchorMin = keyBtnRt.anchorMax = new Vector2(1f, 0.5f);
            keyBtnRt.pivot = new Vector2(1f, 0.5f);
            keyBtnRt.anchoredPosition = new Vector2(-RowPadding, 0f);   // 行垂直中心，与功能页开关槽同位
            row.ButtonLabel = _lastButtonLabel;   // 取 BuildMenuButton 新建的文本（不可 GetComponentInChildren，会取到待销毁旧文本）
            _hotkeyRows.Add(row);
        }

        /// <summary>点击快捷键按钮：进入等待按键状态（全屏变暗、修改项提升到遮罩之上整框提亮、下方紧贴确定/取消按钮）。</summary>
        private void BeginWaitKey(HotkeyRow row)
        {
            if (_waitingForKey) return;
            _waitingForKey = true;
            _captureDone = false;
            _capturedKeys.Clear();
            _activeRow = row;
            row.ButtonLabel.text = "请按键...";

            // 全屏变暗层（拦截一切点击，盖住窗口其余部分）
            _waitDimGo = new GameObject("WaitDim");
            var dimRt = _waitDimGo.AddComponent<RectTransform>();
            _waitDimGo.transform.SetParent(_canvasGo.transform, false);
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = Vector2.zero;
            dimRt.offsetMax = Vector2.zero;
            var dimImg = _waitDimGo.AddComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, 0.85f);
            dimImg.raycastTarget = true;

            // 修改项（快捷键行）提升到遮罩之上保持屏幕位置（整框提亮高亮）
            _rowOrigParent = row.Rt.parent;
            _rowOrigIndex = row.Rt.GetSiblingIndex();
            _rowOrigPos = row.Rt.anchoredPosition;
            row.Rt.SetParent(_canvasGo.transform, true);
            row.Rt.SetAsLastSibling();
            row.Bg.color = RowHighlightColor;

            // 确定/取消按钮：作为行子对象紧贴高亮行下方（锚点=行底缘中心，随行一起位于遮罩之上，
            // 不再使用画布坐标——行提升后为左上锚点、按钮为中心锚点，混用坐标系会导致按钮跑到屏幕下方）
            var templateTxt = _buttonTemplate.GetComponentInChildren<Text>(true);
            Font font = ResolveUIFont(templateTxt != null ? templateTxt.font : null);
            Button okBtn = BuildMenuButton(row.Rt, _buttonTemplate, "确定",
                () => EndWaitKey(true), font, WaitButtonSize);
            Button cancelBtn = BuildMenuButton(row.Rt, _buttonTemplate, "取消",
                () => EndWaitKey(false), font, WaitButtonSize);
            var okRt = okBtn.GetComponent<RectTransform>();
            var cancelRt = cancelBtn.GetComponent<RectTransform>();
            // 对齐"请按键"按钮正下方：快捷键按钮中心在行内的 x = 行半宽 - 右内边距 - 按钮半宽
            float keyBtnCenterX = ToggleRowWidth * 0.5f - RowPadding - HotkeyButtonWidth * 0.5f;
            float waitBtnY = -(8f + WaitButtonSize.y * 0.5f);   // 行底缘之下 8px 间隙
            okRt.anchoredPosition = new Vector2(keyBtnCenterX - 68f, waitBtnY);
            cancelRt.anchoredPosition = new Vector2(keyBtnCenterX + 68f, waitBtnY);
            _waitButtonGos.Add(okBtn.gameObject);
            _waitButtonGos.Add(cancelBtn.gameObject);
        }

        /// <summary>结束等待按键状态：apply=true 应用新键并持久化，false 放弃恢复显示；行归位并恢复底色、遮罩与按钮销毁，屏幕恢复正常。</summary>
        private void EndWaitKey(bool apply)
        {
            HotkeyRow row = _activeRow;
            if (apply && _capturedKeys.Count > 0)
            {
                string s = FormatKeys(_capturedKeys);
                if (row.Binding != null)
                {
                    row.Binding.Entry.Value = s;   // ConfigEntry 赋值自动写盘
                    ApplyBinding(row.Binding);     // 同步应用到游戏 Rewired 键盘映射（即时生效）
                }
                else
                {
                    _cfgOpenWindowKey.Value = s;   // ConfigEntry 赋值自动写盘
                    ParseOpenKey(s);
                }
                row.ButtonLabel.text = s;
            }
            else
            {
                row.ButtonLabel.text = GetRowKeyDisplay(row);   // 恢复原显示
            }

            row.Bg.color = RowBgColor;   // 恢复选项框常态底色
            row.Rt.SetParent(_rowOrigParent, true);
            row.Rt.SetSiblingIndex(_rowOrigIndex);
            row.Rt.anchoredPosition = _rowOrigPos;

            if (_waitDimGo != null) { UnityEngine.Object.Destroy(_waitDimGo); _waitDimGo = null; }
            foreach (var go in _waitButtonGos)
                if (go != null) UnityEngine.Object.Destroy(go);
            _waitButtonGos.Clear();

            _waitingForKey = false;
            _captureDone = false;
            _activeRow = null;
        }

        /// <summary>
        /// 等待按键状态下每帧捕获键盘输入：记录按下的键（最多两个，支持组合键），
        /// 全部按键松开后视为输入完成；鼠标/手柄键忽略。捕获过程实时显示在行按钮上。
        /// </summary>
        private void CaptureKeyInput()
        {
            if (_captureDone) return;
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
                _activeRow.ButtonLabel.text = FormatKeys(_capturedKeys);
                if (!Input.anyKey) _captureDone = true;   // 全部松开→输入完成，等待确定/取消
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
        private static void ResolveActionId(GameKeyBinding b)
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
        private static void ApplyBinding(GameKeyBinding b)
        {
            if (b.ActionId < 0) return;
            Rewired.Player player = GetRewiredPlayer(b.PlayerIndex);
            if (player != null) ApplyBinding(b, player);
        }

        /// <summary>将绑定键位写入玩家键盘映射中对应动作的元素（轴分量只改对应方向），Rewired 每帧读取映射故即时生效。</summary>
        private static void ApplyBinding(GameKeyBinding b, Rewired.Player player)
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

        /// <summary>
        /// 生成"拍平"的槽体精灵：游戏按钮精灵下半部为暗色渐变，克隆其纹理并将下半部
        /// 用上半部的一行像素覆盖，使下侧颜色与上侧一致；九宫格 border 保留，失败回退原精灵。
        /// </summary>
        private static Sprite GetFlatSlotSprite(Sprite src)
        {
            if (_flatSlotSprite != null) return _flatSlotSprite;
            try
            {
                Rect r = src.textureRect;
                int w = Mathf.CeilToInt(r.width), h = Mathf.CeilToInt(r.height);
                // 图集纹理通常不可直接读像素，经 RenderTexture 中转
                RenderTexture rt = RenderTexture.GetTemporary(src.texture.width, src.texture.height, 0, RenderTextureFormat.ARGB32);
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(src.texture, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                // 纹理 y 向上：取偏上区域一行，覆盖底部区域（视觉上下侧=上侧颜色）
                int srcRow = Mathf.RoundToInt(h * 0.62f);
                Color[] row = tex.GetPixels(0, srcRow, w, 1);
                for (int y = 0; y < Mathf.RoundToInt(h * 0.38f); y++)
                    tex.SetPixels(0, y, w, 1, row);
                tex.filterMode = FilterMode.Point;   // 像素风保持锐利
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;

                _flatSlotSprite = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f),
                    src.pixelsPerUnit, 0, SpriteMeshType.FullRect, src.border);
            }
            catch { _flatSlotSprite = src; }
            return _flatSlotSprite;
        }

        /// <summary>切换分页：显示目标页，页签选中态通过 interactable 切换（选中页签常亮且不可再点）；滚动条随页切换显隐。</summary>
        private void ShowPage(int index)
        {
            _pageIndex = index;
            if (_page1 != null) _page1.SetActive(index == 0);
            if (_page2 != null) _page2.SetActive(index == 1);
            if (_page3 != null) _page3.SetActive(index == 2);
            if (_tab1 != null) _tab1.interactable = index != 0;
            if (_tab2 != null) _tab2.interactable = index != 1;
            if (_tab3 != null) _tab3.interactable = index != 2;
            if (_toggleScrollbarGo != null) _toggleScrollbarGo.SetActive(index == 0);
            if (_funcScrollbarGo != null) _funcScrollbarGo.SetActive(index == 1);
            if (_hotkeyScrollbarGo != null) _hotkeyScrollbarGo.SetActive(index == 2);
        }

        /// <summary>获取游戏原生 UI 资源（暂停菜单"继续游戏"按钮模板 + 动态中文字体），供各 MOD 窗口克隆复用。</summary>
        internal static bool TryGetNativeAssets(out Button buttonTemplate, out Font font)
        {
            buttonTemplate = null;
            font = null;
            try
            {
                Menu menu = SingletonMonoBehaviour<Menu>.Inst;
                if (menu == null) return false;
                Button src = menu.continueButton;
                if (src == null && menu.mainButtons != null)
                    src = menu.mainButtons.GetComponentInChildren<Button>(true);
                if (src == null) return false;
                buttonTemplate = src;
                Text t = src.GetComponentInChildren<Text>(true);
                // 游戏菜单字体为位图字体时字形图集只含游戏文本用到的汉字（缺"组/默"等字），
                // 需回退系统动态中文字体；为动态字体时直接沿用（与暂停面板按钮一致）
                font = ResolveUIFont(t != null ? t.font : null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 取窗口 UI 字体：游戏菜单字体（Language.current.menuFont）为动态字体时直接沿用，
        /// 与暂停面板按钮完全一致；为位图字体时字形图集只含游戏文本用到的汉字，
        /// 回退到系统动态中文字体（微软雅黑/黑体等），保证任意汉字可渲染。
        /// </summary>
        private static Font ResolveUIFont(Font clonedFont)
        {
            try
            {
                Texts texts = Language.current;
                Font f = texts != null && texts.menuFont != null ? texts.menuFont.font : null;
                if (f != null && f.dynamic) return f;
            }
            catch { }
            if (clonedFont != null && clonedFont.dynamic) return clonedFont;

            // 位图字体缺字形，回退系统动态中文字体（按常见程度依次尝试）
            string[] osFonts = { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "WenQuanYi Micro Hei" };
            foreach (string name in osFonts)
            {
                try
                {
                    Font f = Font.CreateDynamicFontFromOSFont(name, 32);
                    if (f != null) return f;
                }
                catch { }
            }
            return FindFallbackFont();
        }

        /// <summary>打开窗口快捷键的默认值（恢复默认与初始配置共用）。</summary>
        private const string DefaultOpenWindowKey = "F3+F4";

        // ---- 恢复默认确认弹窗 ----
        private GameObject _confirmGo;   // 确认弹窗整体（全屏遮罩+对话框）

        /// <summary>点击"恢复默认"：弹出确认窗口，只恢复当前页设置（确认后不跳转分页）。</summary>
        private void ShowRestoreConfirm()
        {
            if (_confirmGo != null || _waitingForKey) return;
            string pageName = _pageIndex == 0 ? "显示设置" : (_pageIndex == 1 ? "功能设置" : "快捷键设置");

            // 全屏遮罩（拦截对窗口其余部分的点击）
            _confirmGo = new GameObject("RestoreConfirm");
            var dimRt = _confirmGo.AddComponent<RectTransform>();
            _confirmGo.transform.SetParent(_canvasGo.transform, false);
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = Vector2.zero;
            dimRt.offsetMax = Vector2.zero;
            _confirmGo.transform.SetAsLastSibling();
            var dimImg = _confirmGo.AddComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, 0.7f);
            dimImg.raycastTarget = true;

            // 对话框（与选项框同款拍平精灵边框，面板底色）
            var dlgGo = new GameObject("Dialog");
            var dlgRt = dlgGo.AddComponent<RectTransform>();
            dlgGo.transform.SetParent(_confirmGo.transform, false);
            var dlgImg = dlgGo.AddComponent<Image>();
            Sprite slotSprite = _buttonTemplate.image != null ? _buttonTemplate.image.sprite : null;
            if (slotSprite != null)
            {
                dlgImg.sprite = GetFlatSlotSprite(slotSprite);
                dlgImg.type = Image.Type.Sliced;
            }
            dlgImg.color = PanelColor;
            dlgRt.anchorMin = dlgRt.anchorMax = new Vector2(0.5f, 0.5f);
            dlgRt.pivot = new Vector2(0.5f, 0.5f);
            dlgRt.sizeDelta = new Vector2(520f, 220f);
            dlgRt.anchoredPosition = Vector2.zero;

            var templateTxt = _buttonTemplate.GetComponentInChildren<Text>(true);
            Font font = ResolveUIFont(templateTxt != null ? templateTxt.font : null);

            // 提示文字（上移留出按钮位置）
            CreateLabel(dlgGo.transform, "确认将“" + pageName + "”恢复默认设置吗？", font, 26,
                new Color(1f, 1f, 1f, 0.92f), 40f);

            // 确认/取消按钮（对话框底部居中并排）
            var dlgBtnSize = new Vector2(160f, 52f);
            Button okBtn = BuildMenuButton(dlgGo.transform, _buttonTemplate, "确认",
                () => { RestoreCurrentPage(); CloseRestoreConfirm(); }, font, dlgBtnSize);
            Button cancelBtn = BuildMenuButton(dlgGo.transform, _buttonTemplate, "取消",
                CloseRestoreConfirm, font, dlgBtnSize);
            okBtn.GetComponent<RectTransform>().anchoredPosition = new Vector2(-92f, 42f);
            cancelBtn.GetComponent<RectTransform>().anchoredPosition = new Vector2(92f, 42f);
        }

        /// <summary>关闭恢复默认确认弹窗。</summary>
        private void CloseRestoreConfirm()
        {
            if (_confirmGo != null) { UnityEngine.Object.Destroy(_confirmGo); _confirmGo = null; }
        }

        /// <summary>只恢复当前页设置为默认：显示页重置全部显示开关；功能页重置两个功能开关；快捷键页重置打开窗口键与全部游戏按键改绑。保持在当前页。</summary>
        private void RestoreCurrentPage()
        {
            if (_pageIndex == 0)
            {
                ShowAllDefenses = ShowOnlyBuiltDefenses = ShowAllCamps = ShowStatues = ShowMountSpawns
                    = ShowGreedHealth = ShowCropProgress = ShowChests = ShowTechPoints
                    = ShowMap = ShowMapEnemies = ShowClassStats = ShowCoins = ShowTimeSeason = false;
                for (int i = 0; i < _displayToggleCount && i < _toggles.Count; i++)
                    if (_toggles[i].Fill != null) _toggles[i].Fill.color = ToggleOffColor;
                UpdateMapSubRowStates();
                MapOverlayBehaviour.RequestHeavyRefresh();
                SaveSettings();
            }
            else if (_pageIndex == 1)
            {
                LinkPurseTreasury = BellInstantBoard = false;
                for (int i = _displayToggleCount; i < _toggles.Count; i++)
                    if (_toggles[i].Fill != null) _toggles[i].Fill.color = ToggleOffColor;
                SaveSettings();
            }
            else
            {
                _cfgOpenWindowKey.Value = DefaultOpenWindowKey;   // ConfigEntry 赋值自动写盘
                ParseOpenKey(DefaultOpenWindowKey);
                foreach (var b in _gameBindings)
                {
                    b.Entry.Value = b.DefaultKey.ToString();
                    ApplyBinding(b);
                }
                foreach (var r in _hotkeyRows)
                    if (r.ButtonLabel != null) r.ButtonLabel.text = GetRowKeyDisplay(r);
            }
        }

        /// <summary>游戏字体不可用时回退到 Unity 内置动态字体（新旧运行时命名均尝试）。</summary>
        private static Font FindFallbackFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            return null;
        }

        /// <summary>
        /// 确保有可用的 UI 事件系统：游戏自身 EventSystem 活跃时直接复用，
        /// 否则自建一套（StandaloneInputModule），关窗时停用以避免多事件系统告警。
        /// </summary>
        private void EnsureEventSystem()
        {
            EventSystem current = EventSystem.current;
            if (current != null && current.isActiveAndEnabled)
            {
                _eventSystemOurs = false;
                return;
            }

            if (_eventSystemGo == null)
            {
                _eventSystemGo = new GameObject("BetterKingdom.ModWindow.EventSystem");
                _eventSystemGo.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_eventSystemGo);
                _eventSystemGo.AddComponent<EventSystem>();
                _eventSystemGo.AddComponent<StandaloneInputModule>();
            }
            _eventSystemGo.SetActive(true);
            _eventSystemOurs = true;
        }
    }

    /// <summary>
    /// 模组窗口打开期间拦截游戏的暂停菜单入口（ESC 触发），
    /// 防止按 ESC 关窗的瞬间游戏菜单同时弹出。
    /// </summary>
    [HarmonyPatch(typeof(Game), "TryShowMenu")]
    public static class GameTryShowMenuPatch
    {
        public static bool Prefix() => !ModWindowBehaviour.IsOpen;
    }

    /// <summary>与 TryShowMenu 同理，拦截静态入口（内部可能不经 TryShowMenu）。</summary>
    [HarmonyPatch(typeof(Game), "ShowMenuIfInGame")]
    public static class GameShowMenuIfInGamePatch
    {
        public static bool Prefix() => !ModWindowBehaviour.IsOpen;
    }
}
