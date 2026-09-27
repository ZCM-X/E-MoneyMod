using System;
using System.Reflection;
using HarmonyLib;
using Manager;
using MelonLoader;
using UI;
using UnityEngine;

namespace EMoneyMod
{
    /// <summary>
    /// 原版在进入投币/决定信用点界面时，只会在它自己的 e-money 条件全通过时才显示提示。
    /// 现在 fake e-money 的场景下，Native 条件不一定全过，所以这里直接在 ViewUpdate 之后
    /// 把对应面板的电子支付提示对象重新激活，按 Select 仍然走游戏原版逻辑。
    ///
    /// 面板结构（ModeSelect / TicketSelect 相同）:
    ///   _creditWindow.transform.GetChild(0).GetChild(1) = 投币面板, child5 = e-money 提示
    ///   _creditWindow.transform.GetChild(0).GetChild(2) = 决定面板, child5 = e-money 提示
    /// </summary>
    internal static class EntryPromptPatch
    {
        private static bool _warned;
        private static bool _loggedShown;
        private static bool _loggedRunning;
        private static FieldInfo _modeSelectIdField;
        private static FieldInfo _ticketSelectIdField;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            TryPatch(harmony, typeof(Monitor.ModeSelect.ModeSelectMonitor), "ModeSelectViewUpdate", "ModeSelectMonitor.ViewUpdate");
            TryPatch(harmony, typeof(Monitor.TicketSelect.TicketSelectMonitor), "TicketSelectViewUpdate", "TicketSelectMonitor.ViewUpdate");
        }

        private static void TryPatch(HarmonyLib.Harmony harmony, System.Type targetType, string postfixName, string label)
        {
            try
            {
                MethodInfo target = AccessTools.Method(targetType, "ViewUpdate", new System.Type[0], null);
                MethodInfo postfix = AccessTools.Method(typeof(EntryPromptPatch), postfixName);
                if (target == null || postfix == null)
                {
                    ModLog.Warning("[EMoneyMod] 找不到 " + label + " 的补丁目标/方法");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                ModLog.Debug("[EMoneyMod] 已挂电子支付提示补丁: " + label);
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 挂电子支付提示补丁失败 " + label + ": " + e.Message);
            }
        }

        private static void ModeSelectViewUpdate(Monitor.ModeSelect.ModeSelectMonitor __instance)
        {
            if (!_loggedRunning)
            {
                _loggedRunning = true;
                ModLog.Debug("[EMoneyMod] ModeSelect ViewUpdate 补丁运行中, state=" + __instance._state);
            }
            int panel = GetPanel(__instance._state);
            if (panel < 0)
            {
                return;
            }
            int monitorId = GetMonitorId(__instance, ref _modeSelectIdField);
            TryForceSelect(__instance, monitorId);
            ShowPrompt(__instance._creditWindow, panel, monitorId);
        }

        private static void TicketSelectViewUpdate(Monitor.TicketSelect.TicketSelectMonitor __instance)
        {
            if (!_loggedRunning)
            {
                _loggedRunning = true;
                ModLog.Debug("[EMoneyMod] TicketSelect ViewUpdate 补丁运行中, state=" + __instance._state);
            }
            int panel = GetPanel(__instance._state);
            if (panel < 0)
            {
                return;
            }
            int monitorId = GetMonitorId(__instance, ref _ticketSelectIdField);
            TryForceSelect(__instance, monitorId);
            ShowPrompt(__instance._creditWindow, panel, monitorId);
        }

        private static int GetPanel(Monitor.ModeSelect.ModeSelectMonitor.MonitorState state)
        {
            if (state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.InsertCoinFadeIn
                || state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.InsertCoinFadeInWait
                || state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.InsertCoinWait)
            {
                return 1;
            }
            if (state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.DecideCreditFadeIn
                || state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.DecideCreditFadeInWait
                || state == Monitor.ModeSelect.ModeSelectMonitor.MonitorState.DecideCreditWait)
            {
                return 2;
            }
            return -1;
        }

        private static int GetPanel(Monitor.TicketSelect.TicketSelectMonitor.MonitorState state)
        {
            if (state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.InsertCoinFadeIn
                || state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.InsertCoinFadeInWait
                || state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.InsertCoinWait)
            {
                return 1;
            }
            if (state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.DecideCreditFadeIn
                || state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.DecideCreditFadeInWait
                || state == Monitor.TicketSelect.TicketSelectMonitor.MonitorState.DecideCreditWait)
            {
                return 2;
            }
            return -1;
        }

        private static void TryForceSelect(Monitor.ModeSelect.ModeSelectMonitor instance, int monitorId)
        {
            if (!instance._isSelectButton
                && InputManager.GetButtonDown(monitorId, InputManager.ButtonSetting.Select))
            {
                instance._isSelectButton = true;
                ModLog.Debug("[EMoneyMod] ModeSelect ViewUpdate 捕获 Select, 强制置位");
            }
        }

        private static void TryForceSelect(Monitor.TicketSelect.TicketSelectMonitor instance, int monitorId)
        {
            if (!instance._isSelectButton
                && InputManager.GetButtonDown(monitorId, InputManager.ButtonSetting.Select))
            {
                instance._isSelectButton = true;
                ModLog.Debug("[EMoneyMod] TicketSelect ViewUpdate 捕获 Select, 强制置位");
            }
        }
        private static int GetMonitorId(object instance, ref FieldInfo cache)
        {
            if (cache == null)
            {
                cache = AccessTools.Field(instance.GetType(), "_monitorID");
            }
            if (cache == null)
            {
                return 0;
            }
            return (int)cache.GetValue(instance);
        }

        private static void ShowPrompt(GameObject creditWindow, int panelIndex, int monitorId)
        {
            if (creditWindow == null)
            {
                return;
            }
            try
            {
                Transform panel = creditWindow.transform.GetChild(0).GetChild(panelIndex);
                if (panel == null)
                {
                    return;
                }
                Transform emoneyPrompt = panel.GetChild(5);
                if (emoneyPrompt == null)
                {
                    return;
                }

                if (!emoneyPrompt.gameObject.activeSelf)
                {
                    emoneyPrompt.gameObject.SetActive(true);
                }

                // 原版 InsertCoin 分支会切 1P/2P 图标，这里也补一次。
                Transform icon = emoneyPrompt.GetChild(1);
                if (icon != null)
                {
                    MultipleImage image = icon.GetComponent<MultipleImage>();
                    if (image != null)
                    {
                        image.ChangeSprite(monitorId);
                    }
                }

                if (!_loggedShown)
                {
                    _loggedShown = true;
                    ModLog.Debug("[EMoneyMod] 电子支付提示对象已显示: " + emoneyPrompt.name
                        + " panel=" + panelIndex + " (monitor " + (monitorId + 1) + "P) active="
                        + emoneyPrompt.gameObject.activeInHierarchy);
                }
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    ModLog.Warning("[EMoneyMod] 显示电子支付提示失败: " + e.Message);
                }
            }
        }
    }
}