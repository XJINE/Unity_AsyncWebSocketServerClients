using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace AsyncWebSocketServerClients {
public class AsyncWebSocketClient : MonoBehaviour
{
    #region Field

    public bool     debugLog;
    public bool     autoConnect  = true;
    public Encoding textEncoding = Encoding.UTF8;

    [SerializeField] private IPEndPointInfo server;
    [SerializeField] private int            bufferSize = 4 * 1024; // 4096 bytes

    public UnityEvent         connected;
    public UnityEvent         disconnected;
    public UnityEvent<byte[]> binaryReceived;
    public UnityEvent<string> textReceived;
    public UnityEvent         messageSent;

    public UnityEvent<Exception> connectionFailed;
    public UnityEvent<Exception> disconnectionFailed;
    public UnityEvent<Exception> cleanupFailed;
    public UnityEvent<Exception> messageSendFailed;
    public UnityEvent<Exception> messageReceiveFailed;

    private          ClientWebSocket         _webSocket;
    private          CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new ();

    #endregion

    #region Property

    public bool IsConnected { get; private set; }

    #endregion Property

    #region Method

    private void Start()
    {
        if (autoConnect)
        {
            ConnectToServer();
        }
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    private void OnDestroy()
    {
        DisconnectFromServer();
    }

    public async void ConnectToServer()
    {
        try
        {
            var webSocketUri = server.WsUrl;

            if (IsConnected)
            {
                throw new Exception($"Already connected to server: {webSocketUri}");
            }

            _webSocket               = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            if (debugLog) { Debug.Log($"Connecting to server: {webSocketUri}"); }

            await _webSocket.ConnectAsync(new Uri(webSocketUri), _cancellationTokenSource.Token);

            IsConnected = true;

            _mainThreadActions.Enqueue(() => connected.Invoke());

            if (debugLog) { Debug.Log($"Connected to server: {webSocketUri}"); }

            _ = ReceiveMessage();
        }
        catch (Exception exception)
        {
            if (debugLog) { Debug.LogError($"Connection failed: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => connectionFailed.Invoke(exception));

            CleanupConnection();
        }
        finally
        {
            CleanupConnection();
        }
    }

    public async void DisconnectFromServer()
    {
        if (!IsConnected)
        {
            if (debugLog) { Debug.LogWarning("Not connected to server"); }
            return;
        }
        try
        {
            _cancellationTokenSource?.Cancel();
            
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }

            if (debugLog) { Debug.Log("Disconnected from server"); }
        }
        catch (Exception exception)
        {
            if (debugLog) { Debug.LogError($"Disconnect error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => disconnectionFailed.Invoke(exception));
        }
        finally
        {
            CleanupConnection();
        }
    }

    private void CleanupConnection()
    {
        IsConnected = false;

        try
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            
            _webSocket?.Dispose();
            _webSocket = null;

            _mainThreadActions.Enqueue(() => disconnected.Invoke());
        }
        catch (Exception exception)
        {
            if (debugLog) { Debug.LogError($"Cleanup error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => cleanupFailed.Invoke(exception));
        }
    }

    private async Task ReceiveMessage()
    {
        var buffer = new byte[bufferSize];

        try
        {
            while (IsConnected
                   && _webSocket?.State == WebSocketState.Open
                   && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                    {
                        if (debugLog) { Debug.Log("Server closed connection"); }

                        return; // Exit the loop and cleanup connection.
                    }
                    case WebSocketMessageType.Binary:
                    {
                        if (debugLog) { Debug.Log("Binary received"); }

                        _mainThreadActions.Enqueue(() => binaryReceived.Invoke(buffer));

                        break;
                    }
                    case WebSocketMessageType.Text:
                    {
                        var message = textEncoding.GetString(buffer, 0, result.Count);

                        if (debugLog) { Debug.Log($"Text received: {message}"); }

                        _mainThreadActions.Enqueue(() => textReceived.Invoke(message));

                        break;
                    }
                    default: continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if(debugLog){ Debug.Log("Receive loop cancelled"); }
        }
        catch (Exception exception)
        {
            if(debugLog){ Debug.LogError($"Receive error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => messageReceiveFailed.Invoke(exception));
        }
    }

    public new void SendMessage(string text)
    {
        SendMessage(textEncoding.GetBytes(text), WebSocketMessageType.Text);
    }

    public void SendMessage(byte[] data)
    {
        SendMessage(data, WebSocketMessageType.Binary);
    }

    private async void SendMessage(byte[] data, WebSocketMessageType type)
    {
        try
        {
            if (!IsConnected || _webSocket?.State != WebSocketState.Open)
            {
                if(debugLog){ Debug.LogWarning("Cannot send message: not connected"); }

                throw new Exception("Cannot send message: not connected");
            }

            await _webSocket.SendAsync(new ArraySegment<byte>(data), type, true, _cancellationTokenSource.Token);

            if(debugLog){ Debug.Log($"Message Sent"); }

            _mainThreadActions.Enqueue(() => messageSent.Invoke());
        }
        catch (Exception exception)
        {
            if(debugLog){ Debug.LogError($"Message Send error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => messageSendFailed.Invoke(exception));
        }
    }

    #endregion Method
}}