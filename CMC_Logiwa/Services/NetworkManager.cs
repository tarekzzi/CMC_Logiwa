//using CMC_Logiwa.Services;
//using System;
//using System.IO;
//using System.Net.Sockets;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//namespace CMC_Logiwa.Services
//{
//    public sealed class NetworkManager : IDisposable
//    {
//        private const char STX = '\u0002';
//        private const char ETX = '\u0003';

//        private TcpClient _client;
//        private NetworkStream _stream;
//        private StreamReader _reader;

//        private CancellationTokenSource _cts;
//        private Task _listenTask;

//        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
//        private readonly SemaphoreSlim _disconnectLock = new SemaphoreSlim(1, 1);

//        private readonly FormatProcessMessages _formatter;

//        public event Action<string> OnMessageReceived;   // RECV + INFO
//        public event Action<string> OnMessageSent;       // SENT
//        public event Action<string> OnError;             // ERROR

//        public string ServerIp { get; }
//        public int Port { get; }
//        public bool IsConnected => _client?.Connected == true;

//        public NetworkManager(string serverIp, int port)
//        {
//            ServerIp = serverIp;
//            Port = port;

//            _formatter = new FormatProcessMessages();
//        }

//        public async Task ConnectAsync()
//        {
//            try
//            {
//                _client = new TcpClient();
//                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

//                await _client.ConnectAsync(ServerIp, Port);

//                _stream = _client.GetStream();

//                // CW1000/CIS messages are ASCII-safe (digits, pipes, A-Z). ASCII avoids hidden unicode issues.
//                _reader = new StreamReader(_stream, Encoding.ASCII, false, 1024, true);

//                _cts = new CancellationTokenSource();
//                _listenTask = ListenForMessagesAsync(_cts.Token);

//                LogInfo($"Connected to {ServerIp}:{Port}");
//            }
//            catch (Exception ex)
//            {
//                LogError($"Connection failed: {ex.Message}");
//                throw;
//            }
//        }

//        private async Task ListenForMessagesAsync(CancellationToken token)
//        {
//            var buffer = new char[4096];
//            var messageBuilder = new StringBuilder();
//            bool inMessage = false;

//            try
//            {
//                while (!token.IsCancellationRequested && IsConnected)
//                {
//                    int charsRead = await _reader.ReadAsync(buffer, 0, buffer.Length);
//                    if (charsRead == 0)
//                    {
//                        await Task.Delay(100, token);
//                        continue;
//                    }

//                    for (int i = 0; i < charsRead; i++)
//                    {
//                        char c = buffer[i];

//                        if (c == STX)
//                        {
//                            inMessage = true;
//                            messageBuilder.Clear();
//                            continue;
//                        }

//                        if (c == ETX)
//                        {
//                            inMessage = false;

//                            // FIX: Log inbound message ONCE only.
//                            // Do NOT also raise raw payload events elsewhere.
//                            string message = messageBuilder.ToString().Trim();

//                            if (!string.IsNullOrWhiteSpace(message))
//                            {
//                                LogProtocol(message, isSent: false);   // raises OnMessageReceived "RECV: ..."
//                                await ProcessReceivedMessage(message); // no logging inside
//                            }

//                            continue;
//                        }

//                        if (inMessage)
//                            messageBuilder.Append(c);
//                    }
//                }
//            }
//            catch (OperationCanceledException) { }
//            catch (Exception ex)
//            {
//                if (!token.IsCancellationRequested)
//                    LogError($"Read error: {ex.Message}");
//            }
//            finally
//            {
//                if (!token.IsCancellationRequested)
//                    LogError("Connection terminated unexpectedly");
//            }
//        }

//        private async Task ProcessReceivedMessage(string message)
//        {
//            // IMPORTANT: No LogProtocol here (already logged in ListenForMessagesAsync).
//            var result = _formatter.ProcessIncomingMessage(message);
//            if (result == null)
//                return;

//            // Route to handlers (e.g., HBT -> HeartbeatHandler)
//            await MessageRouter.Handle(result, this, _formatter);
//        }

//        /// <summary>
//        /// Sends a payload WITHOUT STX/ETX. This method frames it as: <STX>payload<ETX>.
//        /// Example payload: "0001|ENQ|1|200001234|"
//        /// </summary>
//        public async Task SendAsync(string payload)
//        {
//            if (payload == null) payload = string.Empty;

//            string framed = $"{STX}{payload}{ETX}";

