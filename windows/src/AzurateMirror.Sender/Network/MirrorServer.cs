using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AzurateMirror.Sender.Capture;

namespace AzurateMirror.Sender.Network;

/// <summary>
/// Phase-2 bare server: listens on a LAN-local TCP port, accepts one client at a time,
/// sends VIDEO_CONFIG once connected and streams VIDEO_FRAME for every encoded access unit.
/// No Stop/Pause/Refresh yet (that lands in the control-channel phase) - Dispose() tears
/// everything down.
/// </summary>
public sealed class MirrorServer : IDisposable
{
    public event Action<string>? Log;
    /// <summary>Fired once a new client's HELLO/HELLO_ACK handshake completes, with the mode it
    /// requested (0=mirror, 1=extend) - subscribers should apply that mode and resend
    /// VIDEO_CONFIG, since a fresh client has no SPS/PPS/decoder state yet.</summary>
    public event Action<CaptureMode>? ClientHandshakeCompleted;
    /// <summary>Fired when the connected client sends CONTROL{"cmd":"request_keyframe"} - typically
    /// right after MirrorActivity's Surface is recreated (app resumed from background) and needs
    /// a fresh IDR immediately rather than waiting for the encoder's normal keyframe interval.</summary>
    public event Action? KeyframeRequested;
    /// <summary>Fired when a PONG comes back for a PING this server sent, with the measured
    /// round-trip time in milliseconds - drives the live latency readout in the dashboard.</summary>
    public event Action<double>? LatencyMeasured;
    /// <summary>Fired for each touchpad event (Task #14): action is "down"/"move"/"up", xNorm/yNorm
    /// are [0,1] normalized against the client's video content rect (letterbox already excluded
    /// on the Android side) - the subscriber maps these onto the virtual display's real pixel
    /// bounds and injects the actual mouse move/click.</summary>
    public event Action<string, double, double>? TouchReceived;

    private readonly TcpListener _listener;
    private readonly int _port;
    private Thread? _acceptThread;
    private volatile bool _running;
    private TcpClient? _client;
    private readonly object _clientLock = new();
    private readonly System.Diagnostics.Stopwatch _pingStopwatch = new();
    private volatile bool _pingInFlight;

    public MirrorServer(int port)
    {
        _port = port;
        // Binds all local interfaces (LAN + loopback for the adb-reverse USB path) - never a
        // public/internet-facing bind. See docs/PRIVACY_CHECKLIST.md.
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _running = true;
        _listener.Start();
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MirrorServer-Accept" };
        _acceptThread.Start();
        Log?.Invoke($"Listening on 0.0.0.0:{_port}");
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break; // listener stopped
            }

            lock (_clientLock)
            {
                _client?.Close();
                _client = client;
            }
            Log?.Invoke($"Client connected: {client.Client.RemoteEndPoint}");

