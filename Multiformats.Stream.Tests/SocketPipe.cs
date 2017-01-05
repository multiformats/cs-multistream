using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Multiformats.Stream.Tests
{
    public static class SocketPipe
    {
        private static readonly Random random = new Random(Environment.TickCount);

        private static Socket MakeSocket()
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = false,
                NoDelay = false,
                LingerState = new LingerOption(true, 5),
            };
        }

        public static Tuple<System.IO.Stream, System.IO.Stream> Create(int timeout = 500)
        {
            var aSocket = MakeSocket();
            var listener = MakeSocket();

            var endPoint = new IPEndPoint(IPAddress.Loopback, random.Next(2048, 8192));

            listener.Bind(endPoint);
            listener.Listen(1);
            var tcs = new TaskCompletionSource<Socket>();
            listener.BeginAccept(ar =>
            {
                var socket = ((Socket) ar.AsyncState).EndAccept(ar);
                if (socket != null)
                    tcs.TrySetResult(socket);
                else
                    tcs.TrySetException(new Exception("Socket did not connect"));
            }, listener);

            var tcsA = new TaskCompletionSource<bool>();
            aSocket.BeginConnect(endPoint, ar =>
            {
                var socket = ((Socket) ar.AsyncState);
                socket.EndConnect(ar);

                tcsA.TrySetResult(socket.Connected);
            }, aSocket);

            if (!tcs.Task.Wait(timeout))
                throw new Exception("Could not accept connection");

            if (!tcsA.Task.Wait(timeout))
                throw new Exception("Could not create connection");

            var bSocket = tcs.Task.Result;

            return new Tuple<System.IO.Stream, System.IO.Stream>(new NetworkStream(aSocket, true), new NetworkStream(bSocket, true));
        }
    }
}
