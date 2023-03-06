﻿using EuNet.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EuNet.Server
{
    /// <summary>
    /// 하나의 서버를 기동하기 위한 클래스
    /// 인스턴스 하나당 한개 서버의 모든 기능을 담당함
    /// 데이터전송, RPC, 세션관리 등을 담당
    /// </summary>
    public class NetServer : IServer
    {
        private readonly ServerOption _serverOption;
        private ServerState _state = ServerState.None;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private IListener _listener;
        private ISessionFactory _sessionFactory;
        private readonly SessionManager _sessionManager;
        private readonly P2pManager _p2pManager;
        private UdpSocketEx _udpSocket;
        private Thread _updateThread;
        private List<IRpcInvokable> _rpcHandlers;
        private readonly NetStatistic _statistic;

        /// <summary>
        /// 서버 이름
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 세션 매니저
        /// </summary>
        public SessionManager SessionManager => _sessionManager;

        /// <summary>
        /// P2P 매니저
        /// </summary>
        public P2pManager P2pManager => _p2pManager;

        /// <summary>
        /// 현재 접속중인 세션의 개수
        /// </summary>
        public int SessionCount => _sessionManager.SessionCount;

        /// <summary>
        /// 현재 서버 상태
        /// </summary>
        public ServerState State => _state;

        /// <summary>
        /// 세션이 연결되었음
        /// </summary>
        public event Action<ISession> OnSessionConnected;

        /// <summary>
        /// 세션 연결이 종료되었음
        /// </summary>
        public event Action<ISession> OnSessionClosed;

        /// <summary>
        /// 세션이 데이터를 받음
        /// </summary>
        public Func<ISession, NetDataReader, Task> OnSessionReceived { get; set; }

        /// <summary>
        /// 세션이 요청을 받음
        /// </summary>
        public Func<ISession, NetDataReader, NetDataWriter, Task> OnSessionRequestReceived { get; set; }

        /// <summary>
        /// 세션에서 에러가 발생함
        /// </summary>
        public Action<ISession, Exception> OnSessionErrored { get; set; }

        /// <summary>
        /// 서버 전반적으로 에러가 발생함
        /// </summary>
        public Action<Exception> OnErrored { get; set; }

        /// <summary>
        /// 네트워크 통계
        /// </summary>
        public NetStatistic Statistic => _statistic;

        public NetServer(
            ServerOption serverOption,
            NetStatistic statistics = null,
            ILoggerFactory loggerFactory = null,
            ISessionFactory sessionFactory = null)
        {
            Name = serverOption.Name;

            _serverOption = serverOption;
            //_serverOptions.PacketFilter = _serverOptions.PacketFilter ?? new XorPacketFilter();

            _loggerFactory = loggerFactory ?? DefaultLoggerFactory.Create(builder => { builder.AddConsoleLogger(); });
            _logger = _loggerFactory.CreateLogger("Server");

            _statistic = statistics ?? new NetStatistic();

            _sessionFactory = sessionFactory ?? new DefaultSessionFactory(_serverOption, _loggerFactory, _statistic);

            _sessionManager = new SessionManager(_serverOption.MaxSession);

            if (_serverOption.IsServiceUdp)
            {
                _p2pManager = new P2pManager();
                OnSessionClosed += _p2pManager.OnSessionClose;
            }

            _rpcHandlers = new List<IRpcInvokable>();

            // 옵션 체크
            if(_serverOption.IsServiceUdp)
            {
                // UDP 를 사용한다면 서버 세션 업데이트 주기가 짧아야 함
                if (_serverOption.SessionUpdateInternval > 100)
                {
                    _logger.LogWarning("ServerOption.SessionUpdateInternval is too long. It is recommended to set it to 30");
                }
            }
            else
            {
                if (_serverOption.SessionUpdateInternval < 1000)
                {
                    _logger.LogWarning("ServerOption.SessionUpdateInternval is too short. It is recommended to set it to 1000 or more");
                }
            }
            
        }

        /// <summary>
        /// RPC 서비스를 등록함.
        /// </summary>
        /// <param name="service"></param>
        public void AddRpcService(IRpcInvokable service)
        {
            if(_state != ServerState.None &&
                _state != ServerState.Stopped)
                throw new Exception("Only possible when the server is stopped");

            if (_rpcHandlers.Contains(service))
                throw new Exception("Already exist IRpcInvokable in _rpcHandlers");

            _logger.LogInformation($"Add Rpc Service : {service.GetType().Name}");

            _rpcHandlers.Add(service);
        }

        /// <summary>
        /// 서버를 시작
        /// </summary>
        public async Task StartAsync()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"----------\t[{_serverOption.Name}] powered by EuNet\t----------");
            builder.AppendLine();
            try { builder.AppendLine($"Public IP : {NetUtil.GetPublicIpAddress()}"); } catch { }
            try { builder.AppendLine($"Private IP : {NetUtil.GetLocalIpAddress()}"); } catch { }
            _logger.LogInformation(builder.ToString());

            var state = _state;

            if (state != ServerState.None && state != ServerState.Stopped)
            {
                throw new InvalidOperationException($"The server cannot be started right now, because its state is {state}.");
            }

            _state = ServerState.Starting;

            await StartListenAsync();

            _updateThread = new Thread(UpdateLoopThread);
            _updateThread.IsBackground = true;
            _updateThread.Start();

            _logger.LogInformation($"Update thread started : {_updateThread.ThreadState}");

            _state = ServerState.Started;

            _logger.LogInformation($"Max session : {_serverOption.MaxSession}");
            _logger.LogInformation("Server started!");
        }

        /// <summary>
        /// 서버를 정지
        /// </summary>
        public async Task StopAsync()
        {
            var state = _state;

            if (state != ServerState.Started)
            {
                throw new InvalidOperationException($"The server cannot be stopped right now, because its state is {state}.");
            }

            _state = ServerState.Stopping;

            _logger.LogInformation("Stopping... listener");
            if (_listener.IsRunning)
            {
                await _listener.StopAsync();
            }

            _logger.LogInformation("Stopping... all session");
            await _sessionFactory.ShutdownAsync();

            _updateThread.Join();
            _updateThread = null;

            if (_udpSocket != null)
            {
                _logger.LogInformation("Stopping... udp service");
                _udpSocket.Close(false);
                _udpSocket = null;
            }

            _p2pManager?.Clear();

            _logger.LogInformation("Stopped!");

            _state = ServerState.Stopped;
        }

        private Task<bool> StartListenAsync()
        {
            if (_serverOption.IsServiceUdp == true)
            {
                _udpSocket = new UdpSocketEx(_loggerFactory.CreateLogger("ServerUdpSocket"), OnPreProcessUdpRawData);

                var udpEndPoint = NetUtil.GetEndPoint(_serverOption.UdpServerAddress, _serverOption.UdpServerPort);

                if (_udpSocket.CreateServer(udpEndPoint, _serverOption.UdpReuseAddress) == false)
                {
                    _logger.LogError($"Failed to bind udp server : {_udpSocket.LocalPort}");
                    return Task.FromResult(false);
                }
            }

            _listener = new TcpListener(_serverOption, _loggerFactory.CreateLogger("TcpListener"));
            _listener.NewClientAccepted += OnNewClientAccept;

            if (!_listener.Start())
            {
                _logger.LogError($"Failed to listen {_listener}.");
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        protected virtual void OnNewClientAccept(IListener listener, Socket socket)
        {
            var session = _sessionFactory.Create() as ServerSession;
            if (session == null)
            {
                // 최대수용유저를 넘어섰음
                _logger.LogError("no more create session by sessionFactory");
                return;
            }

            session.OnPreProcessPacket = OnPreProcessPacket;
            session.OnReceived = OnSessionReceived;
            session.OnRequestReceived = OnSessionRequestReceive;
            session.OnErrored = OnSessionErrored;

            session.Init(new SessionInitializeInfo()
            {
                AcceptedTcpSocket = socket,
                UdpServiceSocket = _udpSocket
            });

            HandleSession(session).DoNotAwait();
        }

        private async Task HandleSession(ServerSession session)
        {
            _sessionManager.InsertSession(session);

            try
            {
                _logger.LogInformation($"A new session connected: {session.SessionId}");
                session.OnSessionConnected();
                OnSessionConnected?.Invoke(session);

                try
                {
                    await session.RunAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to handle the session {session.SessionId}.");
                    session.Close();
                }

                if (_udpSocket != null)
                {
                    _udpSocket.RemoveSession(session);
                }

                _logger.LogInformation($"The session disconnected: {session.SessionId}");

                OnSessionClosed?.Invoke(session);
                await session.OnSessionClosed();
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to handle the session {session.SessionId}.");
            }
            finally
            {
                _sessionManager.RemoveSession(session);
                _sessionFactory.Release(session);
            }
        }

        private bool OnPreProcessPacket(ISession session, NetPacket poolingPacket)
        {
            try
            {
                switch (poolingPacket.Property)
                {
                    case PacketProperty.AliveCheck:
                        {
                            byte type = poolingPacket.RawData[NetPacket.HeaderSize];
                            //_logger.LogInformation($"Received PacketProperty.AliveCheck Type[{type}]");

                            if (type == 0xFF)
                            {
                                var packet = NetPool.PacketPool.Alloc(PacketProperty.AliveCheck);
                                packet.RawData[NetPacket.HeaderSize] = 0;
                                session.TcpChannel.SendAsync(packet);
                            }
                        }
                        break;
                    case PacketProperty.JoinP2p:
                        {
                            try
                            {
                                //long connectId = BitConverter.ToInt64(poolingPacket.RawData, 5);
                            }
                            finally
                            {
                                NetPool.PacketPool.Free(poolingPacket);
                            }
                        }
                        break;
                    case PacketProperty.LeaveP2p:
                        {
                            try
                            {
                                //long connectId = BitConverter.ToInt64(poolingPacket.RawData, 5);
                            }
                            finally
                            {
                                NetPool.PacketPool.Free(poolingPacket);
                            }
                        }
                        break;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                OnErrored?.Invoke(ex);
                return true;
            }

            return true;
        }

        private async Task OnSessionRequestReceive(ISession session, NetDataReader reader, NetDataWriter writer)
        {
            var preReaderPos = reader.Position;
            var preWriterPos = writer.Length;

            foreach (var handler in _rpcHandlers)
            {
                reader.Position = preReaderPos;
                writer.Length = preWriterPos;

                var result = await handler.Invoke(session, reader, writer);
                if (result == true)
                    return;
            }

            reader.Position = preReaderPos;
            writer.Length = preWriterPos;

            await OnSessionRequestReceived(session, reader, writer);
        }

        private bool OnPreProcessUdpRawData(byte[] data, int size, NetPacket cachedPacket, IPEndPoint endPoint)
        {
            Interlocked.Increment(ref _statistic.UdpReceivedCount);
            Interlocked.Add(ref _statistic.UdpReceivedBytes, size);

            try
            {
                switch (cachedPacket.Property)
                {
                    case PacketProperty.UserData:
                    case PacketProperty.Ack:
                    case PacketProperty.ViewRequest:
                        {
                            // P2p 데이터를 릴레이하자
                            try
                            {
                                ushort sessionId = cachedPacket.P2pSessionId;

                                // 0이라면 서버와의 udp 통신
                                if (sessionId == 0)
                                    return false;

                                Interlocked.Increment(ref _statistic.RelayServCount);
                                Interlocked.Add(ref _statistic.RelayServBytes, size);

                                var targetSession = _sessionManager.FindSession(sessionId) as ServerSession;
                                if (targetSession == null)
                                    return true;

                                var targetEp = targetSession.UdpChannel.PunchedEndPoint;
                                if (targetEp == null)
                                    return true;

                                ISession senderSession;
                                _udpSocket.TryGetSession(endPoint, out senderSession);
                                if (senderSession == null)
                                    return true;

                                //_logger.LogInformation($"Relay to {sessionId}  {targetSession.UdpChannel.PunchedEndPoint} from {senderSession.SessionId}");

                                // 보낸이를 수정해서 보내주자
                                cachedPacket.P2pSessionId = senderSession.SessionId;

                                SocketError error = SocketError.Success;
                                _udpSocket.SendTo(data, 0, size, targetEp, ref error);
                            }
                            catch
                            {

                            }
                        }
                        break;
                    case PacketProperty.RequestConnection:
                        {
                            ushort sessionId = cachedPacket.SessionIdForConnection;

                            NetDataReader reader = new NetDataReader(cachedPacket);

                            long connectId = reader.ReadInt64();
                            IPEndPoint localEp = reader.ReadIPEndPoint();

                            var session = _sessionManager.FindSession(sessionId) as ServerSession;

                            if (session != null &&
                                session.ConnectId == connectId)
                            {
                                session.UdpChannel.LocalEndPoint = localEp;
                                session.UdpChannel.RemoteEndPoint = endPoint;

                                if (session.UdpChannel.SetPunchedEndPoint(endPoint) == true)
                                {
                                    _udpSocket.AddSession(session);
                                    //_logger.LogInformation($"Connect Udp {session.SessionId} {endPoint}");
                                }

                                // 응답을 보내자
                                NetPacket sendPacket = NetPool.PacketPool.Alloc(PacketProperty.ResponseConnection);
                                try
                                {
                                    // 서버는 0번임
                                    sendPacket.SessionIdForConnection = 0;
                                    sendPacket.DeliveryMethod = DeliveryMethod.Unreliable;
                                    SocketError error = SocketError.Success;
                                    _udpSocket.SendTo(sendPacket.RawData, 0, sendPacket.Size, endPoint, ref error);

                                    //_logger.LogInformation($"send ResponseConnection to {endPoint}");
                                }
                                finally
                                {
                                    NetPool.PacketPool.Free(sendPacket);
                                }
                            }
                        }
                        break;
                    case PacketProperty.ResponseConnection:
                        {

                        }
                        break;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                OnErrored?.Invoke(ex);
                return true;
            }

            return true;
        }

        private void UpdateLoopThread(object state)
        {
            int elapsedTime = 0;
            int updateInterval = _serverOption.SessionUpdateInternval;

            Stopwatch swForSleep = new Stopwatch();
            Stopwatch swForElapsedTime = new Stopwatch();
            swForElapsedTime.Start();

            while (_state == ServerState.Starting || _state == ServerState.Started)
            {
                swForSleep.Restart();

                swForElapsedTime.Stop();
                elapsedTime = (int)swForElapsedTime.ElapsedMilliseconds;
                swForElapsedTime.Restart();

                _sessionManager.InvokeAllSession((session =>
                {
                    try
                    {
                        session.Update(elapsedTime);
                    }
                    catch (Exception ex)
                    {
                        OnSessionErrored?.Invoke(session, ex);
                    }
                }));

                swForSleep.Stop();

                int sleepTime = updateInterval - (int)swForSleep.ElapsedMilliseconds;
                if (sleepTime <= 0)
                    sleepTime = 0;
                if (sleepTime > updateInterval)
                    sleepTime = updateInterval;

                Thread.Sleep(sleepTime);
            }
        }

        private bool disposedValue = false; // To detect redundant calls

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try
                    {
                        if (_state != ServerState.Started)
                        {
                            StopAsync().Wait();
                        }
                    }
                    catch
                    {
                    }
                }

                disposedValue = true;
            }
        }
    }
}
