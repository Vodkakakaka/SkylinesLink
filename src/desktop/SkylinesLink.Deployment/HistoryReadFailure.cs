using System;

namespace SkylinesLink.Deployment;

public enum HistoryReadCode
{
    InvalidRequest = 80,
    StoreMissing = 81,
    AuthorityBusy = 82,
    Unauthorized = 83,
    SubjectChanged = 84,
    HistoryMissing = 85,
    Incompatible = 86,
    CorruptStore = 87,
    IoFailed = 88
}

public sealed class HistoryReadException : Exception
{
    public HistoryReadException(HistoryReadCode code) : base(code.ToString()) => Code = code;
    public HistoryReadCode Code { get; }
}
