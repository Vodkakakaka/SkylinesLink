#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Notifications;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    // The stock writer ports use these fields without starting a registered
    // icon system. The queued commands are evidence and are never applied.
    internal sealed class PreviewIconOutput
    {
        internal const int CommandLimit = 256;

        private readonly List<NativeQueue<IconCommandBuffer.Command>> queues =
            new List<NativeQueue<IconCommandBuffer.Command>>(1);
        private Bindings? bindings;
        private IconCommandSystem? holder;
        private JobHandle joinedDependency;
        private bool preparationAttempted, nativeEntered, readAttempted, releaseAttempted;

        internal IconCommandSystem System => holder != null && !releaseAttempted
            ? holder : throw new InvalidOperationException("PREVIEW_ICON_OUTPUT_UNAVAILABLE");
        internal bool WriterJoined => !joinedDependency.Equals(default(JobHandle));
        internal bool ResourcesReleased => (!nativeEntered || WriterJoined) && queues.Count == 0;

        internal void Prepare()
        {
            RequireMainThread();
            Require(!preparationAttempted && !releaseAttempted, "PREVIEW_ICON_PREPARATION_REPEATED");
            preparationAttempted = true;
            bindings = new Bindings();
            var system = new IconCommandSystem();
            bindings.Queues.SetValue(system, queues);
            holder = system;
            RequireReady();
        }

        internal void RequireReady()
        {
            RequireMainThread();
            Require(!nativeEntered, "PREVIEW_ICON_ALREADY_ENTERED");
            var fields = RequireBindings();
            Require(queues.Count == 0 && (int)fields.BufferIndex.GetValue(holder)! == 0
                && ((JobHandle)fields.Dependencies.GetValue(holder)!).Equals(default(JobHandle)),
                "PREVIEW_ICON_HAS_PRIOR_OUTPUT");
        }

        internal void MarkNativeEntered()
        {
            RequireReady();
            nativeEntered = true;
        }

        internal void ConfirmJoin(JobHandle completedDependency)
        {
            RequireMainThread();
            Require(nativeEntered && !releaseAttempted && !completedDependency.Equals(default(JobHandle)),
                "PREVIEW_ICON_JOIN_UNCONFIRMED");
            if (WriterJoined)
            {
                Require(joinedDependency.Equals(completedDependency), "PREVIEW_ICON_JOIN_CHANGED");
                return;
            }

            Require(completedDependency.IsCompleted, "PREVIEW_ICON_JOIN_UNCONFIRMED");
            var fields = RequireBindings();
            var writer = (JobHandle)fields.Dependencies.GetValue(holder)!;
            Require(!writer.Equals(default(JobHandle))
                && queues.Count == 1 && (int)fields.BufferIndex.GetValue(holder)! == 1 && queues[0].IsCreated,
                "PREVIEW_ICON_WRITER_UNCONFIRMED");
            // Stock combines dependencies even with one registered producer.
            // Join the actual registered writer instead of comparing handles.
            writer.Complete();
            joinedDependency = completedDependency;
        }

        internal IconCommandBuffer.Command[] ReadAfterJoin(JobHandle completedDependency, int limit)
        {
            ConfirmJoin(completedDependency);
            Require(!readAttempted && limit >= 0 && limit <= CommandLimit, "PREVIEW_ICON_READ_UNAVAILABLE");
            readAttempted = true;
            var queue = queues[0];
            int count = queue.Count;
            Require(count >= 0 && count <= limit, "PREVIEW_ICON_COMMAND_BUDGET");
            if (count == 0) return Array.Empty<IconCommandBuffer.Command>();
            using var sample = queue.ToArray(Allocator.Temp);
            Require(sample.Length == count, "PREVIEW_ICON_COMMAND_COUNT_CHANGED");
            return sample.ToArray();
        }

        internal void Release()
        {
            RequireMainThread();
            if (releaseAttempted || nativeEntered && !WriterJoined) return;
            releaseAttempted = true;
            if (queues.Count == 0) return;
            Require(WriterJoined && queues.Count == 1, "PREVIEW_ICON_RELEASE_OWNERSHIP_UNCONFIRMED");
            var queue = queues[0];
            queue.Dispose();
            queues.Clear();
        }

        private Bindings RequireBindings()
        {
            Require(holder != null && !releaseAttempted && holder.World == null && bindings != null
                && ReferenceEquals(bindings.Queues.GetValue(holder), queues), "PREVIEW_ICON_BINDINGS_CHANGED");
            return bindings!;
        }

        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "PREVIEW_ICON_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(code);
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Queues, BufferIndex, Dependencies;

            internal Bindings()
            {
                var type = typeof(IconCommandSystem);
                Require(type.Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(IconCommandBuffer).Module == type.Module
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "PREVIEW_ICON_NATIVE_BUILD_UNQUALIFIED");
                Queues = Field(type, "m_Queues", typeof(List<NativeQueue<IconCommandBuffer.Command>>));
                BufferIndex = Field(type, "m_BufferIndex", typeof(int));
                Dependencies = Field(type, "m_Dependencies", typeof(JobHandle));
            }

            private static FieldInfo Field(Type owner, string name, Type type)
            {
                var field = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_ICON_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
