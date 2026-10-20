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
    ///
    /// ── 设置页也用它（2026-10-20）──
    /// 订阅者报 bug 时，最常见的来回是"你装的是哪一版？"。
    /// 把版本号显示在模组设置页顶部，玩家截图就能带上，省一轮问答。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RimForexBootstrap
    {
        /// <summary>
        /// 给人看的短版本标签，形如 <c>0.2.2 (b3c0723)</c>。
        ///
        /// 为什么不用完整 InformationalVersion：它带 40 位提交哈希
        /// （<c>0.2.2+b3c0723abc…</c>），在设置页里会占满一整行。
        /// 这里把哈希截到 7 位 —— 足够在 GitHub 上唯一定位到某次提交，
        /// 又不会把界面撑爆。完整值仍然只出现在 Player.log 里。
        /// </summary>
        public static string VersionLabel { get; private set; }

        static RimForexBootstrap()
        {
            string full = Describe();
            VersionLabel = Compact(full);

            Log.Message("[RimForex] " + full + " loaded. "
                + "FX trading terminal available under Production.");
        }

        /// <summary>
        /// 把 <c>0.2.2+b3c0723abc…</c> 压成 <c>0.2.2 (b3c0723)</c>。
        /// 没有 '+' 就原样返回；取不到元数据时返回 "unknown"。
        /// </summary>
        private static string Compact(string full)
        {
            if (string.IsNullOrEmpty(full)) return "unknown";

            int plus = full.IndexOf('+');
            if (plus < 0) return full;

            string ver = full.Substring(0, plus);
            string hash = full.Substring(plus + 1);
            if (hash.Length > 7) hash = hash.Substring(0, 7);

            return hash.Length > 0 ? ver + " (" + hash + ")" : ver;
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
