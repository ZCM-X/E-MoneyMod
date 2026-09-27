using System;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// Centralized logging. Normal runs stay quiet; set EMONEYMOD_VERBOSE=1
    /// to enable diagnostic logs.
    /// </summary>
    internal static class ModLog
    {
        private static readonly bool Verbose =
            string.Equals(Environment.GetEnvironmentVariable("EMONEYMOD_VERBOSE"), "1",
                StringComparison.OrdinalIgnoreCase);

        internal static void Info(string message)
        {
            MelonLogger.Msg("[EMoneyMod] " + Clean(message));
        }

        internal static void Debug(string message)
        {
            if (Verbose)
            {
                MelonLogger.Msg("[EMoneyMod][Debug] " + Clean(message));
            }
        }

        internal static void Warning(string message)
        {
            MelonLogger.Warning("[EMoneyMod] " + Clean(message));
        }

        internal static void Error(string message)
        {
            MelonLogger.Error("[EMoneyMod] " + Clean(message));
        }

        private static string Clean(string message)
        {
            const string prefix = "[EMoneyMod] ";
            if (message != null && message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return message.Substring(prefix.Length);
            }
            return message ?? string.Empty;
        }
    }
}
