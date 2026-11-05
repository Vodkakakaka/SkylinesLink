using System;
using Colossal.Core;
using Game.Modding;
using Game.SceneFlow;
using SkylinesLink.Infrastructure.Installation;

namespace SkylinesLink.Mod.Native
{
    /// <summary>从真实 Mod 实例捕获自身资产来源；后台只接收普通值。</summary>
    public static class NativeOwnBundleLocator
    {
        public static ModBundleOrigin Capture(IMod mod)
        {
            if (!MainThreadDispatcher.isMainThread)
                throw new InvalidOperationException("OWN_BUNDLE_MAIN_THREAD_REQUIRED");
            if (mod == null) throw new InvalidOperationException("OWN_BUNDLE_SOURCE_UNAVAILABLE");
            try
            {
                var game = GameManager.instance;
                var manager = game?.modManager;
                if (manager == null || !manager.TryGetExecutableAsset(mod, out var asset) || asset == null)
                    throw new InvalidOperationException("OWN_BUNDLE_SOURCE_UNAVAILABLE");
                var assembly = mod.GetType().Assembly;
                if (!ReferenceEquals(asset.assembly, assembly))
                    throw new InvalidOperationException("OWN_BUNDLE_SOURCE_UNAVAILABLE");

                var meta = asset.GetMeta();
                Guid moduleId = assembly.ManifestModule.ModuleVersionId;
                if (string.IsNullOrWhiteSpace(meta.path) || moduleId == Guid.Empty ||
                    !ReferenceEquals(game, GameManager.instance) || !ReferenceEquals(manager, game!.modManager) ||
                    !ReferenceEquals(asset.assembly, assembly))
                    throw new InvalidOperationException("OWN_BUNDLE_SOURCE_UNAVAILABLE");

                // A packaged path remains an origin fact, never an executable
                // location. The Core verifier decides supported layouts later.
                return new ModBundleOrigin(meta.path, meta.packaged, moduleId);
            }
            catch (Exception)
            {
                // SDK metadata failures can include paths; expose only a fixed
                // category, with no native exception or URI retained as inner data.
                throw new InvalidOperationException("OWN_BUNDLE_SOURCE_UNAVAILABLE");
            }
        }
    }
}
