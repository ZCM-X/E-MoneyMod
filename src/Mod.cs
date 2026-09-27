using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(EMoneyMod.Mod), "EMoneyMod - Fake Payment", "0.1.0", "ZCM-X")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace EMoneyMod
{
    /// <summary>
    /// 假电子支付:
    ///   把 AMDaemon 的 e-money 可用性/认证/品牌/操作状态全部伪装成正常，
    ///   支付时不再等真实读卡器，直接把对应点数加进一个虚拟 credit 缓冲。
    /// </summary>
    public class Mod : MelonMod
    {
        /// <summary>
        /// 必须最先执行: 把嵌入的 HidSharp 注册进 AssemblyResolve,
        /// 之后任何地方触碰到 HidSharp 类型都能正常解析。
        /// </summary>
        static Mod()
        {
            EmbeddedAssemblyLoader.Install();
        }

        public override void OnInitializeMelon()
        {
            try
            {
                VirtualCredit.Load();
                AquaMaiCompat.Detect();

                HarmonyInstance.PatchAll();
                EntryUnlock.Apply(HarmonyInstance);
                EntryPromptPatch.Apply(HarmonyInstance);
                BalanceForcePatch.Apply(HarmonyInstance);
                CreditDisplayPatch.Apply(HarmonyInstance);
                NativeCreditStore.Apply(HarmonyInstance);
                VirtualCredit.Apply(HarmonyInstance);
                AimeReaderPatch.Apply(HarmonyInstance);
                ModLog.Info("v" + typeof(Mod).Assembly.GetName().Version
                    + " 假支付已启用");

                // 打印各点数方法上挂了哪些补丁（用来确认 AquaMai 是怎么改点数的）
                AquaMaiCompat.DumpPatchInfo();

                // 如果 Mods 下有 EMoneyMod.soundtest.txt，就依次试听所有电子支付音效
                EMoneySoundCompat.RunSoundTestIfRequested();
            }
            catch (Exception e)
            {
                ModLog.Error("EMoneyMod 挂补丁失败: " + e);
            }
        }

        public override void OnLateInitializeMelon()
        {
            ExternalCardReceiver.Start();
        }

        public override void OnUpdate()
        {
            ExternalCardReceiver.Poll();
        }

        public override void OnApplicationQuit()
        {
            ExternalCardReceiver.Stop();
        }
    }
    /// <summary>
    /// 解锁电子支付入口: 原版在 GotoBalance()/IsEnableEMoneyTransition() 里要求
    /// "不是免费游玩 + 投币可用 + 当前 credit <= 14", 有点数就直接不让进电子支付。
    /// 这里把这几个判断全部改成 true, 让它无论当前 credit 多少都能打开电子支付窗口。
    /// </summary>
    internal static class EntryUnlock
    {
        private static readonly string[] Targets =
        {
            "Monitor.ModeSelect.ModeSelectMonitor|GotoBalance",
            "Monitor.TicketSelect.TicketSelectMonitor|GotoBalance",
            "MagicalPassMonitor|IsGotoBalanceEnabled",
            "MagicalPassProcess|IsEnableEMoneyTransition"
        };

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo prefixMethod = typeof(EntryUnlock).GetMethod(
                "ForceTrue",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (string target in Targets)
            {
                string[] parts = target.Split('|');
                string typeName = parts[0];
                string methodName = parts[1];
                try
                {
                    Type type = AccessTools.TypeByName(typeName);
                    if (type == null)
                    {
                        ModLog.Warning("[EMoneyMod] 找不到类型 " + typeName + ", 跳过电子支付入口解锁");
                        continue;
                    }
                    MethodInfo method = AccessTools.Method(type, methodName, new Type[0], null);
                    if (method == null)
                    {
                        ModLog.Warning("[EMoneyMod] 找不到方法 " + typeName + "." + methodName + ", 跳过");
                        continue;
                    }
                    harmony.Patch(method, new HarmonyMethod(prefixMethod));
                    ModLog.Debug("[EMoneyMod] 已解除电子支付限制: " + typeName + "." + methodName);
                }
                catch (Exception e)
                {
                    ModLog.Warning("[EMoneyMod] 解锁 " + target + " 失败: " + e.Message);
                }
            }
        }

        public static bool ForceTrue(ref bool __result)
        {
            __result = true;
            return false;
        }
    }
}
