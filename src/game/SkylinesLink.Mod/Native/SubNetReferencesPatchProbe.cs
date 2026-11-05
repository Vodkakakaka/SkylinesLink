#if SKYLINESLINK_INTEGRATION_PROBE
namespace SkylinesLink.Mod.Native
{
    internal static class SubNetReferencesPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe() => NativeReferencesPatchObservation.ObserveSubNets();
    }
}
#endif
