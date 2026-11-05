using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>BeginRead只受理有限读取，不等待游戏线程、磁盘或输出。</summary>
    public interface IDeferredReadCapabilityDispatcher
    {
        DeferredCapabilityRead BeginRead(string capabilityId, string schemaRevision, JToken arguments, GrantSnapshot subject);
    }

    /// <summary>
    /// 一个回复消费者；结果是本消费者独占、发布后不可再改的有界纯值DOM。
    /// BeginRead必须冻结typed输入，不保留request/arguments父DOM；共享仅限不可变事实。
    /// consumer.Dispose不得抛异常，幂等且不阻塞，仅撤销本消费者的等待；返回前使Completion终结，
    /// 并独立持有共享或已开始的原生采样至实际完成。
    /// </summary>
    public sealed class DeferredCapabilityRead : IDisposable
    {
        private IDisposable? consumer;
        private readonly Func<JToken, JToken>? currentResult;
        public DeferredCapabilityRead(Task<JToken> completion, IDisposable consumer, Func<JToken, JToken>? currentResult = null)
        {
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
            this.consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
            this.currentResult = currentResult;
        }
        public Task<JToken> Completion { get; }
        // Sender calls only after Completion has ended. This final guard reads
        // ordinary scope values; it must never wait for or access native work.
        public JToken GetCurrentResult()
        {
            var result = Completion.GetAwaiter().GetResult();
            return currentResult == null ? result : currentResult(result);
        }
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref consumer, null);
            if (current == null) return;
            try { current.Dispose(); }
            finally { if (Completion.IsFaulted) _ = Completion.Exception; }
        }
    }
}
