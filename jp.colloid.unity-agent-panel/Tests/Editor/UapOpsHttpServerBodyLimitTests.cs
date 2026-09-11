using System.IO;
using System.Text;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// SEC-3: UapOpsHttpServer buffers each request body BEFORE the Bearer
    /// check runs (auth lives inside UapOpsRequestHandler.Handle), so the
    /// buffering itself must be bounded or any unauthenticated local
    /// process could OOM the Unity Editor with one giant POST. These tests
    /// drive the internal bounded reader directly over a MemoryStream --
    /// HTTP-socket integration tests are deliberately NOT part of the
    /// EditMode suite (thread/timing; see UapOpsHttpServer's doc comment).
    /// </summary>
    [TestFixture]
    public class UapOpsHttpServerBodyLimitTests
    {
        private static string Read(byte[] payload, int maxBytes, out bool overLimit)
        {
            using (var ms = new MemoryStream(payload))
            {
                return UapOpsHttpServer.ReadBodyBounded(ms, Encoding.UTF8, maxBytes, out overLimit);
            }
        }

        [Test]
        public void SmallBody_ReturnsItIntact()
        {
            bool overLimit;
            string body = Read(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}"), 1024, out overLimit);
            Assert.IsFalse(overLimit);
            Assert.AreEqual("{\"jsonrpc\":\"2.0\"}", body);
        }

        [Test]
        public void EmptyBody_ReturnsEmptyString()
        {
            bool overLimit;
            string body = Read(new byte[0], 1024, out overLimit);
            Assert.IsFalse(overLimit);
            Assert.AreEqual(string.Empty, body);
        }

        [Test]
        public void BodyExactlyAtTheLimit_IsAccepted()
        {
            bool overLimit;
            var payload = new byte[100];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)'a'; }
            string body = Read(payload, 100, out overLimit);
            Assert.IsFalse(overLimit);
            Assert.AreEqual(100, body.Length);
        }

        [Test]
        public void BodyOneByteOverTheLimit_IsRejected_WithNullBody()
        {
            bool overLimit;
            var payload = new byte[101];
            string body = Read(payload, 100, out overLimit);
            Assert.IsTrue(overLimit);
            Assert.IsNull(body);
        }

        [Test]
        public void OversizedBody_StopsReading_InsteadOfBufferingItAll()
        {
            // The reader must bail as soon as the limit is crossed -- an
            // attacker's remaining gigabytes are never pulled off the
            // socket. CountingStream records how much was actually read.
            var counting = new CountingStream(64 * 1024);
            bool overLimit;
            string body = UapOpsHttpServer.ReadBodyBounded(counting, Encoding.UTF8, 1024, out overLimit);
            Assert.IsTrue(overLimit);
            Assert.IsNull(body);
            Assert.Less(counting.TotalRead, 64 * 1024,
                "the reader consumed the whole oversized stream instead of"
                + " stopping at the limit");
        }

        [Test]
        public void CjkBody_RoundTripsThroughUtf8()
        {
            bool overLimit;
            string cjk = "{\"text\":\"日本語のペイロード\"}";
            string body = Read(Encoding.UTF8.GetBytes(cjk), 1024, out overLimit);
            Assert.IsFalse(overLimit);
            Assert.AreEqual(cjk, body);
        }

        [Test]
        public void Utf8Bom_IsStripped_LikeTheOldStreamReaderPathDid()
        {
            // The pre-SEC-3 code decoded via StreamReader(InputStream),
            // which swallows a leading BOM; the bounded reader must not
            // regress that (a BOM-prefixed "{" would otherwise break the
            // JSON parse downstream).
            bool overLimit;
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            byte[] json = Encoding.UTF8.GetBytes("{}");
            var payload = new byte[bom.Length + json.Length];
            bom.CopyTo(payload, 0);
            json.CopyTo(payload, bom.Length);
            string body = Read(payload, 1024, out overLimit);
            Assert.IsFalse(overLimit);
            Assert.AreEqual("{}", body);
        }

        [Test]
        public void NullEncoding_DefaultsToUtf8()
        {
            bool overLimit;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("día")))
            {
                string body = UapOpsHttpServer.ReadBodyBounded(ms, null, 1024, out overLimit);
                Assert.AreEqual("día", body);
            }
        }

        [Test]
        public void TheProductionLimit_AdmitsARealisticToolCall_AndBlocksAGigabyteClass()
        {
            // Pins the constant's order of magnitude: a ~200 KB tool call
            // (a large script file inside an argument) fits; anything in
            // the hundreds-of-MB class cannot even be declared.
            Assert.Greater(UapOpsHttpServer.MaxRequestBodyBytes, 200 * 1024);
            Assert.Less(UapOpsHttpServer.MaxRequestBodyBytes, 100 * 1024 * 1024);
        }

        /// <summary>
        /// Endless-ish zero stream that counts how many bytes were actually
        /// pulled, to prove the reader stops at the limit.
        /// </summary>
        private sealed class CountingStream : Stream
        {
            private readonly long _length;
            private long _position;
            public long TotalRead { get; private set; }

            public CountingStream(long length) { _length = length; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long remaining = _length - _position;
                if (remaining <= 0) { return 0; }
                int toRead = (int)System.Math.Min(count, remaining);
                _position += toRead;
                TotalRead += toRead;
                return toRead;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position
            {
                get { return _position; }
                set { throw new System.NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new System.NotSupportedException(); }
            public override void SetLength(long value) { throw new System.NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new System.NotSupportedException(); }
        }
    }
}
