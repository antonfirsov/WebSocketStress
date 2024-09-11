﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace WebSocketStress;

internal class StressServer
{
    private readonly Configuration _config;
    private readonly WebSocketCreationOptions _options;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    public IPEndPoint ServerEndpoint => (IPEndPoint)_listener.LocalEndPoint!;

    private Task? _serverTask;
    private Socket _listener;

    public StressServer(Configuration config)
    {
        _config = config;
        _options = new WebSocketCreationOptions()
        {
            IsServer = true,
            SubProtocol = null,
            KeepAliveInterval = config.KeepAliveInterval
        };

        if (config.KeepAliveTimeout is TimeSpan timeout && typeof(WebSocketCreationOptions).GetProperty("KeepAliveTimeout") is PropertyInfo keepAliveTimeoutProperty)
        {
            keepAliveTimeoutProperty.SetValue(_options, timeout);
        }

        IPEndPoint ep = config.ServerEndpoint;
        _listener = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(ep);
    }

    public Task Start()
    {
        _serverTask = Task.Run(StartCore);
        return _serverTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    private async Task StartCore()
    {
        _listener.Listen();

        IEnumerable<Task> workers = Enumerable.Range(1, 2 * _config.MaxConnections).Select(_ => RunSingleWorker());
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            _listener.Dispose();
        }

        async Task RunSingleWorker()
        {
            while (!_cts.IsCancellationRequested)
            {
                Log? log = null;
                try
                {
                    using Socket handlerSocket = await _listener.AcceptAsync(_cts.Token);
                    using WebSocket serverWebSocket = WebSocket.CreateFromStream(new NetworkStream(handlerSocket, ownsSocket: true), _options);
                    log = await HandleConnection(serverWebSocket, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    if (_config.LogServer)
                    {
                        lock (Console.Out)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkRed;
                            if (e is WebSocketException wex)
                            {
                                Console.WriteLine($"Server: unhandled WebSocketException({wex.WebSocketErrorCode}): {e}");
                            }
                            else
                            {
                                Console.WriteLine($"Server: unhandled exception: {e}");
                            }
                            Console.WriteLine();
                            Console.ResetColor();
                        }
                    }
                }
                log?.WriteLine("HandleConnection DONE.");
            }
        }
    }

    private static readonly byte[] s_endLine = [(byte)'\n'];

    private async Task<Log> HandleConnection(WebSocket ws, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        DateTime lastReadTime = DateTime.Now;

        byte[] khem = new byte[8];
        if ((await ws.ReceiveAsync(khem, token)).MessageType != WebSocketMessageType.Binary)
        {
            throw new Exception("Server failed receiving connectionId.");
        }
        Log log = new Log("Server", BitConverter.ToInt64(khem));

        DataSegmentSerializer serializer = new DataSegmentSerializer(log);
        InputProcessor inputProcessor = new InputProcessor(ws, log);

        _ = Task.Run(Monitor);

        try
        {
            await inputProcessor.RunAsync(Callback, cts.Token);
        }
        catch (OperationCanceledException) when (inputProcessor.Aborted)
        {
        }

        if (!inputProcessor.Aborted)
        {
            log.WriteLine("inputProcessor.RunAsync DONE.  CloseOutputAsync...");
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token);
            }
            catch (WebSocketException e) when (e.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely || ws.State == WebSocketState.Aborted)
            {
            }
            
            log.WriteLine("CloseOutputAsync DONE.");
        }

        return log;

        async Task<bool> Callback(ReadOnlySequence<byte> buffer)
        {
            lastReadTime = DateTime.Now;

            DataSegment? chunk = null;
            try
            {
                if (buffer.Length == 0)
                {
                    log.WriteLine("buffer.Length == 0, server terminating???");
                    // got an empty line, client is closing the connection
                    // echo back the empty line and return 'true' to signal completion.
                    await ws.WriteAsync(s_endLine, token);
                    return true;
                }

                chunk = serializer.Deserialize(buffer);
                await serializer.SerializeAsync(ws, chunk.Value, token: token);
                await ws.WriteAsync(s_endLine, token);
                return false;
            }
            catch (WebSocketException e)
            {
                if (ws.State == WebSocketState.Aborted || e.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    return true;
                }
                log.WriteLine($"Unexpected WebSocketException? ws.State: {ws.State}, e.WebSocketErrorCode: {e.WebSocketErrorCode}");
                throw;
            }
            catch (DataMismatchException e)
            {
                if (_config.LogServer)
                {
                    lock (Console.Out)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Server: {e.Message}");
                        Console.ResetColor();;
                    }
                }
                return true;
            }
            finally
            {
                chunk?.Return();
            }
        }

        async Task Monitor()
        {
            do
            {
                await Task.Delay(1000);

                if (DateTime.Now - lastReadTime >= TimeSpan.FromSeconds(10))
                {
                    cts.Cancel();
                }

            } while (!cts.IsCancellationRequested);
        }
    }
}