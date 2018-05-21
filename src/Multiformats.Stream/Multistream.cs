using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Multiformats.Stream
{
    public class Multistream : System.IO.Stream
    {
        private readonly System.IO.Stream _stream;
        private readonly MultistreamHandshaker _handshaker;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override int ReadTimeout
        {
            get { return _stream.ReadTimeout; }
            set { _stream.ReadTimeout = value; }
        }

        public override int WriteTimeout
        {
            get { return _stream.WriteTimeout; }
            set { _stream.WriteTimeout = value; }
        }


        protected Multistream(System.IO.Stream stream, params string[] protocols)
        {
            _stream = stream;
            _handshaker = new MultistreamHandshaker(this, /*Debugger.IsAttached ? TimeSpan.Zero :*/ TimeSpan.FromSeconds(3), protocols);
        }

        public static Multistream Create(System.IO.Stream stream, string protocol) => new Multistream(stream, protocol);

        public static Multistream CreateSelect(System.IO.Stream stream, string protocol)
            => new Multistream(stream, MultistreamMuxer.ProtocolId, protocol);

        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var task = ReadAsync(buffer, offset, count, CancellationToken.None);
            task.Wait(ReadTimeout);
            return task.IsFaulted ? -1 : task.Result;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _handshaker.EnsureHandshakeCompleteAsync(MultistreamHandshaker.HandshakeDirection.Incoming, cancellationToken).ConfigureAwait(false);

            if (count == 0)
                return 0;

            return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait(WriteTimeout);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _handshaker.EnsureHandshakeCompleteAsync(MultistreamHandshaker.HandshakeDirection.Outgoing, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _handshaker?.Dispose();
                _stream?.Dispose();
            }
        }
    }
}