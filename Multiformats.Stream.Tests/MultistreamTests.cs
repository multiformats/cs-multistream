using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaryEncoding;
using NUnit.Framework;

namespace Multiformats.Stream.Tests
{
    [TestFixture]
    public class MultistreamTests
    {
        private static void UsePipe(Action<System.IO.Stream, System.IO.Stream> action, int timeout = 1000, bool verify = false)
        {
            var ab = SocketPipe.Create(timeout);

            try
            {
                action(ab.Item1, ab.Item2);
                if (verify)
                    VerifyPipe(ab.Item1, ab.Item2);
            }
            finally
            {
                ab.Item1?.Dispose();
                ab.Item2?.Dispose();
            }
        }

        private static async Task UsePipeAsync(Func<System.IO.Stream, System.IO.Stream, Task> action, int timeout = 1000, bool verify = false)
        {
            var ab = SocketPipe.Create(timeout);

            try
            {
                await action(ab.Item1, ab.Item2);
                if (verify)
                    await VerifyPipeAsync(ab.Item1, ab.Item2);
            }
            finally
            {
                ab.Item1?.Dispose();
                ab.Item2?.Dispose();
            }
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

        [Test]
        public void TestProtocolNegotation()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = Task.Run(() => mux.Negotiate(a));

                Assert.DoesNotThrow(() => MultistreamMuxer.SelectProtoOrFail("/a", b));

                Assert.That(negotiator.Wait(500), Is.True);
                Assert.That(negotiator.Result.Protocol, Is.EqualTo("/a"));
            }, verify: true);
        }

        [Test]
        public Task Async_TestProtocolNegotation()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = mux.NegotiateAsync(a, CancellationToken.None);

                Assert.DoesNotThrowAsync(() => MultistreamMuxer.SelectProtoOrFailAsync("/a", b, CancellationToken.None));

                Assert.That(negotiator.Result.Protocol, Is.EqualTo("/a"));
            }, verify: true);
        }

        [Test]
        public void TestInvalidProtocol()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                Task.Run(() => mux.Negotiate(a));

                var ms = Multistream.Create(b, "/THIS_IS_WRONG");

                Assert.Throws(typeof(AggregateException), () => ms.Write(Array.Empty<byte>(), 0, 0));
            });
        }

        [Test]
        public void Async_TestInvalidProtocol()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.NegotiateAsync(a, CancellationToken.None);

                var ms = Multistream.Create(b, "/THIS_IS_WRONG");

                Assert.ThrowsAsync<Exception>(() => ms.WriteAsync(Array.Empty<byte>(), 0, 0));

            });
        }

        [Test]
        public void TestSelectOne()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = Task.Run(() => mux.Negotiate(a));

                Assert.DoesNotThrow(() => MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b));

                Assert.That(negotiator.Wait(500), Is.True);
                Assert.That(negotiator.Result.Protocol, Is.EqualTo("/c"));
            }, verify: true);
        }

        [Test]
        public void Async_TestSelectOne()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                var negotiator = mux.NegotiateAsync(a, CancellationToken.None);

                Assert.DoesNotThrowAsync(() => MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None));

                Assert.That(negotiator.Wait(500), Is.True);
                Assert.That(negotiator.Result.Protocol, Is.EqualTo("/c"));
            }, verify: true);
        }

        [Test]
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

        [Test]
        public void Async_TestSelectFails()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", null));

                mux.NegotiateAsync(a, CancellationToken.None);

                Assert.ThrowsAsync<NotSupportedException>(() => MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e" }, b, CancellationToken.None));

            });
        }

        [Test]
        public void TestRemoveProtocol()
        {
            var mux = new MultistreamMuxer();
            mux.AddHandler(new TestHandler("/a", null));
            mux.AddHandler(new TestHandler("/b", null));
            mux.AddHandler(new TestHandler("/c", null));

            var protos = mux.Protocols.ToList();
            protos.Sort();
            Assert.That(protos, Is.EqualTo(new[] { "/a", "/b", "/c" }));

            mux.RemoveHandler("/b");

            protos = mux.Protocols.ToList();
            protos.Sort();
            Assert.That(protos, Is.EqualTo(new[] { "/a", "/c" }));
        }

        [Test]
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

                    Assert.That(selected.Protocol, Is.EqualTo("/c"));

                    done.Set();
                });

                var sel = MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b);
                Assert.That(sel, Is.EqualTo("/c"));
                Assert.That(done.WaitOne(500), Is.True);

            }, verify: true);
        }

        [Test]
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

                    Assert.That(selected.Protocol, Is.EqualTo("/c"));

                    done.Set();
                });

                var sel = await MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None);
                Assert.That(sel, Is.EqualTo("/c"));
                Assert.That(done.WaitOne(500), Is.True);

            }, verify: true);
        }

        [Test]
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

        [Test]
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

        [Test]
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
                    Assert.That(selected.Protocol, Is.EqualTo("/c"));

                    var msg = new byte[5];
                    var bytesRead = a.Read(msg, 0, msg.Length);
                    Assert.That(bytesRead, Is.EqualTo(msg.Length));

                    done.Set();
                });

                var lb = Multistream.CreateSelect(b, "/c");
                var outmsg = Encoding.UTF8.GetBytes("hello");
                lb.Write(outmsg, 0, outmsg.Length);

                Assert.That(done.WaitOne(500), Is.True);

                VerifyPipe(a, lb);
            });
        }

        [Test]
        public void TestHandleFunc()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler(new TestHandler("/a", null));
                mux.AddHandler(new TestHandler("/b", null));
                mux.AddHandler(new TestHandler("/c", (p, s) =>
                {
                    Assert.That(p, Is.EqualTo("/c"));

                    return true;
                }));

                Task.Run(() => MultistreamMuxer.SelectProtoOrFail("/c", a));

                Assert.That(mux.Handle(b), Is.True);
            }, verify: true);
        }

        [Test]
        public void TestAddHandlerOverride()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler("/foo", (p, s) =>
                {
                    Assert.Fail("should not get executed");
                    return false;
                });
                mux.AddHandler("/foo", (p, s) => true);

                Task.Run(() => MultistreamMuxer.SelectProtoOrFail("/foo", a));

                Assert.That(mux.Handle(b), Is.True);
            }, verify: true);
        }


        //[Test]
        public Task TestAddSyncAndAsyncHandlers()
        {
            return UsePipeWithMuxerAsync(async (a, b, mux) =>
            {
                mux.AddHandler("/foo", asyncHandle: async (p, s, c) =>
                {
                    var buffer = new byte[4096];
                    new Random(Environment.TickCount).NextBytes(buffer);
                    await s.WriteAsync(buffer, 0, buffer.Length, c);
                    return true;
                });

                MultistreamMuxer.SelectProtoOrFailAsync("/foo", a, CancellationToken.None);

                var result = await mux.HandleAsync(b, CancellationToken.None);

                Assert.True(result);
            }, verify: true);
        }

        //TODO: there is a deadlock somewhere
        //[Test]
        public void TestLazyAndMuxWrite()
        {
            UsePipeWithMuxer((a, b, mux) =>
            {
                mux.AddHandler("/a", null);
                mux.AddHandler("/b", null);
                mux.AddHandler("/c", null);

                var done = new ManualResetEvent(false);

                Task.Run(() =>
                {
                    var selected = mux.Negotiate(a);
                    Assert.That(selected.Protocol, Is.EqualTo("/c"));

                    var msg = Encoding.UTF8.GetBytes("hello");
                    a.Write(msg, 0, msg.Length);

                    done.Set();
                });

                var lb = Multistream.CreateSelect(b, "/c");
                var msgin = new byte[5];
                var received = lb.Read(msgin, 0, msgin.Length);
                Assert.That(received, Is.EqualTo(msgin.Length));
                Assert.That(Encoding.UTF8.GetString(msgin), Is.EqualTo("hello"));

                Assert.That(done.WaitOne(500), Is.True);

                VerifyPipe(a, lb);
            });
        }

        [Test]
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
                Assert.That((int)n, Is.EqualTo(buf.Length - count));

                ulong nitems;
                Binary.Varint.Read(buf, out nitems);
                Assert.That((int)nitems, Is.EqualTo(protos.Length));

                for (var i = 0; i < (int) nitems; i++)
                {
                    var token = MultistreamMuxer.ReadNextToken(buf);
                    Assert.That(mset.ContainsKey(token), Is.True);
                }

                Assert.That(buf.Position, Is.EqualTo(buf.Length));
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
            Assert.That(n, Is.EqualTo(buf.Length));
            Assert.That(buf, Is.EqualTo(mes));

            n = b.Read(buf, 0, buf.Length);
            Assert.That(n, Is.EqualTo(buf.Length));
            Assert.That(buf, Is.EqualTo(mes));
        }

        private static async Task VerifyPipeAsync(System.IO.Stream a, System.IO.Stream b)
        {
            var mes = new byte[1024];
            new Random().NextBytes(mes);

            var aw = a.WriteAsync(mes, 0, mes.Length);
            var bw = b.WriteAsync(mes, 0, mes.Length);

            await Task.WhenAll(aw, bw).ConfigureAwait(false);

            var buf = new byte[mes.Length];
            var n = await a.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
            Assert.That(n, Is.EqualTo(buf.Length));
            Assert.That(buf, Is.EqualTo(mes));

            n = await b.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
            Assert.That(n, Is.EqualTo(buf.Length));
            Assert.That(buf, Is.EqualTo(mes));
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
