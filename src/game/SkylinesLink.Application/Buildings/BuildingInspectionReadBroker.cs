using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Buildings
{
    public sealed class BuildingInspectionReadBroker : ScopedReadBroker<BuildingInspectionQuery, BuildingInspectionCapture>
    {
        public BuildingInspectionReadBroker(WorldContextTracker context, Func<Action, Task>? publishWorker = null)
            : base(context, publishWorker) { }

        public ReadLease Capture(BuildingInspectionQuery query) => Request(query ?? throw new ArgumentNullException(nameof(query)));

        private protected override bool SameQuery(BuildingInspectionQuery current, BuildingInspectionQuery requested) =>
            current.SameAs(requested);

        private protected override bool MatchesCapture(BuildingInspectionQuery query, BuildingInspectionCapture capture) =>
            query.SameAs(capture.Query) && (query.SelectedInfo ||
                capture.Reference.Index == query.Index && capture.Reference.Version == query.Version);

        private protected override WorldContextSnapshot ScopeOf(BuildingInspectionCapture capture) => capture.Scope;
        private protected override bool HasResultScope(JToken result) => (string?)result["kind"] != "unavailable";
        private protected override JToken Encode(BuildingInspectionCapture capture) => BuildingInspectionQueryService.Encode(capture);
        private protected override JToken Unavailable(string reason) => BuildingInspectionQueryService.Unavailable(reason);
    }
}
