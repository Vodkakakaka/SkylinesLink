using System;
using System.Collections.Generic;
using Colossal.Core;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using Game.Serialization;
using SkylinesLink.Application.Context;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    /// <summary>观察 OnLoad 交入的原生 World；不加载城市、不创建系统、不推断持久城市身份。</summary>
    public sealed class NativeWorldContextObserver : IDisposable
    {
        private readonly object sync = new object();
        private readonly UpdateSystem updateSystem;
        private readonly WorldContextTracker context;
        private readonly Action<string>? report;
        private readonly Func<bool> updater;
        private GameManager? manager;
        private World? world;
        private LoadGameSystem? loader;
        private bool started, stopRequested, preloadHeld, updaterMayBeHeld;
        private string? lastIssue;

        public NativeWorldContextObserver(UpdateSystem updateSystem, WorldContextTracker context, Action<string>? report = null)
        {
            this.updateSystem = updateSystem ?? throw new ArgumentNullException(nameof(updateSystem));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.report = report;
            updater = Tick;
        }

        public void Start()
        {
            RequireMainThread();
            lock (sync)
            {
                if (stopRequested) throw new InvalidOperationException("The world observer has stopped.");
                if (context.IsStopped) throw new InvalidOperationException("The world context lifetime has stopped.");
                if (started) return;
                try
                {
                    SetUnavailable();
                    manager = GameManager.instance;
                    world = updateSystem.World;
                    if (manager == null || world == null || !world.IsCreated)
                        throw new InvalidOperationException("The native world source is unavailable.");
                    loader = world.GetExistingSystemManaged<LoadGameSystem>();
                    if (!HasCurrentSource()) throw new InvalidOperationException("The native world source does not match.");

                    // Set ownership before attempting registration: failed registration
                    // can leave a callback installed and still needs owned cleanup.
                    preloadHeld = true;
                    manager.onGamePreload += OnPreload;
                    updaterMayBeHeld = true;
                    if (MainThreadDispatcher.RegisterUpdater(updater) == Guid.Empty)
                        throw new InvalidOperationException("The world updater was not registered.");
                    started = true;
                    Tick();
                }
                catch (Exception failure)
                {
                    started = false;
                    stopRequested = true;
                    ReportIssue("WORLD_OBSERVER_START_FAILED");
                    try { ReleaseOwnedResources(); }
                    catch (Exception releaseFailure) { throw new AggregateException(failure, releaseFailure); }
                    throw;
                }
            }
        }

        private void OnPreload(Purpose purpose, GameMode mode)
        {
            // This event precedes replacement of loader.context. It only
            // invalidates the old scope; neither its arguments nor that old
            // context prove which world will become ready.
            lock (sync)
            {
                if (!started || stopRequested || context.IsStopped) return;
                try { context.EnterLoading(); }
                catch (Exception)
                {
                    TrySetUnavailable();
                    ReportIssue("WORLD_OBSERVER_INVALIDATION_FAILED");
                }
            }
        }

        private bool Tick()
        {
            // Func<bool> keeps this updater registered. The Action overload is
            // one-shot. No exception may escape into the native updater loop.
            lock (sync)
            {
                if (!started || stopRequested || context.IsStopped) return true;
                try
                {
                    RequireMainThread();
                    if (!HasCurrentSource())
                    {
                        SetUnavailable();
                        ReportIssue("WORLD_OBSERVER_SOURCE_UNAVAILABLE");
                        return false;
                    }
                    var source = manager!;
                    if (source.isGameLoading)
                    {
                        if (context.Read().State != "loading") context.EnterLoading();
                    }
                    else if (source.state == GameManager.State.WorldReady && source.gameMode == GameMode.MainMenu)
                    {
                        if (context.Read().State != "menu") context.EnterMenu();
                    }
                    else if (source.state == GameManager.State.WorldReady && source.gameMode == GameMode.Game)
                    {
                        ObserveReady();
                    }
                    else SetUnavailable();
                    lastIssue = null;
                }
                catch (Exception)
                {
                    TrySetUnavailable();
                    ReportIssue("WORLD_OBSERVER_OBSERVATION_FAILED");
                }
                return false;
            }
        }

        private bool HasCurrentSource() => manager != null && world != null && loader != null && world.IsCreated &&
            ReferenceEquals(manager, GameManager.instance) && ReferenceEquals(updateSystem.World, world) &&
            ReferenceEquals(world.GetExistingSystemManaged<UpdateSystem>(), updateSystem) &&
            ReferenceEquals(loader.World, world) && ReferenceEquals(world.GetExistingSystemManaged<LoadGameSystem>(), loader);

        private void ObserveReady()
        {
            var observed = context.Read();
            if (observed.State == "ready") return;
            // A late attachment can establish the current scope without having
            // witnessed its historical load. This is not an AI Load receipt.
            long generation = observed.State == "loading" ? observed.Generation : context.EnterLoading();
            if (!context.ObserveReady(generation)) throw new InvalidOperationException("The observed world scope changed.");
        }

        private void SetUnavailable()
        {
            if (context.Read().State != "unavailable") context.MarkUnavailable();
        }

        private void TrySetUnavailable()
        {
            try { SetUnavailable(); }
            catch (Exception) { ReportIssue("WORLD_OBSERVER_INVALIDATION_FAILED"); }
        }

        public void Dispose()
        {
            // Stopping only touches managed state and owned managed callbacks.
            // It must invalidate the scope even if a caller unloads off-thread.
            lock (sync)
            {
                stopRequested = true;
                started = false;
                Exception? invalidationFailure = null;
                try { SetUnavailable(); }
                catch (Exception failure) { invalidationFailure = failure; }
                try { ReleaseOwnedResources(); }
                catch (Exception failure)
                {
                    if (invalidationFailure != null) throw new AggregateException(invalidationFailure, failure);
                    throw;
                }
                if (invalidationFailure != null) throw invalidationFailure;
            }
        }

        private void ReleaseOwnedResources()
        {
            var failures = new List<Exception>(2);
            if (updaterMayBeHeld)
            {
                try
                {
                    // RegisterUpdater can throw after insertion but before
                    // returning its Guid. Find only this instance's delegate.
                    Guid owned = MainThreadDispatcher.GetUpdaterGuid(updater);
                    if (owned != Guid.Empty)
                    {
                        try { MainThreadDispatcher.UnregisterUpdater(owned); }
                        catch (Exception)
                        {
                            // Native logging can throw after the dictionary removal.
                            // Actual ownership, rather than the log result, decides close.
                            if (MainThreadDispatcher.GetUpdaterGuid(updater) != Guid.Empty) throw;
                        }
                    }
                    if (MainThreadDispatcher.GetUpdaterGuid(updater) != Guid.Empty)
                        throw new InvalidOperationException("The owned world updater remains registered.");
                    updaterMayBeHeld = false;
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (preloadHeld)
            {
                try
                {
                    if (ReferenceEquals(manager, null)) throw new InvalidOperationException("The preload registration source is unknown.");
                    manager!.onGamePreload -= OnPreload;
                    preloadHeld = false;
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0)
            {
                ReportIssue("WORLD_OBSERVER_RELEASE_FAILED");
                throw new AggregateException("World observer callback release was not confirmed.", failures);
            }
        }

        private void ReportIssue(string category)
        {
            if (lastIssue == category) return;
            lastIssue = category;
            try { report?.Invoke(category); }
            catch (Exception) { /* Reporting must not interrupt native loading or updates. */ }
        }

        private static void RequireMainThread()
        {
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("World observation requires the main thread.");
        }
    }
}
