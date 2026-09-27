using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 给 HINATA Go 的 Sender 模式用的本地接收器。
    /// HINATA Go Instance 填 http://127.0.0.1:7666/ (或本机局域网IP:7666) 后,
    /// 刷卡会把 { action: "SET_CARD_V2", body: { card: ... } } POST 进来。
    /// 我们只关心"有卡到达", 收到后完成当前假电子支付。
    /// </summary>
    internal static class ExternalCardReceiver
    {
        private const int Port = 7666;

        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static int _pending;

        internal static void Start()
        {
            try
            {
                HttpListener listener = new HttpListener();
                // 只监听本机回环地址, 不走局域网/互联网。
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");

                listener.Start();
                _listener = listener;
                _running = true;

                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();

                ModLog.Info("HINATA Go 本机收卡服务已启动: http://127.0.0.1:" + Port + "/");
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod] HINATA Go 收卡服务启动失败: " + e.Message);
            }
        }

        internal static void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch
            {
            }
        }

        internal static void SignalCardFromHid()
        {
            Interlocked.Exchange(ref _pending, 1);
        }

        internal static void Poll()
        {
            if (Interlocked.Exchange(ref _pending, 0) == 1)
            {
                if (FakeEMoney.IsWaitingAime)
                {
                    HinataHidReader.Stop();
                    AimeReaderPatch.StopScan();
                    FakeEMoney.OnExternalCardRead();
                }
            }
        }

        private static void Loop()
        {
            while (_running && _listener != null)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running)
                    {
                        return;
                    }
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    Handle(ctx);
                }
                catch
                {
                    try
                    {
                        Reply(ctx, 500, "{\"ok\":false,\"error\":\"internal_error\"}");
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                Reply(ctx, 404, "{\"ok\":false,\"error\":\"not_found\"}");
                return;
            }

            string body = ReadBody(ctx);
            if (string.IsNullOrEmpty(body))
            {
                Reply(ctx, 400, "{\"ok\":false,\"error\":\"empty_body\"}");
                return;
            }

            if (!FakeEMoney.IsWaitingAime)
            {
                Reply(ctx, 409, "{\"ok\":false,\"error\":\"not_waiting_for_payment\"}");
                return;
            }

            Interlocked.Exchange(ref _pending, 1);
            ModLog.Debug("[EMoneyMod] HINATA Go 收到刷卡请求, 长度 " + body.Length);
            Reply(ctx, 200, "{\"ok\":true}");
        }

        private static string ReadBody(HttpListenerContext ctx)
        {
            try
            {
                if (!ctx.Request.HasEntityBody)
                {
                    return "";
                }
                using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return "";
            }
        }

        private static void Reply(HttpListenerContext ctx, int code, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static bool TryAddPrefix(HttpListener listener, string prefix)
        {
            try
            {
                listener.Prefixes.Add(prefix);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static System.Collections.Generic.List<string> GetLocalIpv4()
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        {
                            result.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }
    }
}
