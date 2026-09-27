using System;
using System.IO;
using System.Reflection;

namespace EMoneyMod
{
    /// <summary>
    /// 让 EMoneyMod.dll 单文件分发：HidSharp.dll 以嵌入资源的方式打包进来，
    /// 运行时由 AssemblyResolve 从内存里加载，Mods 目录不再需要单独放 HidSharp.dll。
    ///
    /// 注意: HidSharp 不依赖自身所在路径(Windows 上只 P/Invoke 系统 hid.dll /
    /// setupapi.dll), 所以 Assembly.Load(byte[]) 这种"没有 Location"的加载方式没问题。
    /// </summary>
    internal static class EmbeddedAssemblyLoader
    {
        /// <summary>嵌入资源名, 与 csproj 里的 LogicalName 一致。</summary>
        private const string HidSharpResource = "EMoneyMod.Embedded.HidSharp.dll";
        private const string HidSharpName = "HidSharp";

        private static int _installed;

        /// <summary>挂上 AssemblyResolve；重复调用无副作用。</summary>
        internal static void Install()
        {
            if (System.Threading.Interlocked.Exchange(ref _installed, 1) == 1)
            {
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requested = new AssemblyName(args.Name);
                if (!string.Equals(requested.Name, HidSharpName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // 已经加载过就直接复用, 避免重复 Load 出两份程序集。
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, HidSharpName, StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded;
                    }
                }

                byte[] raw = ReadResource(HidSharpResource);
                if (raw == null)
                {
                    ModLog.Warning("[EMoneyMod] 嵌入的 HidSharp 资源缺失, 读卡功能不可用");
                    return null;
                }
                return Assembly.Load(raw);
            }
            catch (Exception e)
            {
                try
                {
                    ModLog.Warning("[EMoneyMod] 加载嵌入的 HidSharp 失败: " + e.Message);
                }
                catch
                {
                }
                return null;
            }
        }

        private static byte[] ReadResource(string name)
        {
            Assembly self = typeof(EmbeddedAssemblyLoader).Assembly;
            using (Stream stream = self.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    return null;
                }
                byte[] buffer = new byte[stream.Length];
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }
                    offset += read;
                }
                return buffer;
            }
        }
    }
}
