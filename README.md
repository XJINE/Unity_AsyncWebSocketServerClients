# Unity_AsyncWebSocketServerClients

This project has failed due to an issu with Unity.

https://discussions.unity.com/t/websocket-server-in-standalone-build/832108

```csharp
var context = await _httpListener.GetContextAsync();

if (context.Request.IsWebSocketRequest)
{
    _ = HandleClient(context);
}
```

``HttpListenerRequest.IsWebSocketRequest`` always returns `false`.

```csharp
WebSocketProtocolComponent.s_DllFileName = Path.Combine(Environment.SystemDirectory, "websocket.dll");
WebSocketProtocolComponent.s_WebSocketDllHandle = SafeLoadLibrary.LoadLibraryEx(WebSocketProtocolComponent.s_DllFileName);
if (WebSocketProtocolComponent.s_WebSocketDllHandle.IsInvalid)
return;
```

This may be caused by the internal static class ``System.Net.WebSockets.WebSocketProtocolComponent``.
This code or something else is not working correctly, even though there is `websocket.dll` and the correct OS settings.

Because it worked correctly in a pure .NET/C# project x(
