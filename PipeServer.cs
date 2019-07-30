/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Dashboard.Pipes
{
    /// <summary>
    ///     <see cref="NamedPipeClientStream" /> based IPC server
    /// </summary>
    public class PipeServer
    {
        private const int NumberOfThreads = 2;
        public const int ReadTimeOut = 30000;
        private readonly Dictionary<string, object> services = new Dictionary<string, object>();
        private readonly Dictionary<string, Type> types = new Dictionary<string, Type>();
        private List<Task> serverTask;

        /// <summary>
        ///     Construct server
        /// </summary>
        /// <param name="pipe">Name of the pipe to use</param>
        public PipeServer(string pipe)
        {
            PipeName = pipe;
            CancellationToken = new CancellationTokenSource();
            KnownTypes = new List<Type>();

            AllowedProcessesToCommunicate = new List<string>();
        }

        /// <summary>
        ///     List of process pathes to allow communication with this server.
        ///     If empty, all processes are allowed to communicate
        /// </summary>
        public List<string> AllowedProcessesToCommunicate { get; }

        /// <summary>
        ///     Name of the pipe
        /// </summary>
        public string PipeName { get; }

        /// <summary>
        ///     Token to cancel server operations
        /// </summary>
        public CancellationTokenSource CancellationToken { get; }

        /// <summary>
        ///     List of types registered with serializer
        ///     <seealso cref="System.Runtime.Serialization.DataContractSerializer.KnownTypes" />
        /// </summary>
        public List<Type> KnownTypes { get; }


        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

        /// <summary>
        ///     Returns the Process ID from the connecting pipe client
        /// </summary>
        /// <param name="pipeServer">ServerStream to get connecting Process ID</param>
        /// <returns></returns>
        private static uint GetNamedPipeClientProcId(NamedPipeServerStream pipeServer)
        {
            var hPipe = pipeServer.SafePipeHandle.DangerousGetHandle();

            return GetNamedPipeClientProcessId(hPipe, out var nProcId) ? nProcId : 0;
        }

        /// <summary>
        ///     Returns the full process path + executable from the connecting pipe client
        /// </summary>
        /// <param name="pipeServer">ServerStream to get connecting Process ID</param>
        /// <returns></returns>
        private static string GetNamedPipeClientProcessPath(NamedPipeServerStream pipeServer)
        {
            var pid = GetNamedPipeClientProcId(pipeServer);

            if (pid == 0)
                return string.Empty;

            try
            {
                var callingProcess = Process.GetProcessById((int) pid);

                return callingProcess.MainModule?.FileName;
            }
            catch (Exception)
            {
                // Ignore
            }

            return string.Empty;
        }

        /// <summary>
        ///     Register a service interface on the server
        /// </summary>
        /// <typeparam name="T">The interface of the service</typeparam>
        /// <param name="instance">Instance of a class implementing the service</param>
        public void RegisterService<T>(T instance)
        {
            if (!(instance is T))
                throw new InvalidOperationException("Instance must implement service interface");

            services[typeof(T).Name] = instance;
            types[typeof(T).Name] = typeof(T);

            IpcStream.ScanInterfaceForTypes(typeof(T), KnownTypes);
        }

        /// <summary>
        ///     Register a service interface on the server
        /// </summary>
        /// <param name="t">The interface of the service</param>
        /// <param name="instance">Instance of a class implementing the service</param>
        public void RegisterService(Type t, object instance)
        {
            services[t.Name] = instance;
            types[t.Name] = t;

            IpcStream.ScanInterfaceForTypes(t, KnownTypes);
        }

        /// <summary>
        ///     Deregister a service interface from the server
        /// </summary>
        /// <typeparam name="T">The interface of the service</typeparam>
        public void DeregisterService<T>()
        {
            services.Remove(typeof(T).Name);
            types.Remove(typeof(T).Name);
        }

        /// <summary>
        ///     Deregister a service interface from the server
        /// </summary>
        /// <param name="t">The interface of the service</param>
        public void DeregisterService(Type t)
        {
            services.Remove(t.Name);
            types.Remove(t.Name);
        }

        /// <summary>
        ///     Start running the server tasks
        ///     Note, this will spawn asynchronous servers and return immediatly
        /// </summary>
        public void Start()
        {
            if (serverTask != null)
                throw new InvalidOperationException("Already running");

            // asynchonously start the receiving threads, store task for later await
            serverTask = new List<Task>();
            DoStart();
        }

        /// <summary>
        ///     Spawn server tasks
        /// </summary>
        protected virtual void DoStart()
        {
            // Start +1 receive actions
            for (short i = 0; i < NumberOfThreads; ++i)
            {
                var t = Task.Factory.StartNew(ReceiveAction);
                serverTask.Add(t);
            }
        }

        /// <summary>
        ///     Stop the server
        /// </summary>
        public virtual void Stop()
        {
            // send cancel token and wait for threads to end
            CancellationToken.Cancel();
            Task.WaitAll(serverTask.ToArray(), 500);
            serverTask = null;
        }

        /// <summary>
        ///     Wait on connection and received messages
        /// </summary>
        protected virtual void ReceiveAction()
        {
            try
            {
                var ps = new PipeSecurity();

                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));

                if (WindowsIdentity.GetCurrent().Owner != null)
                    ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().Owner,
                        PipeAccessRights.FullControl, AccessControlType.Allow));

                using (var serverStream =
                    new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NumberOfThreads,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0, 0,
                        ps))
                {
                    var t = serverStream.WaitForConnectionAsync(CancellationToken.Token);
                    t.GetAwaiter().GetResult();

                    while (ProcessMessage(serverStream))
                    {
                    }
                }

                serverTask.Add(Task.Factory.StartNew(ReceiveAction));
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        ///     Enumerates the given type (and all it's subinterfaces) for the given method
        /// </summary>
        /// <param name="t">Type to enumerate</param>
        /// <param name="method">Method name to search for</param>
        /// <returns>Method info, if found</returns>
        private MethodInfo GetServiceMethod(Type t, string method)
        {
            var baseType = t.GetMethod(method);

            if (baseType != null)
                return baseType;

            var interfaces = t.GetInterfaces();

            foreach (var i in interfaces)
            {
                var inType = GetServiceMethod(i, method);

                if (inType != null)
                    return inType;
            }

            return null;
        }

        /// <summary>
        ///     Process a received message by calling the corresponding method on the service instance and
        ///     returning the return value over the network.
        /// </summary>
        /// <param name="source">Network stream</param>
        private bool ProcessMessage(NamedPipeServerStream source)
        {
            // Checks, if the stream is from an allowed client ...
            if (!CheckPipeServerStreamForAllowedProcess(source))
                return false;

            var stream = new IpcStream(source, KnownTypes);

            var msg = stream.ReadMessage();

            // this was a close-connection notification
            if (msg == null || msg.StatusMsg == StatusMessage.CloseConnection)
                return false;
            if (msg.StatusMsg == StatusMessage.Ping)
                return true;

            var processedOk = false;
            var error = "";
            object rv = null;

            // find the service
            if (services.TryGetValue(msg.Service, out var instance) && instance != null)
            {
                // get the method
                var method = instance.GetType().GetMethod(msg.Method);

                // double check method existence against type-list for security
                // typelist will contain interfaces instead of instances
                if (GetServiceMethod(types[msg.Service], msg.Method) != null && method != null)
                    try
                    {
                        // invoke method
                        rv = method.Invoke(instance, msg.Parameters);
                        processedOk = true;
                    }
                    catch (Exception e)
                    {
                        error = e.ToString();
                    }
                else
                    error = "Could not find method";
            }
            else
            {
                error = "Could not find service";
            }

            // return either return value or error message
            var returnMsg = processedOk ? new IpcMessage {Return = rv} : new IpcMessage {Error = error};

            stream.WriteMessage(returnMsg);

            // if there's more to come, keep reading a next message
            return msg.StatusMsg == StatusMessage.KeepAlive;
        }

        /// <summary>
        ///     Checks the current source pipe against the list of allowed processes
        /// </summary>
        /// <param name="source">PipeClient stream</param>
        /// <returns>TRUE if list is empty or process is in the list of allowed processes</returns>
        private bool CheckPipeServerStreamForAllowedProcess(NamedPipeServerStream source)
        {
            // If list is empty, allow connection
            if (AllowedProcessesToCommunicate.Count == 0)
                return true;

            // Get full process path from stream
            var pipeProcess = GetNamedPipeClientProcessPath(source);

            // Check if process is within the list of allowed processes
            if (string.IsNullOrWhiteSpace(pipeProcess))
                return false;

            pipeProcess = pipeProcess.ToLower();

            foreach (var p in AllowedProcessesToCommunicate)
            {
                if (p.ToLower().Equals(pipeProcess))
                {
                    return true;
                }
            }

            return false;
        }
    }
}