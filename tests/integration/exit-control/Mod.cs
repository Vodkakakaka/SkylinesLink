using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Core;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Application = UnityEngine.Application;

namespace ExitLifecycleControl
{
    // An independent menu-only differential control. No product assemblies or
    // linked product source, ECS systems, city loading, or IPC are used here.
    public sealed class Mod : IMod
    {
        private Session? session;
        public void OnLoad(UpdateSystem updateSystem)
        {
            try
            {
                Session? created = Session.FromLaunch();
                if (created == null) return;
                session = created;
                created.Start();
            }
            catch (Exception error) { UnityEngine.Debug.Log("ExitLifecycleControl activation rejected: " + error.GetType().Name); }
        }
        public void OnDispose() => session?.RequestStop();
    }

    internal sealed class Session
    {
        private readonly string runId, directory, processStart;
        private readonly int processId, holdSeconds, deadlineSeconds;
        private readonly long started = Stopwatch.GetTimestamp();
        private Guid updater;
        private IntPtr window;
        private bool stopped, ownsRendering, startingWritten, activeWritten;
        private bool quitScheduled;
        private bool quitInvoked;
        private volatile bool stopRequested;
        private int previousFrameRate, previousVSync, heldSamples;
        private bool previousBackground, hiddenObserved;
        private long readyAt;
        private string? failure;

        private Session(string id, string directory, int hold, int deadline)
        {
            runId = id; this.directory = directory; holdSeconds = hold; deadlineSeconds = deadline;
            using (var process = Process.GetCurrentProcess())
            { processId = process.Id; processStart = process.StartTime.ToUniversalTime().ToString("O"); }
        }

