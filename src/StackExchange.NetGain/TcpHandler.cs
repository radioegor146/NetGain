using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.IO;
using NLog;

namespace StackExchange.NetGain
{
    public partial class TcpHandler : IDisposable
    {
        protected NetContext Context { get; }

        public TcpHandler(int concurrentOperations = 0)
        {
            Context = new NetContext(AsyncHandler, this);
            if (concurrentOperations <= 0) concurrentOperations = 2 * Environment.ProcessorCount;
            this.concurrentOperations = new Semaphore(concurrentOperations, concurrentOperations);
            MutexTimeout = 10000;
            ConnectTimeout = 5000;
            MaxIncomingQuota = DefaultMaxIncomingQuota;
            MaxOutgoingQuota = DefaultMaxOutgoingQuota;
        }

        internal const int DefaultMaxIncomingQuota = 2048, DefaultMaxOutgoingQuota = 16384;

        public int ConnectTimeout { get; set; }
        public int MutexTimeout { get; set; }
        /// <summary>
        /// The maximum amount of data, per connection, to allow inbound
        /// </summary>
        public int MaxIncomingQuota { get; set; }
        /// <summary>
        /// The maximum amount of data, per connection, to allow inbound
        /// </summary>
        public int MaxOutgoingQuota { get; set; }
        protected Connection OpenConnection(IPEndPoint endpoint, Action<Connection> connectionInitializer)
        {
            Socket tmp = null;
            try
            {
                tmp = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                var asyncState = tmp.BeginConnect(endpoint, null, null);
                if (asyncState.AsyncWaitHandle.WaitOne(ConnectTimeout, true))
                {
                    tmp.EndConnect(asyncState); // checks for exception
                    var conn = ProtocolFactory.CreateConnection(endpoint) ?? new Connection();

                    connectionInitializer?.Invoke(conn);

                    conn.Socket = tmp;
                    var processor = ProtocolFactory.GetProcessor();
                    conn.SetProtocol(processor);
                    processor.InitializeOutbound(Context, conn); 
                    StartReading(conn);
                    conn.InitializeClientHandshake(Context);
                    tmp = null;
                    return conn;
                }
                else
                {
                    Close();
                    throw new TimeoutException("Unable to connect to endpoint");
                }
            }
            finally
            {
                ((IDisposable) tmp)?.Dispose();
            }
        }
        protected void CloseConnection(Connection connection)
        {
            var socket = connection?.Socket;
            if (socket == null)
                return;
            try { socket.Shutdown(SocketShutdown.Both); }
            catch { /* swallow */ }
            try { socket.Close(); }
            catch (Exception ex) { Trace.WriteLine(ex.Message); }
            try { ((IDisposable)socket).Dispose(); }
            catch (Exception ex) { Trace.WriteLine(ex.Message); }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
        }
        protected virtual void Close()
        {
            
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing) Close();
        }


