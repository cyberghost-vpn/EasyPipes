/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using Castle.DynamicProxy;

namespace Dashboard.Pipes
{
    /// <summary>
    ///     <see cref="NamedPipeClientStream" /> based IPC client
    /// </summary>
    public class PipeClient
    {
        /// <summary>
        ///     Ping timer
        /// </summary>
        private Timer _timer;

        /// <summary>
        ///     Castle.Proxy generator
        /// </summary>
        private readonly ProxyGenerator _proxyGenerator;

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="pipeName">Name of the pipe</param>
        public PipeClient(string pipeName)
        {
            PipeName = pipeName;
            KnownTypes = new List<Type>();

            _proxyGenerator = new ProxyGenerator();
        }

        /// <summary>
        ///     Name of the pipe
        /// </summary>
        public string PipeName { get; }

        /// <summary>
        ///     Pipe data stream
        /// </summary>
        protected IpcStream Stream { get; set; }

        /// <summary>
        ///     List of types registered with serializer
        ///     <seealso cref="System.Runtime.Serialization.DataContractSerializer.KnownTypes" />
        /// </summary>
        public List<Type> KnownTypes { get; }

        /// <summary>
        ///     Scans the service interface and builds proxy class
        /// </summary>
        /// <typeparam name="T">Service interface, must equal server-side</typeparam>
        /// <returns>Proxy class for remote calls</returns>
        public T GetServiceProxy<T>()
        {
            IpcStream.ScanInterfaceForTypes(typeof(T), KnownTypes);

            return (T) _proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(T), new Proxy<T>(this));
        }

        /// <summary>
        ///     Connect to server. This opens a persistent connection allowing multiple remote calls
        ///     until <see cref="Disconnect(bool)" /> is called.
        /// </summary>
        /// <param name="keepAlive">Whether to send pings over the connection to keep it alive</param>
        /// <returns>True if succeeded, false if not</returns>
        public virtual bool Connect(bool keepAlive = true)
        {
            var source = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                source.Connect(500);
            }
            catch (TimeoutException)
            {
                return false;
            }

            Stream = new IpcStream(source, KnownTypes);

            if (keepAlive)
                StartPing();

            return true;
        }

        protected void StartPing()
        {
            _timer = new Timer(
                state => { SendMessage(new IpcMessage {StatusMsg = StatusMessage.Ping}); },
                null,
                PipeServer.ReadTimeOut / 2,
                PipeServer.ReadTimeOut / 2);
        }

        /// <summary>
        ///     Send the provided <see cref="IpcMessage" /> over the datastream
        ///     Opens and closes a connection if not open yet
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <returns>Return value from the Remote call</returns>
        protected object SendMessage(IpcMessage message)
        {
            // if not connected, this is a single-message connection
            var closeStream = false;
            if (Stream == null)
            {
                if (!Connect(false))
                    throw new TimeoutException("Unable to connect");
                closeStream = true;
            }
            else if (message.StatusMsg == StatusMessage.None)
            {
                // otherwise tell server to keep connection alive
                message.StatusMsg = StatusMessage.KeepAlive;
            }

            IpcMessage rv;
            lock (Stream)
            {
                Stream.WriteMessage(message);

                // don't wait for answer on keepAlive-ping
                if (message.StatusMsg == StatusMessage.Ping)
                    return null;

                rv = Stream.ReadMessage();
            }

            if (closeStream)
                Disconnect(false);

            if (rv?.Error != null)
                throw new InvalidOperationException(rv.Error);

            return rv?.Return;
        }

        /// <summary>
        ///     Disconnect from the server
        /// </summary>
        /// <param name="sendCloseMessage">
        ///     Indicate whether to send a closing notification
        ///     to the server (if you called Connect(), this should be true)
        /// </param>
        public virtual void Disconnect(bool sendCloseMessage = true)
        {
            // send close notification
            if (sendCloseMessage)
            {
                // stop keepAlive ping
                _timer.Dispose();

                var msg = new IpcMessage {StatusMsg = StatusMessage.CloseConnection};
                Stream.WriteMessage(msg);
            }

            Stream?.Dispose();

            Stream = null;
        }

        private class Proxy<T> : IInterceptor
        {
            public Proxy(PipeClient c)
            {
                PipeClient = c;
            }

            public PipeClient PipeClient { get; }

            public void Intercept(IInvocation invocation)
            {
                // build message for intercepted call
                var msg = new IpcMessage
                {
                    Service = typeof(T).Name,
                    Method = invocation.Method.Name,
                    Parameters = invocation.Arguments
                };

                // send message
                invocation.ReturnValue = PipeClient.SendMessage(msg);
            }
        }
    }
}