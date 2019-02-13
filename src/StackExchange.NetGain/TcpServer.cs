using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace StackExchange.NetGain
{
    public class TcpServer : TcpHandler
    {
        public IMessageProcessor MessageProcessor { get; set; }

        private Tuple<Socket, EndPoint>[] connectSockets;
        public TcpServer(int concurrentOperations = 0) : base(concurrentOperations)
        {
            
        }
        
        public override string ToString()
        {
            return "server";
        }

        protected override void Close()
        {
            Stop();
            base.Close();
        }
        private volatile bool stopped;
        public bool IsStopped => stopped;

        public void Stop()
        {
            stopped = true;
            if (timer != null)
            {
                timer.Dispose();
                timer = null;
            }
            var ctx = Context;
            ctx?.DoNotAccept();

            var proc = MessageProcessor;
            if (proc != null)
            {
                Logger?.Info($"{Connection.GetLogIdent()}\tShutting down connections...");
                foreach (var pair in allConnections)
                {
                    var conn = pair.Value;
                    try
                    {
                        proc.OnShutdown(ctx, conn); // processor first
                        conn.GracefulShutdown(ctx); // then protocol
                    }
                    catch (Exception ex)
                    { Logger?.Error(ex, Connection.GetIdent(conn)); }
                }
                Thread.Sleep(100);
            }
            
            foreach (var pair in allConnections)
            {
                var conn = pair.Value;
                var socket = conn.Socket;
                if (socket == null)
                    continue;
                try { socket.Close(); }
                catch (Exception ex) { Logger?.Error(ex, Connection.GetIdent(conn)); }
                try { ((IDisposable)socket).Dispose(); }
                catch (Exception ex) { Logger?.Error(ex, Connection.GetIdent(conn)); }
            }
            if (connectSockets != null)
            {
                foreach (var tuple in connectSockets)
                {
                    var connectSocket = tuple.Item1;
                    if (connectSocket == null) continue;
                    EndPoint endpoint = null;
                    
                    try
                    {
                        endpoint = connectSocket.LocalEndPoint;
                        Logger?.Info($"{Connection.GetConnectIdent(endpoint)}\tService stopping: {endpoint}");
                        connectSocket.Close();
                    }
                    catch (Exception ex) { Logger?.Error(ex, Connection.GetConnectIdent(endpoint)); }
                    try { ((IDisposable)connectSocket).Dispose(); }
                    catch (Exception ex) { Logger?.Error(ex, Connection.GetConnectIdent(endpoint)); }
                }
                connectSockets = null;
                var tmp = MessageProcessor;
                tmp?.EndProcessor(Context);
            }
            WriteLog();
        }

        private readonly ConcurrentDictionary<long,Connection> allConnections = new ConcurrentDictionary<long,Connection>();
        
        private Timer timer;

        public int Backlog { get; set; }
        
        private const int logFrequency = 10000;

        public void Start(string configuration, params IPEndPoint[] endpoints)
        {
            if (endpoints == null || endpoints.Length == 0) throw new ArgumentNullException(nameof(endpoints));

            if (connectSockets != null) throw new InvalidOperationException("Already started");

            connectSockets = new Tuple<Socket, EndPoint>[endpoints.Length];
            var tmp = MessageProcessor;
            tmp?.StartProcessor(Context, configuration);
            for (var i = 0; i < endpoints.Length; i++)
            {
                Logger?.Info($"{Connection.GetConnectIdent(endpoints[i])}\tService starting: {endpoints[i]}");
                EndPoint endpoint = endpoints[i];
                var connectSocket = StartAcceptListener(endpoint);
                if (connectSocket == null) throw new InvalidOperationException("Unable to start all endpoints");
                connectSockets[i] = Tuple.Create(connectSocket, endpoint);
            }

            timer = new Timer(Heartbeat, null, logFrequency, logFrequency);
        }

        private void ResurrectDeadListeners()
        {
            if (IsStopped)
                return;
            var haveLock = false;
            try
            {
                Monitor.TryEnter(connectSockets, 100, ref haveLock);
                if (!haveLock)
                    return;
                for (var i = 0; i < connectSockets.Length;i++)
                {
                    if (connectSockets[i].Item1 == null)
                    {
                        ResurrectListenerAlreadyHaveLock(i);
                    }
                }
            }
            finally
            {
                if (haveLock) Monitor.Exit(connectSockets);
            }
        }

        private void ResurrectListenerAlreadyHaveLock(int i)
        {
            var endpoint = connectSockets[i].Item2;
            try
            {
                Logger?.Error($"Restarting listener on {endpoint}...");
                var newSocket = StartAcceptListener(endpoint);
                connectSockets[i] = Tuple.Create(newSocket, endpoint);
                Logger?.Error(newSocket == null ? $"Unable to restart listener on {endpoint}..." : $"Restarted listener on {endpoint}...");
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, $"Restart failed on {endpoint}");
            }
        }
        protected override void OnAcceptFailed(SocketAsyncEventArgs args, Socket socket)
        {
            if (IsStopped) return;
            try
            {
                base.OnAcceptFailed(args, socket);

                Logger?.Error(new Exception(args.SocketError.ToString()), "Listener failure");

                lock(connectSockets)
                {
                    for (var i = 0; i < connectSockets.Length; i++)
                    {
                        if (!ReferenceEquals(connectSockets[i].Item1, socket))
                            continue;
                        // clearly mark it as dead (makes it easy to spot in heartbeat)
                        connectSockets[i] = Tuple.Create((Socket)null, connectSockets[i].Item2);

                        if (ImmediateReconnectListeners)
                        {
                            // try to resurrect promptly, but there's a good chance this will fail
                            // and will be handled by heart-beat
                            ResurrectListenerAlreadyHaveLock(i);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, "Epic fail in OnAcceptFailed");
            }
        }

        public void KillAllListeners()
        {
            lock (connectSockets)
            {
                foreach (var tuple in connectSockets)
                {
                    var socket = tuple?.Item1;
                    socket?.Close();
                }
            }
        }

        private Socket StartAcceptListener(EndPoint endpoint)
        {
            try
            {
                var connectSocket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                connectSocket.Bind(endpoint);
                connectSocket.Listen(Backlog);
                var args = Context.GetSocketArgs();
                args.UserToken = connectSocket; // the state on each connect attempt is the originating socket
                StartAccept(args);
                return connectSocket;
            } catch(Exception ex)
            {
                Logger?.Error($"Unable to start listener on {endpoint}: {ex.Message}");
                return null;
            }
        }

        public override void OnAuthenticate(Connection connection, StringDictionary claims)
        {
            var tmp = MessageProcessor;
            tmp?.Authenticate(Context, connection, claims);
            base.OnAuthenticate(connection, claims);
        }
        public override void OnAfterAuthenticate(Connection connection)
        {
            var tmp = MessageProcessor;
            tmp?.AfterAuthenticate(Context, connection);
            base.OnAfterAuthenticate(connection);
        }
        protected override void OnAccepted(Connection connection)
        {
            var tmp = MessageProcessor;
            tmp?.OpenConnection(Context, connection);
            allConnections.TryAdd(connection.Id, connection);
            
            StartReading(connection);
        }

        protected override void OnFlushed(Connection connection)
        {
            var tmp = MessageProcessor;
            tmp?.Flushed(Context, connection);
            base.OnFlushed(connection);
        }
        protected override int GetCurrentConnectionCount()
        {
            return allConnections.Count;
        }
        protected internal override void OnClosing(Connection connection)
        {
            if (allConnections.TryRemove(connection.Id, out var found) && found == connection)
            {
                var tmp = MessageProcessor;
                tmp?.CloseConnection(Context, connection);
                // anything else we should do at connection shutdown
            }
            base.OnClosing(connection);
        }
        public override void OnReceived(Connection connection, object value)
        {
            var proc = MessageProcessor;
            proc?.Received(Context, connection, value);
            base.OnReceived(connection, value);
        }

        private readonly object heartbeatLock = new object();
        public void Heartbeat()
        {
            lock(heartbeatLock) // don't want timer and manual invoke conflicting
            {
                var tmp = MessageProcessor;
                if (tmp != null)
                {
                    try
                    {
                        tmp.Heartbeat(Context);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error(ex, $"{Connection.GetHeartbeatIdent()}\tHeartbeat");
                    }
                }
                ResurrectDeadListeners();
                WriteLog();
            }
        }

        public bool ImmediateReconnectListeners { get; set; } = true;

        private void Heartbeat(object sender)
        {
            Heartbeat();
        }


        private int broadcastCounter;
        public override string BuildLog()
        {
            var proc = MessageProcessor;
            var procStatus = proc?.ToString() ?? "";
            return base.BuildLog() + " bc:" + Thread.VolatileRead(ref broadcastCounter) + " " + procStatus;
        }

        private void BroadcastProcessIterator(IEnumerator<Connection> iterator, Func<Connection, object> selector, NetContext ctx)
        {

            bool cont;
            do
            {
                Connection conn;
                lock (iterator)
                {
                    cont = iterator.MoveNext();
                    conn = cont ? iterator.Current : null;
                }
                try
                {
                    if (cont && conn != null && conn.IsAlive)
                    {
                        var message = selector(conn);
                        if (message != null)
                        {
                            conn.Send(ctx, message);
                            Interlocked.Increment(ref broadcastCounter);
                        }
                    }
                }
                catch
                { // if an individual connection errors... KILL IT! and then gulp down the exception
                    try
                    {
                        conn?.Shutdown(ctx);
                    }
                    catch
                    {
                        //ignored
                    }
                }
            } while (cont);
        }

        public void Broadcast(Func<Connection, object> selector)
        {
            // manually dual-threaded; was using Parallel.ForEach, but that caused unconstrained memory growth
            var ctx = Context;
            using (var enumerator = allConnections.Values.GetEnumerator())
                using (var workerDone = new AutoResetEvent(false))
                {
                    ThreadPool.QueueUserWorkItem(x =>
                    {
                        BroadcastProcessIterator(enumerator, selector, ctx);
                        workerDone.Set();
                    });
                    BroadcastProcessIterator(enumerator, selector, ctx);
                    workerDone.WaitOne();
                }
        }


        public void Broadcast(object message, Func<Connection, bool> predicate = null)
        {
            if (message == null)
            {
                // nothing to send
            }
            else if (predicate == null)
            {  // no test
                Broadcast(conn => message);
            }
            else
            {
                Broadcast(conn => predicate(conn) ? message : null);
            }
        }

        public int ConnectionTimeoutSeconds { get; set; }

    }
}
