using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AMDaemon;
using HarmonyLib;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 虚拟 credit 缓冲。
    /// 游戏读 CreditUnit.Credit 时把缓冲加上去；消费时优先扣缓冲，不够的部分再回退到原生 AMDaemon。
    /// 这部分思路参考 AquaMai 的 VirtualCoin，但只从"假 e-money 支付"入口加钱。
    /// </summary>
    internal static class VirtualCredit
    {
        private static int _balance;

        internal static int Balance
        {
            get { return _balance; }
        }

        /// <summary>点数由游戏自己的 appfile.dat 备份读取，实际加载在备份就绪后完成。</summary>
        internal static void Load()
        {
            ModLog.Debug("[EMoneyMod] 点数保存在游戏 appfile.dat 的本地备份记录中");
        }

        internal static void ApplyLoadedBalance(int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            if (_balance <= 0)
            {
                _balance = amount > 9999 ? 9999 : amount;
            }
            else
            {
                _balance = _balance + amount > 9999 ? 9999 : _balance + amount;
            }
        }

        /// <summary>把当前点数交给游戏自己的备份记录保存。</summary>
        internal static void Save()
        {
            NativeCreditStore.RequestSave(_balance);
        }

        internal static void Add(int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            _balance += amount;
            if (_balance > 9999)
            {
                _balance = 9999;
            }
            Save();
        }

        /// <summary>
        /// 清零虚拟点数。游戏清点数备份（Credit.ClearBackup，比如测试模式的
        /// “备份数据清除”）时也会调这里，保证两边一起清。
        /// </summary>
        internal static void Reset()
        {
            _balance = 0;
            Save();
            ModLog.Debug("[EMoneyMod] 虚拟点数已清零");
        }

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, "get_Credit", new System.Type[0], "GetCredit", false, "CreditUnit.Credit");
            Patch(harmony, "get_IsZero", new System.Type[0], "GetIsZero", false, "CreditUnit.IsZero");
            Patch(harmony, "get_AddableCoin", new System.Type[0], "GetAddableCoin", false, "CreditUnit.AddableCoin");
            Patch(harmony, "IsGameCostEnough", new System.Type[] { typeof(int), typeof(int) }, "IsGameCostEnough", true, "CreditUnit.IsGameCostEnough");
            Patch(harmony, "PayGameCost", new System.Type[] { typeof(int), typeof(int) }, "PayGameCost", true, "CreditUnit.PayGameCost");
        }

        private static void Patch(HarmonyLib.Harmony harmony, string methodName, System.Type[] parameterTypes, string patchName, bool prefix, string label)
        {
            try
            {
                System.Reflection.MethodInfo target = AccessTools.Method(typeof(CreditUnit), methodName, parameterTypes, null);
                System.Reflection.MethodInfo patch = AccessTools.Method(typeof(VirtualCredit), patchName);
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
                ModLog.Debug("[EMoneyMod] 已挂虚拟点数补丁: " + label);
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 挂虚拟点数补丁失败 " + label + ": " + e.Message);
            }
        }
        private static void GetCredit(ref uint __result)
        {
            __result += (uint)_balance;
        }
        private static void GetIsZero(ref bool __result)
        {
            if (_balance > 0)
            {
                __result = false;
            }
        }
        private static void GetAddableCoin(ref uint __result)
        {
            // 没有投币器等硬件时，原生值可能是 0；假支付要让它至少能通过支付前的容量检查。
            if (__result < 24u)
            {
                __result = 24u;
            }
        }
        private static bool IsGameCostEnough(CreditUnit __instance, ref bool __result, int gameCostIndex, int count)
        {
            try
            {
                int needCost = GetNeedCost(__instance, gameCostIndex, count);
                if (_balance >= needCost)
                {
                    __result = true;
                    return false;
                }
                if (_balance > 0)
                {
                    needCost -= _balance;
                    __result = CallApi("CreditUnit_isGameCostEnough",
                        new object[] { GetPointer(__instance), gameCostIndex, needCost });
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] IsGameCostEnough 补丁异常, 回退原版: " + e.Message);
                return true;
            }
        }
        private static bool PayGameCost(CreditUnit __instance, ref bool __result, int gameCostIndex, int count)
        {
            try
            {
                int needCost = GetNeedCost(__instance, gameCostIndex, count);
                if (_balance >= needCost)
                {
                    _balance -= needCost;
                    Save();
                    __result = true;
                    return false;
                }
                if (_balance > 0)
                {
                    needCost -= _balance;
                    _balance = 0;
                    Save();
                    __result = CallApi("CreditUnit_payGameCost",
                        new object[] { GetPointer(__instance), gameCostIndex, needCost });
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] PayGameCost 补丁异常, 回退原版: " + e.Message);
                return true;
            }
        }

        // ── 下面这段是从 AquaMai VirtualCoin 借鉴的 native API 反射调用 ──
        private static PropertyInfo _pointerProp;
        private static MethodInfo _callApi;
        private static readonly Dictionary<string, MethodInfo> _apiMethods = new Dictionary<string, MethodInfo>();

        private static int GetNeedCost(CreditUnit instance, int gameCostIndex, int count)
        {
            LazyCollection<uint> gameCosts = instance.GameCosts;
            if (gameCosts == null)
            {
                throw new Exception("GameCosts is null");
            }
            return count * (int)gameCosts[gameCostIndex];
        }

        private static IntPtr GetPointer(CreditUnit instance)
        {
            if (_pointerProp == null)
            {
                _pointerProp = AccessTools.Property(typeof(CreditUnit), "Pointer");
            }
            if (_pointerProp == null)
            {
                throw new Exception("CreditUnit.Pointer not found");
            }
            object value = _pointerProp.GetValue(instance, null);
            if (value == null)
            {
                throw new Exception("CreditUnit.Pointer is null");
            }
            return (IntPtr)value;
        }

        private static MethodInfo GetApiMethod(string apiName, params Type[] parameterTypes)
        {
            MethodInfo cached;
            if (_apiMethods.TryGetValue(apiName, out cached))
            {
                return cached;
            }

            Type apiType = AccessTools.TypeByName("AMDaemon.Api");
            if (apiType == null)
            {
                throw new Exception("AMDaemon.Api not found");
            }

            MethodInfo method = AccessTools.Method(apiType, apiName, parameterTypes, null);
            if (method == null)
            {
                throw new Exception("AMDaemon.Api." + apiName + " not found");
            }

            _apiMethods[apiName] = method;
            return method;
        }

        private static bool CallApi(string apiName, object[] args)
        {
            if (_callApi == null)
            {
                Type apiType = AccessTools.TypeByName("AMDaemon.Api");
                if (apiType == null)
                {
                    throw new Exception("AMDaemon.Api not found");
                }

                // 找 Call<bool>(Func<bool>) 的泛型重载
                _callApi = apiType
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(delegate (MethodInfo m)
                    {
                        if (m.Name != "Call" || !m.IsGenericMethodDefinition || m.GetGenericArguments().Length != 1)
                        {
                            return false;
                        }
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length != 1 || !ps[0].ParameterType.IsGenericType)
                        {
                            return false;
                        }
                        return ps[0].ParameterType.GetGenericTypeDefinition() == typeof(Func<>);
                    });
                if (_callApi == null)
                {
                    throw new Exception("AMDaemon.Api.Call<T>(Func<T>) not found");
                }
                _callApi = _callApi.MakeGenericMethod(typeof(bool));
            }

            MethodInfo apiMethod = GetApiMethod(apiName, null);
            Func<bool> func = delegate { return (bool)apiMethod.Invoke(null, args); };
            return (bool)_callApi.Invoke(null, new object[] { func });
        }
    }

    /// <summary>
    /// 游戏清点数备份时（测试模式“备份数据清除” → AMDaemon.Credit.ClearBackup），
    /// 顺带把本 mod 的虚拟点数也清零，让 mod 的点和游戏自己的点数备份一起被清。
    /// </summary>
    [HarmonyPatch(typeof(AMDaemon.Credit), "ClearBackup")]
    internal static class CreditClearBackupPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            VirtualCredit.Reset();
        }
    }
}
