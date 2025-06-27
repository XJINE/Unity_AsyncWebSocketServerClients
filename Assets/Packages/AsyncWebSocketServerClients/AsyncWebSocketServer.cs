using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace AsyncWebSocketServerClients {
public class AsyncWebSocketServer : MonoBehaviour
{
    #region Field

    public bool     debugLog     = true;
    public bool     autoStart    = true;
    public bool     guidMode     = false;
    public Encoding textEncoding = Encoding.UTF8;

    [SerializeField] private IPEndPointInfo endPoint   = new ("127.0.0.1", 8080);
    [SerializeField] private int            bufferSize = 4 * 1024; // 4096 bytes

    public UnityEvent<string>         clientConnected;
    public UnityEvent<string>         clientDisconnected;
    public UnityEvent<string, byte[]> binaryReceived;     // ClientId, Binary
    public UnityEvent<string, string> textReceived;       // ClientId, Text
    public UnityEvent<string>         messageSent;        // ClientId
    
    public UnityEvent<Exception> serverStartFailed;
    public UnityEvent<Exception> serverStopFailed;
    public UnityEvent<Exception> clientAcceptFailed;
    public UnityEvent<Exception> clientHandleFailed;
    public UnityEvent<Exception> messageSendFailed;
    public UnityEvent<Exception> messageReceiveFailed;

    private          HttpListener                  _httpListener;
    private          CancellationTokenSource       _cancellationTokenSource;
    private readonly Dictionary<string, WebSocket> _clients           = new ();
    private readonly ConcurrentQueue<Action>       _mainThreadActions = new ();

    #endregion Field

    #region Property

    public bool                                  IsRunning { get; private set; }
    public ReadOnlyDictionary<string, WebSocket> Clients   { get; private set; }

    #endregion Property

    #region Method

    private void Awake()
    {
        Clients = new ReadOnlyDictionary<string, WebSocket>(_clients);
    }

    private void Start()
    {
        if (autoStart)
        {
            StartServer();
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
        StopServer();
    }

    public void StartServer()
    {
        try
        {
            if (IsRunning)
            {
                throw new Exception("Server is already running");
            }

            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(endPoint.HttpUrl);
            _httpListener.Start();

            IsRunning = true;

            _cancellationTokenSource = new CancellationTokenSource();

            if (debugLog) { Debug.Log($"WebSocket Server started on {endPoint.HttpUrl}"); }

            _ = AcceptClient();
        }
        catch (Exception exception)
        {
            if(debugLog){ Debug.LogError($"Failed to start server: {exception.Message}");}

            _mainThreadActions.Enqueue(() => serverStartFailed.Invoke(exception));

            StopServer();
        }
    }

    public void StopServer()
    {
        try
        {
            if (!IsRunning)
            {
                throw new Exception("Server is not running");
            }

            if (debugLog) { Debug.Log("Stopping server..."); }

            _cancellationTokenSource?.Cancel();

            foreach (var clientId in _clients.Keys.ToArray())
            {
                DisconnectClient(clientId);
            }

            _httpListener?.Stop();
            _httpListener?.Close();

            if(debugLog){ Debug.Log("Server stopped"); }
        }
        catch (Exception exception)
        {
            if(debugLog){ Debug.LogError($"Error stopping server: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => serverStopFailed.Invoke(exception));
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task AcceptClient()
    {
        try
        {
            while (IsRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var context = await _httpListener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleClient(context);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            if (debugLog) { Debug.Log("HttpListener disposed"); }
        }
        catch (Exception exception)
        {
            if (debugLog) { Debug.LogError($"Accept clients error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => clientAcceptFailed.Invoke(exception));
        }
    }

    private async Task HandleClient(HttpListenerContext context)
    {
        var clientId = guidMode ? Guid.NewGuid().ToString()
                                : context.Request.RemoteEndPoint?.Address.ToString();
        try
        {
            var webSocketContext = await context.AcceptWebSocketAsync(null);
            var webSocket        = webSocketContext.WebSocket;

            _clients[clientId] = webSocket;

            if (debugLog) { Debug.Log($"Client {clientId} connected"); }

            _mainThreadActions.Enqueue(() => clientConnected.Invoke(clientId));

            await ReceiveMessage(clientId, webSocket);
        }
        catch (Exception exception)
        {
            if (debugLog) { Debug.LogError($"WebSocket client error: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => clientHandleFailed.Invoke(exception));
        }
        finally
        {
            if (_clients.ContainsKey(clientId))
            {
                DisconnectClient(clientId);
            }
        }
    }

    private async Task ReceiveMessage(string clientId, WebSocket webSocket)
    {
        var buffer = new byte[bufferSize];

        try
        {
            while (webSocket.State == WebSocketState.Open
                && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                    {
                        if(debugLog){ Debug.Log($"Client {clientId} requested close"); }

                        return; // Goto HandleWebSocketClient finally.
                    }
                    case WebSocketMessageType.Binary:
                    {
                        if(debugLog){ Debug.Log($"Binary message from {clientId}"); }

                        _mainThreadActions.Enqueue(() => binaryReceived.Invoke(clientId, buffer));

                        break;
                    }
                    case WebSocketMessageType.Text:
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        if(debugLog){ Debug.Log($"Message from {clientId}: {message}"); }

                        _mainThreadActions.Enqueue(() => textReceived.Invoke(clientId, message));

                        break;
                    }
                    default: continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if(debugLog) { Debug.Log($"Client {clientId} message handling cancelled"); }
        }
        catch (Exception exception)
        {
            if(debugLog) { Debug.LogError($"Message handling error for {clientId}: {exception.Message}"); }

            _mainThreadActions.Enqueue(() => messageReceiveFailed.Invoke(exception));
        }
    }

    public void DisconnectClient(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var webSocket))
        {
            return;
        }

        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server disconnect", CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            if(debugLog){ Debug.LogError($"Error disconnecting client {clientId}: {exception.Message}"); };
        }
        finally
        {
            _clients.Remove(clientId);
            webSocket?.Dispose();

            if(debugLog){ Debug.Log($"Client {clientId} disconnected"); }

            _mainThreadActions.Enqueue(() => clientDisconnected.Invoke(clientId));
        }
    }

    public void SendMessage(string clientId, string text)
    {
        SendMessage(clientId, textEncoding.GetBytes(text), WebSocketMessageType.Text);
    }

    public void SendMessage(string clientId, byte[] binary)
    {
        SendMessage(clientId, binary, WebSocketMessageType.Binary);
    }

    private async void SendMessage(string clientId, byte[] message, WebSocketMessageType messageType)
    {
        try
        {
            if (!_clients.TryGetValue(clientId, out var webSocket))
            {
                throw new Exception($"Client {clientId} not found");
            }

            if (webSocket.State != WebSocketState.Open)
            {
                throw new Exception($"Client {clientId} is not connected");
            }

            await webSocket.SendAsync(new ArraySegment<byte>(message), messageType, true, CancellationToken.None);

            if (debugLog) { Debug.Log($"Message sent to {clientId}"); }

            _mainThreadActions.Enqueue(() => messageSent.Invoke(clientId));
        }
        catch (Exception exception)
        {
            if(debugLog) { Debug.LogError($"Send error to {clientId}"); }

            _mainThreadActions.Enqueue(() => messageSendFailed.Invoke(exception));

            DisconnectClient(clientId);
        }
    }

    public void SendMessageToAll(string text)
    {
        foreach (var clientId in _clients.Keys.ToArray())
        {
            SendMessage(clientId, text);
        }
    }

    public void SendMessageToAll(byte[] binary)
    {
        foreach (var clientId in _clients.Keys.ToArray())
        {
            SendMessage(clientId, binary);
        }
    }

    #endregion Method
}}