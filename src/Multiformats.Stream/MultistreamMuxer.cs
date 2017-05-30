using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaryEncoding;

namespace Multiformats.Stream
{
    public class MultistreamMuxer
    {
        private static readonly Exception ErrMessageTooLarge = new Exception("Messages over 64k are not allowed");

        private static readonly Exception ErrMessageMissingNewline =
            new Exception("Message did not have trailing newline");

        private static readonly Exception ErrIncorrectVersion = new Exception("Incorrect version");
        private static readonly byte[] BytesNotAvailable = Encoding.UTF8.GetBytes("na");
        private static readonly byte[] BytesMessageTooLarge = Encoding.UTF8.GetBytes("Messages over 64k are not allowed");

        internal const string ProtocolId = "/multistream/1.0.0";
        private const byte Delimiter = (byte) '\n';
        private static readonly byte[] ProtocolIdBytes = Encoding.UTF8.GetBytes(ProtocolId);
        private readonly ReaderWriterLockSlim _handlerLock;

        private readonly List<IMultistreamHandler> _handlers;

        public MultistreamMuxer()
        {
            _handlers = new List<IMultistreamHandler>();
            _handlerLock = new ReaderWriterLockSlim();
        }

        public string[] Protocols => _handlerLock.Read(() => _handlers.Select(h => h.Protocol).ToArray());

        public MultistreamMuxer AddHandler(string protocol, StreamHandlerFunc handle = null, AsyncStreamHandlerFunc asyncHandle = null)
        {
            RemoveHandler(protocol);

            return AddHandler(new FuncStreamHandler(protocol, handle, asyncHandle));
        }

        public MultistreamMuxer AddHandler<THandler>(THandler handler)
            where THandler : IMultistreamHandler
        {
            RemoveHandler(handler);

            if (handler == null)
                handler = Activator.CreateInstance<THandler>();

            _handlerLock.Write(() => _handlers.Add(handler));

            return this;
        }

        public void RemoveHandler<THandler>(THandler handler)
            where THandler : IMultistreamHandler
        {
            _handlerLock.Write(() =>
            {
                if (handler == null)
                    handler = _handlers.OfType<THandler>().SingleOrDefault();

                if ((handler != null) && _handlers.Contains(handler))
                    _handlers.Remove(handler);
            });
        }

        public void RemoveHandler(string protocol)
        {
            var handler = FindHandler(protocol);
            if (handler != null)
            {
                _handlerLock.Write(() => _handlers.Remove(handler));
            }
        }

        private IMultistreamHandler FindHandler(string protocol) => _handlerLock.Read(() => _handlers.SingleOrDefault(h => h.Protocol.Equals(protocol)));

        public bool Handle(System.IO.Stream stream)
        {
            var result = Negotiate(stream);
            if (result.Handler == null)
                return false;

            return result.Handler.Handle(result.Protocol, stream);
        }

        public async Task<bool> HandleAsync(System.IO.Stream stream, CancellationToken cancellationToken)
        {
            var result = await NegotiateAsync(stream, cancellationToken).ConfigureAwait(false);
            if (result.Handler == null)
                return false;

            return await result.Handler.HandleAsync(result.Protocol, stream, cancellationToken).ConfigureAwait(false);
        }

        public void Ls(System.IO.Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                var handlers = _handlerLock.Read(() => _handlers.ToArray());
                Binary.Varint.Write(ms, (ulong)handlers.Length);

                foreach (var handler in handlers)
                {
                    DelimWrite(ms, Encoding.UTF8.GetBytes(handler.Protocol));
                }

                using (var ms2 = new MemoryStream())
                {
                    Binary.Varint.Write(ms2, (ulong)ms.Length);
                    ms.Seek(0, SeekOrigin.Begin);
                    ms.CopyTo(ms2);
                    ms2.Seek(0, SeekOrigin.Begin);
                    ms2.CopyTo(stream);
                }
            }
        }

