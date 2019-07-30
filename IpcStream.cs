/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace Dashboard.Pipes
{
    /// <summary>
    ///     Class implementing the communication protocol
    /// </summary>
    public class IpcStream : IDisposable
    {
        private bool _disposed;

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="s">Network stream</param>
        /// <param name="knowntypes">List of types to register with serializer</param>
        public IpcStream(Stream s, IReadOnlyList<Type> knowntypes)
        {
            BaseStream = s;
            KnownTypes = knowntypes;
        }

        /// <summary>
        ///     Underlying network stream
        /// </summary>
        public Stream BaseStream { get; }

        /// <summary>
        ///     Types registered with the serializer
        /// </summary>
        public IReadOnlyList<Type> KnownTypes { get; }

        public void Dispose()
        {
            BaseStream.Close();
            _disposed = true;
        }

        ~IpcStream()
        {
            if (!_disposed)
                Dispose();
        }

        /// <summary>
        ///     Read the raw network message
        /// </summary>
        /// <returns>byte buffer</returns>
        protected byte[] ReadBytes()
        {
            var length = BaseStream.ReadByte() * 256;
            length += BaseStream.ReadByte();

            length = Math.Max(length, 0);

            var buffer = new byte[length];

            if (length > 0)
                BaseStream.Read(buffer, 0, length);

            return buffer;
        }

        /// <summary>
        ///     Write a raw network message
        /// </summary>
        /// <param name="buffer">byte buffer</param>
        protected void WriteBytes(byte[] buffer)
        {
            var length = buffer.Length;
            if (length > ushort.MaxValue)
                throw new InvalidOperationException("Message is too long");

            try
            {
                BaseStream.WriteByte((byte) (length / 256));
                BaseStream.WriteByte((byte) (length & 255));
                BaseStream.Write(buffer, 0, length);
                BaseStream.Flush();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"COULD NOT WRITEBYTES {e.Message}");
            }
        }

        /// <summary>
        ///     Read the next <see cref="IpcMessage" /> from the network
        /// </summary>
        /// <returns>The received message</returns>
        public IpcMessage ReadMessage()
        {
            // read the raw message
            var msg = ReadBytes();

            try
            {
                // deserialize
                var serializer = new DataContractSerializer(typeof(IpcMessage), KnownTypes);
                var rdr = XmlDictionaryReader
                    .CreateBinaryReader(msg, XmlDictionaryReaderQuotas.Max);

                return serializer.ReadObject(rdr) as IpcMessage;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        ///     Write a <see cref="IpcMessage" /> to the network
        /// </summary>
        /// <param name="msg">Message to write</param>
        public void WriteMessage(IpcMessage msg)
        {
            // serialize
            var serializer = new DataContractSerializer(typeof(IpcMessage), KnownTypes);
            using (var stream = new MemoryStream())
            {
                var writer = XmlDictionaryWriter.CreateBinaryWriter(stream);

                serializer.WriteObject(writer, msg);
                writer.Flush();

                // write the raw message
                WriteBytes(stream.ToArray());
            }
        }

        /// <summary>
        ///     Scan an interface for parameter and return <see cref="Type" />s
        /// </summary>
        /// <param name="T">The interface type</param>
        /// <param name="knownTypes">List to add found types to</param>
        public static void ScanInterfaceForTypes(Type T, IList<Type> knownTypes)
        {
            // scan used types
            foreach (var mi in T.GetMethods())
            {
                Type t;
                foreach (var pi in mi.GetParameters())
                {
                    t = pi.ParameterType;

                    if (!t.IsClass && !t.IsInterface)
                        continue;

                    if (!knownTypes.Contains(t))
                        knownTypes.Add(t);
                }

                t = mi.ReturnType;
                if (!t.IsClass && !t.IsInterface)
                    continue;

                if (!knownTypes.Contains(t))
                    knownTypes.Add(t);
            }
        }
    }
}