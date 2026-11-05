using System;

namespace SkylinesLink.Application.Context
{
    public interface IEnvironmentLeaseProvider
    {
        IDisposable? TryAcquire(string environmentId);
    }
}
