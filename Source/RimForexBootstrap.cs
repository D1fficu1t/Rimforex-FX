using Verse;

namespace RimForex
{
    /// <summary>用于在加载时输出一次标识，方便在 Player.log 中确认模组已被加载。</summary>
    [StaticConstructorOnStartup]
    public static class RimForexBootstrap
    {
        static RimForexBootstrap()
        {
            Log.Message("[RimForex] Alpha 0.1.0 loaded. FX trading terminal available under Production.");
        }
    }
}
