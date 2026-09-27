using HarmonyLib;
using Manager;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 只用于确认：物理/键盘的选择键到底有没有被游戏 InputManager 识别成 Select。
    /// 如果这里没有日志，说明问题在 segatools / Maimoller 映射，不在 AMDaemon。
    /// </summary>
    internal static class InputDiagnosticPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InputManager), "GetButtonDown",
            new System.Type[] { typeof(int), typeof(InputManager.ButtonSetting) })]
        private static void GetButtonDownLog(int monitorId, InputManager.ButtonSetting button, ref bool __result)
        {
            if (__result && button == InputManager.ButtonSetting.Select)
            {
                ModLog.Debug("[EMoneyMod] 游戏 InputManager 收到 Select 输入: " + (monitorId + 1) + "P");
            }
        }
    }
}