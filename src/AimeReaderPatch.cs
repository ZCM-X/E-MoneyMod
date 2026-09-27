using System;
using System.Reflection;
using AMDaemon;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 在假电子支付等待刷卡时，复刻原版 TryAime 的读取状态机：
    ///   AdvCheck -> AnyRead -> NewAime/NewFelica/Done
    /// 区别只在于原版会等玩家点确认，这里自动确认，然后完成假支付。
    /// </summary>
    internal static class AimeReaderPatch
    {
        private static bool _loggedPatch;
        private static bool _loggedStart;
        private static int _lastDiagAt;
        private static int _lastCancelAt;
        private static int _stage;
        private static int _lastBlinkAt;
        private static bool _blinkOn;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(AimeReaderManager), "Execute", new System.Type[0], null);
                MethodInfo prefix = AccessTools.Method(typeof(AimeReaderPatch), "ExecutePrefix");
                MethodInfo postfix = AccessTools.Method(typeof(AimeReaderPatch), "ExecutePostfix");
                if (target == null || postfix == null || prefix == null)
                {
                    ModLog.Warning("[EMoneyMod] 找不到 AimeReaderManager.Execute 补丁目标");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                ModLog.Debug("[EMoneyMod] 已挂 Aime 读卡器补丁: AimeReaderManager.Execute");
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 挂 Aime 读卡器补丁失败: " + e.Message);
            }
        }

        /// <summary>
        /// 关键：我们用 HID 直连读卡器时，必须让游戏这边的 Aime 状态机**完全停住**。
        ///
        /// 原因是 segatools.ini 里 [aimeio] path=hinata.dll —— 那个驱动也连着同一个
        /// HINATA 读卡器。如果游戏同时还在走 Aime 读取流程，AMDaemon 会通过 hinata.dll
        /// 往同一个读卡器发 PN532 帧，和我们的帧互相抢答，手机端就会读失败
        /// （表现为 SELECT AID 只回一个 0x27，而不是完整的 FCI + 90 00）。
        /// 本地测试工具没有游戏进程，所以一直是干净的。
        /// </summary>
        public static bool ExecutePrefix()
        {
            if (!HinataHidReader.IsRunning)
            {
                return true;
            }

            // 我们独占读卡器期间: 顺便把游戏侧还在跑的 Aime 命令也取消掉,
            // 尽量让 AMDaemon 别再通过 hinata.dll 去轮询同一个读卡器。
            int now = Environment.TickCount;
            if (unchecked(now - _lastCancelAt) >= 150)
            {
                _lastCancelAt = now;
                try
                {
                    if (Aime.UnitCount > 0)
                    {
                        Aime.Units[0].Cancel();
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        public static void ExecutePostfix(AimeReaderManager __instance)
        {
            if (HinataHidReader.IsRunning)
            {
                return;
            }
            if (!FakeEMoney.IsWaitingAime || __instance == null)
            {
                return;
            }
            try
            {
                if (!_loggedStart)
                {
                    _loggedStart = true;
                    ModLog.Debug("[EMoneyMod] Aime 读取状态机开始 (stage=0)");
                }

                Diagnostic(__instance);

                // 原版 TryAime: AdvCheck -> AnyRead -> GetResult
                if (_stage == 0)
                {
                    BlinkBlue();
                    if (__instance.AdvCheck())
                    {
                        _stage = 1;
                        ModLog.Debug("[EMoneyMod] Aime stage 0 -> 1 (AdvCheck)");
                    }
                    return;
                }

                if (_stage == 1)
                {
                    BlinkBlue();
                    if (__instance.GetResult() == AimeReaderManager.Result.Error)
                    {
                        ModLog.Warning("[EMoneyMod] Aime 读取错误: " + __instance.GetErrorCategory());
                        return;
                    }
                    if (__instance.AnyRead())
                    {
                        _stage = 2;
                        ModLog.Debug("[EMoneyMod] Aime stage 1 -> 2 (AnyRead)");
                    }
                    return;
                }

                AimeReaderManager.Result result = __instance.GetResult();
                if (_stage == 2)
                {
                    switch (result)
                    {
                        case AimeReaderManager.Result.Done:
                            Complete(__instance, "Done");
                            return;
                        case AimeReaderManager.Result.NewAime:
                            __instance.Confirm(true, false);
                            _stage = 3;
                            ModLog.Debug("[EMoneyMod] Aime NewAime -> 自动确认");
                            return;
                        case AimeReaderManager.Result.NewFelica:
                            __instance.Confirm(true, true);
                            _stage = 3;
                            ModLog.Debug("[EMoneyMod] Aime NewFelica -> 自动确认");
                            return;
                        case AimeReaderManager.Result.Error:
                            ModLog.Warning("[EMoneyMod] Aime 读取错误: " + __instance.GetErrorCategory());
                            return;
                    }
                    return;
                }

                if (_stage == 3 && result == AimeReaderManager.Result.Done)
                {
                    Complete(__instance, "Done after confirm");
                }
            }
            catch (Exception e)
            {
                if (!_loggedPatch)
                {
                    _loggedPatch = true;
                    ModLog.Warning("[EMoneyMod] Aime 读卡处理异常: " + e.Message);
                }
            }
        }

        private static void Complete(AimeReaderManager instance, string reason)
        {
            ModLog.Debug("[EMoneyMod] Aime 读卡完成(" + reason + "), 结束假支付");
            FakeEMoney.OnAimeCardRead();
            SetSuccessLed();
        }

        private static void Diagnostic(AimeReaderManager instance)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastDiagAt) < 1000)
            {
                return;
            }
            _lastDiagAt = now;
            if (Aime.UnitCount > 0)
            {
                AimeUnit unit = Aime.Units[0];
                ModLog.Debug("[EMoneyMod][AimeDiag] stage=" + _stage
                    + " result=" + instance.GetResult()
                    + " busy=" + unit.IsBusy
                    + " confirm=" + unit.HasConfirm
                    + " hasResult=" + unit.HasResult
                    + " error=" + unit.HasError
                    + " led=" + unit.LedStatus);
            }
        }

        internal static void StartScan()
        {
            try
            {
                _stage = 0;
                _loggedStart = false;
                FakeEMoney.WaitAimeForever();

                // 先让游戏这边的 Aime 读取彻底停下来, 再抢读卡器。
                // 否则 AMDaemon 会通过 aimeio(hinata.dll) 同时轮询同一个读卡器,
                // 两边的 PN532 帧互相干扰。
                StopScan();

                if (HinataHidReader.TryStart())
                {
                    ModLog.Debug("[EMoneyMod] 已切换 HINATA HID 直连读卡 (游戏侧 Aime 已停)");
                    return;
                }
                if (Aime.UnitCount <= 0)
                {
                    ModLog.Debug("[EMoneyMod] 没有 Aime 读卡器单元, 假支付将超时自动完成");
                    return;
                }

                AmManager am = SingletonStateMachine<AmManager, AmManager.EState>.Instance;
                if (am == null || am.AimeReader == null)
                {
                    ModLog.Debug("[EMoneyMod] AimeReaderManager 还没就绪, 假支付将超时自动完成");
                    return;
                }

                FakeEMoney.WaitAimeForever();
                am.AimeReader.EnableRead(true);
                ModLog.Debug("[EMoneyMod] 已启动 Aime 读卡器扫描, 请刷卡");
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] 启动 Aime 读卡器失败: " + e.Message);
            }
        }

        internal static void StopScan()
        {
            try
            {
                AmManager am = SingletonStateMachine<AmManager, AmManager.EState>.Instance;
                if (am != null && am.AimeReader != null)
                {
                    am.AimeReader.EnableRead(false);
                }
                if (Aime.UnitCount > 0)
                {
                    Aime.Units[0].Cancel();
                    Aime.Units[0].SetLed(false, false, false);
                }
            }
            catch
            {
            }
        }

        private static void BlinkBlue()
        {
            if (Aime.UnitCount <= 0)
            {
                return;
            }
            int now = Environment.TickCount;
            if (unchecked(now - _lastBlinkAt) < 500)
            {
                return;
            }
            _lastBlinkAt = now;
            _blinkOn = !_blinkOn;
            Aime.Units[0].SetLed(false, false, _blinkOn);
        }

        private static void SetSuccessLed()
        {
            try
            {
                if (Aime.UnitCount > 0)
                {
                    Aime.Units[0].SetLedStatus(AimeLedStatus.Success);
                }
            }
            catch
            {
            }
        }
    }
}