            try
            {
                var stream = client.GetStream();
                var header = Protocol.ReadHeader(stream);
                var payload = header.Length > 0 ? Protocol.ReadExact(stream, header.Length) : Array.Empty<byte>();
                if (header.Type == Protocol.Hello)
                {
                    CaptureMode mode = payload.Length > 1 && payload[1] == (byte)CaptureMode.Mirror
                        ? CaptureMode.Mirror
                        : CaptureMode.Extend; // unknown/missing -> default to Extend per spec
                    string deviceName = payload.Length > 2 ? System.Text.Encoding.UTF8.GetString(payload, 2, payload.Length - 2) : "(unknown)";
                    Log?.Invoke($"HELLO from '{deviceName}' mode={mode}");
                    SendHelloAck(stream, accepted: true);
                    ClientHandshakeCompleted?.Invoke(mode);

                    ClientReadLoop(client, stream);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Handshake failed: {ex.Message}");
            }
        }
    }

    /// <summary>Keeps reading client->server messages (CONTROL/STATS_CLIENT/PING) for as long as
    /// this client stays connected. Runs on the accept thread - fine since we only ever have one
    /// client at a time in this phase; a slow/hung client just blocks accepting the next one,
    /// which self-resolves once that client disconnects or a new one displaces it via _client.</summary>
    private void ClientReadLoop(TcpClient client, System.IO.Stream stream)
    {
        try
        {
            while (_running && client.Connected)
            {
                var header = Protocol.ReadHeader(stream);
                var payload = header.Length > 0 ? Protocol.ReadExact(stream, header.Length) : Array.Empty<byte>();

                switch (header.Type)
                {
                    case Protocol.Control:
                        string json = System.Text.Encoding.UTF8.GetString(payload);
                        Log?.Invoke($"CONTROL received: {json}");
                        if (json.Contains("request_keyframe"))
                        {
                            KeyframeRequested?.Invoke();
                        }
                        else if (json.Contains("\"touch\""))
                        {
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(json);
                                var root = doc.RootElement;
                                string action = root.GetProperty("action").GetString() ?? "";
                                double xNorm = root.GetProperty("xNorm").GetDouble();
                                double yNorm = root.GetProperty("yNorm").GetDouble();
                                TouchReceived?.Invoke(action, xNorm, yNorm);
                            }
                            catch (Exception ex) { Log?.Invoke($"Bad touch CONTROL payload: {ex.Message}"); }
                        }
                        break;
                    case Protocol.Ping:
                        lock (_clientLock)
                        {
                            try { Protocol.WriteFrame(stream, Protocol.Pong, Array.Empty<byte>(), 0); }
                            catch { /* connection likely gone; next read will throw and exit the loop */ }
                        }
                        break;
                    case Protocol.Pong:
                        // Echo of a PING *we* sent (see SendPing) - the elapsed time since then is
                        // this connection's round-trip latency right now.
                        if (_pingInFlight)
                        {
                            _pingInFlight = false;
                            LatencyMeasured?.Invoke(_pingStopwatch.Elapsed.TotalMilliseconds);
                        }
                        break;
                    // STATS_CLIENT: not consumed yet (HUD lands in a later task) - just drained
                    // so it doesn't desync the stream.
                }
            }
        }
        catch (Exception)
        {
            // Client disconnected or errored - fall through, AcceptLoop's outer while(_running)
            // will accept whatever connects next.
        }
    }

    private static void SendHelloAck(System.IO.Stream stream, bool accepted)
    {
        var payload = new byte[5];
        payload[0] = (byte)(accepted ? 1 : 0);
        Protocol.WriteUInt32BE(payload, 1, 1); // sessionId, hardcoded to 1 for Phase 2
        Protocol.WriteFrame(stream, Protocol.HelloAck, payload, 0);
    }

    /// <summary>Sends VIDEO_CONFIG with the encoder's SPS/PPS. Call once after the encoder has produced its first keyframe.</summary>
    public void SendVideoConfig(int width, int height, byte fps, byte[] sps, byte[] pps, ulong timestampMs)
    {
        var stream = GetActiveStream();
        if (stream is null) return;

        var payload = new byte[4 + 4 + 1 + 1 + 2 + sps.Length + 2 + pps.Length];
        int o = 0;
        Protocol.WriteUInt32BE(payload, o, (uint)width); o += 4;
        Protocol.WriteUInt32BE(payload, o, (uint)height); o += 4;
        payload[o++] = fps;
        payload[o++] = 1; // codecId 1 = H264
        Protocol.WriteUInt16BE(payload, o, (ushort)sps.Length); o += 2;
        Buffer.BlockCopy(sps, 0, payload, o, sps.Length); o += sps.Length;
        Protocol.WriteUInt16BE(payload, o, (ushort)pps.Length); o += 2;
        Buffer.BlockCopy(pps, 0, payload, o, pps.Length);

        lock (_clientLock)
        {
            try { Protocol.WriteFrame(stream, Protocol.VideoConfig, payload, timestampMs); }
            catch (Exception ex) { Log?.Invoke($"SendVideoConfig failed: {ex.Message}"); }
        }
    }

    public void SendVideoFrame(EncodedAccessUnit unit)
    {
        var stream = GetActiveStream();
        if (stream is null) return;

        var payload = new byte[1 + unit.AnnexB.Length];
        payload[0] = (byte)(unit.IsKeyFrame ? 1 : 0);
        Buffer.BlockCopy(unit.AnnexB, 0, payload, 1, unit.AnnexB.Length);

        lock (_clientLock)
        {
            try { Protocol.WriteFrame(stream, Protocol.VideoFrame, payload, unit.TimestampMs); }
            catch (Exception ex) { Log?.Invoke($"SendVideoFrame failed: {ex.Message}"); }
        }
    }

    /// <summary>Sends a PING if the previous one already got its PONG (or none is in flight yet) -
    /// call on a fixed interval (~1/sec) from the pipeline loop. Silently no-ops while a PING is
    /// still outstanding so a slow/dead link doesn't pile up unanswered PINGs.</summary>
    public void SendPing()
    {
        if (_pingInFlight)
        {
            Log?.Invoke($"SendPing: skipped, already in flight for {_pingStopwatch.ElapsedMilliseconds}ms");
            return;
        }
        var stream = GetActiveStream();
        if (stream is null) return;

        lock (_clientLock)
        {
            try
            {
                _pingStopwatch.Restart();
                _pingInFlight = true;
                Protocol.WriteFrame(stream, Protocol.Ping, Array.Empty<byte>(), 0);
            }
            catch (Exception ex) { _pingInFlight = false; Log?.Invoke($"SendPing failed: {ex.Message}"); }
        }
    }

    public bool HasClient
    {
        get { lock (_clientLock) return _client?.Connected == true; }
    }

    /// <summary>The connected client's remote IP, or null if nobody's connected. "127.0.0.1"
    /// means USB (via adb reverse); anything else means WiFi/LAN - used to auto-detect which
    /// transport is actually active rather than trusting a UI toggle that can't control it (the
    /// transport is chosen on the Android side by which host address it dials).</summary>
    public string? ClientRemoteAddress
    {
        get
        {
            lock (_clientLock)
            {
                if (_client is not { Connected: true }) return null;
                return (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            }
        }
    }

    private System.IO.Stream? GetActiveStream()
    {
        lock (_clientLock)
        {
            if (_client is { Connected: true }) return _client.GetStream();
            return null;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        lock (_clientLock) { _client?.Close(); _client = null; }
    }
}