        public async Task LsAsync(System.IO.Stream stream, CancellationToken cancellationToken)
        {
            using (var ms = new MemoryStream())
            {
                var handlers = _handlerLock.Read(() => _handlers.ToArray());

                Binary.Varint.Write(ms, (ulong)handlers.Length);

                foreach (var handler in handlers)
                {
                    await DelimWriteAsync(ms, Encoding.UTF8.GetBytes(handler.Protocol), cancellationToken).ConfigureAwait(false);
                }

                using (var ms2 = new MemoryStream())
                {
                    Binary.Varint.Write(ms2, (ulong)ms.Length);
                    ms.Seek(0, SeekOrigin.Begin);
                    await ms.CopyToAsync(ms2).ConfigureAwait(false);
                    ms2.Seek(0, SeekOrigin.Begin);
                    await ms2.CopyToAsync(stream).ConfigureAwait(false);
                }
            }
        }

        public NegotiationResult Negotiate(System.IO.Stream stream)
        {
            DelimWriteBuffered(stream, ProtocolIdBytes);

            var token = ReadNextToken(stream);
            if (token == null)
                return null;

            if (token != ProtocolId)
            {
                stream.Dispose();
                throw ErrIncorrectVersion;
            }

            while (true)
            {
                token = ReadNextToken(stream);
                if (token == null)
                    break;

                switch (token)
                {
                    case "ls":
                        Ls(stream);
                        break;
                    default:
                        var handler = FindHandler(token);
                        if (handler == null)
                        {
                            DelimWriteBuffered(stream, BytesNotAvailable);
                            continue;
                        }

                        DelimWriteBuffered(stream, Encoding.UTF8.GetBytes(token));

                        return new NegotiationResult(token, handler);
                }
            }
            return null;
        }

