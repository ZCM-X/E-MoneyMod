using HarmonyLib;
using Manager;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// fake e-money 场景下 AMDaemon 的原生条件未必会通过，导致 SelectButtonAnim() 不设置
    /// _isSelectButton，按选择键看起来没反应。这里在收到 Select 输入后直接强制置位，
    /// 后续仍然走游戏自己的 EMoneyWait / StartEMoneyWindow 流程。
    /// </summary>
    internal static class SelectButtonForcePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Monitor.ModeSelect.ModeSelectMonitor), "SelectButtonAnim")]
        private static bool ModeSelectSelectButtonAnim(
            Monitor.ModeSelect.ModeSelectMonitor __instance,
            InputManager.ButtonSetting button)
        {
            if (button != InputManager.ButtonSetting.Select)
            {
                return true;
            }
            __instance._isSelectButton = true;
            ModLog.Debug("[EMoneyMod] ModeSelect 收到 Select 输入, 强制打开电子支付");
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Monitor.TicketSelect.TicketSelectMonitor), "SelectButtonAnim")]
        private static bool TicketSelectSelectButtonAnim(
            Monitor.TicketSelect.TicketSelectMonitor __instance,
            InputManager.ButtonSetting button)
        {
            if (button != InputManager.ButtonSetting.Select)
            {
                return true;
            }
            __instance._isSelectButton = true;
            ModLog.Debug("[EMoneyMod] TicketSelect 收到 Select 输入, 强制打开电子支付");
            return false;
        }
    }
}