        internal static Session? FromLaunch()
        {
            const string prefix = "--exit-lifecycle-control=";
            string[] entries = Environment.GetCommandLineArgs().Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (entries.Length == 0) return null;
            Require(entries.Length == 1 && MainThreadDispatcher.isMainThread, "CONTROL_LAUNCH_INVALID");
            string id = entries[0].Substring(prefix.Length);
            Require(id.Length == 32 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "CONTROL_ID_INVALID");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skylineslink", "integration");
            string path = Path.Combine(directory, "exit-control-" + id + ".json");
            Ordinary(path);
            JObject config;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(stream.Length > 0 && stream.Length <= 4096, "CONTROL_CONFIG_BUDGET");
                using (var text = new StreamReader(stream, new UTF8Encoding(false, true)))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                {
                    config = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    Require(!reader.Read(), "CONTROL_CONFIG_TRAILING_CONTENT");
                }
            }
            string[] names = { "runId", "createdAtUtc", "expiresAtUtc", "expectedUserDataPath", "readyHoldSeconds", "deadlineSeconds" };
            Require(config.Count == names.Length && config.Properties().All(property => names.Contains(property.Name)), "CONTROL_CONFIG_SHAPE");
            string Text(string key)
            { Require(config[key]?.Type == JTokenType.String, "CONTROL_CONFIG_STRING"); return (string)config[key]!; }
            Require(Text("runId") == id && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(Text("expectedUserDataPath")),
                Path.GetFullPath(EnvPath.kUserDataPath)), "CONTROL_USER_DATA_MISMATCH");
            DateTime created = DateTime.Parse(Text("createdAtUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            DateTime expires = DateTime.Parse(Text("expiresAtUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            using (var process = Process.GetCurrentProcess())
                Require(created <= process.StartTime.ToUniversalTime() && created > DateTime.UtcNow.AddMinutes(-20)
                    && expires > DateTime.UtcNow && expires > created && expires - created <= TimeSpan.FromMinutes(20), "CONTROL_CONFIG_EXPIRED");
            Require(config["readyHoldSeconds"]?.Type == JTokenType.Integer && config["deadlineSeconds"]?.Type == JTokenType.Integer,
                "CONTROL_CONFIG_DURATION");
            long hold = (long)config["readyHoldSeconds"]!, deadline = (long)config["deadlineSeconds"]!;
            Require(hold >= 10 && hold <= 90 && deadline >= 180 && deadline <= 600, "CONTROL_CONFIG_DURATION");
            foreach (string suffix in new[] { "starting", "active", "result" })
                Require(!File.Exists(Path.Combine(directory, "exit-control-" + id + "." + suffix + ".json")), "CONTROL_RESULT_EXISTS");
            return new Session(id, directory, (int)hold, (int)deadline);
        }

        internal void Start()
        {
            Require(MainThreadDispatcher.isMainThread && updater == Guid.Empty, "CONTROL_START_INVALID");
            updater = MainThreadDispatcher.RegisterUpdater(Tick);
        }
        internal void RequestStop()
        {
            stopRequested = true;
            if (MainThreadDispatcher.isMainThread) StopOnMainThread();
            else MainThreadDispatcher.RunOnMainThread(StopOnMainThread);
        }
        private void StopOnMainThread()
        {
            if (stopped)
            {
                quitScheduled = false;
                if (!quitInvoked) RestoreRendering();
                return;
            }
            Finish(false, "CONTROL_EXTERNAL_STOP");
        }
        private bool Tick()
        {
            if (stopped) return true;
            try
            {
                Require(MainThreadDispatcher.isMainThread, "CONTROL_THREAD_INVALID");
                if (stopRequested) { Finish(false, "CONTROL_EXTERNAL_STOP"); return true; }
                HoldRendering(); HideWindow(); heldSamples++;
                if (!startingWritten && hiddenObserved) { Write("starting", Snapshot()); startingWritten = true; }
                GameManager? manager = GameManager.instance;
                bool ready = manager != null && manager.state == GameManager.State.WorldReady && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading;
                if (manager != null && manager.gameMode == GameMode.Game) { Finish(false, "CONTROL_PLAYER_GAME_ENTERED"); return true; }
                if (ready)
                {
                    if (readyAt == 0) readyAt = Stopwatch.GetTimestamp();
                    Require(ProductAssemblies().Length == 0, "CONTROL_PRODUCT_ASSEMBLY_LOADED");
                    if (!activeWritten && hiddenObserved) { Write("active", Snapshot()); activeWritten = true; }
                    if (Elapsed(readyAt) >= holdSeconds && activeWritten) { Finish(true, null); return true; }
                }
                else readyAt = 0;
                if (Elapsed(started) >= deadlineSeconds) { Finish(false, "CONTROL_DEADLINE"); return true; }
            }
            catch (Exception error)
            {
                failure = error is Rejection rejected ? rejected.Code : "CONTROL_FAILED";
                Finish(false, failure);
            }
            return stopped;
        }

        private void HoldRendering()
        {
            if (!ownsRendering)
            {
                previousFrameRate = Application.targetFrameRate; previousVSync = QualitySettings.vSyncCount;
                previousBackground = Application.runInBackground; ownsRendering = true;
            }
            else
            {
                if (Application.targetFrameRate != 30) previousFrameRate = Application.targetFrameRate;
                if (QualitySettings.vSyncCount != 0) previousVSync = QualitySettings.vSyncCount;
                if (!Application.runInBackground) previousBackground = false;
            }
            if (!Application.runInBackground) Application.runInBackground = true;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != 30) Application.targetFrameRate = 30;
        }
        private void RestoreRendering()
        {
            if (!ownsRendering) return;
            Exception? first = null;
            try { if (Application.targetFrameRate == 30) Application.targetFrameRate = previousFrameRate; }
            catch (Exception error) { first = error; }
            try { if (QualitySettings.vSyncCount == 0) QualitySettings.vSyncCount = previousVSync; }
            catch (Exception error) { first ??= error; }
            try { if (Application.runInBackground) Application.runInBackground = previousBackground; }
            catch (Exception error) { first ??= error; }
            if (first != null) throw first;
            ownsRendering = false;
        }
        private void HideWindow()
        {
            if (window == IntPtr.Zero || !IsWindow(window))
            {
                IntPtr found = IntPtr.Zero; int count = 0;
                Require(EnumWindows((handle, _) => {
                    GetWindowThreadProcessId(handle, out uint owner);
                    if (owner != processId) return true;
                    var name = new StringBuilder(128); GetClassName(handle, name, name.Capacity);
                    if (name.ToString() != "UnityWndClass") return true;
                    found = handle; return ++count <= 1;
                }, IntPtr.Zero) && count <= 1, "CONTROL_WINDOW_AMBIGUOUS");
                window = found;
            }
            if (window == IntPtr.Zero) return;
            GetWindowThreadProcessId(window, out uint pid); Require(pid == processId, "CONTROL_WINDOW_IDENTITY_CHANGED");
            if (IsWindowVisible(window)) ShowWindow(window, 0);
            hiddenObserved = !IsWindowVisible(window);
        }
        private JObject Snapshot() => new JObject {
            ["runId"] = runId, ["processId"] = processId, ["processStartTimeUtc"] = processStart,
            ["sampledAtUtc"] = DateTime.UtcNow.ToString("O"), ["targetFrameRate"] = Application.targetFrameRate,
            ["vSyncCount"] = QualitySettings.vSyncCount, ["runInBackground"] = Application.runInBackground,
            ["hidden"] = hiddenObserved, ["heldSamples"] = heldSamples, ["readyHoldSeconds"] = holdSeconds,
            ["continuousReadySeconds"] = readyAt == 0 ? 0 : Elapsed(readyAt),
            ["menuReadyObserved"] = readyAt != 0, ["productAssemblies"] = new JArray(ProductAssemblies()),
            ["controlAssemblyMvid"] = typeof(Mod).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            ["gameAssemblyMvid"] = typeof(GameManager).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            ["failureCode"] = failure == null ? JValue.CreateNull() : new JValue(failure)
        };
        private void Finish(bool quit, string? reason)
        {
            if (stopped) return;
            stopped = true; failure ??= reason;
            JObject result;
            try { result = Snapshot(); }
            catch (Exception) { failure ??= "CONTROL_SNAPSHOT_FAILED"; result = new JObject { ["runId"] = runId, ["processId"] = processId }; }
            try
            {
                if (updater != Guid.Empty) { MainThreadDispatcher.UnregisterUpdater(updater); updater = Guid.Empty; }
            }
            catch (Exception) { failure ??= "CONTROL_UPDATER_RELEASE_FAILED"; }
            string[] products;
            try { products = ProductAssemblies(); }
            catch (Exception) { failure ??= "CONTROL_ASSEMBLY_OBSERVATION_FAILED"; products = new[] { "unobserved" }; }
            bool canQuit = quit && failure == null && readyAt != 0 && products.Length == 0;
            var manager = GameManager.instance;
            canQuit &= manager != null && manager.state == GameManager.State.WorldReady && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading;
            // This launch owns only process-local engine values. A terminal
            // quit leaves them in this process; its exit/crash discards them.
            // An aborted test must hand its still-running process back.
            if (!canQuit)
                try { RestoreRendering(); }
                catch (Exception) { failure ??= "CONTROL_RENDERING_RELEASE_FAILED"; }
            result["frameRateBeforeQuit"] = Application.targetFrameRate; result["vSyncBeforeQuit"] = QualitySettings.vSyncCount;
            result["backgroundBeforeQuit"] = Application.runInBackground; result["updaterRemoved"] = updater == Guid.Empty;
            result["ownedRuntimeScalarsReleased"] = !ownsRendering; result["productAssemblies"] = new JArray(products);
            result["runtimePolicyEndsWithProcess"] = canQuit;
            result["terminalRestoreAttempted"] = false;
            result["persistentSettingsWriteAttempted"] = false;
            result["failureCode"] = failure == null ? JValue.CreateNull() : new JValue(failure);
            result["normalQuitPrepared"] = canQuit; result["passedBeforeProcessExit"] = canQuit;
            result["quitDelayFrames"] = 1;
            try { Write("result", result); }
            catch (Exception)
            {
                try { RestoreRendering(); } catch (Exception) { }
                UnityEngine.Debug.Log("ExitLifecycleControl result write failed; runtime handback attempted."); return;
            }
            // This receipt records preconditions, not a successful native quit.
            // Actual call return and process exit have independent observations.
            if (canQuit)
            {
                try
                {
                    quitScheduled = true;
                    MainThreadDispatcher.WaitXFrames(1).ContinueWith(_ => {
                        if (MainThreadDispatcher.isMainThread) InvokeQuit();
                        else MainThreadDispatcher.RunOnMainThread(InvokeQuit);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception)
                {
                    quitScheduled = false;
                    try { RestoreRendering(); } catch (Exception) { }
                    UnityEngine.Debug.Log("ExitLifecycleControl quit scheduling failed; runtime handback attempted.");
                }
            }
        }
        private void InvokeQuit()
        {
            try
            {
                if (!quitScheduled || stopRequested)
                {
                    quitScheduled = false;
                    if (!quitInvoked) RestoreRendering();
                    return;
                }
                quitScheduled = false;
                var manager = GameManager.instance;
                Require(MainThreadDispatcher.isMainThread && ProductAssemblies().Length == 0 && manager != null
                    && manager.state == GameManager.State.WorldReady && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading,
                    "CONTROL_QUIT_SCOPE_CHANGED");
                quitInvoked = true;
                GameManager.QuitGame(); UnityEngine.Debug.Log("ExitLifecycleControl normal quit call returned.");
            }
            catch (Exception error)
            {
                quitInvoked = false;
                try { RestoreRendering(); } catch (Exception) { }
                UnityEngine.Debug.Log("ExitLifecycleControl normal quit call failed: " + error.GetType().Name);
            }
        }
        private void Write(string suffix, JObject value)
        {
            string path = Path.Combine(directory, "exit-control-" + runId + "." + suffix + ".json"); Ordinary(directory);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value.ToString(Formatting.None));
            Require(bytes.Length <= 8192, "CONTROL_RECEIPT_BUDGET");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        private static string[] ProductAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? "").Where(name => name.StartsWith("SkylinesLink", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        private static double Elapsed(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;
        private static void Ordinary(string path)
        {
            for (string? part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                Require((File.GetAttributes(part) & FileAttributes.ReparsePoint) == 0, "CONTROL_REPARSE_PATH");
        }
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception
        { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }
        private delegate bool WindowVisitor(IntPtr window, IntPtr arg);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor callback, IntPtr arg);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int maximum);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
