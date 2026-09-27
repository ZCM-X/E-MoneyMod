using System;
using System.Collections.Generic;
using System.Reflection;
using AMDaemon;
using Balance;
using HarmonyLib;

namespace EMoneyMod
{
    /// <summary>
    /// fake e-money 场景下 AMDaemon 原生状态会让 BalanceCommon 判定不可用，
    /// 并把供应商列表留空，最终在 BrandBottomInfo 里越界崩游戏。
    /// 这里兜底可用性、颜色，并在没有原生供应商列表时填一套假品牌。
    /// </summary>
    internal static class BalanceForcePatch
    {
        private static bool _loggedEnableRequest;
        private static bool _loggedBrands;
        private static readonly FieldInfo BrandTypeListField = AccessTools.Field(typeof(BalanceCommon), "_valueListEMoneyBrandTypeID");
        private static readonly FieldInfo BrandEnableListField = AccessTools.Field(typeof(BalanceCommon), "_valueListEMoneyBrandEnable");
        private static readonly FieldInfo BrandNumField = AccessTools.Field(typeof(BalanceCommon), "_nEMoneyBrandIconNum");
        private static readonly FieldInfo BrandCursorField = AccessTools.Field(typeof(BalanceCommon), "_nEMoneyBrandIconCursorID");
        private static readonly FieldInfo BrandCurrentField = AccessTools.Field(typeof(BalanceCommon), "_nEMoneyCurrentBrandTypeID");
        private static readonly FieldInfo BrandPreviousSetField = AccessTools.Field(typeof(BalanceCommon), "_isSetPreviousBrandTypeID");

        private static readonly EMoneyBrandId[] FakeBrands =
        {
            EMoneyBrandId.Nanaco,
            EMoneyBrandId.Edy,
            EMoneyBrandId.Transport,
            EMoneyBrandId.Waon,
            EMoneyBrandId.Paseli,
            EMoneyBrandId.iD
        };

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(BalanceCommon), "IsEnableRequest",
                new System.Type[0], "ForceTrueVoid", "BalanceCommon.IsEnableRequest");
            Patch(harmony, typeof(BalanceCommon), "IsEnableBaranceBrand",
                new System.Type[] { typeof(bool) }, "ForceTrueBool", "BalanceCommon.IsEnableBaranceBrand");
            Patch(harmony, typeof(BalanceCommon), "RequestOKButtonGrayOut",
                new System.Type[] { typeof(bool) }, "ForceWhite", "BalanceCommon.RequestOKButtonGrayOut");
            Patch(harmony, typeof(Monitor.EmoneyCreditIconController), "ChangeCreditIconColor",
                new System.Type[] { typeof(bool) }, "ForceWhite", "EmoneyCreditIconController.ChangeCreditIconColor");
            Patch(harmony, typeof(Monitor.EmoneyBrandIconController), "ChangeBrandIconColor",
                new System.Type[] { typeof(bool) }, "ForceWhite", "EmoneyBrandIconController.ChangeBrandIconColor");

