using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ScreenTimeGuard
{
    /// <summary>
    /// Pembingkaian pesan: 4 byte panjang (little-endian) diikuti payload UTF-8.
    /// JSON yang dipakai multi-baris, jadi framing per baris tidak bisa dipakai.
    /// </summary>
    internal static class Frame
    {
        const int MaxMessage = 4 * 1024 * 1024;

        public static void Write(Stream s, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] head = BitConverter.GetBytes(body.Length);
            s.Write(head, 0, 4);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        public static string Read(Stream s)
        {
            byte[] head = ReadExactly(s, 4);
            if (head == null) return null;
            int len = BitConverter.ToInt32(head, 0);
            if (len < 0 || len > MaxMessage) throw new IOException("Panjang pesan tidak valid.");
            if (len == 0) return "";
            byte[] body = ReadExactly(s, len);
            if (body == null) return null;
            return Encoding.UTF8.GetString(body);
        }

        static byte[] ReadExactly(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return null;
                off += n;
            }
            return buf;
        }
    }

    public delegate IpcResponse RequestHandler(IpcRequest request);

    public class IpcServer
    {
        const int MaxInstances = 8;

        readonly RequestHandler _handler;
        Thread _thread;
        volatile bool _stop;
        int _acceptFailures;

        public IpcServer(RequestHandler handler)
        {
            _handler = handler;
        }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ipc-server";
            _thread.Start();
        }

        public void Stop() { _stop = true; }

        static PipeSecurity BuildSecurity()
        {
            PipeSecurity ps = new PipeSecurity();

            // Pemilik proses harus punya CreateNewInstance, kalau tidak instance pipa
            // KEDUA dan seterusnya gagal dibuat ("Access to the path is denied") begitu
            // ada satu koneksi yang masih dilayani.
            try
            {
                using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                    ps.AddAccessRule(new PipeAccessRule(
                        me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            catch { }

            // Semua pengguna interaktif boleh mengirim perintah; otorisasi sebenarnya
            // dilakukan lewat password di dalam pesan. Sengaja TIDAK diberi
            // CreateNewInstance supaya anak tidak bisa memalsukan server pipa ini.
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        void Loop()
        {
            while (!_stop)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        Paths.PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                        PipeOptions.None, 8192, 8192, BuildSecurity());
                    server.WaitForConnection();
                    _acceptFailures = 0;

                    // Dilayani di thread lain supaya perintah yang lama (mengunduh
                    // pembaruan) tidak memblokir perintah lain yang masuk.
                    NamedPipeServerStream accepted = server;
                    server = null;
                    ThreadPool.QueueUserWorkItem(delegate { Serve(accepted); });
                }
                catch (Exception ex)
                {
                    // Dibatasi supaya kegagalan beruntun tidak membanjiri log.txt.
                    _acceptFailures++;
                    if (!_stop && (_acceptFailures == 1 || _acceptFailures % 60 == 0))
                        Log.Write("IPC server error (kegagalan ke-" + _acceptFailures + "): " + ex.Message);
                    Thread.Sleep(_acceptFailures > 5 ? 5000 : 500);
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Dispose(); }
                        catch { }
                    }
                }
            }
        }

        void Serve(NamedPipeServerStream server)
        {
            try
            {
                string raw = Frame.Read(server);
                if (raw == null) return;

                IpcResponse response;
                try
                {
                    IpcRequest req = Json.Read<IpcRequest>(raw);
                    response = _handler(req);
                }
                catch (Exception ex)
                {
                    response = new IpcResponse();
                    response.Ok = false;
                    response.Error = "Permintaan tidak valid: " + ex.Message;
                }

                if (response != null)
                {
                    Frame.Write(server, Json.Write(response));
                    try { server.WaitForPipeDrain(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (!_stop) Log.Write("IPC serve error: " + ex.Message);
            }
            finally
            {
                try { if (server.IsConnected) server.Disconnect(); }
                catch { }
                try { server.Dispose(); }
                catch { }
            }
        }
    }

    public static class IpcClient
    {
        public static bool AgentAvailable()
        {
            IpcRequest req = new IpcRequest();
            req.Command = "PING";
            IpcResponse r = Send(req, 600);
            return r != null && r.Ok;
        }

        public static IpcResponse Send(IpcRequest request) { return Send(request, 4000); }

        public static IpcResponse Send(IpcRequest request, int timeoutMs)
        {
            try
            {
                using (NamedPipeClientStream client =
                    new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut))
                {
                    client.Connect(timeoutMs);
                    Frame.Write(client, Json.Write(request));
                    string raw = Frame.Read(client);
                    if (raw == null) return null;
                    return Json.Read<IpcResponse>(raw);
                }
            }
            catch (TimeoutException) { return null; }
            catch (Exception ex)
            {
                Log.Write("IPC client error (" + request.Command + "): " + ex.Message);
                return null;
            }
        }

        public static IpcResponse Send(string command, string password, string arg1, string arg2, string payload)
        {
            IpcRequest req = new IpcRequest();
            req.Command = command;
            req.Password = password == null ? "" : password;
            req.Arg1 = arg1 == null ? "" : arg1;
            req.Arg2 = arg2 == null ? "" : arg2;
            req.Payload = payload == null ? "" : payload;
            return Send(req);
        }
    }
}