        private readonly Semaphore concurrentOperations;
        internal void StartAccept(SocketAsyncEventArgs args)
        {
AcceptMore:
            try
            {
                args.AcceptSocket = null; // make sure cleared
                var connectSocket = (Socket)args.UserToken;
                if (!connectSocket.AcceptAsync(args))
                {
                    // one was hot; process now
                    AcceptCompleted(args, false);
                    goto AcceptMore; // this is to prevent a stack dive
                }
            } 
            catch(Exception ex)
            {
                Logger?.Fatal(ex, $"{Connection.GetIdent(args)}\tStartAccept");
            }
        }
        private void AsyncHandler(object sender, SocketAsyncEventArgs args)
        {
#if VERBOSE
            Debug.WriteLine(string.Format("[{0}]\t{1}, {2}: {3} bytes", ToString(), args.LastOperation, args.SocketError, args.BytesTransferred));
#endif
            var gotTheConch = false;
            try
            {
                var hiPri = !(args.UserToken is Connection conn) || conn.HighPriority;
                gotTheConch = !hiPri && concurrentOperations.WaitOne(MutexTimeout);
                if (gotTheConch || hiPri)
                {
                    if (args.SocketError == SocketError.Success)
                    {
                        switch (args.LastOperation)
                        {
                            case SocketAsyncOperation.Accept:
                                AcceptCompleted(args, true);
                                break;
                            case SocketAsyncOperation.Receive:
                                ReceiveCompleted(args);
                                break;
                            case SocketAsyncOperation.Send:
                                SendCompleted(args);
                                break;
                            case SocketAsyncOperation.Disconnect:
                                CloseSocket(args);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        CloseSocket(args);
                        if (args.LastOperation == SocketAsyncOperation.Accept)
                        {
                            var connectSocket = (Socket)args.UserToken;
                            OnAcceptFailed(args, connectSocket);
                        }
                    }
                }
                else
                {
                    Logger?.Error($"{Connection.GetIdent(args)}\tForced to drop a connection because the server did not respond");
                    CloseSocket(args);
                }
            }
            finally
            {
                if (gotTheConch) concurrentOperations.Release();
            }
        }
        protected virtual void OnAcceptFailed(SocketAsyncEventArgs args, Socket socket)
        { }
        protected virtual void OnFlushed(Connection connection)
        { }
        private void SendCompleted(SocketAsyncEventArgs args)
        {
            try
            {
            ProcessMore:
                var conn = (Connection) args.UserToken;

                if (args.BytesTransferred != 0 && args.SocketError == SocketError.Success)
                {
                    conn?.Seen(); // update LastSeen

                    Interlocked.Add(ref totalBytesSent, args.BytesTransferred);
#if DEBUG && LOG_OUTBOUND
                    conn.LogOutput(args.Buffer, args.Offset, args.BytesTransferred);
#endif
                    var state = (Connection)args.UserToken;
                    if (args.BytesTransferred == args.Count)
                    {
                        var toWrite = state.ReadOutgoing(Context, args.Buffer, 0, args.Buffer.Length);

                        if(toWrite > 0)
                        {
                            args.SetBuffer(args.Buffer, 0, toWrite);
                            if (!state.Socket.SendAsync(args)) goto ProcessMore;
                        } else
                        {
                            OnFlushed(conn);
                            Context.Recycle(args);
                        }
                    }
                    else
                    {
                        // more to be sent from that buffer
                        args.SetBuffer(args.Buffer, args.Offset + args.BytesTransferred, args.Count - args.BytesTransferred);
                        if (!state.Socket.SendAsync(args)) goto ProcessMore;
                    }
                }
                else
                {
                    Logger?.Error(new Exception($"Socket was closed: {args.SocketError}"),
                        $"{Connection.GetIdent(args)}\tSocket closed");
                    CloseSocket(args);
                }
            }
            catch (ObjectDisposedException)
            {
                CloseSocket(args);
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, $"{Connection.GetIdent(args)}\tSend");
                CloseSocket(args);
            }
        }


        private byte[] microBuffer;
        private int microBufferIndex;
        void SetFullBuffer(SocketAsyncEventArgs args)
        {
            var buffer = Context.GetBuffer();
            args.SetBuffer(buffer, 0, buffer.Length);
        }
        void SetMicroBuffer(SocketAsyncEventArgs args)
        {
            int index;
            byte[] buffer;
            lock (Context)
            {
                if (microBuffer == null)
                {
                    microBuffer = Context.GetBuffer();
                }
                buffer = microBuffer;
                index = microBufferIndex++;
                if (microBufferIndex == microBuffer.Length)
                { 
                    // will need a new one next time
                    microBufferIndex = 0;
                    microBuffer = null;
                }
            }
            args.SetBuffer(buffer, index, 1);
        }

        public event Action<Message> Received;
        public virtual void OnReceived(Connection connection, object value)
        {
            var handler = Received;
            var handled = false;
            if (handler != null)
            {
                var msg = new Message(Context, connection, value);
                handler(msg);
                handled = msg.IsHandled;
            }

            if (handled)
                return;
            if (connection.UserToken is ClientNode node)
            {
                node.SetResult(value);
            }
        }
        protected void StartReading(Connection connection)
        {
            if (!connection.CanRead)
                return;
            var socket = connection.Socket;
            var args = Context.GetSocketArgs();
            args.UserToken = connection;
            SetMicroBuffer(args);
            // set a **tiny portion** of a shard buffer until we have reason to think they are sending us data
            if (!socket.ReceiveAsync(args)) 
                ReceiveCompleted(args);
        }