        public async Task<NegotiationResult> NegotiateAsync(System.IO.Stream stream, CancellationToken cancellationToken)
        {
            await DelimWriteBufferedAsync(stream, ProtocolIdBytes, cancellationToken).ConfigureAwait(false);

            var token = await ReadNextTokenAsync(stream, cancellationToken).ConfigureAwait(false);
            if (token == null)
                return null;

            if (token != ProtocolId)
            {
                stream.Dispose();
                throw ErrIncorrectVersion;
            }

            while (true)
            {
                token = await ReadNextTokenAsync(stream, cancellationToken).ConfigureAwait(false);
                if (token == null)
                    break;

                switch (token)
                {
                    case "ls":
                        await LsAsync(stream, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        var handler = FindHandler(token);
                        if (handler == null)
                        {
                            await DelimWriteBufferedAsync(stream, BytesNotAvailable, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await DelimWriteBufferedAsync(stream, Encoding.UTF8.GetBytes(token), cancellationToken).ConfigureAwait(false);

                        return new NegotiationResult(token, handler);
                }
            }
            return null;
        }

        internal static void DelimWrite(System.IO.Stream stream, byte[] message)
        {
            Binary.Varint.Write(stream, (ulong) (message.Length + 1));

            stream.Write(message, 0, message.Length);
            stream.WriteByte(Delimiter);
        }

        internal static async Task DelimWriteAsync(System.IO.Stream stream, byte[] message, CancellationToken cancellationToken)
        {
            Binary.Varint.Write(stream, (ulong)(message.Length + 1));

            await stream.WriteAsync(message, 0, message.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new [] { Delimiter }, 0, 1, cancellationToken).ConfigureAwait(false);
        }

        private static void DelimWriteBuffered(System.IO.Stream stream, byte[] message)
        {
            using (var buffer = new MemoryStream())
            {
                DelimWrite(buffer, message);

                var bytes = buffer.ToArray();
                stream.Write(bytes, 0, bytes.Length);
            }
            stream.Flush();
        }

        private static async Task DelimWriteBufferedAsync(System.IO.Stream stream, byte[] message, CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                await DelimWriteAsync(buffer, message, cancellationToken).ConfigureAwait(false);

                var bytes = buffer.ToArray();
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
            await stream.FlushAsync(cancellationToken);
        }

        public static string ReadNextToken(System.IO.Stream stream)
        {
            ulong length = 0;
            Binary.Varint.Read(stream, out length);
            if (length == 0)
                return string.Empty;

            if (length > 64 * 1024)
            {
                DelimWriteBuffered(stream, BytesMessageTooLarge);

                throw ErrMessageTooLarge;
            }

            var buffer = new byte[length];
            int res = 0;
            int total = 0;
            while ((res = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += res;
                if (total == (int) length)
                    break;

                Task.Delay(1).Wait();
            }
            if (res <= 0)
                return string.Empty;

            if (total != buffer.Length)
                throw new Exception("could not read token");

            if ((buffer.Length == 0) || (buffer[length - 1] != Delimiter))
                throw ErrMessageMissingNewline;

            return Encoding.UTF8.GetString(buffer, 0, buffer.Length - 1);
        }

        public static async Task<string> ReadNextTokenAsync(System.IO.Stream stream, CancellationToken cancellationToken)
        {
            var length = await Binary.Varint.ReadUInt64Async(stream).ConfigureAwait(false);
            if (length == 0)
                return string.Empty;

            if (length > 64 * 1024)
            {
                await DelimWriteBufferedAsync(stream, BytesMessageTooLarge, cancellationToken).ConfigureAwait(false);

                throw ErrMessageTooLarge;
            }

            var buffer = new byte[length];
            int res = 0;
            int total = 0;
            while ((res = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += res;
                if (total == (int)length)
                    break;

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            if (res <= 0)
                return string.Empty;

            if (total != buffer.Length)
                throw new Exception("could not read token");

            if ((buffer.Length == 0) || (buffer[length - 1] != Delimiter))
                throw ErrMessageMissingNewline;

            return Encoding.UTF8.GetString(buffer, 0, buffer.Length - 1);
        }

        private static void Handshake(System.IO.Stream stream)
        {
            var token = ReadNextToken(stream);

            if (token != ProtocolId)
                throw new Exception("Received mismatch in protocol id");

            DelimWrite(stream, ProtocolIdBytes);
        }

        private static async Task HandshakeAsync(System.IO.Stream stream, CancellationToken cancellationToken)
        {
            var token = await ReadNextTokenAsync(stream, cancellationToken).ConfigureAwait(false);

            if (token != ProtocolId)
                throw new Exception("Received mismatch in protocol id");

            await DelimWriteAsync(stream, ProtocolIdBytes, cancellationToken).ConfigureAwait(false);
        }

        public static void SelectProtoOrFail(string proto, System.IO.Stream stream)
        {
            Handshake(stream);
            TrySelect(proto, stream);
        }

        public static async Task SelectProtoOrFailAsync(string proto, System.IO.Stream stream, CancellationToken cancellationToken)
        {
            await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);
            await TrySelectAsync(proto, stream, cancellationToken).ConfigureAwait(false);
        }

        public static string SelectOneOf(string[] protocols, System.IO.Stream stream)
        {
            Handshake(stream);

            var protocol = protocols.SingleOrDefault(p => TrySelect(p, stream));
            if (protocol == null)
                throw new NotSupportedException($"Protocols given are not supported: {string.Join(", ", protocols)}.");

            return protocol;
        }

        public static async Task<string> SelectOneOfAsync(string[] protocols, System.IO.Stream stream, CancellationToken cancellationToken)
        {
            await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);

            foreach (var protocol in protocols)
            {
                if (await TrySelectAsync(protocol, stream, cancellationToken).ConfigureAwait(false))
                    return protocol;
            }
            
            throw new NotSupportedException($"Protocols given are not supported: {string.Join(", ", protocols)}.");
        }

        private static bool TrySelect(string proto, System.IO.Stream stream)
        {
            DelimWrite(stream, Encoding.UTF8.GetBytes(proto));

            var token = ReadNextToken(stream);
            if (string.IsNullOrEmpty(token))
                return false;

            if (token == proto)
                return true;

            if (token == "na")
                return false;

            throw new Exception($"Unrecognized response: {token}");
        }

        private static async Task<bool> TrySelectAsync(string proto, System.IO.Stream stream, CancellationToken cancellationToken)
        {
            await DelimWriteAsync(stream, Encoding.UTF8.GetBytes(proto), cancellationToken).ConfigureAwait(false);

            var token = await ReadNextTokenAsync(stream, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                return false;

            if (token == proto)
                return true;

            if (token == "na")
                return false;

            throw new Exception($"Unrecognized response: {token}");
        }
    }
}
 