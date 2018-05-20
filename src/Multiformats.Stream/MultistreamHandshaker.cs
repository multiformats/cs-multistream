﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiformats.Stream
{
    internal class MultistreamHandshaker : IDisposable
    {
        private readonly Multistream _ms;
        private readonly TimeSpan _timeout;
        private readonly IEnumerable<string> _protocols;
        private bool _hasReceived;
        private bool _hasSent;
        private readonly ReaderWriterLockSlim _lock;
        private readonly SemaphoreSlim _readLock;
        private readonly SemaphoreSlim _writeLock;

        public bool HasReceived => _lock.Read(() => _hasReceived, (int)_timeout.TotalMilliseconds);
        public bool HasSent => _lock.Read(() => _hasSent, (int)_timeout.TotalMilliseconds);
        public bool IsComplete => _lock.Read(() => _hasSent && _hasReceived, (int) _timeout.TotalMilliseconds);

        public enum HandshakeDirection
        {
            Outgoing,
            Incoming
        }

        public MultistreamHandshaker(Multistream ms, TimeSpan timeout, IEnumerable<string> protocols)
        {
            _ms = ms;
            _timeout = timeout;
            _protocols = protocols;
            _lock = new ReaderWriterLockSlim();
            _readLock = new SemaphoreSlim(1,1);
            _writeLock = new SemaphoreSlim(1,1);
        }
        
        public Task EnsureHandshakeCompleteAsync(HandshakeDirection direction, CancellationToken cancellationToken)
        {
            if (IsComplete || _writeLock.CurrentCount == 0)
                return Task.CompletedTask;

            switch (direction)
            {
                case HandshakeDirection.Outgoing:
                    return Task.WhenAll(ReadHandshakeAsync(cancellationToken), WriteHandshakeAsync(cancellationToken));
                case HandshakeDirection.Incoming:
                    return Task.WhenAll(WriteHandshakeAsync(cancellationToken), ReadHandshakeAsync(cancellationToken));
            }
            return Task.FromResult(true);
        }

        private async Task ReadHandshakeAsync(CancellationToken cancellationToken)
        {
            if (HasReceived)
                return;

            if (!await _readLock.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException("Receiving handshake timed out.");
            try
            {
                _lock.Write(() => _hasReceived = true, (int)_timeout.TotalMilliseconds);

                foreach (var protocol in _protocols)
                {
                    var token = await MultistreamMuxer.ReadNextTokenAsync(_ms, cancellationToken).ConfigureAwait(false);

                    if (token != protocol)
                        throw new Exception($"Protocol mismatch, {token} != {protocol}");
                }
            }
            finally
            {
                _readLock.Release();
            }
        }

        private async Task WriteHandshakeAsync(CancellationToken cancellationToken)
        {
            if (HasSent)
                return;

            if (!await _writeLock.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException("Sending handshake timed out.");
            try
            {
                _lock.Write(() => _hasSent = true, (int)_timeout.TotalMilliseconds);

                foreach (var protocol in _protocols)
                {
                    await MultistreamMuxer.DelimWriteAsync(_ms, Encoding.UTF8.GetBytes(protocol), cancellationToken).ConfigureAwait(false);
                }

                await _ms.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _readLock?.Dispose();
            _writeLock?.Dispose();
        }
    }
}