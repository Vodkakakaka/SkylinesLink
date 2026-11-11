#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Tools;
using Game.Triggers;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class PreviewCommitBarrier : ToolOutputBarrier
    {
        internal const int ObjectBufferIndex = 0;
        internal const int NetBufferIndex = 1;
        internal const int PaymentBufferIndex = 2;
        internal const int RelocationNotificationBufferIndex = 1;
        internal const int RelocationPaymentBufferIndex = 2;
        internal const int RelocationNetBufferIndex = 3;

        private bool creationStarted, destructionStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_COMMIT_BARRIER_UNSCHEDULED_ONLY");

        internal EntityCommandBuffer Borrow(int index)
        {
            if (!CreationReturned || destructionStarted || index < 0 || index >= PendingCount || !PendingBuffers[index].IsCreated)
                throw new InvalidOperationException("PREVIEW_COMMIT_BUFFER_UNAVAILABLE");
            return PendingBuffers[index];
        }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            if (creationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("PREVIEW_COMMIT_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }

    // A registered, enabled receiver for the stock Apply producers. Its own
    // event consumers never run. Only the verified actions are handed over.
    [DisableAutoCreation]
    public sealed partial class PreviewCommitTriggerSink : TriggerSystem
    {
        private readonly List<NativeQueue<TriggerAction>> queues = new List<NativeQueue<TriggerAction>>(2);
        private FieldInfo? queuesField, dependenciesField;
        private bool captureAttempted, creationStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal int QueueCount => queues.Count;

        protected override void OnCreate()
        {
            creationStarted = true;
            if (!MainThreadDispatcher.isMainThread ||
                typeof(TriggerSystem).Module.ModuleVersionId.ToString("N") != "e15551e93dea48558dde1da3cea7d7ef")
                throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_ABI_UNQUALIFIED");
            queuesField = Field("m_Queues", typeof(List<NativeQueue<TriggerAction>>));
            dependenciesField = Field("m_Dependencies", typeof(JobHandle));
            queuesField.SetValue(this, queues);
            Enabled = true;
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_UNSCHEDULED_ONLY");

        internal TriggerAction[] CaptureJoined(JobHandle objects, JobHandle net)
        {
            if (captureAttempted || !CreationReturned || !Enabled || queues.Count != 2
                || !queues[0].IsCreated || !queues[1].IsCreated || !ReferenceEquals(queuesField!.GetValue(this), queues)
                || objects.Equals(default(JobHandle)) || !objects.IsCompleted || net.Equals(default(JobHandle)) || !net.IsCompleted)
                throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_INPUT_UNCONFIRMED");
            captureAttempted = true;
            JoinRegisteredWriter(requireWriter: true);
            if (queues[0].Count != 6 || queues[1].Count != 0)
                throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_COUNT_CHANGED");
            using var actions = queues[0].ToArray(Allocator.Temp);
            if (actions.Length != 6) throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_COUNT_CHANGED");
            return actions.ToArray();
        }

        internal TriggerAction CaptureRelocationObjectJoined(JobHandle objects)
        {
            if (captureAttempted || !CreationReturned || !Enabled || queues.Count != 1 || !queues[0].IsCreated
                || !ReferenceEquals(queuesField!.GetValue(this), queues) || objects.Equals(default(JobHandle))
                || !objects.IsCompleted || !((JobHandle)dependenciesField!.GetValue(this)!).Equals(objects))
                throw new InvalidOperationException("PREVIEW_COMMIT_RELOCATION_TRIGGER_INPUT_UNCONFIRMED");
            captureAttempted = true;
            JoinRegisteredWriter(requireWriter: true);
            if (queues[0].Count != 1)
                throw new InvalidOperationException("PREVIEW_COMMIT_RELOCATION_TRIGGER_COUNT_CHANGED");
            using var actions = queues[0].ToArray(Allocator.Temp);
            if (actions.Length != 1) throw new InvalidOperationException("PREVIEW_COMMIT_RELOCATION_TRIGGER_COUNT_CHANGED");
            return actions[0];
        }

        internal void RequireRelocationNetEmpty(JobHandle net)
        {
            if (!captureAttempted || !CreationReturned || !Enabled || queues.Count != 2
                || !queues[0].IsCreated || !queues[1].IsCreated || !ReferenceEquals(queuesField!.GetValue(this), queues)
                || net.Equals(default(JobHandle)) || !net.IsCompleted)
                throw new PreviewCommitObjects.Rejection("PREVIEW_COMMIT_RELOCATION_NET_TRIGGER_UNCONFIRMED");
            JoinRegisteredWriter(requireWriter: true);
            if (queues[0].Count != 1 || queues[1].Count != 0)
                throw new PreviewCommitObjects.Rejection("PREVIEW_COMMIT_RELOCATION_NET_TRIGGER_COUNT_CHANGED");
        }

        protected override void OnDestroy()
        {
            if (queues.Count != 0)
            {
                JoinRegisteredWriter(requireWriter: false);
                for (int index = queues.Count - 1; index >= 0; index--)
                {
                    var queue = queues[index];
                    queue.Dispose();
                    queues.RemoveAt(index);
                }
            }
            DestructionReturned = true;
        }

        private void JoinRegisteredWriter(bool requireWriter)
        {
            var writer = (JobHandle)dependenciesField!.GetValue(this)!;
            if (requireWriter && writer.Equals(default(JobHandle)))
                throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_WRITER_MISSING");
            writer.Complete();
        }

        private static FieldInfo Field(string name, Type type)
        {
            var field = typeof(TriggerSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return field != null && field.FieldType == type && !field.IsInitOnly
                ? field : throw new InvalidOperationException("PREVIEW_COMMIT_TRIGGER_FIELD_UNQUALIFIED");
        }
    }
}
#endif
