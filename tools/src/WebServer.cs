// Minimal HTTP server for the configuration UI: static files from ui\, a JSON API and a
// server-sent-events stream for live button presses. Loopback only; requests must carry the
// right Host header and API writes need an X-Airdeck header, so web pages in the user's
// browser cannot drive it (the custom header forces a CORS preflight we never approve).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class HttpResult
{
    public int Status = 200;
    public string ContentType = "application/json; charset=utf-8";
    public byte[] Body = new byte[0];

    public static HttpResult Json(object value) { return new HttpResult { Body = Encoding.UTF8.GetBytes(global::Json.Write(value)) }; }
    public static HttpResult Error(int status, string message)
    {
        var r = Json(new Dictionary<string, object> { { "error", message } });
        r.Status = status;
        return r;
    }
}

class WebServer : IDisposable
{
    public delegate HttpResult ApiHandler(string method, string path, string body);

    readonly string uiDir;
    readonly ApiHandler api;
    TcpListener listener;
    Thread acceptThread, pushThread;
    volatile bool running;
    readonly List<Stream> sseClients = new List<Stream>();
    readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>();

    public int Port { get; private set; }
    public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

    public WebServer(string uiDir, ApiHandler api)
    {
        this.uiDir = Path.GetFullPath(uiDir);
        this.api = api;
    }

    public void Start(int preferredPort)
    {
        for (int port = preferredPort; port < preferredPort + 20; port++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Port = port;
                break;
            }
            catch (SocketException) { listener = null; }
        }
        if (listener == null) throw new InvalidOperationException("no free port for the UI server");
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" };
        acceptThread.Start();
        pushThread = new Thread(PushLoop) { IsBackground = true, Name = "sse-push" };
        pushThread.Start();
        Log.Write("UI server on {0}", Url);
    }

    // Queue a server-sent event for every connected UI.
    public void Broadcast(string evt, object data)
    {
        if (!running) return;
        outbox.Add(Encoding.UTF8.GetBytes("event: " + evt + "\ndata: " + global::Json.Write(data).Replace("\n", "") + "\n\n"));
    }

    void PushLoop()
    {
        var ping = Encoding.UTF8.GetBytes(": ping\n\n");
        while (running)
        {
            byte[] msg;
            if (!outbox.TryTake(out msg, 15000)) msg = ping;
            lock (sseClients)
            {
                foreach (var s in sseClients.ToList())
                {
                    try { s.Write(msg, 0, msg.Length); s.Flush(); }
                    catch (Exception) { sseClients.Remove(s); try { s.Dispose(); } catch { } }
                }
            }
        }
    }

    void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception) { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return b < 0 && sb.Length == 0 ? null : sb.ToString();
    }

    void Handle(TcpClient client)
    {
        bool keepOpen = false;
        var stream = client.GetStream();
        try
        {
            client.ReceiveTimeout = 10000;
            string requestLine = ReadLine(stream);
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0].ToUpperInvariant();
            string target = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(stream)))
            {
                int c = line.IndexOf(':');
                if (c > 0) headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }

            string host;
            headers.TryGetValue("Host", out host);
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) { Write(stream, HttpResult.Error(403, "bad host")); return; }

            string body = "";
            string len;
            if (headers.TryGetValue("Content-Length", out len))
            {
                int n = int.Parse(len);
                if (n > 4 * 1024 * 1024) { Write(stream, HttpResult.Error(413, "too large")); return; }
                var buf = new byte[n];
                int read = 0;
                while (read < n) { int got = stream.Read(buf, read, n - read); if (got <= 0) break; read += got; }
                body = Encoding.UTF8.GetString(buf, 0, read);
            }

            string path = Uri.UnescapeDataString(target.Split('?')[0]);
            if (path == "/api/events")
            {
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\n: hello\n\n");
                stream.Write(head, 0, head.Length);
                stream.Flush();
                client.ReceiveTimeout = 0;
                lock (sseClients) sseClients.Add(stream);
                keepOpen = true;
                return;
            }
            if (path.StartsWith("/api/"))
            {
                if (method != "GET" && !headers.ContainsKey("X-Airdeck")) { Write(stream, HttpResult.Error(403, "missing X-Airdeck header")); return; }
                Write(stream, api(method, path, body));
                return;
            }
            Write(stream, Static(path));
        }
        catch (Exception ex) { Log.Write("http error: {0}", ex.Message); }
        finally
        {
            if (!keepOpen) { try { stream.Dispose(); client.Close(); } catch { } }
        }
    }

    HttpResult Static(string path)
    {
        if (path == "/") path = "/index.html";
        string full = Path.GetFullPath(Path.Combine(uiDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(uiDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return HttpResult.Error(404, "not found");
        string ext = Path.GetExtension(full).ToLowerInvariant();
        string type = ext == ".html" ? "text/html; charset=utf-8" : ext == ".css" ? "text/css; charset=utf-8"
                    : ext == ".js" ? "text/javascript; charset=utf-8" : ext == ".svg" ? "image/svg+xml"
                    : ext == ".png" ? "image/png" : ext == ".json" ? "application/json; charset=utf-8" : "application/octet-stream";
        return new HttpResult { ContentType = type, Body = File.ReadAllBytes(full) };
    }

    static void Write(Stream s, HttpResult r)
    {
        string reason = r.Status == 200 ? "OK" : r.Status == 404 ? "Not Found" : r.Status == 403 ? "Forbidden" : "Error";
        var head = Encoding.ASCII.GetBytes(string.Format(
            "HTTP/1.1 {0} {1}\r\nContent-Type: {2}\r\nContent-Length: {3}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n",
            r.Status, reason, r.ContentType, r.Body.Length));
        s.Write(head, 0, head.Length);
        s.Write(r.Body, 0, r.Body.Length);
        s.Flush();
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
        outbox.Add(new byte[0]);
        lock (sseClients) { foreach (var s in sseClients) try { s.Dispose(); } catch { } sseClients.Clear(); }
    }
}
