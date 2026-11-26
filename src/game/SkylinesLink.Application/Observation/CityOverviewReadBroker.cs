using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Observation
{
    public sealed class CityOverviewReadBroker : ScopedReadBroker<ValueTuple, CityOverviewCapture>
    {
        public CityOverviewReadBroker(WorldContextTracker context, Func<Action, Task>? publishWorker = null)
            : base(context, publishWorker) { }

        public ReadLease Capture() => Request(default);

        private protected override WorldContextSnapshot ScopeOf(CityOverviewCapture capture) => capture.Scope;
        private protected override bool HasResultScope(JToken result) => (string?)result["kind"] == "overview";
        private protected override JToken Encode(CityOverviewCapture capture) => CityOverviewQueryService.Encode(capture);
        private protected override JToken Unavailable(string reason) => CityOverviewQueryService.Unavailable(reason);
    }
}
