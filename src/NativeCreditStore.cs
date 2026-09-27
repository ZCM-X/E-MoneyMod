using System;
using HarmonyLib;
using Manager;
using AmBackup = AMDaemon.Backup;
using GameBackup = Manager.Backup;

namespace EMoneyMod
{
    /// <summary>
    /// Stores the mod balance in the game's own local backup record.
    /// AMDaemon writes that record to appdata\SDEZ\appfile.dat, so clearing
    /// appdata also clears the mod balance and no sidecar file is needed.
    /// </summary>
    internal static class NativeCreditStore
    {
        private const int MagicSlot = 61;
        private const int VersionSlot = 62;
        private const int BalanceSlot = 63;
        private const uint Magic = 0x454D4F4E; // "EMON"
        private const uint Version = 1;

        private static readonly object Sync = new object();
        private static BackupLocalParameterRecord _record;
        private static bool _ready;
        private static bool _loaded;
        private static bool _saveRequested;
        private static int _pendingBalance;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(GameBackup), "Execute_Ready", "BackupReadyPostfix");
            Patch(harmony, typeof(Manager.Credit), "Execute", "CreditExecutePostfix");
        }

        internal static void RequestSave(int balance)
        {
            lock (Sync)
            {
                _pendingBalance = Clamp(balance);
                _saveRequested = true;
            }
        }

        private static void Patch(HarmonyLib.Harmony harmony, System.Type targetType, string targetMethod, string patchMethod)
        {
            try
            {
                System.Reflection.MethodInfo target =
                    AccessTools.Method(targetType, targetMethod, new System.Type[0], null);
                System.Reflection.MethodInfo postfix = AccessTools.Method(typeof(NativeCreditStore), patchMethod);
                if (target == null || postfix == null)
                {
                    ModLog.Warning("找不到 " + targetType.FullName + "." + targetMethod + " 的备份补丁目标");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                ModLog.Debug("已挂游戏备份点数补丁: " + targetType.Name + "." + targetMethod);
            }
            catch (Exception e)
            {
                ModLog.Warning("挂游戏备份点数补丁失败 " + targetType.Name + "." + targetMethod + ": " + e.Message);
            }
        }

        private static void BackupReadyPostfix(GameBackup __instance)
        {
            if (__instance == null)
            {
                return;
            }
            try
            {
                BackupLocalParameterRecord record = __instance.localParameter.getRecord() as BackupLocalParameterRecord;
                if (record == null || record.reserved == null || record.reserved.Length <= BalanceSlot)
                {
                    return;
                }

                int loadedBalance = 0;
                lock (Sync)
                {
                    if (_ready)
                    {
                        return;
                    }
                    _record = record;
                    _ready = true;
                    loadedBalance = ReadBalance(record);
                    _loaded = true;
                }

                if (loadedBalance > 0)
                {
                    VirtualCredit.ApplyLoadedBalance(loadedBalance);
                    ModLog.Info("已从游戏 appfile.dat 读回点数: " + loadedBalance);
                }
                FlushPendingSave();
            }
            catch (Exception e)
            {
                ModLog.Warning("读取游戏备份点数失败: " + e.Message);
            }
        }

        private static void CreditExecutePostfix()
        {
            FlushPendingSave();
        }

        private static void FlushPendingSave()
        {
            BackupLocalParameterRecord record;
            int balance;
            lock (Sync)
            {
                if (!_ready || !_loaded || !_saveRequested || _record == null)
                {
                    return;
                }
                record = _record;
                balance = _pendingBalance;
                _saveRequested = false;
            }

            try
            {
                record.reserved[MagicSlot] = Magic;
                record.reserved[VersionSlot] = Version;
                record.reserved[BalanceSlot] = (uint)Clamp(balance);

                if (AmBackup.SaveRecordByData(record))
                {
                    ModLog.Debug("点数已写入游戏备份: " + balance);
                }
                else
                {
                    lock (Sync)
                    {
                        _saveRequested = true;
                    }
                }
            }
            catch (Exception e)
            {
                lock (Sync)
                {
                    _saveRequested = true;
                }
                ModLog.Warning("写入游戏备份点数失败: " + e.Message);
            }
        }

        private static int ReadBalance(BackupLocalParameterRecord record)
        {
            if (record.reserved[MagicSlot] != Magic || record.reserved[VersionSlot] != Version)
            {
                return 0;
            }
            return Clamp((int)record.reserved[BalanceSlot]);
        }

        private static int Clamp(int value)
        {
            if (value < 0)
            {
                return 0;
            }
            return value > 9999 ? 9999 : value;
        }
    }
}
