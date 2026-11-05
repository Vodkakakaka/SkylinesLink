using System;

namespace SkylinesLink.Mod.Connection
{
    public sealed class PipeProtocolException : Exception
    {
        public PipeProtocolException(string code) : base(code) { Code = code; }
        public string Code { get; }
    }
}
