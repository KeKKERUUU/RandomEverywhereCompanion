// HPG Mission Companion - WebSocket server compatible with the HPG voice server protocol (localhost:5997).
//   {"Text":"...", "Interrupt":true}   speak (tags allowed), Interrupt cancels what is playing and queued
//   {"FileName":"x.wav"}               play audio\x.wav
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HPGMissionCompanion
{
    internal sealed class Server
    {
        readonly List<TcpClient> clients = new List<TcpClient>();
        TcpListener listener;
        volatile bool stopping;
        public bool AllowFileMessages = true;

        // listenAll false: 127.0.0.1 only. The mission connects to localhost, and a program that listens on the loopback address only does not make
        // Windows Firewall ask to open the port (the question that came at every start with 0.0.0.0, even though the mission works without the answer)
        public void Start(int port, bool listenAll)
        {
            stopping = false;
            listener = new TcpListener(listenAll ? IPAddress.Any : IPAddress.Loopback, port);
            listener.Start();
            Ctx.Log("[SERVER] Started on " + (listenAll ? "0.0.0.0" : "127.0.0.1") + ":" + port);
            Thread accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch (Exception) { }
            lock (clients)
            {
                foreach (TcpClient c in clients) { try { c.Close(); } catch (Exception) { } }
                clients.Clear();
            }
            Ctx.Player.CancelAll();
            Ctx.Log("[SERVER] Stopped");
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (clients) { clients.Add(client); }
                    Ctx.Log("[ACCEPT] client " + client.Client.RemoteEndPoint);
                    Thread t = new Thread(delegate () { ClientLoop(client); });
                    t.IsBackground = true;
                    t.Start();
                }
                catch (Exception)
                {
                    if (stopping) break;
                }
            }
        }

        void ClientLoop(TcpClient client)
        {
            string remote = "";
            try { remote = client.Client.RemoteEndPoint.ToString(); } catch (Exception) { }
            try
            {
                NetworkStream stream = client.GetStream();
                if (!Handshake(stream)) return;
                List<byte> fragments = new List<byte>();
                while (!stopping)
                {
                    int b0 = stream.ReadByte();
                    int b1 = stream.ReadByte();
                    if (b0 < 0 || b1 < 0) break;
                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0f;
                    bool masked = (b1 & 0x80) != 0;
                    ulong length = (ulong)(b1 & 0x7f);
                    if (length == 126) { byte[] e = ReadExact(stream, 2); length = (ulong)((e[0] << 8) | e[1]); }
                    else if (length == 127) { byte[] e = ReadExact(stream, 8); length = 0; foreach (byte x in e) length = (length << 8) | x; }
                    if (length > 1024 * 1024) { Ctx.Log("[ERROR] message too large"); break; }
                    byte[] mask = masked ? ReadExact(stream, 4) : null;
                    byte[] payload = ReadExact(stream, (int)length);
                    if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                    if (opcode == 8) { SendFrame(stream, 8, payload); break; }
                    if (opcode == 9) { SendFrame(stream, 10, payload); continue; }
                    if (opcode == 10) continue;
                    if (opcode == 1 || opcode == 0)
                    {
                        fragments.AddRange(payload);
                        if (fin)
                        {
                            string text = Encoding.UTF8.GetString(fragments.ToArray());
                            fragments.Clear();
                            if (text.Length > 0) Handle(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopping) Ctx.Log("[ERROR] Client: " + ex.Message);
            }
            finally
            {
                Ctx.Log("[DISCONNECT] client " + remote);
                lock (clients) { clients.Remove(client); }
                try { client.Close(); } catch (Exception) { }
            }
        }

        bool Handshake(NetworkStream stream)
        {
            List<byte> head = new List<byte>();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return false;
                head.Add((byte)b);
                int n = head.Count;
                if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
                if (n > 16384) return false;
            }
            string request = Encoding.UTF8.GetString(head.ToArray());
            if (!Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase)) return false;
            Match key = Regex.Match(request, "Sec-WebSocket-Key: (.*)", RegexOptions.IgnoreCase);
            if (!key.Success) return false;
            string accept = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key.Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            stream.Write(response, 0, response.Length);
            return true;
        }

        static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n <= 0) throw new IOException("connection closed");
                done += n;
            }
            return buffer;
        }

        static void SendFrame(NetworkStream stream, int opcode, byte[] payload)
        {
            int n = Math.Min(payload.Length, 125);
            byte[] frame = new byte[2 + n];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)n;
            Array.Copy(payload, 0, frame, 2, n);
            stream.Write(frame, 0, frame.Length);
        }

        static string Field(Dictionary<string, object> map, string name)
        {
            foreach (KeyValuePair<string, object> kv in map)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value != null) return Convert.ToString(kv.Value);
            return null;
        }

        static bool Flag(Dictionary<string, object> map, string name)
        {
            string v = Field(map, name);
            if (v == null) return false;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        void Handle(string raw)
        {
            Handle(raw, (text, interrupt) => Ctx.Player.EnqueueText(text, interrupt),
                (path, interrupt) => Ctx.Player.EnqueueFile(path, interrupt), () => Ctx.Player.CancelAll());
        }

        // Exercise the production protocol dispatcher without starting a player or listener.
        internal void Handle(string raw, Action<string, bool> enqueueText,
            Action<string, bool> enqueueFile, Action cancel)
        {
            Ctx.Log("[RAW] " + raw);
            try
            {
                Dictionary<string, object> map = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                if (map == null) { Ctx.Log("[ERROR] MSG NOT IMPLEMENTED: " + raw); return; }
                bool interrupt = Flag(map, "Interrupt");
                string speech = Field(map, "Text");
                string file = Field(map, "FileName");
                if (!AllowFileMessages && !string.IsNullOrEmpty(file))
                {
                    Ctx.Log("[MODE] A recorded-file request was ignored in synthetic mode.");
                    return;
                }
                if (interrupt) Ctx.Log("[INTERRUPT]");
                if (!string.IsNullOrWhiteSpace(speech))
                {
                    enqueueText(speech, interrupt);
                }
                else if (!string.IsNullOrWhiteSpace(file))
                {
                    string name = file.Replace("\\", "").Replace("..", "");
                    string path = Path.GetFullPath(Path.Combine(Ctx.AudioDir, name));
                    if (!AppSounds.Provide(path)) { Ctx.Log("[ERROR] File does not exist: " + name); return; }
                    Ctx.Log("[PLAY] " + name);
                    enqueueFile(path, interrupt);
                }
                else if (interrupt)
                {
                    cancel();
                }
                else
                {
                    Ctx.Log("[ERROR] MSG NOT IMPLEMENTED: " + raw);
                }
            }
            catch (Exception ex)
            {
                Ctx.Log("[ERROR] can't read message: " + ex.Message + "\n" + raw);
            }
        }
    }
}