            Patch(harmony, typeof(Manager.EMoney), "IsAvalilable",
                new System.Type[] { typeof(AMDaemon.EMoneyBrandId) }, "ForceTrueSimple", "Manager.EMoney.IsAvalilable");
            Patch(harmony, typeof(Manager.EMoney), "IsAvalilableBalance",
                new System.Type[] { typeof(AMDaemon.EMoneyBrandId) }, "ForceTrueSimple", "Manager.EMoney.IsAvalilableBalance");
            Patch(harmony, typeof(Manager.EMoney), "Pay",
                new System.Type[0], "ForcePay", "Manager.EMoney.Pay");
            Patch(harmony, typeof(Manager.EMoney), "get_IsInOperating",
                new System.Type[0], "ForceInOperating", "Manager.EMoney.IsInOperating");
            Patch(harmony, typeof(Manager.Credit), "get_AddableCoin",
                new System.Type[0], "ForceAddableCoin", "Manager.Credit.AddableCoin");
            Patch(harmony, typeof(BalanceCommon), "SetBrandIconNum",
                new System.Type[] { typeof(bool) }, "ForceBrandList", "BalanceCommon.SetBrandIconNum");
            Patch(harmony, typeof(BalanceCommon), "BrandBottomInfo",
                new System.Type[] { typeof(bool), typeof(bool) }, "EnsureBrandsPrefix", "BalanceCommon.BrandBottomInfo");
            Patch(harmony, typeof(BalanceCommon), "ViewUpdate",
                new System.Type[0], "ForceReaderResult", "BalanceCommon.ViewUpdate");
            Patch(harmony, typeof(AMDaemon.EMoneyOperation), "get_IsCancellable",
                new System.Type[0], "ForceCancellable", "EMoneyOperation.IsCancellable");
        }

        private static void Patch(HarmonyLib.Harmony harmony, System.Type type, string methodName,
            System.Type[] parameterTypes, string prefixName, string label)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName, parameterTypes, null);
                MethodInfo prefix = AccessTools.Method(typeof(BalanceForcePatch), prefixName);
                if (target == null || prefix == null)
                {
                    ModLog.Warning("[EMoneyMod] 找不到 " + label + " 的目标/补丁方法");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                ModLog.Debug("[EMoneyMod] 已挂支付可用性补丁: " + label);
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 挂支付可用性补丁失败 " + label + ": " + e.Message);
            }
        }

        public static bool ForceTrueVoid(ref bool __result)
        {
            if (!_loggedEnableRequest)
            {
                _loggedEnableRequest = true;
                ModLog.Debug("[EMoneyMod] BalanceCommon.IsEnableRequest 已强制返回 true");
            }
            __result = true;
            return false;
        }

        public static bool ForceTrueBool(bool isBalance, ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool ForceWhite(ref bool isGray)
        {
            isGray = false;
            return true;
        }

        public static bool ForcePay(Manager.EMoney __instance, ref bool __result)
        {
            Manager.EMoney.EConfig config = __instance.Config;
            FakeEMoney.BeginFakePayment((int)config.brand_, config.coins_);
            AimeReaderPatch.StartScan();
            AccessTools.Field(typeof(Manager.EMoney), "operation_")
                .SetValue(__instance, Manager.EMoney.Operation.None);
            ModLog.Debug("[EMoneyMod] Manager.EMoney.Pay 已强制开始假支付");
            __result = true;
            return false;
        }

        public static bool ForceInOperating(ref bool __result)
        {
            __result = FakeEMoney.IsBusy;
            return false;
        }

        public static bool ForceCancellable(ref bool __result)
        {
            // 刷卡前允许取消；刷卡后/等结果阶段才禁用。
            __result = !FakeEMoney.CardRead;
            return false;
        }

        public static bool ForceReaderResult(BalanceCommon __instance)
        {
            if (!FakeEMoney.IsResultReady || FakeEMoney.IsBusy)
            {
                return true;
            }
            BalanceCommon.BalanceCommonState state = (BalanceCommon.BalanceCommonState)AccessTools.Field(typeof(BalanceCommon), "_state").GetValue(__instance);
            if (state != BalanceCommon.BalanceCommonState.EMoney01ReaderPreWait
                && state != BalanceCommon.BalanceCommonState.EMoney01ReaderWait)
            {
                return true;
            }
            FakeEMoney.EnsureCreditAdded();
            AccessTools.Field(typeof(BalanceCommon), "_isEMoneyReaderSuccess").SetValue(__instance, true);
            AccessTools.Field(typeof(BalanceCommon), "_isEMoneyReaderFailed").SetValue(__instance, false);
            AccessTools.Field(typeof(BalanceCommon), "_isEMoneyUnconfirm").SetValue(__instance, false);
            AccessTools.Field(typeof(BalanceCommon), "_requestInvalid").SetValue(__instance, false);
            AccessTools.Field(typeof(BalanceCommon), "_state").SetValue(__instance, BalanceCommon.BalanceCommonState.EMoney01ReaderResultFadeIn);
            ModLog.Debug("[EMoneyMod] 假支付结果生效, 强制进入成功结果界面");
            return true;
        }

        public static bool ForceAddableCoin(ref int __result)
        {
            __result = 24;
            return false;
        }
        public static bool ForceTrueSimple(ref bool __result)
        {
            __result = true;
            return false;
        }
        public static bool ForceBrandList(BalanceCommon __instance, bool isBalance)
        {
            EnsureBrands(__instance);
            return false;
        }

        public static bool EnsureBrandsPrefix(BalanceCommon __instance, bool disp_brand_icon, bool disp_credit_info)
        {
            EnsureBrandsIfEmpty(__instance);
            return true;
        }

        private static void EnsureBrands(BalanceCommon instance)
        {
            List<EMoneyBrandId> list = (List<EMoneyBrandId>)BrandTypeListField.GetValue(instance);
            List<bool> enable = (List<bool>)BrandEnableListField.GetValue(instance);
            list.Clear();
            enable.Clear();
            for (int i = 0; i < FakeBrands.Length; i++)
            {
                list.Add(FakeBrands[i]);
                enable.Add(true);
            }
            BrandNumField.SetValue(instance, FakeBrands.Length);
            BrandCursorField.SetValue(instance, 0);
            BrandCurrentField.SetValue(instance, FakeBrands[0]);
            BrandPreviousSetField.SetValue(instance, true);
            instance.IsRightLimits = false;
            instance.IsLeftLimits = true;

            if (!_loggedBrands)
            {
                _loggedBrands = true;
                ModLog.Debug("[EMoneyMod] 已注入假电子支付品牌: " + string.Join(", ", FakeBrands));
            }
        }

        private static void EnsureBrandsIfEmpty(BalanceCommon instance)
        {
            List<EMoneyBrandId> list = (List<EMoneyBrandId>)BrandTypeListField.GetValue(instance);
            if (list == null || list.Count == 0)
            {
                EnsureBrands(instance);
            }
        }
    }
}
