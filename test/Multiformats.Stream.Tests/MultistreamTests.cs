using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaryEncoding;
using Xunit;
using Xunit.Sdk;

namespace Multiformats.Stream.Tests
{
    public class MultistreamTests
    {
        private static async Task RunWithConnectedNetworkStreamsAsync(Func<NetworkStream, NetworkStream, Task> func)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start(1);
                var clientEndPoint = (IPEndPoint)listener.LocalEndpoint;

                using (var client = new TcpClient(clientEndPoint.AddressFamily))
                {
                    var remoteTask = listener.AcceptTcpClientAsync();
                    var clientConnectTask = client.ConnectAsync(clientEndPoint.Address, clientEndPoint.Port);

                    await Task.WhenAll(remoteTask, clientConnectTask);

                    using (var remote = remoteTask.Result)
                    using (var serverStream = new NetworkStream(remote.Client, true))
                    using (var clientStream = new NetworkStream(client.Client, true))
                    {
                        await func(serverStream, clientStream);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void UsePipe(Action<System.IO.Stream, System.IO.Stream> action, int timeout = 1000, bool verify = false)
        {
            RunWithConnectedNetworkStreamsAsync((a, b) =>
            {
                try
                {
                    action(a, b);
                    if (verify)
                        VerifyPipe(a, b);

                    return Task.CompletedTask;
                }
                catch (Exception e)
                {
                    return Task.FromException(e);
                }
            }).Wait();
        }

        private static async Task UsePipeAsync(Func<System.IO.Stream, System.IO.Stream, Task> action, int timeout = 1000, bool verify = false)
        {
            await RunWithConnectedNetworkStreamsAsync(async (a, b) =>
            {
                await action(a, b);
                if (verify)
                    await VerifyPipeAsync(a, b);
            });
        }

        private static void UsePipeWithMuxer(Action<System.IO.Stream, System.IO.Stream, MultistreamMuxer> action,
            int timeout = 1000, bool verify = false)
        {
            UsePipe((a, b) => action(a, b, new MultistreamMuxer()), timeout, verify);
        }

        private static Task UsePipeWithMuxerAsync(Func<System.IO.Stream, System.IO.Stream, MultistreamMuxer, Task> action,
            int timeout = 1000, bool verify = false)
        {
            return UsePipeAsync((a, b) => action(a, b, new MultistreamMuxer()), timeout, verify);
        }

        [Fact]
        public void TestProtocolNegotation()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = Task.Run(() => mux.Negotiate(a));

                MultistreamMuxer.SelectProtoOrFail("/a", b);

                Assert.True(negotiator.Wait(500));
                Assert.Equal(negotiator.Result.Protocol, "/a");
            }, verify: true);
        }

        [Fact]
        public Task Async_TestProtocolNegotation()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = mux.NegotiateAsync(a, CancellationToken.None);

                await MultistreamMuxer.SelectProtoOrFailAsync("/a", b, CancellationToken.None);

