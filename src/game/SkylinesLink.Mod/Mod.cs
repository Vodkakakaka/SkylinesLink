using System;
using Colossal.Logging;
using Game;
using Game.Modding;
#if SKYLINESLINK_INTEGRATION_PROBE
using SkylinesLink.Application.Context;
#endif

namespace SkylinesLink.Mod
{
    /// <summary>原生装配入口。普通接入先核实际已载发行，开发探针另由编译符号隔离。</summary>
    public sealed class Mod : IMod
    {
        private static readonly ILog Log = LogManager.GetLogger("SkylinesLink.Mod").SetShowsErrorsInUI(false);
#if SKYLINESLINK_INTEGRATION_PROBE
        private WorldContextTracker? context;
        private Native.MenuProbeSession? menuProbe;
#else
        private Native.NormalModSession? session;
#endif

        public void OnLoad(UpdateSystem updateSystem)
        {
#if SKYLINESLINK_INTEGRATION_PROBE
            if (context != null)
            {
                Log.Info("SkylinesLink load ignored while its previous lifetime is still held.");
                return;
            }
            context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
            context.MarkUnavailable();
            // 游戏以 GetUninitializedObject 装配 IMod，必须在 OnLoad 显式创建会话。
            menuProbe = Native.MenuProbeSession.TryStart(this, updateSystem, message => Log.Info(message));
            Log.Info(menuProbe == null ? "SkylinesLink integration probe inactive; no per-launch request." :
                "SkylinesLink integration probe loaded; bounded per-launch experiment.");
#else
            if (session != null && !session.IsFullyReleased)
            {
                Log.Info("SkylinesLink load ignored while its previous lifetime is still held.");
                return;
            }
            // IMod can be created without its constructor. Own a fully constructed
            // root before SDK registration so partial startup is still releasable.
            session = new Native.NormalModSession(this, updateSystem, message => Log.Info(message));
            try
            {
                session.Start();
                Log.Info("SkylinesLink normal connection root loaded; readiness is available in mod settings.");
            }
            catch (Exception error)
            {
                Log.Info("SkylinesLink connection startup unavailable: " + error.GetType().Name);
                try { session.RequestStop(); }
                catch (Exception closeError) { Log.Info("SkylinesLink cleanup incomplete: " + closeError.GetType().Name); }
            }
#endif
        }

        public void OnDispose()
        {
#if SKYLINESLINK_INTEGRATION_PROBE
            var disposedContext = context;
            disposedContext?.Stop();
            menuProbe?.RequestStop();
            menuProbe = null;
            context = null;
#else
            try { session?.RequestStop(); }
            catch (Exception error)
            {
                Log.Info("SkylinesLink connection stop incomplete: " + error.GetType().Name);
            }
#endif
            Log.Info("SkylinesLink stop requested; pending work retains its own lifetime.");
        }
    }
}
