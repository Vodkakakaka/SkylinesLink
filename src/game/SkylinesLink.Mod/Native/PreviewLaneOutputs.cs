#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class PreviewLaneBarrier : ModificationBarrier4
    {
        private bool creationStarted, destructionStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            if (!MainThreadDispatcher.isMainThread)
                throw new InvalidOperationException("PREVIEW_LANE_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_LANE_BARRIER_CANNOT_PLAYBACK");

        internal EntityCommandBuffer BorrowSingle()
        {
            if (!CreationReturned || destructionStarted || PendingCount != 1)
                throw new InvalidOperationException("PREVIEW_LANE_SINGLE_BUFFER_REQUIRED");
            return PendingBuffers[0];
        }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            if (creationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("PREVIEW_LANE_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }

    // Only the native LaneSystem queue port is used. The real references
    // consumer owns a different instance and never sees this queue.
    [DisableAutoCreation]
    public sealed partial class PreviewLaneSkipSink : LaneReferencesSystem
    {
        private FieldInfo? queueField, writerField;
        private bool writerJoined, destructionStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool HasQueue => queueField != null && ReadQueue().IsCreated;

        protected override void OnCreate()
        {
            RequireMainThread();
            Enabled = false;
            if (typeof(LaneReferencesSystem).Module.ModuleVersionId.ToString("N") != "e15551e93dea48558dde1da3cea7d7ef")
                throw new InvalidOperationException("PREVIEW_LANE_SINK_BUILD_UNQUALIFIED");
            queueField = Field("m_SkipLaneQueue", typeof(NativeQueue<Lane>));
            writerField = Field("m_SkipLaneDeps", typeof(JobHandle));
            if (HasQueue || !ReadWriter().Equals(default(JobHandle)))
                throw new InvalidOperationException("PREVIEW_LANE_SINK_ALREADY_USED");
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_LANE_SKIP_SINK_CANNOT_CONSUME");

        internal void RequireWriter(JobHandle expected)
        {
            RequireMainThread();
            if (!CreationReturned || destructionStarted || writerJoined || !HasQueue
                || expected.Equals(default(JobHandle)) || !ReadWriter().Equals(expected) || !expected.IsCompleted)
                throw new InvalidOperationException("PREVIEW_LANE_SKIP_WRITER_UNCONFIRMED");
            writerJoined = true;
        }

        internal int ReadJoinedCount()
        {
            RequireMainThread();
            if (!writerJoined || destructionStarted || !HasQueue)
                throw new InvalidOperationException("PREVIEW_LANE_SKIP_READ_UNAVAILABLE");
            return ReadQueue().Count;
        }

        protected override void OnDestroy()
        {
            RequireMainThread();
            destructionStarted = true;
            if (HasQueue)
            {
                if (!writerJoined) throw new InvalidOperationException("PREVIEW_LANE_SKIP_RELEASE_UNRESOLVED");
                ReadQueue().Dispose();
                queueField!.SetValue(this, default(NativeQueue<Lane>));
                writerField!.SetValue(this, default(JobHandle));
            }
            DestructionReturned = true;
        }

        private NativeQueue<Lane> ReadQueue() => (NativeQueue<Lane>)queueField!.GetValue(this)!;
        private JobHandle ReadWriter() => (JobHandle)writerField!.GetValue(this)!;
        private static FieldInfo Field(string name, Type type)
        {
            var field = typeof(LaneReferencesSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != type || field.IsInitOnly)
                throw new InvalidOperationException("PREVIEW_LANE_SINK_FIELD_UNQUALIFIED");
            return field;
        }
        private static void RequireMainThread()
        {
            if (!MainThreadDispatcher.isMainThread)
                throw new InvalidOperationException("PREVIEW_LANE_SINK_MAIN_THREAD_REQUIRED");
        }
    }
}
#endif