                Assert.Equal(negotiator.Result.Protocol, "/a");
            }, verify: true);
        }

        [Fact]
        public void TestInvalidProtocol()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                Task.Run(() => mux.Negotiate(a));

                var ms = Multistream.Create(b, "/THIS_IS_WRONG");

                Assert.Throws<AggregateException>(() => ms.Write(Array.Empty<byte>(), 0, 0));
            });
        }

        [Fact]
        public Task Async_TestInvalidProtocol()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.NegotiateAsync(a, CancellationToken.None);

                var ms = Multistream.Create(b, "/THIS_IS_WRONG");

                await Assert.ThrowsAsync<Exception>(() => ms.WriteAsync(Array.Empty<byte>(), 0, 0));
            });
        }

        [Fact]
        public void TestSelectOne()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = Task.Run(() => mux.Negotiate(a));

                MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b);

                Assert.True(negotiator.Wait(500));
                Assert.Equal(negotiator.Result.Protocol, "/c");
            }, verify: true);
        }

        [Fact]
        public void Async_TestSelectOne()
        {
            UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = mux.NegotiateAsync(a, CancellationToken.None);

                await MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None);

                Assert.True(negotiator.Wait(500));
                Assert.Equal(negotiator.Result.Protocol, "/c");
            }, verify: true);
        }

        [Fact]
        public void TestSelectFails()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                Task.Run(() => mux.Negotiate(a));

                Assert.Throws<NotSupportedException>(() => MultistreamMuxer.SelectOneOf(new[] { "/d", "/e" }, b));
            });
        }

        [Fact]
        public Task Async_TestSelectFails()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                mux.NegotiateAsync(a, CancellationToken.None);

                await Assert.ThrowsAsync<NotSupportedException>(() => MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e" }, b, CancellationToken.None));
            });
        }

        [Fact]
        public void TestRemoveProtocol()
        {
            var mux = new MultistreamMuxer();
            mux.AddHandler(new TestHandler("/a", null));
            mux.AddHandler(new TestHandler("/b", null));
            mux.AddHandler(new TestHandler("/c", null));

            var protos = mux.Protocols.ToList();
            protos.Sort();
            Assert.Equal(protos, new[] { "/a", "/b", "/c" });

            mux.RemoveHandler("/b");

            protos = mux.Protocols.ToList();
            protos.Sort();
            Assert.Equal(protos, new[] { "/a", "/c" });
        }

        [Fact]
        public void TestSelectOneAndWrite()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var done = new ManualResetEvent(false);
                Task.Run(() =>
                {
                    var selected = mux.Negotiate(a);

                    Assert.Equal(selected.Protocol, "/c");

                    done.Set();
                });

                var sel = MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b);
                Assert.Equal(sel, "/c");
                Assert.True(done.WaitOne(500));

            }, verify: true);
        }

        [Fact]
        public Task Async_TestSelectOneAndWrite()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var done = new ManualResetEvent(false);
                Task.Run(async () =>
                {
                    var selected = await mux.NegotiateAsync(a, CancellationToken.None);

                    Assert.Equal(selected.Protocol, "/c");

                    done.Set();
                });

                var sel = await MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None);
                Assert.Equal(sel, "/c");
                Assert.True(done.WaitOne(500));

            }, verify: true);
        }

        [Fact]
        public void TestLazyConns()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var la = Task.Run(() => Multistream.CreateSelect(a, "/c")).Result;
                var lb = Multistream.CreateSelect(b, "/c");

                VerifyPipeAsync(la, lb).Wait();
            });
        }

        [Fact]
        public Task Async_TestLazyConns()
        {
            return UsePipeWithMuxerAsync((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var la = Task.Run(() =>Multistream.CreateSelect(a, "/c")).Result;
                var lb = Multistream.CreateSelect(b, "/c");

                return VerifyPipeAsync(la, lb);
            });
        }

        [Fact]
        public void TestLazyAndMux()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var done = new ManualResetEvent(false);

                Task.Run(() =>
                {
                    var selected = mux.Negotiate(a);
                    Assert.Equal(selected.Protocol, "/c");

                    var msg = new byte[5];
                    var bytesRead = a.Read(msg, 0, msg.Length);
                    Assert.Equal(bytesRead, msg.Length);

                    done.Set();
                });

                var lb = Multistream.CreateSelect(b, "/c");
                var outmsg = Encoding.UTF8.GetBytes("hello");
                lb.Write(outmsg, 0, outmsg.Length);

                Assert.True(done.WaitOne(500));

                VerifyPipe(a, lb);
            });
        }

        [Fact]
        public void TestHandleFunc()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", (p, s) =>
                {
                    Assert.Equal(p, "/c");

                    return true;
                }));

                Task.Run(() => MultistreamMuxer.SelectProtoOrFail("/c", a));

                Assert.True(mux.Handle(b));
            }, verify: true);
        }

        [Fact]
        public void TestAddHandlerOverride()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler("/foo", (p, s) => throw new XunitException("should not get executed"));
                mux.AddHandler("/foo", (p, s) => true);

                Task.Run(() => MultistreamMuxer.SelectProtoOrFail("/foo", a));

                Assert.True(mux.Handle(b));
            }, verify: true);
        }


        [Fact]
        public Task TestAddSyncAndAsyncHandlers()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler("/foo", asyncHandle: (p, s, c) => Task.FromResult(true));

                var selectTask = MultistreamMuxer.SelectProtoOrFailAsync("/foo", a, CancellationToken.None);

                var result = await mux.HandleAsync(b, CancellationToken.None);

                await selectTask;

                Assert.True(result);
            }, verify: true);
        }

        //TODO: there is a deadlock somewhere
        [Fact]
        public void TestLazyAndMuxWrite()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler("/a", null);
                mux.AddHandler("/b", null);
                mux.AddHandler("/c", null);

                var doneTask = Task.Factory.StartNew(() =>
                {
                    var selected = mux.Negotiate(a);
                    Assert.Equal("/c", selected.Protocol);

                    var msg = Encoding.UTF8.GetBytes("hello");
                    a.Write(msg, 0, msg.Length);
                });

                var lb = Multistream.CreateSelect(b, "/c");
                var msgin = new byte[5];
                var received = lb.Read(msgin, 0, msgin.Length);
                Assert.Equal(received, msgin.Length);
                Assert.Equal("hello", Encoding.UTF8.GetString(msgin));

                Assert.True(doneTask.Wait(500));

                VerifyPipe(a, lb);
            });
        }

        [Fact]
        public void TestLs()
        {
            SubTestLs();
            SubTestLs("a");
            SubTestLs("a", "b", "c", "d", "e");
            SubTestLs("", "a");
        }

        private static void SubTestLs(params string[] protos)
        {
            var mr = new MultistreamMuxer();
            var mset = new Dictionary<string, bool>();
            foreach (var proto in protos)
            {
                mr.AddHandler(proto, null);
                mset.Add(proto, true);
            }

            using (var buf = new MemoryStream())
            {
                mr.Ls(buf);
                buf.Seek(0, SeekOrigin.Begin);

                ulong n;
                var count = Binary.Varint.Read(buf, out n);
                Assert.Equal((int)n, buf.Length - count);

                ulong nitems;
                Binary.Varint.Read(buf, out nitems);
                Assert.Equal((int)nitems, protos.Length);

                for (var i = 0; i < (int) nitems; i++)
                {
                    var token = MultistreamMuxer.ReadNextToken(buf);
                    Assert.True(mset.ContainsKey(token));
                }

                Assert.Equal(buf.Position, buf.Length);
            }
        }

        private static void VerifyPipe(System.IO.Stream a, System.IO.Stream b)
        {
            var mes = new byte[1024];
            new Random().NextBytes(mes);
            Task.Run(() =>
            {
                a.Write(mes, 0, mes.Length);
                b.Write(mes, 0, mes.Length);
            }).Wait();

            var buf = new byte[mes.Length];
            var n = a.Read(buf, 0, buf.Length);
            Assert.Equal(n, buf.Length);
            Assert.Equal(buf, mes);

            n = b.Read(buf, 0, buf.Length);
            Assert.Equal(n, buf.Length);
            Assert.Equal(buf, mes);
        }

        private static async Task VerifyPipeAsync(System.IO.Stream a, System.IO.Stream b)
        {
            var mes = new byte[1024];
            new Random().NextBytes(mes);
            mes[0] = 0x01;

            var aw = a.WriteAsync(mes, 0, mes.Length);
            var bw = b.WriteAsync(mes, 0, mes.Length);

            await Task.WhenAll(aw, bw).ConfigureAwait(false);

            var buf = new byte[mes.Length];
            var n = await a.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
            Assert.Equal(n, buf.Length);
            Assert.Equal(buf, mes);

            n = await b.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
            Assert.Equal(n, buf.Length);
            Assert.Equal(buf, mes);
        }

        private class TestHandler : IMultistreamHandler
        {
            private readonly Func<string, System.IO.Stream, bool> _action;

            public string Protocol { get; }

            public TestHandler(string protocol, Func<string, System.IO.Stream, bool> action)
            {
                _action = action;
                Protocol = protocol;
            }

            public bool Handle(string protocol, System.IO.Stream stream) => _action?.Invoke(protocol, stream) ?? false;
            public Task<bool> HandleAsync(string protocol, System.IO.Stream stream, CancellationToken cancellationToken)
            {
                return Task.Factory.FromAsync(
                    (p, s, cb, o) => ((Func<string, System.IO.Stream, bool>)o).BeginInvoke(p, s, cb, o),
                    (ar) => ((Func<string, System.IO.Stream, bool>)ar.AsyncState).EndInvoke(ar),
                    protocol, stream, state: _action);
            }
        }
    }
}
