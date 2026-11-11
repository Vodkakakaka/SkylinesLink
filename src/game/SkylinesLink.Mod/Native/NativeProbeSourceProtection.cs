#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Shared by the two native qualification owners. Captures identities, not
    // a world copy or a global version; never restores player state.
    internal sealed class NativeProbeSourceProtection
    {
        private const int MaxSourceSystems = 4096;
        private const int MaxAppendedSystems = 16;
        private readonly World source;
        private readonly World? defaultWorld;
        private readonly World[] worlds;
        private readonly ComponentSystemBase[] systems;
        private readonly ToolSystem tool;
        private readonly ToolBaseSystem activeTool;
        private readonly Entity selected;
        private readonly int selectedIndex;
        private readonly List<ToolBaseSystem> tools;
        private readonly ToolBaseSystem[] toolContents;
        private bool defaultSame, worldsSame, systemsSame, toolSame, activeSame, selectedSame, selectedIndexSame, listSame, contentsSame;
        private string? failure;

        internal NativeProbeSourceProtection(World source)
        {
            RequireMainThread();
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            defaultWorld = World.DefaultGameObjectInjectionWorld;
            worlds = ReadWorlds(); systems = ReadSystems(source, MaxSourceSystems);
            tool = source.GetExistingSystemManaged<ToolSystem>() ?? throw new InvalidOperationException("PROBE_TOOL_SOURCE_UNAVAILABLE");
            activeTool = tool.activeTool; selected = tool.selected; selectedIndex = tool.selectedIndex;
            tools = tool.tools;
            if (tools == null || tools.Count > 256) throw new InvalidOperationException("PROBE_TOOL_LIST_BUDGET");
            toolContents = tools.ToArray();
        }

        internal bool Contains<T>() where T : ComponentSystemBase
        { foreach (var item in systems) if (item is T) return true; return false; }

        internal static bool IsRegistered(World world, ComponentSystemBase expected)
        { foreach (var item in ReadSystems(world, MaxSourceSystems + MaxAppendedSystems)) if (ReferenceEquals(item, expected)) return true; return false; }

        internal bool Passed => failure == null && defaultSame && worldsSame && systemsSame && toolSame && activeSame
            && selectedSame && selectedIndexSame && listSame && contentsSame;

        // During creation, only these exact appended instances are allowed.
        // After cleanup callers pass none, requiring the original list back.
        internal void Observe(params ComponentSystemBase[] appended)
        {
            RequireMainThread();
            defaultSame = worldsSame = systemsSame = toolSame = activeSame = selectedSame = selectedIndexSame = listSame = contentsSame = false;
            try
            {
                if (appended == null || appended.Length > MaxAppendedSystems) throw new InvalidOperationException();
                defaultSame = ReferenceEquals(defaultWorld, World.DefaultGameObjectInjectionWorld);
                worldsSame = Same(worlds, ReadWorlds());
                var current = ReadSystems(source, MaxSourceSystems + MaxAppendedSystems);
                systemsSame = current.Length == systems.Length + appended.Length;
                for (int index = 0; systemsSame && index < systems.Length; index++) systemsSame = ReferenceEquals(current[index], systems[index]);
                for (int index = 0; systemsSame && index < appended.Length; index++) systemsSame = ReferenceEquals(current[systems.Length + index], appended[index]);
                toolSame = ReferenceEquals(source.GetExistingSystemManaged<ToolSystem>(), tool);
                activeSame = ReferenceEquals(tool.activeTool, activeTool);
                selectedSame = tool.selected == selected; selectedIndexSame = tool.selectedIndex == selectedIndex;
                var currentTools = tool.tools; listSame = ReferenceEquals(currentTools, tools);
                contentsSame = currentTools != null && currentTools.Count == toolContents.Length;
                for (int index = 0; contentsSame && index < toolContents.Length; index++) contentsSame = ReferenceEquals(currentTools![index], toolContents[index]);
            }
            catch (Exception) { failure ??= "PROTECTION_OBSERVATION_UNAVAILABLE"; }
        }

        // Cached ordinary facts only, including after source disposal.
        internal JObject Snapshot() => new JObject {
            ["observed"] = true, ["worldCount"] = worlds.Length, ["sourceSystemCount"] = systems.Length, ["toolCount"] = toolContents.Length,
            ["defaultUnchanged"] = defaultSame, ["worldReferencesUnchanged"] = worldsSame, ["sourceSystemOrderUnchanged"] = systemsSame,
            ["toolSystemUnchanged"] = toolSame, ["activeToolUnchanged"] = activeSame, ["selectedUnchanged"] = selectedSame,
            ["selectedIndexUnchanged"] = selectedIndexSame, ["toolListReferenceUnchanged"] = listSame, ["toolContentsUnchanged"] = contentsSame,
            ["failureCode"] = failure == null ? JValue.CreateNull() : new JValue(failure)
        };

        private static World[] ReadWorlds()
        {
            var all = World.All;
            if (all.Count > 64) throw new InvalidOperationException("PROBE_WORLD_OBSERVATION_BUDGET");
            var result = new World[all.Count]; for (int index = 0; index < result.Length; index++) result[index] = all[index]; return result;
        }
        private static ComponentSystemBase[] ReadSystems(World world, int maximum)
        {
            if (!world.IsCreated) throw new InvalidOperationException("PROBE_SOURCE_WORLD_UNAVAILABLE");
            var all = world.Systems;
            if (all.Count > maximum) throw new InvalidOperationException("PROBE_SYSTEM_OBSERVATION_BUDGET");
            var result = new ComponentSystemBase[all.Count]; for (int index = 0; index < result.Length; index++) result[index] = all[index]; return result;
        }
        private static bool Same<T>(T[] left, T[] right) where T : class
        { if (left.Length != right.Length) return false; for (int index = 0; index < left.Length; index++) if (!ReferenceEquals(left[index], right[index])) return false; return true; }
        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("PROBE_PROTECTION_MAIN_THREAD_REQUIRED"); }
    }
}
#endif
