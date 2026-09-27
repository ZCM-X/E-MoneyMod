using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 直接通过 Windows HID 打开 HINATA 读卡器，并用 PN532 轮询任意卡(FeliCa/Type-A)。
    /// 这是从 Project-HINATA/hinata-rs 与 hinata_go 的协议移植的最小读卡实现。
    /// 仅在假电子支付等待刷卡时启动，启动前会关掉 AimeIO 扫描。
    /// </summary>
    internal static class HinataHidReader
    {
        private const int HinataVid = 0xF822;
        private const byte ReportId = 1;
        private const byte Pn532Header = 0xE2;

        private static readonly object IoLock = new object();
        private static readonly object QueueLock = new object();
        private static readonly Queue<byte[]> Reports = new Queue<byte[]>();

        private static HidStream _readStream;
        private static HidStream _writeStream;
        private static Thread _pollThread;
        private static Thread _readThread;
        private static volatile bool _running;
        private static int _productId;
        private static bool _loggedFirstError;
        private static bool _loggedDevices;
        private static TypeARfProfile _lastTypeAProfile;
        private static readonly Random Rng = new Random();

        internal static bool IsRunning
        {
            get { return _running; }
        }

        internal static bool TryStart()
        {
            Stop();
            try
            {
                List<HidDevice> devices = DeviceList.Local
                    .GetHidDevices(HinataVid)
                    .OrderByDescending(d => d.MaxInputReportLength)
                    .ToList();

                if (devices.Count == 0)
                {
                    ModLog.Debug("[EMoneyMod][HID] 没有找到 HINATA 读卡器");
                    return false;
                }

                if (!_loggedDevices)
                {
                    _loggedDevices = true;
                    foreach (HidDevice d in devices)
                    {
                        ModLog.Debug("[EMoneyMod][HID] device in=" + d.MaxInputReportLength + " out=" + d.MaxOutputReportLength + " path=" + d.DevicePath);
                    }
                }

                HidDevice readDevice = devices.OrderByDescending(d => d.MaxInputReportLength).FirstOrDefault(d => d.MaxInputReportLength > 0);
                HidDevice writeDevice = devices.OrderByDescending(d => d.MaxOutputReportLength).FirstOrDefault(d => d.MaxOutputReportLength > 0);
                if (readDevice == null) readDevice = devices[0];
                if (writeDevice == null) writeDevice = readDevice;

                HidStream readStream = null;
                HidStream writeStream = null;
                if (!readDevice.TryOpen(out readStream))
                {
                    ModLog.Warning("[EMoneyMod][HID] 打开读通道失败");
                    return false;
                }
                if (ReferenceEquals(readDevice, writeDevice))
                {
                    writeStream = readStream;
                }
                else if (!writeDevice.TryOpen(out writeStream))
                {
                    readStream.Dispose();
                    ModLog.Warning("[EMoneyMod][HID] 打开写通道失败");
                    return false;
                }

                readStream.ReadTimeout = 100;
                if (!ReferenceEquals(readStream, writeStream))
                {
                    writeStream.WriteTimeout = 100;
                }

                _readStream = readStream;
                _writeStream = writeStream;
                _productId = readDevice.ProductID;
                _running = true;

                _pollThread = new Thread(PollLoop);
                _pollThread.IsBackground = true;
                _pollThread.Start();
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Start();

                ModLog.Debug("[EMoneyMod][HID] using readIn=" + readDevice.MaxInputReportLength
                    + " readOut=" + readDevice.MaxOutputReportLength
                    + " writeIn=" + writeDevice.MaxInputReportLength
                    + " writeOut=" + writeDevice.MaxOutputReportLength);
                ModLog.Debug("[EMoneyMod][HID] HINATA 读卡器直连成功: PID=0x"
                    + _productId.ToString("X4"));
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod][HID] 打开 HINATA 读卡器异常: " + e.Message);
                Stop();
                return false;
            }
        }

        internal static void Stop()
        {
            _running = false;
            try
            {
                if (_pollThread != null && _pollThread.IsAlive)
                {
                    _pollThread.Join(300);
                }
            }
            catch
            {
            }
            _pollThread = null;
            try
            {
                if (_readThread != null && _readThread.IsAlive)
                {
                    _readThread.Join(300);
                }
            }
            catch
            {
            }
            _readThread = null;

            try
            {
                if (_readStream != null)
                {
                    _readStream.Dispose();
                }
            }
            catch
            {
            }
            try
            {
                if (_writeStream != null && !ReferenceEquals(_writeStream, _readStream))
                {
                    _writeStream.Dispose();
                }
            }
            catch
            {
            }

            _readStream = null;
            _writeStream = null;
            lock (QueueLock)
            {
                Reports.Clear();
            }
        }

        internal static void SetLed(byte r, byte g, byte b)
        {
            try
            {
                if (_writeStream != null)
                {
                    lock (IoLock)
                    {
                        WriteHinata(0x07, new byte[] { r, g, b });
                    }
                }
            }
            catch
            {
            }
        }

        private static void PollLoop()
        {
            // 先简单等主线程把 AimeIO 停掉。
            Thread.Sleep(150);
            int lastBlink = 0;
            bool blinkOn = false;
            while (_running)
            {
                int now = Environment.TickCount;
                if (unchecked(now - lastBlink) >= 500)
                {
                    lastBlink = now;
                    blinkOn = !blinkOn;
                    SetLed(0, 0, blinkOn ? (byte)255 : (byte)0);
                }

                try
                {
                    if (PollAnyCard())
                    {
                        ModLog.Debug("[EMoneyMod][HID] 检测到任意卡片, 完成假支付");
                        SetLed(0, 0, 255);
                        ExternalCardReceiver.SignalCardFromHid();
                        break;
                    }
                }
                catch (Exception e)
                {
                    if (_running)
                    {
                        ModLog.Warning("[EMoneyMod][HID] 轮询异常: " + e.Message);
                    }
                    break;
                }
                Thread.Sleep(80);
            }
            _running = false;
        }

        private static bool PollAnyCard()
        {
            // 先做一次 FeliCa 预探测，但不立刻完成支付。
            // 这样普通 FeliCa 卡不会被 Type-A 的 RF 配置挡住；
            // 如果同时是 Apple Pay，后面 Type-A SELECT_AID 成功后仍优先按 Apple Pay 处理。
            byte[] felicaCandidate = null;
            try
            {
                byte[] felica = Pn532Request(0x4A, new byte[] { 1, 1, 0x00, 0xFF, 0xFF, 0x01, 0x00 }, 220);
                if (felica.Length > 0 && felica[0] > 0)
                {
                    felicaCandidate = felica;
                    ModLog.Debug("[EMoneyMod][HID] FeliCa 预探测命中: " + BitConverter.ToString(felica));
                }
            }
            catch (Exception e)
            {
                LogFirstError(e);
            }

            // Apple Pay 出对钩依赖 Type-A 的 T-Union SELECT_AID。
            foreach (TypeARfProfile profile in ProfilesForProduct(_productId))
            {
                if (!_running)
                {
                    return false;
                }
                try
                {
                    SetTypeARfProfile(profile);
                    byte[] res = Pn532Request(0x4A, new byte[] { 1, 0 }, 180);
                    if (res.Length > 0 && res[0] > 0)
                    {
                        _lastTypeAProfile = profile;
                        ModLog.Debug("[EMoneyMod][HID] Type-A 检测到卡片: " + BitConverter.ToString(res));

                        int retryWindowMs = felicaCandidate != null ? 1600 : 8000;
                        if (TryTypeATUnionWithRetry(retryWindowMs))
                        {
                            TryRelease();
                            return true;
                        }

                        if (felicaCandidate != null)
                        {
                            ModLog.Debug("[EMoneyMod][HID] Type-A 未出钩, 回退普通 FeliCa");
                            TryFelicaRead(felicaCandidate);
                            TryRelease();
                            return true;
                        }
                        return false;
                    }
                }
                catch (Exception e)
                {
                    LogFirstError(e);
                }
            }

            // Type-A 没命中时，再按普通 FeliCa 卡处理。
            if (felicaCandidate != null)
            {
                ModLog.Debug("[EMoneyMod][HID] FeliCa 普通卡处理");
                TryFelicaRead(felicaCandidate);
                TryRelease();
                return true;
            }

            return false;
        }

        /// <summary>
        /// T-Union 读卡序列（带重试）。
        ///
        /// 关键发现（2026-09-27 实测）：
        ///   手机出对钩只需要 **SELECT_AID 那一条 APDU 成功**（返回完整 FCI + 9000），
        ///   后面的 READ_INFO / READ_BALANCE 失败也不影响对钩。
        ///
        /// 为什么要重试：游戏侧的 aimeio（segatools 的 hinata.dll，跑在 amdaemon.exe 里）
        /// 会同时轮询同一个读卡器，它发的 RFConfiguration / InRelease 会把我们刚激活的
        /// Type-A 目标打掉，于是 InDataExchange 立刻失败（返回 0x27）。
        /// 所以这里把"重新配 RF -> 重新激活目标 -> SELECT_AID"反复重试，
        /// 总有一次能落在它的间隙里。
        /// </summary>
        private static bool TryTypeATUnionWithRetry(int retryWindowMs)
        {
            const int MaxAttempts = 900;
            byte[] selectAid = { 0x00, 0xA4, 0x04, 0x00, 0x08, 0xA0, 0x00, 0x00, 0x06, 0x32, 0x01, 0x01, 0x05 };

            bool selected = false;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(retryWindowMs);
            int attempt = 0;
            while (_running && attempt < MaxAttempts && DateTime.UtcNow < deadline)
            {
                attempt++;
                if (attempt % 40 == 0)
                {
                    try
                    {
                        byte[] felicaFallback =
                            Pn532Request(0x4A, new byte[] { 1, 1, 0x00, 0xFF, 0xFF, 0x01, 0x00 }, 160);
                        if (felicaFallback.Length > 0 && felicaFallback[0] > 0)
                        {
                            ModLog.Debug("[EMoneyMod][HID] Type-A 重试中 FeliCa 兜底命中: "
                                + BitConverter.ToString(felicaFallback));
                            TryFelicaRead(felicaFallback);
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
                try
                {
                    // 每一轮都重新配 RF 并重新激活目标。
                    if (_lastTypeAProfile != null)
                    {
                        SetTypeARfProfile(_lastTypeAProfile);
                    }
                    byte[] poll = Pn532Request(0x4A, new byte[] { 1, 0 }, 140);
                    if (poll.Length == 0 || poll[0] == 0)
                    {
                        Jitter(2, 8);
                        continue;
                    }

                    // 单次激活后不要只发一条就放弃。hinata 可能刚好插进来，
                    // 连续 burst 更容易抓住它没插队的空窗。
                    for (int burst = 0; burst < 6; burst++)
                    {
                        byte[] r = ApduOnce(selectAid, 260);
                        if (r != null)
                        {
                            ModLog.Debug("[EMoneyMod][HID] Type-A SELECT_AID 成功(第 " + attempt
                                + " 轮, burst " + (burst + 1) + "): " + BitConverter.ToString(r));
                            selected = true;
                            break;
                        }
                        Jitter(0, 3);
                    }
                    if (selected)
                    {
                        break;
                    }
                }
                catch
                {
                }

                // 加一点随机抖动, 避免每一轮都正好踩在 hinata 轮询周期的同一个相位上。
                Jitter(2, 8);
            }

            if (!selected)
            {
                ModLog.Debug("[EMoneyMod][HID] Type-A SELECT_AID 在 " + retryWindowMs
                    + "ms / " + attempt + " 次内没成功，继续轮询");
                return false;
            }

            // 让 RF 场再多撑一会儿, 给手机时间把"完成"提示显示出来。
            Thread.Sleep(350);

            // 剩下两条尽力而为, 失败无所谓（实测经常失败, 不影响对钩）。
            ApduOnce(new byte[] { 0x00, 0xB0, 0x95, 0x00, 0x1E });
            ApduOnce(new byte[] { 0x80, 0x5C, 0x00, 0x02, 0x04 });
            return true;
        }

        /// <summary>重试之间的随机小间隔（0~14ms），用来打散和 hinata 轮询周期的相位。</summary>
        private static void Jitter()
        {
            Jitter(0, 15);
        }

        private static void Jitter(int minMs, int maxMs)
        {
            try
            {
                Thread.Sleep(Rng.Next(minMs, maxMs));
            }
            catch
            {
            }
        }

        /// <summary>发一条 APDU；成功（PN532 状态 0x00 且 SW=9000）返回响应，否则返回 null。</summary>
        private static byte[] ApduOnce(byte[] apdu)
        {
            return ApduOnce(apdu, 2500);
        }

        private static byte[] ApduOnce(byte[] apdu, int timeoutMs)
        {
            byte[] payload = new byte[1 + apdu.Length];
            payload[0] = 0x01; // Tg
            Array.Copy(apdu, 0, payload, 1, apdu.Length);

            byte[] res;
            try
            {
                res = Pn532Request(0x40, payload, timeoutMs);
            }
            catch
            {
                return null;
            }
            if (res == null || res.Length < 3)
            {
                return null;
            }
            if (res[0] != 0x00)
            {
                return null;   // PN532 状态不是 0x00 -> 这次被抢了
            }
            if (res[res.Length - 2] != 0x90 || res[res.Length - 1] != 0x00)
            {
                return null;   // APDU 没有 9000
            }
            return res;
        }
        private static void LogFirstError(Exception e)
        {
            if (_loggedFirstError) return;
            _loggedFirstError = true;
            ModLog.Warning("[EMoneyMod][HID] PN532 轮询异常: " + e.Message);
        }

        private static void TryFelicaRead(byte[] pollResponse)
        {
            // 复刻 HINATA Go 的 FeliCa Read Without Encryption。
            // AIC / Suica 都读一遍, 让 iPhone/Apple Wallet 看到一次真正的读卡查询
            // (只是查询, 不是交易), 通常会响一声 / 显示对号。
            try
            {
                if (pollResponse.Length < 12) return;
                byte[] idm = new byte[8];
                Array.Copy(pollResponse, 4, idm, 0, 8);

                byte[] aic = ReadFelicaService(idm, 0x000B, 0x0000, "AIC");
                ReadFelicaService(idm, 0x090F, 0x0000, "Suica0");
                ReadFelicaService(idm, 0x090F, 0x0001, "Suica1");

                if (aic != null)
                {
                    ModLog.Debug("[EMoneyMod][HID] FeliCa AIC 读卡完成: " + BitConverter.ToString(aic));
                }
            }
            catch (Exception e)
            {
                LogFirstError(e);
            }
        }

        /// <summary>
        /// FeliCa "Read Without Encryption" 单次服务读取。
        /// payload = [Tg, LEN, 0x06, IDm(8), 0x01, 服务码(2), 0x01, 块号(2)]
        /// </summary>
        private static byte[] ReadFelicaService(byte[] idm, ushort service, ushort block, string label)
        {
            List<byte> input = new List<byte>();
            input.Add(0x06);
            input.AddRange(idm);
            input.Add(0x01);
            input.Add((byte)(service >> 8));
            input.Add((byte)(service & 0xFF));
            input.Add(0x01);
            input.Add((byte)(block >> 8));
            input.Add((byte)(block & 0xFF));

            byte[] payload = new byte[2 + input.Count];
            payload[0] = 0x01; // Tg
            payload[1] = (byte)(input.Count + 1);
            Array.Copy(input.ToArray(), 0, payload, 2, input.Count);

            try
            {
                byte[] res = Pn532Request(0x40, payload);
                ModLog.Debug("[EMoneyMod][HID] FeliCa " + label + " 返回: " + BitConverter.ToString(res));
                return res;
            }
            catch (Exception e)
            {
                // 某个服务读不到不影响其它服务。
                ModLog.Warning("[EMoneyMod][HID] FeliCa " + label + " 失败: " + e.Message);
                return null;
            }
        }

        private static void TryRelease()
        {
            try
            {
                Pn532Request(0x52, new byte[] { 1 });
            }
            catch
            {
            }
        }

        private static void SetTypeARfProfile(TypeARfProfile profile)
        {
            byte gsNOn = (byte)((profile.CwGsNOn << 4) | 0x04);
            byte[] payload =
            {
                0x0A,
                profile.RfCfg,
                gsNOn,
                profile.CwGsP,
                0x11,
                0x4D,
                0x85,
                0x61,
                0x6F,
                0x26,
                0x62,
                0x87
            };
            Pn532Request(0x32, payload, 250);
        }

        private static byte[] Pn532Request(byte command, byte[] payload)
        {
            return Pn532Request(command, payload, 2500);
        }

        private static byte[] Pn532Request(byte command, byte[] payload, int timeoutMs)
        {
            byte[] packet = BuildPn532Packet(0xD4, command, payload);
            lock (IoLock)
            {
                ClearReports();
                WriteHinata(Pn532Header, packet);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    int reportWait = Math.Max(1, Math.Min(300, timeoutMs));
                    byte[] report = WaitReport(0xE2, reportWait);
                    if (report == null || report.Length < 8)
                    {
                        continue;
                    }

                    byte[] response = report.Skip(2).ToArray(); // skip HID report id + HINATA header
                    if (IsAck(response))
                    {
                        continue;
                    }

                    try
                    {
                        Pn532Packet parsed = ParsePn532Packet(response);
                        if (parsed.Direction == 0xD5 && parsed.Command == command)
                        {
                            return parsed.Payload;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            throw new TimeoutException("Pn532 timeout: 0x" + command.ToString("X2"));
        }

        private static void WriteHinata(byte command, byte[] payload)
        {
            byte[] data = new byte[2 + payload.Length];
            data[0] = ReportId;
            data[1] = command;
            Array.Copy(payload, 0, data, 2, payload.Length);
            _writeStream.Write(data, 0, data.Length);
        }

        private static byte[] WaitReport(byte header, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (QueueLock)
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (Reports.Count > 0)
                    {
                        byte[] report = Reports.Dequeue();
                        if (report.Length >= 2 && report[1] == header)
                        {
                            return report;
                        }
                        continue;
                    }
                    int wait = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (wait <= 0)
                    {
                        break;
                    }
                    System.Threading.Monitor.Wait(QueueLock, Math.Min(wait, 20));
                }
            }
            return null;
        }

        private static void ClearReports()
        {
            lock (QueueLock)
            {
                Reports.Clear();
                System.Threading.Monitor.PulseAll(QueueLock);
            }
        }

        private static void ReadLoop()
        {
            byte[] buffer = new byte[256];
            while (_running && _readStream != null)
            {
                try
                {
                    int count = _readStream.Read(buffer, 0, buffer.Length);
                    if (count > 0)
                    {
                        byte[] data = new byte[count];
                        Array.Copy(buffer, data, count);
                        lock (QueueLock)
                        {
                            Reports.Enqueue(data);
                            System.Threading.Monitor.PulseAll(QueueLock);
                        }
                    }
                }
                catch
                {
                    if (_running)
                    {
                        Thread.Sleep(30);
                    }
                }
            }
        }

        private static bool IsAck(byte[] data)
        {
            return data.Length >= 6 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFF
                && data[3] == 0x00 && data[4] == 0xFF && data[5] == 0x00;
        }

        private static byte[] BuildPn532Packet(byte direction, byte command, byte[] payload)
        {
            int len = payload.Length + 2;
            byte lcs = (byte)((byte)(~len) + 1);
            var list = new List<byte>();
            list.AddRange(new byte[] { 0x00, 0x00, 0xFF, (byte)len, lcs, direction, command });
            list.AddRange(payload);
            byte dcs = direction;
            dcs = (byte)(dcs + command);
            foreach (byte b in payload)
            {
                dcs = (byte)(dcs + b);
            }
            dcs = (byte)((byte)(~dcs) + 1);
            list.Add(dcs);
            list.Add(0x00);
            return list.ToArray();
        }

        private static Pn532Packet ParsePn532Packet(byte[] data)
        {
            if (data.Length < 7) throw new Exception("short pn532 packet");
            if (data[0] != 0x00 || data[1] != 0x00 || data[2] != 0xFF) throw new Exception("bad preamble");
            byte len = data[3];
            byte lcs = data[4];
            if ((byte)(len + lcs) != 0) throw new Exception("bad lcs");
            byte direction = data[5];
            byte command = data[6];
            int payloadEnd = 5 + len;
            if (data.Length < payloadEnd + 1) throw new Exception("short payload");
            byte dcs = 0;
            for (int i = 5; i < payloadEnd; i++)
            {
                dcs = (byte)(dcs + data[i]);
            }
            if ((byte)(dcs + data[payloadEnd]) != 0) throw new Exception("bad dcs");
            byte[] payload = new byte[len - 2];
            Array.Copy(data, 7, payload, 0, payload.Length);
            return new Pn532Packet { Direction = direction, Command = (byte)(command - 1), Payload = payload };
        }

        private static TypeARfProfile[] ProfilesForProduct(int productId)
        {
            if (productId == 0x0147)
            {
                return new[]
                {
                    new TypeARfProfile(0x59, 0x0F, 0x3F),
                    new TypeARfProfile(0x69, 0x0F, 0x2B)
                };
            }
            if (productId == 0x0148)
            {
                return new[]
                {
                    new TypeARfProfile(0x29, 0x03, 0x11),
                    new TypeARfProfile(0x49, 0x0B, 0x0C),
                    new TypeARfProfile(0x59, 0x0F, 0x3F)
                };
            }
            return new[] { new TypeARfProfile(0x59, 0x0F, 0x3F) };
        }

        private sealed class Pn532Packet
        {
            public byte Direction;
            public byte Command;
            public byte[] Payload;
        }

        private sealed class TypeARfProfile
        {
            public readonly byte RfCfg;
            public readonly byte CwGsNOn;
            public readonly byte CwGsP;

            public TypeARfProfile(byte rfCfg, byte cwGsNOn, byte cwGsP)
            {
                RfCfg = rfCfg;
                CwGsNOn = cwGsNOn;
                CwGsP = cwGsP;
            }
        }
    }
}
