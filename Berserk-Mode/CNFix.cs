using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BerserkMode.CNFix
{
    // ============================== 插件入口 ==============================
    // 只负责装配：字体、词条、钩子、场景扫描，外加 F9 诊断转储。
    [BepInPlugin("berserkmode.cnfix", "Berserk Mode Chinese Fix", "1.2.0")]
    public class CNFixPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<int> FontSizeDelta;
        internal static ConfigEntry<bool> AutoDump;
        internal static Font CnFont;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

        private const uint FR_PRIVATE = 0x10;

        private void Awake()
        {
            Log = Logger;
            FontSizeDelta = Config.Bind("Font", "FontSizeDelta", 0,
                "换用中文字体时对原字号的增减，一般不用改");
            AutoDump = Config.Bind("Diagnostics", "AutoDump", false,
                "每次场景加载 10 秒后自动转储全部 Text 状态到 BepInEx/CNFix_dump.txt（排查问题时才开，平时关闭省 I/O）");

            LoadFont();
            Translation.Load();
            new Harmony("berserkmode.cnfix").PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo($"CNFix 已加载：字体={(CnFont != null)} 词条={Translation.Exact.Count} " +
                        $"（归一化 {Translation.Normalized.Count} 前缀规则 {Translation.Prefixes.Count}）");
        }

        private void Update()
        {
            // F9：在问题画面手动转储全部 Text 状态
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Diagnostics.Dump();
                Log.LogInfo("手动转储已触发（F9）");
            }
            if (dumpTimer < 0f) return;
            dumpTimer -= Time.unscaledDeltaTime;
            if (dumpTimer <= 0f)
            {
                dumpTimer = -1f;
                Diagnostics.Dump();
            }
        }

        private float dumpTimer = -1f;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 序列化在 Text 里的初始值不走 set_text，场景切换后主动过一遍；
            // 窄条裁剪由 OnPopulateMesh 钩子兜底，这里无需重复处理。
            int fonts = 0, translated = 0;
            var all = Resources.FindObjectsOfTypeAll<Text>();
            foreach (var t in all)
            {
                if (t == null) continue;
                var tr = Translation.Translate(t.text);
                if (tr != null)
                {
                    t.text = tr;
                    translated++;
                }
                if (TextTuner.Apply(t)) fonts++;
            }
            Log.LogInfo($"场景 {scene.name}：静态翻译 {translated}，换字体 {fonts}/{all.Length}");
            // 诊断转储调度：AutoDump 关闭时 -1（Update 早退），开启时 10 秒后转储一次
            dumpTimer = AutoDump.Value ? 10f : -1f;
        }

        private void LoadFont()
        {
            try
            {
                string fontsDir = Path.Combine(Paths.PluginPath, "CNFix", "fonts");
                string regular = Path.Combine(fontsDir, "AlibabaPuHuiTi-3-55-Regular.ttf");
                string bold = Path.Combine(fontsDir, "AlibabaPuHuiTi-3-85-Bold.ttf");
                if (!File.Exists(regular) || !File.Exists(bold))
                {
                    Log.LogWarning($"缺少阿里巴巴普惠体字体文件（应位于 {fontsDir}），文本仍会翻译但显示原字体（汉字将缺字）");
                    return;
                }
                // 两个字重同属一个家族：Regular 为默认面，Bold 供粗体文本取用
                foreach (var p in new[] { regular, bold })
                    if (AddFontResourceExW(p, FR_PRIVATE, IntPtr.Zero) == IntPtr.Zero)
                        Log.LogWarning($"注册字体失败：{p}");
                CnFont = Font.CreateDynamicFontFromOSFont("阿里巴巴普惠体 3.0", 100);
                Log.LogInfo("中文字体就绪：阿里巴巴普惠体 3.0");
            }
            catch (Exception e)
            {
                Log.LogError($"字体加载异常：{e}");
            }
        }
    }

    // ============================== Text 钩子 ==============================
    // 三个挂点，各自的职责：
    //   set_text      —— 文本赋值的总闸：翻译 + 换字体（覆盖游戏代码里所有 .text = 动态写入）
    //   OnEnable      —— 运行时实例化面板（设置/改键等不走 sceneLoaded 的）激活瞬间补一遍
    //   OnPopulateMesh —— 字形网格生成的一刻 rect 必是排版后的最终值，在此修窄条裁剪
    //                     （LayoutGroup/ContentSizeFitter/脚本驱动全在此前完成，事件驱动无轮询）
    [HarmonyPatch]
    internal static class TextHooks
    {
        internal static int EnableFires; // 诊断计数

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Text), "text", MethodType.Setter)]
        static void OnSetText(Text __instance, ref string value)
        {
            try
            {
                TextTuner.Apply(__instance);
                if (string.IsNullOrEmpty(value)) return;
                var tr = Translation.Translate(value);
                if (tr == null) return;
                value = tr;
                // 能力升级台说明为 3 行文本，中文字体行高偏大会把第三行压进下方绿字区域，压缩行距补偿
                if (tr.Contains("当前加成："))
                    __instance.lineSpacing = 0.8f;
            }
            catch (Exception e)
            {
                CNFixPlugin.Log.LogError($"set_text 处理异常：{e}\n原文：{value}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Text), "OnEnable")]
        static void OnEnable(Text __instance)
        {
            EnableFires++;
            try
            {
                TextTuner.Apply(__instance);
                var tr = Translation.Translate(__instance.text);
                if (tr != null) __instance.text = tr; // 回灌中文会被 CJK 快速否决，不会二次处理
            }
            catch (Exception e)
            {
                CNFixPlugin.Log.LogError($"OnEnable 处理异常：{e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Text), "OnPopulateMesh")]
        static void OnPopulate(Text __instance)
        {
            TextTuner.FixNarrow(__instance);
        }
    }

    // ============================== 文本外观调整 ==============================
    // 字体与窄条裁剪的全部逻辑收敛于此，供钩子与场景扫描共用。
    internal static class TextTuner
    {
        // 已放宽裁剪的文本（供诊断转储标注）
        internal static readonly HashSet<Text> NarrowFixed = new HashSet<Text>();

        // 换中文字体并按需调整字号；返回是否发生了换字体
        internal static bool Apply(Text t)
        {
            if (t == null || CNFixPlugin.CnFont == null || t.font == CNFixPlugin.CnFont) return false;
            t.font = CNFixPlugin.CnFont;
            var d = CNFixPlugin.FontSizeDelta.Value;
            if (d != 0) t.fontSize = Mathf.Max(1, t.fontSize + d);
            FixNarrow(t);
            return true;
        }

        // 中文字体行高度量大于原版像素字体：截断(Truncate)式窄条（如右下角诅咒一览，
        // rect 高 15px 装字号 20）会把字形整个裁出可见区 → 整行不可见，放宽为溢出。
        // 属性内部会 SetVerticesDirty，下一轮按溢出重绘；改完自身条件即不成立，不会反复重绘。
        internal static bool FixNarrow(Text t)
        {
            try
            {
                if (t == null || t.verticalOverflow != VerticalWrapMode.Truncate) return false;
                var h = t.rectTransform.rect.height;
                if (h <= 0f || h >= t.fontSize * 2f) return false; // 0=未排版 / 大框=正常多行，都不动
                t.verticalOverflow = VerticalWrapMode.Overflow;
                NarrowFixed.Add(t);
                return true;
            }
            catch { return false; } // rect 未初始化时静默放弃，OnPopulateMesh 下次还会经过
        }
    }

    // ============================== 翻译引擎 ==============================
    internal static class Translation
    {
        public static readonly Dictionary<string, string> Exact = new Dictionary<string, string>();
        public static readonly Dictionary<string, string> Normalized = new Dictionary<string, string>();
        public static readonly List<KeyValuePair<string, string>> Prefixes = new List<KeyValuePair<string, string>>();

        // 钩子全部在 Unity 主线程触发，普通字典即可；null 也缓存（负缓存），未命中串不反复走规则
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
        private static readonly HashSet<string> Missed = new HashSet<string>();

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex HasWord = new Regex("[A-Za-z]{3}", RegexOptions.Compiled);

        public static void Load()
        {
            string path = Path.Combine(Paths.PluginPath, "CNFix", "zh.tsv");
            if (!File.Exists(path))
            {
                CNFixPlugin.Log.LogError($"找不到翻译表：{path}");
                return;
            }
            foreach (var line in File.ReadAllLines(path))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string orig = line.Substring(0, tab).Replace("\\n", "\n");
                string trans = line.Substring(tab + 1).Replace("\\n", "\n");
                Exact[orig] = trans;
                var nk = NormKey(orig);
                if (nk.Length > 0 && nk != orig && !Normalized.ContainsKey(nk))
                    Normalized[nk] = trans;
            }
            foreach (var kv in PrefixRules) Prefixes.Add(kv);
            Prefixes.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        }

        public static string Translate(string s)
        {
            // 快速否决：原版游戏文本不含 CJK，中文串只可能是我们的翻译回灌。
            // 省掉整条规则链，也杜绝中文串污染缓存。
            if (string.IsNullOrEmpty(s) || HasCjk(s)) return null;
            string v;
            if (Cache.TryGetValue(s, out v)) return v;
            v = TranslateOnce(s);
            Cache[s] = v;
            return v;
        }

        private static bool HasCjk(string s)
        {
            foreach (var c in s)
                if (c >= 0x2E80) return true; // CJK 部首/标点/汉字/全角统一覆盖
            return false;
        }

        private static string NormKey(string s)
        {
            return Whitespace.Replace(s, " ").Trim();
        }

        // 拼接片段（区域名 / 难度名 / 死因等）递归查表
        private static string Part(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string v;
            if (Exact.TryGetValue(s, out v)) return v;
            if (Normalized.TryGetValue(NormKey(s), out v)) return v;
            return s;
        }

        // 场景序列化文本常带尾部 "\n"/"\n\n"（如 "STANDARD\n\n"），剥掉尾巴再查
        private static string TranslateOnce(string s)
        {
            string core = s.TrimEnd();
            if (core.Length > 0 && core != s)
            {
                var r = TranslateCore(core);
                if (r != null) return r;
            }
            return TranslateCore(s);
        }

        // ---- 声明式正则规则表：预编译，按序匹配，Eval 返回 null 表示本条不适用、继续下一条 ----
        private sealed class Rule
        {
            public readonly Regex Re;
            private readonly Func<Match, string> Eval;
            public Rule(string pattern, Func<Match, string> eval)
            {
                Re = new Regex(pattern, RegexOptions.Compiled);
                Eval = eval;
            }
            public string Run(string s)
            {
                var m = Re.Match(s);
                return m.Success ? Eval(m) : null;
            }
        }

        private static string G(Match m, int i) { return m.Groups[i].Value; }

        private static readonly Rule[] Rules =
        {
            // 结算区域用时行（时间被 <color=white> 包裹，见 PauseManager.DisplayZoneTimes）
            new Rule(@"^(.+?) - (?:<color=white>)?(\d\d:\d\d:\d\d)(?:</color>)?<color=orange> BEST</color>$",
                m => Part(G(m,1)) + " - " + G(m,2) + "<color=orange> 最佳</color>"),
            new Rule(@"^(.+?) - (?:<color=white>)?(\d\d:\d\d:\d\d)(?:</color>)?$",
                m => Part(G(m,1)) + " - " + G(m,2)),
            new Rule(@"^(.+?) - Defeated$",
                m => Part(G(m,1)) + " - 战败"),
            // 死因
            new Rule(@"^Killed by (.+)$", m => "死于 " + Part(G(m,1))),
            // 区域名
            new Rule(@"^Zone (\d+): (.+)$", m => "区域 " + G(m,1) + "：" + Part(G(m,2))),
            // 结算四行
            new Rule(@"^Zone reached: (\d)/6$", m => "已到达区域：" + G(m,1) + "/6"),
            new Rule(@"^PLAYER LEVEL: (\d+)/25$", m => "玩家等级 " + G(m,1) + "/25"),
            new Rule(@"^Level up (\d+)$", m => "升级到 " + G(m,1) + " 级"),
            new Rule(@"^REWARD \+(\d+)$", m => "奖励 +" + G(m,1)),
            // 竞技场与诅咒上限
            new Rule(@"^KILL (\d+) TO OPEN GATES$", m => "击杀 " + G(m,1) + " 以开启闸门"),
            new Rule(@"^HEALTH DRAINS IN (\d+)$", m => "生命将在 " + G(m,1) + " 秒后流失"),
            new Rule(@"^Health drains (\d+)% ?faster$", m => "生命流失加快 " + G(m,1) + "%"),
            new Rule(@"^Take a random curse for the next zone \(Max (\d+)\)$",
                m => "为下一区域抽一条随机诅咒（上限 " + G(m,1) + "）"),
            new Rule(@"^Difficulty has been set to (.+)$", m => "难度已设为 " + Part(G(m,1))),
            new Rule(@"^(\d+) STAT POINTS?$", m => G(m,1) + " 属性点"),
            new Rule(@"^Lvl\. ?(\d+)( MAX)?$", m => "等级 " + G(m,1) + (G(m,2).Length > 0 ? "（满级）" : "")),
            new Rule(@"^(\d+) to ascend$", m => "击杀 " + G(m,1) + " 次即可升格"),
            new Rule(@"^-(\d+) Max HP$", m => "生命上限 -" + G(m,1)),
            new Rule(@"^INTEREST (\d+)$", m => "利息 " + G(m,1)),
            new Rule(@"^SELECT (\d+) MORE ARTIFACTS?$", m => "再选 " + G(m,1) + " 件器物"),
            new Rule(@"^SELECT (\d+) ARTIFACTS$", m => "选择 " + G(m,1) + " 件器物"),
            new Rule(@"^Damage is deferred over ([\d.]+) seconds$", m => "伤害迟滞为 " + G(m,1) + " 秒"),
            // 诅咒选择条目：名字 <color=orange>+N</color>。名字必须查表命中，否则放行给后续规则——
            // 不加守卫会抢先命中 "ADDITIONAL CURSES <color=orange>+1</color>" 等结算行并原样返回
            // （非 null 即被当作翻译成功），把后面的前缀规则全部短路（结算面板漏翻的根因）
            new Rule(@"^(.+?) <color=orange>\+(\d+)</color>$", m =>
            {
                var name = Part(G(m,1));
                return name != G(m,1) ? name + " <color=orange>+" + G(m,2) + "</color>" : null;
            }),
            // Ambrosia / 人造小人等复活次数（InventoryItem.cs:1356 "+" + n + " Resurrection"）
            new Rule(@"^\+(\d+) Resurrections?$", m => "+" + G(m,1) + " 复活"),
            // 诅咒坛整句（ItemPicker.cs:1667）
            new Rule(@"^Take a random curse for the next zone \(Max (\d+)\)\. Get rewards if you survive\.$",
                m => "为下一区域抽一条随机诅咒（上限 " + G(m,1) + "）。活下来即可获得奖励。"),
            // 能力升级台提示（AbilityButton.cs:264）
            new Rule(@"^Ability\nCurrent level: (\d+)\nCurrent buff:$",
                m => "能力\n当前等级：" + G(m,1) + "\n当前加成："),
            // 能力加成行（AbilityButton.cs 各分支 "+N <单位>"），模板表命中才译，防误伤
            new Rule(@"^\+(\d+)(%?) (.+)$", m =>
            {
                string tpl;
                return BuffTemplates.TryGetValue(G(m,3), out tpl)
                    ? tpl.Replace("{0}", G(m,1) + G(m,2))
                    : null;
            }),
            // 升级武器名（Repeating Crossbow +6 等）：名称部分查表命中才翻，避免误伤
            new Rule(@"^(.+?) \+(\d+)$", m =>
            {
                var name = Part(G(m,1));
                return name != G(m,1) ? name + " +" + G(m,2) : null;
            }),
        };

        // 前缀规则（保尾，尾部递归查表：如 "Level up Vigor" → "升级到 活力"）
        private static readonly KeyValuePair<string, string>[] PrefixRules =
        {
            new KeyValuePair<string, string>("<color=white>TOTAL REWARDS:</color> <color=orange>+",
                "<color=white>总奖励：</color><color=orange>+"),
            new KeyValuePair<string, string>("ADDITIONAL CURSES <color=orange>+", "额外诅咒 <color=orange>+"),
            new KeyValuePair<string, string>("ALTERNATE ZONE BONUS <color=orange>+", "替代区域奖励 <color=orange>+"),
            new KeyValuePair<string, string>("MAX CURSES BONUS <color=orange>+", "诅咒拿满奖励 <color=orange>+"),
            new KeyValuePair<string, string>("TITHING BONUS <color=orange>+", "什一税奖励 <color=orange>+"),
            new KeyValuePair<string, string>("INTEREST <color=orange>+", "利息 <color=orange>+"),
            new KeyValuePair<string, string>("TOTAL REWARDS <color=orange>+", "总奖励 <color=orange>+"),
            new KeyValuePair<string, string>("Take a random curse for the next zone (Max ",
                "为下一区域抽一条随机诅咒（上限 "),
            new KeyValuePair<string, string>("Difficulty has been set to ", "难度已设为 "),
            new KeyValuePair<string, string>("Damage is deferred over ", "伤害迟滞延迟（秒）："),
            new KeyValuePair<string, string>("+1% base Damage for every point of Deferred Damage, capped at ",
                "每点迟滞伤害 +1% 基础伤害，上限为 "),
            new KeyValuePair<string, string>("+2% base Damage for every point of Armor, capped at ",
                "每点护甲 +2% 基础伤害，上限为 "),
            new KeyValuePair<string, string>("+5% Base Damage for every Gold, capped at ",
                "每枚金币 +5% 基础伤害，上限为 "),
            new KeyValuePair<string, string>("Experience gained: ", "获得经验："),
            new KeyValuePair<string, string>("Enemies defeated: ", "击杀敌人："),
            new KeyValuePair<string, string>("Zone reached: ", "已到达区域："),
            new KeyValuePair<string, string>("Level reached: ", "到达区域："),
            new KeyValuePair<string, string>("PLAYER LEVEL: ", "玩家等级 "),
            new KeyValuePair<string, string>("Killed by ", "死于 "),
            new KeyValuePair<string, string>("HEALTH DRAINS IN ", "生命将在 "),
            new KeyValuePair<string, string>("Health drains ", "生命流失 "),
            new KeyValuePair<string, string>("Level up ", "升级到 "),
            new KeyValuePair<string, string>("Food level -", "食欲等级 -"),
            new KeyValuePair<string, string>("last zone: ", "上一区域："),
            new KeyValuePair<string, string>("Time: ", "用时："),
            new KeyValuePair<string, string>("Max Stamina +", "充能上限 +"),
            new KeyValuePair<string, string>("Crit Chance +", "暴击率 +"),
            new KeyValuePair<string, string>("Crit chance +", "暴击率 +"),
            new KeyValuePair<string, string>("Crit Damage +", "暴击伤害 +"),
            new KeyValuePair<string, string>("Max Armor +", "护甲上限 +"),
            new KeyValuePair<string, string>("Damage +  ", "伤害 +"),
            new KeyValuePair<string, string>("Damage +", "伤害 +"),
            new KeyValuePair<string, string>("Max HP +", "生命上限 +"),
            new KeyValuePair<string, string>("Max HP -", "生命上限 -"),
            new KeyValuePair<string, string>("Armor +", "护甲 +"),
            new KeyValuePair<string, string>("Stamina +", "充能 +"),
            new KeyValuePair<string, string>("REWARD +", "奖励 +"),
            new KeyValuePair<string, string>("APPLY ", "应用 "),
            new KeyValuePair<string, string>("SELECT ", "选择 "),
            new KeyValuePair<string, string>("KILL ", "击杀 "),
        };

        private static readonly Dictionary<string, string> BuffTemplates = new Dictionary<string, string>
        {
            { "Gold after each zone", "每个区域后 +{0} 金币" },
            { "Base Damage", "基础伤害 +{0}%" },
            { "Dash Speed", "冲刺速度 +{0}%" },
            { "interest on saved gold", "存款利息 +{0}%" },
            { "slower HP Drain", "生命流失减缓 {0}%" },
            { "Max Armor", "护甲上限 +{0}" },
            { "Max Stamina", "充能上限 +{0}" },
            { "Max HP", "生命上限 +{0}" },
            { "Beast Damage", "兽形伤害 +{0}%" },
            { "Crit Damage", "暴击伤害 +{0}%" },
            { "Dash regen", "冲刺回复 +{0}%" },
            { "longer Transformations", "变身持续时间 +{0}%" },
            { "more Gold from Foes", "敌人金币掉落 +{0}%" },
            { "Armor regen", "护甲回复 +{0}%" },
            { "Crit Chance", "暴击率 +{0}%" },
            { "HP from kills", "击杀回复 +{0} 生命" },
            { "Armor", "护甲 +{0}" },
            { "Damage", "伤害 +{0}%" },
            { "Starting Berserk", "开局狂暴 +{0}" },
            { "HP every 8 seconds", "每 8 秒回复 {0} 生命" },
            { "additonal Upgrade to all shop weapons", "商店全部武器额外 +{0} 级升级" },
            { "seconds of Invulnerability after Ascending", "升格后无敌 +{0} 秒" },
        };

        private static string TranslateCore(string s)
        {
            // 1) 精确
            string v;
            if (Exact.TryGetValue(s, out v)) return v;

            // 2) 空白归一化（教程段落等换行/空格不可靠的场景文本）
            var key = NormKey(s);
            if (key.Length > 0 && Normalized.TryGetValue(key, out v)) return v;

            // 3) 正则规则表
            foreach (var rule in Rules)
            {
                var r = rule.Run(s);
                if (r != null) return r;
            }

            // 4) 前缀规则
            foreach (var kv in Prefixes)
                if (s.StartsWith(kv.Key, StringComparison.Ordinal))
                    return kv.Value + Part(s.Substring(kv.Key.Length));

            // 未命中记一次，便于后续补表（能走到这里必为纯 ASCII，见 Translate 快速否决）
            if (s.Length > 2 && Missed.Add(s) && HasWord.IsMatch(s))
                CNFixPlugin.Log.LogDebug("未翻译：" + s.Replace("\n", "\\n"));
            return null;
        }
    }

    // ============================== 诊断转储 ==============================
    internal static class Diagnostics
    {
        internal static void Dump()
        {
            try
            {
                var path = Path.Combine(Paths.BepInExRootPath, "CNFix_dump.txt");
                using (var w = new StreamWriter(path, false))
                {
                    w.WriteLine($"OnEnable 钩子触发次数: {TextHooks.EnableFires}");
                    var all = Resources.FindObjectsOfTypeAll<Text>();
                    w.WriteLine("Text 总数: " + all.Length);
                    foreach (var t in all)
                    {
                        if (t == null) continue;
                        string p = "";
                        for (var tr = t.transform; tr != null; tr = tr.parent)
                            p = tr.name + "/" + p;
                        string font = t.font != null ? t.font.name : "null";
                        string ov = t.verticalOverflow == VerticalWrapMode.Truncate ? "截断" : "溢出";
                        w.WriteLine($"{(t.gameObject.activeInHierarchy ? "ON " : "off")} {(t.enabled ? "en" : "DIS")} | {font} | {p} | {t.text.Replace("\n", "\\n")} | rect高={t.rectTransform.rect.height:F1} 垂直={ov}{(TextTuner.NarrowFixed.Contains(t) ? "(已放宽)" : "")}");
                        // 渲染层详情（排查「激活但看不见」）：仅对激活的条目输出
                        if (t.gameObject.activeInHierarchy && t.enabled)
                            DumpRenderState(w, t);
                    }
                }
                CNFixPlugin.Log.LogInfo("诊断转储已写入 " + path);
            }
            catch (Exception e)
            {
                CNFixPlugin.Log.LogError("转储失败：" + e);
            }
        }

        private static void DumpRenderState(StreamWriter w, Text t)
        {
            try
            {
                var rt = t.rectTransform;
                // 沿层级累计 CanvasGroup 透明度，并找最近的 Canvas
                float cgAlpha = 1f; bool? canvasOn = null; string canvasName = null;
                for (var tr = t.transform; tr != null; tr = tr.parent)
                {
                    var cg = tr.GetComponent<CanvasGroup>();
                    if (cg != null) cgAlpha *= cg.alpha;
                    var cv = tr.GetComponent<Canvas>();
                    if (cv != null && canvasOn == null) { canvasOn = cv.enabled; canvasName = tr.name; }
                }
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var anim = t.GetComponent<Animator>();
                w.WriteLine($"    [渲染] 字号={t.fontSize} bestFit={t.resizeTextForBestFit}({t.resizeTextMinSize}-{t.resizeTextMaxSize}) " +
                            $"颜色RGBA=({t.color.r:F2},{t.color.g:F2},{t.color.b:F2},{t.color.a:F2}) " +
                            $"画布({canvasName} enabled={canvasOn}) 组alpha={cgAlpha:F2} " +
                            $"裁剪={t.canvasRenderer.cull} 继承alpha={t.canvasRenderer.GetInheritedAlpha():F2} " +
                            $"rect={rt.rect.size} 世界角=({corners[0].x:F0},{corners[0].y:F0})~({corners[2].x:F0},{corners[2].y:F0}) " +
                            $"Animator={(anim != null ? (anim.enabled ? "有/启用" : "有/禁用") : "无")}");
            }
            catch (Exception ex)
            {
                w.WriteLine("    [渲染] 读取失败: " + ex.Message);
            }
        }
    }
}
