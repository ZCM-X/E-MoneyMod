using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Monitor;

namespace EMoneyMod
{
    /// <summary>
    /// AMDaemon.CreditUnit.get_Credit 这种小 getter 在 Mono 下可能被内联。
    /// 这里直接补 Manager.Credit 和显示层的 SetCredits。
    /// </summary>
    internal static class CreditDisplayPatch
    {
        private static bool _loggedSetCredits;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(CreditController), "SetCredits",
                new System.Type[] { typeof(int), typeof(int), typeof(int) },
                "SetCreditsPrefix", "CreditController.SetCredits", true);

        }

        private static void Patch(HarmonyLib.Harmony harmony, System.Type type, string methodName,
            System.Type[] parameterTypes, string patchName, string label, bool prefix)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName, parameterTypes, null);
                MethodInfo patch = AccessTools.Method(typeof(CreditDisplayPatch), patchName);
                if (target == null || patch == null)
                {
                    ModLog.Warning("[EMoneyMod] 找不到 " + label + " 的目标/补丁方法");
                    return;
                }
                if (prefix)
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(patch));
                }
                else
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(patch));
                }
                ModLog.Debug("[EMoneyMod] 已挂点数显示补丁: " + label);
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 挂点数显示补丁失败 " + label + ": " + e.Message);
            }
        }

        public static void SetCreditsPrefix(ref int num, int mole, int denomi)
        {
            if (!_loggedSetCredits)
            {
                _loggedSetCredits = true;
                ModLog.Debug("[EMoneyMod] CreditController.SetCredits 补丁运行中, virtual=" + VirtualCredit.Balance + " num=" + num);
            }
            if (num < VirtualCredit.Balance)
            {
                num = VirtualCredit.Balance;
            }
        }


    }
}