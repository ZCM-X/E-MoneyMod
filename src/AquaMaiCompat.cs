using System;
using System.IO;
using System.Reflection;
using AMDaemon;
using HarmonyLib;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 和 AquaMai 的"点数设置"共存。
    ///
    /// AquaMai.toml 里跟点数有关的三处：
    ///   [GameSettings.CreditConfig]   IsFreePlay / LockCredits
    ///   [GameSystem.VirtualCoin]      远程加点（也是内存虚拟点数，重启清零）
    ///   [GameSystem.EnableFreedomMode]
    ///
    /// 叠加关系（已实测 Harmony 语义）：
    ///   prefix 返回 false 跳过原方法时，**postfix 仍然会执行**。
    ///   所以只要 AquaMai 的 LockCredits 是用 prefix 设 `__result = N` 实现的，
    ///   咱的 postfix `__result += 虚拟点数` 就会自动变成 `N + 虚拟点数`。
    ///   本类负责把实际配置和补丁归属打印出来，方便确认到底是不是这样。
    /// </summary>
    internal static class AquaMaiCompat
    {
        internal static bool TomlFound;
        internal static int LockCredits;          // >0 表示 AquaMai 锁定了可用点数
        internal static bool IsFreePlay;
        internal static bool VirtualCoinEnabled;
        internal static bool FreedomModeEnabled;
        internal static string TomlPath;

        internal static void Detect()
        {
            LockCredits = 0;
            IsFreePlay = false;
            VirtualCoinEnabled = false;
            FreedomModeEnabled = false;
            TomlFound = false;

            try
            {
                string modsDir = Path.GetDirectoryName(typeof(Mod).Assembly.Location);
                string gameDir = string.IsNullOrEmpty(modsDir) ? null : Path.GetDirectoryName(modsDir);
                if (string.IsNullOrEmpty(gameDir))
                {
                    return;
                }
                string path = Path.Combine(gameDir, "AquaMai.toml");
                if (!File.Exists(path))
                {
                    ModLog.Debug("[EMoneyMod] 没找到 AquaMai.toml，按没有 AquaMai 点数设置处理");
                    return;
                }
                TomlPath = path;
                TomlFound = true;

                string section = "";
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        if (section == "GameSystem.VirtualCoin") VirtualCoinEnabled = true;
                        else if (section == "GameSystem.EnableFreedomMode") FreedomModeEnabled = true;
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (section == "GameSettings.CreditConfig")
                    {
                        if (key == "LockCredits") LockCredits = ParseInt(val);
                        else if (key == "IsFreePlay") IsFreePlay = ParseBool(val);
                    }
                }

                ModLog.Debug("[EMoneyMod] AquaMai 点数相关设置: LockCredits=" + LockCredits
                    + "  IsFreePlay=" + IsFreePlay
                    + "  VirtualCoin=" + VirtualCoinEnabled
                    + "  FreedomMode=" + FreedomModeEnabled);

                if (VirtualCoinEnabled)
                {
                    ModLog.Warning("[EMoneyMod] AquaMai 的 [GameSystem.VirtualCoin] 也开着 —— 它和本 mod 会重复加点数，建议关掉一个");
                }
                if (IsFreePlay)
                {
                    ModLog.Warning("[EMoneyMod] AquaMai IsFreePlay=true（免费游玩）—— 原版就不需要点数了，假支付加点数会失去意义");
                }
                if (LockCredits > 0)
                {
                    ModLog.Debug("[EMoneyMod] 检测到 AquaMai 锁定 " + LockCredits
                        + " 点；本 mod 的虚拟点数会叠加在它上面（靠 postfix）");
                }
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 解析 AquaMai.toml 失败: " + e.Message);
            }
        }

        private static int ParseInt(string s)
        {
            int v;
            if (int.TryParse(s.Trim().Trim('"', '\''), out v))
            {
                return v;
            }
            return 0;
        }

        private static bool ParseBool(string s)
        {
            return s.Trim().Trim('"', '\'').Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把 patch 过点数相关方法的人都打出来 —— 用来确认 AquaMai 到底怎么改的点数、
        /// 是 prefix 还是 postfix、优先级多少。跑一次游戏看日志即可。
        /// </summary>
        internal static void DumpPatchInfo()
        {
            try
            {
                Dump("CreditUnit.get_Credit",
                    AccessTools.Method(typeof(CreditUnit), "get_Credit", new Type[0], null));
                Dump("CreditUnit.get_IsZero",
                    AccessTools.Method(typeof(CreditUnit), "get_IsZero", new Type[0], null));
                Dump("CreditUnit.get_AddableCoin",
                    AccessTools.Method(typeof(CreditUnit), "get_AddableCoin", new Type[0], null));
                Dump("CreditUnit.IsGameCostEnough",
                    AccessTools.Method(typeof(CreditUnit), "IsGameCostEnough", new Type[] { typeof(int), typeof(int) }, null));
                Dump("CreditUnit.PayGameCost",
                    AccessTools.Method(typeof(CreditUnit), "PayGameCost", new Type[] { typeof(int), typeof(int) }, null));
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 导出补丁信息失败: " + e.Message);
            }
        }

        private static void Dump(string label, MethodBase target)
        {
            if (target == null)
            {
                ModLog.Debug("[EMoneyMod][PatchInfo] " + label + " ：找不到方法");
                return;
            }

            Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
            if (info == null)
            {
                ModLog.Debug("[EMoneyMod][PatchInfo] " + label + " ：没人 patch");
                return;
            }

            ModLog.Debug("[EMoneyMod][PatchInfo] " + label
                + "  prefix=" + Count(info.Prefixes) + " postfix=" + Count(info.Postfixes));

            foreach (Patch p in info.Prefixes) Log("prefix", p);
            foreach (Patch p in info.Postfixes) Log("postfix", p);
        }

        private static int Count(System.Collections.Generic.ICollection<Patch> set)
        {
            return set == null ? 0 : set.Count;
        }

        private static void Log(string kind, Patch p)
        {
            string method = "?";
            try
            {
                // Harmony 各版本字段名不一样，反射取一下更保险
                object m = null;
                PropertyInfo prop = typeof(Patch).GetProperty("PatchMethod");
                if (prop != null)
                {
                    m = prop.GetValue(p, null);
                }
                else
                {
                    FieldInfo fld = typeof(Patch).GetField("patch");
                    if (fld != null)
                    {
                        m = fld.GetValue(p);
                    }
                }
                MethodInfo mi = m as MethodInfo;
                if (mi != null && mi.DeclaringType != null)
                {
                    method = mi.DeclaringType.FullName + "." + mi.Name;
                }
            }
            catch
            {
            }

            ModLog.Debug("[EMoneyMod][PatchInfo]   " + kind
                + " owner=" + p.owner
                + " priority=" + p.priority
                + " index=" + p.index
                + " method=" + method);
        }
    }
}
