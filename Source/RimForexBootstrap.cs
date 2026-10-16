using System.Reflection;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 加载时输出一次标识，方便在 Player.log 里确认模组已被加载。
    ///
    /// ── 版本号不能写死在这里 ──
    /// 最早这里写的是字面量 "Alpha 0.1.0"。后果：About/About.xml 里的 modVersion
    /// 早就升到 0.2.0 了，日志却一直打印 0.1.0 —— 排查问题时会被这个数字误导，
    /// 而且没人会想到要同时改两处。
    ///
    /// 现在直接从程序集元数据读。csproj 开了 GenerateAssemblyInfo，
    /// 且 ContinuousIntegrationBuild 会把源码提交哈希也带上，
    /// 所以这行日志同时能回答两个问题：**这是哪一版、由哪份源码编出来的**。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RimForexBootstrap
    {
        static RimForexBootstrap()
        {
            Log.Message("[RimForex] " + Describe() + " loaded. "
                + "FX trading terminal available under Production.");
        }

        /// <summary>
        /// 形如 <c>0.2.0+14836970c57b1ba4091fc942c63e86ffb50e419e</c>；
        /// 取不到时退回 "unknown"，不抛异常 —— 只为一句话的日志不值得让加载失败。
        /// </summary>
        private static string Describe()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();

                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                {
                    return info.InformationalVersion;
                }

                System.Version v = asm.GetName().Version;
                return v != null ? v.ToString() : "unknown";
            }
            catch (System.Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }
    }
}
