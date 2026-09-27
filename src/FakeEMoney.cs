using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using AMDaemon;
using HarmonyLib;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 把 AMDaemon 的 e-money 包装层伪装成"设备已认证、品牌可用、操作成功"。
    /// 真实扣款/读卡器调用全部被跳过，改为往 VirtualCredit 里加点数。
    /// </summary>
    internal static class FakeEMoney
    {
        private static readonly int[] BrandIds = { 0, 1, 2, 3, 4, 5 };
        private static readonly string[] BrandNames = { "ナナコ", "Edy", "交通系IC", "WAON", "PASELI", "iD" };

        private static readonly Dictionary<EMoneyBrand, int> BrandMap =
            new Dictionary<EMoneyBrand, int>();

        private static readonly LazyCollection<EMoneyBrand> Brands = CreateBrands();
        private static readonly EMoneyResult FakeResult =
            (EMoneyResult)FormatterServices.GetUninitializedObject(typeof(EMoneyResult));

        private static int _lastBrand;
        private static int _lastCoin;
        private static bool _resultReady;
        private static int _busyUntil;
        private static bool _waitingAime;
        private static bool _creditAdded;
        private static bool _cardRead;
        private static bool _completionStarted;

        private static LazyCollection<EMoneyBrand> CreateBrands()
        {
            return new LazyCollection<EMoneyBrand>(
                delegate { return BrandIds.Length; },
                NewBrand,
                true);
        }

        private static EMoneyBrand NewBrand(int index)
        {
            EMoneyBrand brand = (EMoneyBrand)FormatterServices.GetUninitializedObject(typeof(EMoneyBrand));
            BrandMap[brand] = index;
            return brand;
        }

        private static int ClampBrand(int index)
        {
            if (index < 0)
            {
                return 0;
            }
            if (index >= BrandIds.Length)
            {
                return BrandIds.Length - 1;
            }
            return index;
        }

        private static void StartFakeDeal(int brand, int coin)
        {
            _lastBrand = ClampBrand(brand);
            _lastCoin = coin < 0 ? 0 : coin;
            _resultReady = true;
            _busyUntil = Environment.TickCount + 800;
        }

        internal static bool IsBusy
        {
            get { return Environment.TickCount < _busyUntil; }
        }

        internal static bool IsResultReady
        {
            get { return _resultReady; }
        }

        internal static void BeginFakePayment(int brand, int coin)
        {
            if (coin <= 0)
            {
                coin = 1;
            }
            StartFakeDeal(brand, coin);
            _creditAdded = false;
            _waitingAime = true;
            _cardRead = false;
            _completionStarted = false;
            _busyUntil = Environment.TickCount + 5000;
            ModLog.Info("等待刷卡: " + BrandNames[ClampBrand(brand)] + " +" + coin + " credit");
        }

        internal static bool IsWaitingAime
        {
            get { return _waitingAime; }
        }

        internal static bool CardRead
        {
            get { return _cardRead; }
        }

        /// <summary>当前选中的电子支付品牌下标（0=Nanaco 1=Edy 2=Transport 3=Waon 4=Paseli 5=iD 6=Sapica）。</summary>
        internal static int LastBrandIndex
        {
            get { return ClampBrand(_lastBrand); }
        }

        internal static void OnExternalCardRead()
        {
            if (!_waitingAime)
            {
                return;
            }
            ModLog.Debug("[EMoneyMod] 收到 HINATA Go 外部卡片, 完成假支付");
            OnAimeCardRead();
        }

        internal static void OnAimeCardRead()
        {
            if (!_waitingAime || _completionStarted)
            {
                return;
            }

            _completionStarted = true;
            _waitingAime = false;
            _cardRead = true;

            EMoneySoundCompat.PlayTouch(LastBrandIndex);
            int waitMs = EMoneySoundCompat.GetTouchDurationMs(LastBrandIndex);
            ModLog.Debug("[EMoneyMod] 品牌音播放 " + waitMs + "ms 后再同步入账点数音");
            try
            {
                MelonCoroutines.Start(FinishAfterBrandSound(waitMs));
            }
            catch (Exception e)
            {
                ModLog.Warning("启动品牌音后延迟入账失败，立即入账: " + e.Message);
                FinishAfterBrandSoundNow();
            }
        }

        private static IEnumerator FinishAfterBrandSound(int waitMs)
        {
            yield return new UnityEngine.WaitForSeconds(waitMs / 1000f);
            if (_cardRead)
            {
                FinishAfterBrandSoundNow();
            }
        }

        private static void FinishAfterBrandSoundNow()
        {
            if (!_completionStarted)
            {
                return;
            }

            EnsureCreditAdded();
            _resultReady = true;
            _busyUntil = Environment.TickCount + 1500;
            ModLog.Info("刷卡完成: +" + _lastCoin + " credit, 当前余额 " + VirtualCredit.Balance);
        }

        internal static void EnsureCreditAdded()
        {
            if (_creditAdded)
            {
                return;
            }
            _creditAdded = true;
            int coin = _lastCoin <= 0 ? 1 : _lastCoin;
            VirtualCredit.Add(coin);
            ModLog.Debug("[EMoneyMod] 假支付点数已入账: +" + coin + " credit");
            EMoneySoundCompat.PlayCredit();
        }

        internal static void WaitAimeForever()
        {
            _waitingAime = true;
            _cardRead = false;
            _completionStarted = false;
            _busyUntil = int.MaxValue;
            ModLog.Debug("[EMoneyMod] Aime 读卡器已就绪, 本次支付必须刷卡完成");
        }

        // ── AMDaemon.EMoney: 认证 / 服务 / 品牌 ─────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_IsAvailable")]
        private static bool GetIsAvailable(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_IsServiceAlive")]
        private static bool GetIsServiceAlive(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_IsAuthCompleted")]
        private static bool GetIsAuthCompleted(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_AvailableBrandCount")]
        private static bool GetAvailableBrandCount(ref int __result)
        {
            __result = BrandIds.Length;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_AvailableBrands")]
        private static void GetAvailableBrands(ref LazyCollection<EMoneyBrand> __result)
        {
            __result = Brands;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_AvailableBrandsForBalance")]
        private static void GetAvailableBrandsForBalance(ref LazyCollection<EMoneyBrand> __result)
        {
            __result = Brands;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "GetAvailableBrandCount", new Type[] { typeof(bool) })]
        private static bool GetAvailableBrandCountBool(bool forBalance, ref int __result)
        {
            __result = BrandIds.Length;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "GetAvailableBrands", new Type[] { typeof(bool) })]
        private static void GetAvailableBrandsBool(bool forBalance, ref LazyCollection<EMoneyBrand> __result)
        {
            __result = Brands;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "IsBrandAvailable", new Type[] { typeof(EMoneyBrandId) })]
        private static bool IsBrandAvailable(EMoneyBrandId brandId, ref bool __result)
        {
            __result = (int)brandId >= 0 && (int)brandId < BrandIds.Length;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "IsBrandAvailable", new Type[] { typeof(EMoneyBrandId), typeof(bool) })]
        private static bool IsBrandAvailableBalance(EMoneyBrandId brandId, bool forBalance, ref bool __result)
        {
            __result = (int)brandId >= 0 && (int)brandId < BrandIds.Length;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_TerminalId")]
        private static bool GetTerminalId(ref string __result)
        {
            __result = "FAKE-EMONEY-TERMINAL";
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_TerminalSerial")]
        private static bool GetTerminalSerial(ref string __result)
        {
            __result = "FAKE-0001";
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.EMoney), "get_IsReporting")]
        private static bool GetIsReporting(ref bool __result)
        {
            __result = false;
            return false;
        }

        // ── AMDaemon.EMoneyOperation: 支付/余额/操作状态 ─────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_IsDealAvailable")]
        private static bool OpIsDealAvailable(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_CanOperateDeal")]
        private static bool OpCanOperateDeal(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_IsBusy")]
        private static bool OpIsBusy(ref bool __result)
        {
            __result = Environment.TickCount < _busyUntil;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_IsCancellable")]
        private static bool OpIsCancellable(ref bool __result)
        {
            // 原版语义:
            //   还在等刷卡   -> 可取消 (取消/返回键正常显示)
            //   卡已读完、正在结算 -> 不可取消 (取消/返回键置灰)
            // 之前这里一直返回 false, 所以取消键从头到尾都是黑的。
            __result = _waitingAime;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_IsHeldOver")]
        private static bool OpIsHeldOver(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_IsErrorOccurred")]
        private static bool OpIsErrorOccurred(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_HasResult")]
        private static bool OpHasResult(ref bool __result)
        {
            __result = _resultReady;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "get_Result")]
        private static bool OpResult(ref EMoneyResult __result)
        {
            __result = FakeResult;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "PayToCoin",
            new Type[] { typeof(int), typeof(EMoneyBrandId), typeof(uint) })]
        private static bool PayToCoin(int playerIndex, EMoneyBrandId brandId, uint coin, ref bool __result)
        {
            StartFakeDeal((int)brandId, (int)coin);
            VirtualCredit.Add((int)coin);
            ModLog.Debug("[EMoneyMod] 假支付成功: " + BrandNames[ClampBrand((int)brandId)] + " +" + coin + " credit (余额 " + VirtualCredit.Balance + ")");
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "RequestBalance", new Type[] { typeof(EMoneyBrandId) })]
        private static bool RequestBalance(EMoneyBrandId brandId, ref bool __result)
        {
            StartFakeDeal((int)brandId, 0);
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "AuthTerminal", new Type[0])]
        private static bool AuthTerminal(ref bool __result)
        {
            _resultReady = true;
            _busyUntil = Environment.TickCount + 500;
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "RemoveTerminal", new Type[0])]
        private static bool RemoveTerminal(ref bool __result)
        {
            _resultReady = true;
            _busyUntil = Environment.TickCount + 500;
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "CheckDisplay", new Type[0])]
        private static bool CheckDisplay(ref bool __result)
        {
            _resultReady = true;
            _busyUntil = Environment.TickCount + 300;
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "Cancel", new Type[0])]
        private static bool Cancel(ref bool __result)
        {
            bool wasWaiting = _waitingAime;
            _resultReady = false;
            _busyUntil = 0;
            if (wasWaiting)
            {
                // 玩家真的按了取消: 必须停掉 HID 轮询和 Aime 扫描,
                // 否则稍后刷卡还会把点数加进去。
                _waitingAime = false;
                ModLog.Debug("[EMoneyMod] 玩家取消假支付, 停止读卡");
                try
                {
                    HinataHidReader.Stop();
                }
                catch
                {
                }
                try
                {
                    AimeReaderPatch.StopScan();
                }
                catch
                {
                }
            }
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyOperation), "CanAddCoin", new Type[] { typeof(int), typeof(uint) })]
        private static bool CanAddCoin(int playerIndex, uint coin, ref bool __result)
        {
            __result = true;
            return false;
        }

        // ── EMoneyBrand: 伪造品牌列表里的元素 ───────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyBrand), "get_Id")]
        private static bool BrandGetId(EMoneyBrand __instance, ref EMoneyBrandId __result)
        {
            int index;
            if (!BrandMap.TryGetValue(__instance, out index))
            {
                return true;
            }
            __result = (EMoneyBrandId)BrandIds[index];
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyBrand), "get_Name")]
        private static bool BrandGetName(EMoneyBrand __instance, ref string __result)
        {
            int index;
            if (!BrandMap.TryGetValue(__instance, out index))
            {
                return true;
            }
            __result = BrandNames[index];
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyBrand), "get_IconFilePath")]
        private static bool BrandGetIconFilePath(EMoneyBrand __instance, ref string __result)
        {
            int index;
            if (!BrandMap.TryGetValue(__instance, out index))
            {
                return true;
            }
            __result = string.Empty;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyBrand), "get_HasBalance")]
        private static bool BrandGetHasBalance(EMoneyBrand __instance, ref bool __result)
        {
            int index;
            if (!BrandMap.TryGetValue(__instance, out index))
            {
                return true;
            }
            __result = true;
            return false;
        }

        // ── EMoneyResult: 支付结果永远返回成功 ──────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_IsValid")]
        private static bool ResultGetIsValid(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_Status")]
        private static bool ResultGetStatus(ref EMoneyResultStatus __result)
        {
            __result = EMoneyResultStatus.Success;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_Time")]
        private static bool ResultGetTime(ref DateTime __result)
        {
            __result = DateTime.Now;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_Brand")]
        private static bool ResultGetBrand(ref EMoneyBrand __result)
        {
            __result = Brands[ClampBrand(_lastBrand)];
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_DealNumber")]
        private static bool ResultGetDealNumber(ref string __result)
        {
            __result = "FAKE-DEAL-0001";
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_CardNumber")]
        private static bool ResultGetCardNumber(ref string __result)
        {
            __result = "0000000000000000";
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_Amount")]
        private static bool ResultGetAmount(ref int __result)
        {
            __result = _lastCoin;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_BalanceBefore")]
        private static bool ResultGetBalanceBefore(ref int __result)
        {
            __result = 10000;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EMoneyResult), "get_BalanceAfter")]
        private static bool ResultGetBalanceAfter(ref int __result)
        {
            __result = 10000 - _lastCoin;
            return false;
        }
    }
}
