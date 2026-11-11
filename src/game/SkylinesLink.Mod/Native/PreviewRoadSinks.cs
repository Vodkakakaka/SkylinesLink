#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Audio;
using Game.Effects;
using Game.Notifications;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    // RoadConnectionSystem uses only the public writer ports on these holders.
    // They are never registered, and their stock system lifecycle never runs.
    internal sealed class PreviewRoadSinks
    {
        private readonly PreviewIconOutput icons = new PreviewIconOutput();
        private Bindings? bindings;
        private AudioManager? audio;
        private NativeQueue<SourceUpdateInfo> audioQueue;
        private SourceUpdateData audioWriter;
        private bool preparationAttempted, prepared, nativeEntered, joined;
        private bool audioAllocationAttempted, audioAllocated, audioReleased, releaseAttempted;
        private string? releaseFailure;

        internal IconCommandSystem Icons => prepared && !releaseAttempted ? icons.System : throw Failure("PREVIEW_ROAD_ICONS_UNAVAILABLE");
        internal AudioManager Audio => prepared && !releaseAttempted ? audio! : throw Failure("PREVIEW_ROAD_AUDIO_UNAVAILABLE");
        internal bool ResourcesReleased => releaseFailure == null && (!nativeEntered || joined) && icons.ResourcesReleased
            && (!audioAllocationAttempted || audioAllocated && audioReleased);
        internal string? ReleaseFailure => releaseFailure;

        internal void Prepare()
        {
            RequireMainThread();
            Require(!preparationAttempted && !releaseAttempted, "PREVIEW_ROAD_SINK_PREPARATION_REPEATED");
            preparationAttempted = true;
            var fields = new Bindings();
            bindings = fields;
            icons.Prepare();
            audio = new AudioManager();

            // Record the actual owned allocation before setting any holder
            // fields. A partial binding can still release this queue.
            audioAllocationAttempted = true;
            audioQueue = new NativeQueue<SourceUpdateInfo>(Allocator.Persistent);
            audioAllocated = true;
            audioWriter = new SourceUpdateData(audioQueue.AsParallelWriter());
            fields.AudioQueue.SetValue(audio, audioQueue);
            fields.AudioData.SetValue(audio, audioWriter);
            prepared = true;
            RequireReady();
        }

        internal void RequireReady()
        {
            RequireMainThread();
            RequireAudioReady();
            icons.RequireReady();
        }

        internal void MarkNativeEntered()
        {
            RequireMainThread();
            RequireAudioReady();
            icons.MarkNativeEntered();
            nativeEntered = true;
        }

        internal JObject ReadAfterJoin(JobHandle original)
        {
            RequireMainThread();
            Require(nativeEntered && !joined && !original.Equals(default(JobHandle)) && original.IsCompleted,
                "PREVIEW_ROAD_SINK_JOIN_UNCONFIRMED");
            RequireAudioBindings();

            // Road registers only the replacement job on the icon sink. Its
            // final dependency also covers the secondary-lane and audio work.
            IconCommandBuffer.Command[] commands;
            try { commands = icons.ReadAfterJoin(original, PreviewIconOutput.CommandLimit); }
            finally { joined = icons.WriterJoined; }
            int audioCount = audioQueue.Count;
            Require(audioCount >= 0, "PREVIEW_ROAD_SINK_COUNT_INVALID");
            return new JObject { ["iconCount"] = commands.Length, ["audioCount"] = audioCount };
        }

        internal void Release()
        {
            RequireMainThread();
            if (releaseAttempted || nativeEntered && !joined) return;
            releaseAttempted = true;
            try { icons.Release(); }
            catch (Exception) { releaseFailure ??= "PREVIEW_ROAD_ICON_RELEASE_FAILED"; }

            if (audioAllocated)
            {
                try
                {
                    // Clear the holder aliases before retiring the owned
                    // queue. No registered audio system ever sees these fields.
                    try
                    {
                        bindings!.AudioData.SetValue(audio, default(SourceUpdateData));
                        bindings.AudioQueue.SetValue(audio, default(NativeQueue<SourceUpdateInfo>));
                    }
                    finally { audioQueue.Dispose(); audioReleased = true; }
                }
                catch (Exception) { releaseFailure ??= "PREVIEW_ROAD_AUDIO_RELEASE_FAILED"; }
            }
        }

        private void RequireAudioReady()
        {
            Require(!nativeEntered, "PREVIEW_ROAD_SINK_ALREADY_ENTERED");
            RequireAudioBindings();
            Require(audioQueue.Count == 0, "PREVIEW_ROAD_SINK_HAS_PRIOR_OUTPUT");
        }

        private void RequireAudioBindings()
        {
            var fields = bindings ?? throw Failure("PREVIEW_ROAD_SINK_BINDINGS_MISSING");
            Require(prepared && !releaseAttempted && audio != null && audio.World == null
                && audioAllocated && audioQueue.IsCreated
                && ((NativeQueue<SourceUpdateInfo>)fields.AudioQueue.GetValue(audio)!).Equals(audioQueue)
                && ((SourceUpdateData)fields.AudioData.GetValue(audio)!).Equals(audioWriter)
                && ((JobHandle)fields.AudioDependencies.GetValue(audio)!).Equals(default(JobHandle)),
                "PREVIEW_ROAD_SINK_BINDINGS_CHANGED");
        }

        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "PREVIEW_ROAD_SINK_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static InvalidOperationException Failure(string code) => new InvalidOperationException(code);

        private sealed class Bindings
        {
            internal readonly FieldInfo AudioQueue, AudioData, AudioDependencies;

            internal Bindings()
            {
                const string gameMvid = "e15551e93dea48558dde1da3cea7d7ef";
                Require(typeof(AudioManager).Module.ModuleVersionId.ToString("N") == gameMvid
                    && typeof(SourceUpdateData).Module == typeof(AudioManager).Module
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "PREVIEW_ROAD_SINK_NATIVE_BUILD_UNQUALIFIED");
                AudioQueue = Field(typeof(AudioManager), "m_SourceUpdateQueue", typeof(NativeQueue<SourceUpdateInfo>));
                AudioData = Field(typeof(AudioManager), "m_SourceUpdateData", typeof(SourceUpdateData));
                AudioDependencies = Field(typeof(AudioManager), "m_SourceUpdateWriter", typeof(JobHandle));
            }

            private static FieldInfo Field(Type owner, string name, Type type)
            {
                var field = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_ROAD_SINK_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
