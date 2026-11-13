using System;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Buildings
{
    /// <summary>仅保存建筑选择来源的scope证明和自身订阅责任，不保存任何选择值。</summary>
    public sealed class BuildingSelectionScopeWitness
    {
        private readonly object sync = new object();
        private readonly WorldContextTracker context;
        private WorldContextSnapshot? bound;
        private Action? unsubscribe;
        private long ticket;
        private bool proven, stopped;
        public BuildingSelectionScopeWitness(WorldContextTracker context) { this.context = context ?? throw new ArgumentNullException(nameof(context)); }
        public bool IsReleased { get { lock (sync) return unsubscribe == null; } }
        public bool IsBoundTo(WorldContextSnapshot scope) { lock (sync) return !stopped && SameScope(bound, scope); }
        public bool HasProof(WorldContextSnapshot scope)
        {
            lock (sync) return !stopped && proven && scope.State == "ready" && SameScope(bound, scope) && Current(scope);
        }
        // The native adapter supplies only its exact owned delegate's add/remove
        // operations. Retire before touching source code, including partial adds.
        public bool TryBind(WorldContextSnapshot scope, Action<Action> add, Action<Action> remove)
        {
            lock (sync)
            {
                if (!Retire() || stopped || context.IsStopped) return false;
                bound = scope; long ownTicket = ticket;
                Action observed = () => Observe(ownTicket, scope);
                unsubscribe = () => remove(observed);
                try { add(observed); return true; }
                catch { Retire(); return false; }
            }
        }
        private void Observe(long ownTicket, WorldContextSnapshot scope)
        {
            lock (sync)
                if (!stopped && ownTicket == ticket && scope.State == "ready" && SameScope(bound, scope) && Current(scope)) proven = true;
        }
        public void RequestStop()
        {
            lock (sync) { stopped = true; ticket = checked(ticket + 1); bound = null; proven = false; }
        }
        // Native ownership calls this on the main thread. Failed detach remains
        // one owned obligation; no new binding can accumulate behind it.
        public bool TryDetach() { lock (sync) return Retire(); }
        private bool Retire()
        {
            ticket = checked(ticket + 1); bound = null; proven = false;
            if (unsubscribe == null) return true;
            try { unsubscribe(); unsubscribe = null; return true; }
            catch { return false; }
        }
        private bool Current(WorldContextSnapshot scope) => !context.IsStopped && SameScope(context.Read(), scope);
        private static bool SameScope(WorldContextSnapshot? a, WorldContextSnapshot b) => a != null &&
            a.InstanceId == b.InstanceId && a.Generation == b.Generation && a.State == b.State;
    }
}