//            await _sendLock.WaitAsync();
//            try
//            {
//                if (_stream == null)
//                    throw new InvalidOperationException("Not connected.");

//                byte[] data = Encoding.ASCII.GetBytes(framed);
//                await _stream.WriteAsync(data, 0, data.Length);
//                await _stream.FlushAsync();

//                // Log outbound message ONCE only.
//                LogProtocol(payload, isSent: true); // raises OnMessageSent "SENT: ..."
//            }
//            finally
//            {
//                _sendLock.Release();
//            }
//        }

//        public async Task DisconnectAsync()
//        {
//            await _disconnectLock.WaitAsync();
//            try
//            {
//                _cts?.Cancel();

//                if (_listenTask != null)
//                    await Task.WhenAny(_listenTask, Task.Delay(1000));

//                CleanupResources();
//                LogInfo("Disconnected");
//            }
//            finally
//            {
//                _disconnectLock.Release();
//            }
//        }

//        private void CleanupResources()
//        {
//            try { _reader?.Dispose(); } catch { }
//            try { _stream?.Dispose(); } catch { }
//            try { _client?.Dispose(); } catch { }

//            _reader = null;
//            _stream = null;
//            _client = null;

//            try { _cts?.Dispose(); } catch { }
//            _cts = null;

//            _listenTask = null;
//        }

//        private void LogProtocol(string payload, bool isSent)
//        {
//            if (isSent)
//                OnMessageSent?.Invoke("SENT: " + payload);
//            else
//                OnMessageReceived?.Invoke("RECV: " + payload);
//        }

//        private void LogInfo(string message)
//        {
//            OnMessageReceived?.Invoke("INFO: " + message);
//        }

//        private void LogError(string message)
//        {
//            OnError?.Invoke("ERROR: " + message);
//        }

