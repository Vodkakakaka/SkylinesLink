#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Owns the physical registration receipt for one exact managed instance.
    // Native jobs, domain storage, dependency order and permission to destroy
    // remain the caller's responsibility. There is deliberately no Dispose.
    internal sealed class NativeSystemRegistration<T> where T : ComponentSystemBase
    {
        private readonly World source;
        internal T Instance { get; }
        private bool addCalled, addAttempted, addReturned;
        private bool destroyCalled, destroyAttempted, destroyReturned;
        private bool observationAttempted, observationReturned;
        private bool sourceObserved, sourceCreated, registrationObserved, registered;
        private bool instanceWorldObserved, instanceWorldNull, instanceWorldMatchesSource;
        private string? failureCode, exceptionType, observationFailureCode, observationExceptionType;

        // No SDK read, registration or native allocation is performed here.
        internal NativeSystemRegistration(World source, T instance)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
            // World.AddSystemManaged<T> checks its static T before adding the
            // runtime instance. Keep that identity exact for these probe types.
            if (instance.GetType() != typeof(T)) throw new ArgumentException("NATIVE_SYSTEM_STATIC_TYPE_MISMATCH", nameof(instance));
        }

        internal bool AddAttempted => addAttempted;
        internal bool AddReturned => addReturned;
        internal bool DestroyAttempted => destroyAttempted;
        internal bool DestroyReturned => destroyReturned;
        internal bool PhysicalReleased => observationReturned && sourceCreated && registrationObserved
            && !registered && instanceWorldObserved && instanceWorldNull;

        internal void Add()
        {
            RequireMainThread();
            Require(!addCalled && !destroyCalled, "NATIVE_SYSTEM_ADD_ALREADY_CALLED");
            addCalled = true;
            try
            {
                ObserveIdentity();
                Require(!registered && instanceWorldNull, "NATIVE_SYSTEM_ADD_IDENTITY_NOT_FRESH");
                addAttempted = true;
                source.AddSystemManaged<T>(Instance);
                addReturned = true;
                ObserveIdentity();
                Require(registered && instanceWorldMatchesSource, "NATIVE_SYSTEM_ADD_NOT_CONFIRMED");
            }
            catch (Exception error)
            {
                RecordFailure(error, "NATIVE_SYSTEM_ADD_FAILED");
                TryObserveIdentity();
                throw;
            }
        }

        // The caller must first establish domain quiescence. A failed call is
        // never retried here, including a failure in an SDK event callback.
        internal void Destroy()
        {
            RequireMainThread();
            Require(!destroyCalled, "NATIVE_SYSTEM_DESTROY_ALREADY_CALLED");
            destroyCalled = true;
            try
            {
                ObserveIdentity();
                // This also covers an SDK rollback, or failure before Add was
                // entered, but only after observing the actual exact identity.
                if (PhysicalReleased) return;
                Require(addAttempted && registered && instanceWorldMatchesSource,
                    "NATIVE_SYSTEM_DESTROY_REGISTRATION_UNRESOLVED");
                destroyAttempted = true;
                source.DestroySystemManaged(Instance);
                destroyReturned = true;
                ObserveIdentity();
                Require(PhysicalReleased, "NATIVE_SYSTEM_DESTROY_NOT_CONFIRMED");
            }
            catch (Exception error)
            {
                RecordFailure(error, "NATIVE_SYSTEM_DESTROY_FAILED");
                TryObserveIdentity();
                throw;
            }
        }

        private void ObserveIdentity()
        {
            RequireMainThread();
            observationAttempted = true;
            observationReturned = false;
            sourceObserved = registrationObserved = instanceWorldObserved = false;
            sourceCreated = registered = instanceWorldNull = instanceWorldMatchesSource = false;
            try
            {
                sourceCreated = source.IsCreated; sourceObserved = true;
                // A destroyed Source cannot supply a trustworthy registry
                // observation. Do not infer absence from unavailable getters.
                Require(sourceCreated, "NATIVE_SYSTEM_SOURCE_UNAVAILABLE");
                registered = NativeProbeSourceProtection.IsRegistered(source, Instance);
                registrationObserved = true;
                World? actualWorld = Instance.World;
                instanceWorldNull = actualWorld == null;
                instanceWorldMatchesSource = ReferenceEquals(actualWorld, source);
                instanceWorldObserved = true;
                observationReturned = true;
            }
            catch (Exception error)
            {
                observationFailureCode ??= error is Rejection rejected ? rejected.Code : "NATIVE_SYSTEM_OBSERVATION_FAILED";
                observationExceptionType ??= ShortType(error);
                throw;
            }
        }

        private void TryObserveIdentity()
        {
            // Observation failure must not replace the exception from Add or
            // Destroy. Its unknown physical state remains separately visible.
            try { ObserveIdentity(); }
            catch (Exception) { }
        }

        private void RecordFailure(Exception error, string fallback)
        {
            failureCode ??= error is Rejection rejected ? rejected.Code : fallback;
            exceptionType ??= ShortType(error);
        }

        // Cached facts only: diagnostics remain safe after SDK state release.
        internal JObject Snapshot() => new JObject {
            ["addCalled"] = addCalled, ["addAttempted"] = addAttempted, ["addReturned"] = addReturned,
            ["destroyCalled"] = destroyCalled, ["destroyAttempted"] = destroyAttempted, ["destroyReturned"] = destroyReturned,
            ["observationAttempted"] = observationAttempted, ["observationReturned"] = observationReturned,
            ["sourceCreated"] = sourceObserved ? new JValue(sourceCreated) : JValue.CreateNull(),
            ["registered"] = registrationObserved ? new JValue(registered) : JValue.CreateNull(),
            ["instanceWorldNull"] = instanceWorldObserved ? new JValue(instanceWorldNull) : JValue.CreateNull(),
            ["instanceWorldMatchesSource"] = instanceWorldObserved ? new JValue(instanceWorldMatchesSource) : JValue.CreateNull(),
            ["physicalReleased"] = PhysicalReleased, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
            ["observationFailureCode"] = Text(observationFailureCode), ["observationExceptionType"] = Text(observationExceptionType)
        };

        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static string ShortType(Exception error)
        { string name = error.GetType().FullName ?? error.GetType().Name; return name.Length <= 128 ? name : name.Substring(0, 128); }
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NATIVE_SYSTEM_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