        protected internal virtual bool RequestOutgoing(Connection connection)
        {
            if (connection.UserToken is ClientNode node)
            {
                return node.RequestOutgoing();
            }
            return false;
        }

        internal void StartSending(Connection connection)
        {
            SocketAsyncEventArgs args = null;
            try
            {
                var buffer = Context.GetBuffer();
                var toWrite = connection.ReadOutgoing(Context, buffer, 0, buffer.Length);
                if (toWrite > 0)
                {
                    args = Context.GetSocketArgs();
                    args.UserToken = connection;
                    args.SetBuffer(buffer, 0, toWrite);
                    if (!connection.Socket.SendAsync(args)) SendCompleted(args);
                } else
                {
                    Context.Recycle(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, $"{Connection.GetIdent(args)}\tStart-send");
                if (args != null) CloseSocket(args);
            }
        }

        private void ReceiveCompleted(SocketAsyncEventArgs args)
        {
            try
            {
MoreToRead:

                var state = (Connection)args.UserToken;

                if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
                {
                    state?.Seen(); // update LastSeen

                    Interlocked.Add(ref totalBytesReceived, args.BytesTransferred);

                    if (state != null && (MaxIncomingQuota > 0 && state.IncomingBufferedLength + args.BytesTransferred > MaxIncomingQuota))
                    {
                        throw new InvalidOperationException("Incoming buffer exceeded");
                    }

#if VERBOSE
                    Debug.WriteLine("Received: " + BitConverter.ToString(args.Buffer, args.Offset, args.BytesTransferred));
#endif

                    if (state != null)
                    {
                        state.AppendIncoming(Context, args.Buffer, args.Offset, args.BytesTransferred);
                        var tmp = args.Buffer;
                        var recycle = args.Count == tmp.Length; // we pwn it
                        args.SetBuffer(null, 0, 0);
                        if (recycle) Context.Recycle(tmp);

                        var keepReading = state.ProcessBufferedData(Context, out var msgCount);
                        if (msgCount > 0) Interlocked.Add(ref totalMessages, msgCount);

                        if (!state.CanRead)
                        {
                            // allows eager shutdown of reads
                            if (state.IncomingBufferedLength != 0)
                            {
                                throw new InvalidOperationException("Extra data discovered on an outbound-only socket");
                            }

                            keepReading = false;
                        }

                        if (keepReading)
                        {
                            if (state.IncomingBufferedLength == 0)
                            {
                                // use a fraction of a buffer until we think there is something useful to read
                                SetMicroBuffer(args);
                            }
                            else
                            {
                                // we know we're expecting more, since we have some buffered that we can't yet handle
                                SetFullBuffer(args);
                            }

                            try
                            {
                                if (state.CanRead && !state.Socket.ReceiveAsync(args)) goto MoreToRead;
                            }
                            catch (ObjectDisposedException)
                            {
                                // can get this if the client disconnects and the socket gets shut down
                                Debug.WriteLine("EOF/close: " + state);
                                CloseSocket(args);
                            }
                        }
                    }
                }
                else
                {
                    Debug.WriteLine("EOF/close: " + state);
                    CloseSocket(args);
                }
            }
            catch (CloseSocketException)
            { 
                // fairly clean exit
                CloseSocket(args);
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, $"{Connection.GetIdent(args)}\tReceive");
                CloseSocket(args);
            }
        }

        public IProtocolFactory ProtocolFactory { get; set; }

        private volatile bool doNotAccept;
        public void DoNotAccept()
        {
            doNotAccept = true;
        }