//        public void Dispose()
//        {
//            // Same style as your previous app: blocking dispose.
//            // In WinForms, you can also call await DisconnectAsync() from the form instead.
//            DisconnectAsync().Wait();
//            _sendLock?.Dispose();
//            _disconnectLock?.Dispose();
//            _cts?.Dispose();
//        }
//    }
//}
using CMC_Logiwa.Handlers;
using CMC_Logiwa.Models;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CMC_Logiwa.Services
{
    public sealed class NetworkManager : IDisposable
    {
        private const char STX = '\u0002';
        private const char ETX = '\u0003';
        private int _cisCounter = 0;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamReader _reader;

        private CancellationTokenSource _cts;
        private Task _listenTask;

        private Task _heartbeatTask;
        private Task _heartbeatMonitorTask;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _disconnectLock = new SemaphoreSlim(1, 1);

        private DateTime _lastHeartbeatReceivedUtc = DateTime.MinValue;
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(15);
        

        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sqlManager;

        public event Action<string> OnMessageReceived;
        public event Action<string> OnMessageSent;
        public event Action<string> OnError;
        public event Action OnConnected;
        public event Action OnDisconnected;
        public string ServerIp { get; }
        public int Port { get; }

        public string MachineId { get; }
        public bool IsConnected => _client?.Connected == true;
       

        public NetworkManager(string serverIp, int port, SQLManager sqlManager, string machineId)
        {
            ServerIp = serverIp;
            Port = port;

            _formatter = new FormatProcessMessages();
            _sqlManager = sqlManager ?? throw new ArgumentNullException(nameof(sqlManager));
            MachineId = machineId;
        }

        public async Task ConnectAsync()
        {
            try
            {
                _lastHeartbeatReceivedUtc = DateTime.UtcNow;
                _client = new TcpClient();
                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                await _client.ConnectAsync(ServerIp, Port);

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.ASCII, false, 1024, true);

                _cts = new CancellationTokenSource();
                _listenTask = ListenForMessagesAsync(_cts.Token);
                //_heartbeatTask = SendHeartbeatLoopAsync(_cts.Token);
                //_heartbeatMonitorTask = MonitorHeartbeatAsync(_cts.Token);

                LogInfo($"Connected to {ServerIp}:{Port}");

                OnConnected?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ListenForMessagesAsync(CancellationToken token)
        {
            var buffer = new char[4096];
            var messageBuilder = new StringBuilder();
            bool inMessage = false;

            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    int charsRead = await _reader.ReadAsync(buffer, 0, buffer.Length);
                    if (charsRead == 0)
                    {
                        await Task.Delay(100, token);
                        continue;
                    }

                    for (int i = 0; i < charsRead; i++)
                    {
                        char c = buffer[i];

                        if (c == STX)
                        {
                            inMessage = true;
                            messageBuilder.Clear();
                            continue;
                        }

                        if (c == ETX)
                        {
                            inMessage = false;

                            string message = messageBuilder.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                LogProtocol(message, isSent: false);
                                await ProcessReceivedMessage(message);
                            }

                            continue;
                        }

                        if (inMessage)
                            messageBuilder.Append(c);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    LogError($"Read error: {ex.Message}");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    LogError("Connection terminated unexpectedly");
            }
        }

        private async Task ProcessReceivedMessage(string message)
        {
            var result = _formatter.ProcessIncomingMessage(message);
            if (result == null)
                return;

            if (result.MessageType == "hbt")
            {
                _lastHeartbeatReceivedUtc = DateTime.UtcNow;
                return;
            }

            if (result.MessageType == "HBT")
            {
                _lastHeartbeatReceivedUtc = DateTime.UtcNow;
            }

            // FIX: call MessageRouter.Handle that supports HBT + ENQ
            await MessageRouter.Handle(result, this, _formatter, _sqlManager);
        }
        //private async Task SendHeartbeatLoopAsync(CancellationToken token)
        //{
        //    try
        //    {
        //        while (!token.IsCancellationRequested && IsConnected)
        //        {
        //            string heartbeatMessage = _formatter.FormatHeartbeatRequest(MachineId);
        //            await SendAsync(heartbeatMessage);

        //            await Task.Delay(_heartbeatInterval, token);
        //        }
        //    }
        //    catch (OperationCanceledException)
        //    {
        //    }
        //    catch (Exception ex)
        //    {
        //        if (!token.IsCancellationRequested)
        //        {
        //            LogError($"Heartbeat send failed: {ex.Message}");
        //            await DisconnectAsync();
        //        }
        //    }
        //}

        //private async Task MonitorHeartbeatAsync(CancellationToken token)
        //{
        //    try
        //    {
        //        while (!token.IsCancellationRequested)
        //        {
        //            await Task.Delay(1000, token);

        //            if (!IsConnected)
        //                return;

        //            var elapsed = DateTime.UtcNow - _lastHeartbeatReceivedUtc;

        //            if (elapsed > _heartbeatTimeout)
        //            {
        //                LogError("Heartbeat timeout. Remote side is not responding.");
        //                await DisconnectAsync();
        //                return;
        //            }
        //        }
        //    }
        //    catch (OperationCanceledException)
        //    {
        //    }
        //}

        public async Task SendAsync(string payload)
        {
            if (payload == null) payload = string.Empty;
            string framed = $"{STX}{payload}{ETX}";

            await _sendLock.WaitAsync();
            try
            {
                if (_stream == null)
                    throw new InvalidOperationException("Not connected.");

                byte[] data = Encoding.ASCII.GetBytes(framed);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();

                LogProtocol(payload, isSent: true);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _disconnectLock.WaitAsync();
            try
            {
                _cts?.Cancel();

                if (_listenTask != null)
                    await Task.WhenAny(_listenTask, Task.Delay(1000));

                CleanupResources();
                LogInfo("Disconnected");

                OnDisconnected?.Invoke();
            }
            finally
            {
                _disconnectLock.Release();
            }
        }

        private void CleanupResources()
        {
            try { _reader?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }

            _reader = null;
            _stream = null;
            _client = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;

            _listenTask = null;
        }

        private void LogProtocol(string payload, bool isSent)
        {
            if (isSent)
                OnMessageSent?.Invoke("SENT: " + payload);
            else
                OnMessageReceived?.Invoke("RECV: " + payload);
        }

        public void LogInfo(string message) => OnMessageReceived?.Invoke("INFO: " + message);
        public void LogError(string message) => OnError?.Invoke("ERROR: " + message);

        public void Dispose()
        {
            DisconnectAsync().Wait();
            _sendLock?.Dispose();
            _disconnectLock?.Dispose();
            _cts?.Dispose();
        }
        
            

            public string GetNextCisCounter()
            {
                int next = Interlocked.Increment(ref _cisCounter);

                // Keep it in the allowed range 1..999999
                if (next > 999999)
                {
                    Interlocked.Exchange(ref _cisCounter, 1);
                    next = 1;
                }

                return next.ToString();
            }
        
    }
}