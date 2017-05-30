using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Multiformats.Stream
{
    public interface IMultistreamHandler
    {
        string Protocol { get; }
        bool Handle(string protocol, System.IO.Stream stream);
        Task<bool> HandleAsync(string protocol, System.IO.Stream stream, CancellationToken cancellationToken);
    }

    public delegate bool StreamHandlerFunc(string protocol, System.IO.Stream stream);
    public delegate Task<bool> AsyncStreamHandlerFunc(string protocol, System.IO.Stream stream, CancellationToken cancellationToken);

    internal class FuncStreamHandler : IMultistreamHandler
    {
        private readonly StreamHandlerFunc _handle;
        private readonly AsyncStreamHandlerFunc _asyncHandle;
        public string Protocol { get; }

        public FuncStreamHandler(string protocol, StreamHandlerFunc handle = null, AsyncStreamHandlerFunc asyncHandle = null)
        {
            _handle = handle;
            _asyncHandle = asyncHandle;

            Protocol = protocol;
        }

        public bool Handle(string protocol, System.IO.Stream stream)
        {
            if (_handle != null)
                return _handle.Invoke(protocol, stream);

            if (_asyncHandle != null)
                return
                    _asyncHandle.Invoke(protocol, stream, CancellationToken.None)
                        .ConfigureAwait(true)
                        .GetAwaiter()
                        .GetResult();

            return false;
        }

        public Task<bool> HandleAsync(string protocol, System.IO.Stream stream, CancellationToken cancellationToken)
        {
            if (_asyncHandle != null)
                return _asyncHandle(protocol, stream, cancellationToken);

            if (_handle != null)
                return
                    Task.Factory.FromAsync(
                        (p, s, cb, o) => ((Func<string, System.IO.Stream, bool>) o).BeginInvoke(p, s, cb, o),
                        (ar) => ((Func<string, System.IO.Stream, bool>) ar.AsyncState).EndInvoke(ar),
                        protocol, stream, state: _handle);

            return Task.FromResult(false);
        }
    }
}