        private void AcceptCompleted(SocketAsyncEventArgs args, bool startMore)
        {
            Interlocked.Increment(ref totalConnections);
            Socket newSocket = null;
            try
            {
                newSocket = args.AcceptSocket;
                if (doNotAccept)
                {
                    Kill(newSocket);
                }
                else
                {
#if VERBOSE
                    Debug.WriteLine(string.Format("[{0}]\taccepted: from {1} to {2}", this, newSocket.RemoteEndPoint, newSocket.LocalEndPoint));
#endif
                    args.AcceptSocket = null; // clear ASAP to avoid accidental re-use
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        var state = ProtocolFactory.CreateConnection(newSocket.LocalEndPoint) ?? new Connection();
                        state.Prepare(); // logs LastSeen; generates new id
                                         // WriteLog("accepted from " + newSocket.RemoteEndPoint + " to " + newSocket.LocalEndPoint, state);
                        var processor = ProtocolFactory.GetProcessor();
                        state.SetProtocol(processor);
                        processor.InitializeInbound(Context, state);
                        state.Socket = newSocket;
#if DEBUG && LOG_OUTBOUND
                    state.ResetLogOutput();
#endif
                        OnAccepted(state);

                    });
                }
                
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, $"{Connection.GetIdent(args)}\tAccept");
                Kill(newSocket);
            }
            if (startMore)
            {
                StartAccept(args); // look for other clients too
            }
        }

        private static void Kill(Socket socket)
        {
            if (socket == null)
                return;
            try { socket.Close(); } catch { /* ignored */ }
            try { socket.Dispose(); } catch { /* ignored */ }
        }

        protected virtual void OnAccepted(Connection connection)
        {
            throw new NotSupportedException();
        }
        protected internal virtual void OnClosing(Connection connection)
        {
            if (connection.UserToken is ClientNode node)
                node.Close();
        }
        private void CloseSocket(SocketAsyncEventArgs args)
        {
            var state = args.UserToken as Connection;
            if (state != null)
            {
                try
                {
                    OnClosing(state);
                }
                catch (Exception ex)
                {
                    Logger?.Error(ex, $"{Connection.GetIdent(args)}\tClose");
                }
                
            }
            
            var socket = state == null ? args.AcceptSocket : state.Socket;
            if (socket != null)
            {
                try { socket.Shutdown(SocketShutdown.Send); }
                catch { /* swallow */ }
                try { socket.Close(); }
                catch { /* swallow */ }
                try { ((IDisposable)socket).Dispose(); }
                catch { /* swallow */ }
            }
            // release the various objects and buffers
            Context.Recycle(args);
        }

        private int totalConnections;
        private long totalBytesReceived, totalBytesSent, totalMessages;
        private string lastLog;
        protected virtual int GetCurrentConnectionCount()
        {
            return 0;
        }

        public Logger Logger { get; set; } = LogManager.GetCurrentClassLogger();

        protected void WriteLog()
        {
            var log = Logger;
            if (log == null)
                return;
            var newLog = BuildLog();
            if (newLog == lastLog)
                return;
            lastLog = newLog;
            log.Info($"{Connection.GetLogIdent()}\t{newLog}");
        }
        public virtual string BuildLog()
        {
            int tc = Thread.VolatileRead(ref totalConnections),
                cc = GetCurrentConnectionCount();
            long tr = Interlocked.Read(ref totalBytesReceived),
                 ts = Interlocked.Read(ref totalBytesSent),
                 to = Interlocked.Read(ref totalMessages),
                 mem;
            var health = IsAtCapacity ? ", MAXED" : "";
            using(var proc = Process.GetCurrentProcess())
            {
                mem = proc.PrivateMemorySize64 / (1024 * 1024);
            }
            var sb = new StringBuilder();
            sb.AppendFormat("con:{0}/{1} i/o kB:{2}/{3} ops:{4} mem: {5}MB {6}", cc, tc, tr / 1024, ts / 1024, to, mem, health);
            var ext = Context.Extensions;
            if (ext == null)
                return sb.ToString();
            foreach (var o in ext)
            {
                if (o != null) sb.Append(' ').Append(o);
            }
            return sb.ToString();
        }
        public bool IsAtCapacity
        {
         get
         {
             var taken = false;
             try
             {
                 taken = concurrentOperations.WaitOne(0);
             }
             finally
             {
                 if(taken) concurrentOperations.Release();
             }
             return !taken;
         }   
        }

        public virtual void OnAuthenticate(Connection connection, StringDictionary claims) {}
        public virtual void OnAfterAuthenticate(Connection connection) { }

        public void WriteLog(string line, Connection connection = null)
        {
            Logger?.Info($"{(connection == null ? Connection.GetAuditTimestamp() : Connection.GetIdent(connection))}\t{line}");
        }
    }
    internal sealed class CloseSocketException : Exception { }
}
