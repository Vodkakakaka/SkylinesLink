using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>唯一内部帧实现。对整个帧设绝对期限；分配前验证长度，取消关闭真实流。</summary>
    public static class FrameCodec
    {
        public const int HeaderLength = 12;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static async Task<string?> ReadAsync(Stream stream, PipeLimits limits, TimeSpan deadline, CancellationToken cancellation,
            CapacityBudget? payloadBudget = null)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (timeout.Token.Register(stream.Dispose))
            {
                timeout.CancelAfter(deadline);
                var header = new byte[HeaderLength];
                if (!await ReadExactlyAsync(stream, header, true, timeout.Token).ConfigureAwait(false)) return null;
                int length = DecodeLength(header, limits.MaximumPayloadBytes);
                string json;
                try
                {
                    using (var payload = await OwnedPayload.ReadExactlyAsync(stream, length,
                        payloadBudget ?? new CapacityBudget(1, limits.MaximumPayloadBytes), timeout.Token).ConfigureAwait(false))
                    using (var reader = payload.TryAcquireReader()!) json = reader.DecodeUtf8();
                }
                catch (DecoderFallbackException) { throw new PipeProtocolException("INVALID_UTF8"); }
                catch (EndOfStreamException) { throw new PipeProtocolException("TRUNCATED_FRAME"); }
                catch (PayloadCapacityException) { throw new PipeProtocolException("PAYLOAD_BUDGET"); }
                CheckJsonBudget(json, limits);
                return json;
            }
        }

        public static int DecodeLength(byte[] header, int maximumBytes)
        {
            if (header.Length != HeaderLength || header[0] != 'S' || header[1] != 'L' || header[2] != 'N' || header[3] != 'K' ||
                header[4] != 1 || header[5] != 0 || header[6] != 0 || header[7] != 0)
                throw new PipeProtocolException("INVALID_FRAME_HEADER");
            uint length = (uint)header[8] | ((uint)header[9] << 8) | ((uint)header[10] << 16) | ((uint)header[11] << 24);
            if (length == 0 || length > maximumBytes) throw new PipeProtocolException("FRAME_LIMIT");
            return checked((int)length);
        }

        public static async Task WriteAsync(Stream stream, JToken document, PipeLimits limits, CancellationToken cancellation,
            CapacityBudget? payloadBudget = null) => await WriteFrozenAsync(stream, Freeze(document, limits), limits, cancellation, payloadBudget).ConfigureAwait(false);

        // 回复排队前只保有界、独立的不可变文本；原DOM及调用者参数不归发送队列所有。
        internal static FrozenFrame Freeze(JToken document, PipeLimits limits, int? maximumPayloadBytes = null)
        {
            string value;
            int payloadLength;
            int maximum = Math.Min(limits.MaximumPayloadBytes, maximumPayloadBytes ?? limits.MaximumPayloadBytes);
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            using (var text = new LimitedTextWriter(maximum))
            {
                using (var json = new JsonTextWriter(text) { CloseOutput = false, Formatting = Formatting.None }) document.WriteTo(json);
                value = text.ToString();
                payloadLength = StrictUtf8.GetByteCount(value);
                if (payloadLength > maximum) throw new PipeProtocolException("FRAME_LIMIT");
                CheckJsonBudget(value, limits);
            }
            return new FrozenFrame(value, payloadLength);
        }

        internal static async Task WriteFrozenAsync(Stream stream, FrozenFrame frame, PipeLimits limits, CancellationToken cancellation,
            CapacityBudget? payloadBudget = null)
        {
            if (frame.PayloadBytes > limits.MaximumPayloadBytes) throw new PipeProtocolException("FRAME_LIMIT");
            var header = new byte[HeaderLength];
            header[0] = (byte)'S'; header[1] = (byte)'L'; header[2] = (byte)'N'; header[3] = (byte)'K'; header[4] = 1;
            uint length = checked((uint)frame.PayloadBytes);
            header[8] = (byte)length; header[9] = (byte)(length >> 8); header[10] = (byte)(length >> 16); header[11] = (byte)(length >> 24);
            OwnedPayload payload;
            try { payload = OwnedPayload.FromUtf8(frame.Json, payloadBudget ?? new CapacityBudget(1, limits.MaximumPayloadBytes)); }
            catch (PayloadCapacityException) { throw new PipeProtocolException("PAYLOAD_BUDGET"); }
            using (payload)
            using (var reader = payload.TryAcquireReader()!)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (timeout.Token.Register(stream.Dispose))
            {
                timeout.CancelAfter(limits.SendTimeout);
                // 握手期间串行；认证后仅owned sender使用此入口，头和载荷不能交错。
                await stream.WriteAsync(header, 0, header.Length, timeout.Token).ConfigureAwait(false);
                await reader.WriteToAsync(stream, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
        }

        internal sealed class FrozenFrame
        {
            internal FrozenFrame(string json, int payloadBytes) { Json = json; PayloadBytes = payloadBytes; }
            internal string Json { get; }
            internal int PayloadBytes { get; }
        }

        public static void CheckJsonBudget(string json, PipeLimits limits)
        {
            using (var source = new StringReader(json))
            using (var reader = new JsonTextReader(source) { MaxDepth = limits.MaximumJsonDepth, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                int count = 0;
                try
                {
                    while (reader.Read())
                    {
                        if (++count > limits.MaximumJsonTokens) throw new PipeProtocolException("JSON_LIMIT");
                        if (reader.TokenType == JsonToken.Comment || reader.TokenType == JsonToken.Undefined ||
                            reader.TokenType == JsonToken.StartConstructor || reader.TokenType == JsonToken.Raw)
                            throw new PipeProtocolException("INVALID_JSON");
                        if (reader.TokenType == JsonToken.Float && reader.Value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                            throw new PipeProtocolException("INVALID_JSON");
                    }
                }
                catch (JsonReaderException) { throw new PipeProtocolException("INVALID_JSON"); }
            }
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, bool allowEof, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation).ConfigureAwait(false);
                if (read == 0)
                {
                    if (offset == 0 && allowEof) return false;
                    throw new PipeProtocolException("TRUNCATED_FRAME");
                }
                offset += read;
            }
            return true;
        }

        private sealed class LimitedTextWriter : StringWriter
        {
            private readonly int maximum;
            private int count;
            public LimitedTextWriter(int maximum) : base(CultureInfo.InvariantCulture) { this.maximum = maximum; }
            private void Reserve(int length)
            {
                if (length > maximum - count) throw new PipeProtocolException("FRAME_LIMIT");
                count += length;
            }
            public override void Write(char value) { Reserve(1); base.Write(value); }
            public override void Write(string? value) { if (value != null) { Reserve(value.Length); base.Write(value); } }
            public override void Write(char[] buffer, int index, int count) { Reserve(count); base.Write(buffer, index, count); }
        }
    }
}
