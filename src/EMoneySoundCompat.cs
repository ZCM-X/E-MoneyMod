using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Xml;
using AMDaemon;
using MelonLoader;

namespace EMoneyMod
{
    /// <summary>
    /// 电子支付提示音。
    ///
    /// 原版声音不是游戏自己放的：
    ///   Thinca 电子支付终端 -> AMDaemon 推 EMoneySound(Id 形如 "0001-02-00")
    ///   -> Manager.EMoney.onSound() 按 Id 选 Cue -> SoundManager.PlaySystemSE()
    /// （Manager/EMoney.cs:164  AMDaemon.EMoney.SoundHook = onSound;）
    ///
    /// 咱是假支付、没有终端，所以永远收不到事件。这里拿到游戏注册好的 SoundHook，
    /// 伪造一个 EMoneySound 丢进去，让游戏用它自己的映射表去放音。
    ///
    /// 【音效 id 的来源】不是猜的 —— 终端的资源表就在游戏目录里：
    ///     Package\tfps-res-pro\resource.xml
    /// 里面每个 brand code="N" 都有 name 和若干
    ///     sound id="事件号" 0001-02-00.wav
    /// 那个 wav 文件名去掉 .wav 就是游戏要的 Id。
    ///
    /// 事件号：0 = 请刷卡  1 = 失败  2 = 成功  3 = 重试
    ///
    /// （注意：这里的“1 = 失败”不是笔误。交通系/nanaco/PASELI 等品牌的
    /// 资源表里，事件 1 其实是“残高不足/请稍候”这种错误提示音，而事件 2
    /// 才是“ありがとうございました”的成功音，所以成功必须取事件 2。）
    ///
    /// 注意：resource.xml 里的 brand code 是【终端品牌码】，和游戏的 EMoneyBrandId
    /// 不是一回事（终端码 3 = iD，终端码 5 = 交通系），所以这里按 name 去匹配。
    /// </summary>
    internal static class EMoneySoundCompat
    {
        /// <summary>游戏 EMoneyBrandId 顺序：0 Nanaco, 1 Edy, 2 Transport, 3 Waon, 4 Paseli, 5 iD, 6 Sapica</summary>
        private const int BrandCount = 7;

        /// <summary>[游戏品牌][终端事件号] = 音效 Id（如 "0001-02-00"）</summary>
        private static readonly string[][] ByEvent = new string[BrandCount][];
        private static bool _mapLoaded;

        private const string FallbackSuccess = "9999-02-01";
        private const string FallbackFail = "9999-02-99";

        internal static void PlayTouch(int brandIndex)
        {
            Play(brandIndex, 0);
        }

        internal static void PlaySuccess(int brandIndex)
        {
            Play(brandIndex, 2);
        }

        internal static void PlayFailure(int brandIndex)
        {
            Play(brandIndex, 1);
        }

        internal static void PlayRetry(int brandIndex)
        {
            Play(brandIndex, 3);
        }

        private static void Play(int brandIndex, int eventId)
        {
            string id = PickId(brandIndex, eventId);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            PlayId(id);
        }

        private static string PickId(int brandIndex, int eventId)
        {
            EnsureMap();

            if (brandIndex >= 0 && brandIndex < BrandCount)
            {
                string[] slots = ByEvent[brandIndex];
                if (slots != null && eventId >= 0 && eventId < slots.Length && !string.IsNullOrEmpty(slots[eventId]))
                {
                    return slots[eventId];
                }
            }

            if (eventId == 1 || eventId == 3)
            {
                return FallbackFail;
            }
            return FallbackSuccess;
        }

        // ------------------------ 解析 resource.xml ------------------------

        private static void EnsureMap()
        {
            if (_mapLoaded)
            {
                return;
            }
            _mapLoaded = true;

            for (int i = 0; i < BrandCount; i++)
            {
                ByEvent[i] = new string[4];
            }

            try
            {
                string modsDir = Path.GetDirectoryName(typeof(Mod).Assembly.Location);
                string gameDir = string.IsNullOrEmpty(modsDir) ? null : Path.GetDirectoryName(modsDir);
                if (string.IsNullOrEmpty(gameDir))
                {
                    ModLog.Warning("[EMoneyMod][Sound] 找不到游戏目录，用内置默认音效表");
                    return;
                }

                string xmlPath = Path.Combine(Path.Combine(gameDir, "tfps-res-pro"), "resource.xml");
                if (!File.Exists(xmlPath))
                {
                    ModLog.Warning("[EMoneyMod][Sound] 没有 tfps-res-pro\\resource.xml，用内置默认音效表");
                    return;
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(xmlPath);

                XmlNodeList brands = doc.SelectNodes("/thincaResource/brand");
                int matched = 0;
                if (brands != null)
                {
                    // 逆序处理：同一个游戏品牌在 resource.xml 里可能出现两次
                    // （nanaco 既有旧协议终端码 1，又有新协议终端码 7）。
                    // 新协议条目（code 更大）的音效和交通系一致（成功=ありがとうございました），
                    // 先读新协议、旧协议只补空位，让 nanaco 用上新协议这套更统一的音效。
                    for (int bi = brands.Count - 1; bi >= 0; bi--)
                    {
                        XmlNode brand = brands[bi];
                        if (brand.Attributes == null || brand.Attributes["code"] == null)
                        {
                            continue;
                        }
                        string code = brand.Attributes["code"].Value;
                        string name = ChildText(brand, "name");
                        int gi = GameBrandIndex(name);
                        if (gi < 0)
                        {
                            continue;
                        }

                        int filled = 0;
                        foreach (XmlNode sound in brand.SelectNodes("sound"))
                        {
                            if (sound.Attributes == null || sound.Attributes["id"] == null)
                            {
                                continue;
                            }
                            int sid;
                            if (!int.TryParse(sound.Attributes["id"].Value, out sid) || sid < 0 || sid >= 4)
                            {
                                continue;
                            }
                            string file = (sound.InnerText ?? "").Trim();
                            if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                            {
                                file = file.Substring(0, file.Length - 4);
                            }
                            if (file.Length == 0)
                            {
                                continue;
                            }
                            if (ByEvent[gi][sid] == null)
                            {
                                ByEvent[gi][sid] = file;
                                filled++;
                            }
                        }

                        if (filled > 0)
                        {
                            matched++;
                            ModLog.Debug("[EMoneyMod][Sound] 品牌 " + gi + " <- 终端码 " + code
                                + " (" + name + "): 刷卡=" + Show(ByEvent[gi][0])
                                + " 失败=" + Show(ByEvent[gi][1])
                                + " 成功=" + Show(ByEvent[gi][2])
                                + " 重试=" + Show(ByEvent[gi][3]));
                        }
                    }
                }
                ModLog.Debug("[EMoneyMod][Sound] 已从 resource.xml 读取 " + matched + " 个品牌的音效映射");
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod][Sound] 解析 resource.xml 失败: " + e.Message);
            }
        }

        private static string Show(string s)
        {
            return string.IsNullOrEmpty(s) ? "-" : s;
        }

        private static string ChildText(XmlNode parent, string childName)
        {
            XmlNode n = parent.SelectSingleNode(childName);
            return n == null ? "" : (n.InnerText ?? "").Trim();
        }

        /// <summary>按 resource.xml 里的 name 判断它是游戏的哪个品牌。</summary>
        private static int GameBrandIndex(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            string n = name.ToLowerInvariant();

            if (n.Contains("nanaco")) return 0;
            if (n.Contains("edy")) return 1;
            if (n.Contains("交通")) return 2;
            if (n.Contains("waon")) return 3;
            if (n.Contains("paseli")) return 4;
            if (n.Contains("sapica")) return 6;
            if (n == "id") return 5;
            return -1;
        }

        private static string _gameDir;

        private static string GameDir()
        {
            if (_gameDir == null)
            {
                try
                {
                    string modsDir = Path.GetDirectoryName(typeof(Mod).Assembly.Location);
                    _gameDir = string.IsNullOrEmpty(modsDir) ? "" : Path.GetDirectoryName(modsDir);
                }
                catch
                {
                    _gameDir = "";
                }
            }
            return _gameDir;
        }

        /// <summary>读 wav 时长(毫秒), 读不到返回 0。</summary>
        private static int WavDurationMs(string soundId)
        {
            try
            {
                if (string.IsNullOrEmpty(soundId) || string.IsNullOrEmpty(GameDir()))
                {
                    return 0;
                }
                string p = Path.Combine(Path.Combine(GameDir(), "tfps-res-pro"), soundId + ".wav");
                if (!File.Exists(p))
                {
                    return 0;
                }
                byte[] b = File.ReadAllBytes(p);
                if (b.Length < 44)
                {
                    return 0;
                }
                int pos = 12;
                int byteRate = 0;
                int dataSize = 0;
                while (pos + 8 <= b.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                    int size = BitConverter.ToInt32(b, pos + 4);
                    if (size < 0)
                    {
                        break;
                    }
                    if (id == "fmt " && size >= 16)
                    {
                        byteRate = BitConverter.ToInt32(b, pos + 16);
                    }
                    else if (id == "data")
                    {
                        dataSize = size;
                        if (dataSize > b.Length - pos - 8)
                        {
                            dataSize = b.Length - pos - 8;
                        }
                        break;
                    }
                    pos += 8 + size + (size & 1);
                }
                if (byteRate <= 0)
                {
                    return 0;
                }
                return (int)(1000.0 * dataSize / byteRate);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 品牌音（事件 0）的时长，用来等它播完后再入账和播点数音。
        /// </summary>
        internal static int GetTouchDurationMs(int brandIndex)
        {
            int delay = WavDurationMs(PickId(brandIndex, 0));
            if (delay < 600)
            {
                delay = 600;
            }
            if (delay > 3500)
            {
                delay = 3500;
            }
            return delay;
        }

        /// <summary>
        /// 播放点数入账音，等价于游戏 Credit.CoinInHook 里播放的 SE_SYS_CREDIT。
        /// 游戏原版不是放 EMoney 的“成功音”，而是在 AMDaemon 真正加点数时由原生层
        /// 回调 CoinInHook 播放这个音（见 Manager/Credit.cs）。咱是假支付、加的是虚拟点数，
        /// 收不到那个回调，所以在加点数的同一时刻自己补放。
        /// </summary>
        internal static void PlayCredit()
        {
            try
            {
                Manager.SoundManager.PlaySystemSE(Mai2.Mai2Cue.Cue.SE_SYS_CREDIT);
                ModLog.Debug("[EMoneyMod][Sound] 播放点数入账音 SE_SYS_CREDIT");
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod][Sound] 播放点数入账音失败: " + e.Message);
            }
        }
        // ------------------------ 播放 ------------------------

        /// <summary>把伪造的音效事件丢给游戏注册好的回调。</summary>
        internal static void PlayId(string soundId)
        {
            try
            {
                Action<EMoneySound> hook = GetSoundHook();
                if (hook == null)
                {
                    ModLog.Warning("[EMoneyMod][Sound] 游戏的 SoundHook 还没注册，跳过音效 " + soundId);
                    return;
                }

                EMoneySound sound = (EMoneySound)FormatterServices.GetUninitializedObject(typeof(EMoneySound));
                SetMember(sound, "Id", soundId);
                SetMember(sound, "FilePath", null);

                hook(sound);
                ModLog.Debug("[EMoneyMod][Sound] 播放电子支付音效: " + soundId);
            }
            catch (Exception e)
            {
                ModLog.Warning("[EMoneyMod][Sound] 播放音效失败 " + soundId + ": " + e.Message);
            }
        }

        private static Action<EMoneySound> GetSoundHook()
        {
            try
            {
                return AMDaemon.EMoney.SoundHook;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>EMoneySound.Id / FilePath 的 setter 是私有的，只能反射写。</summary>
        private static void SetMember(object obj, string name, object value)
        {
            try
            {
                System.Reflection.PropertyInfo p = obj.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (p != null)
                {
                    System.Reflection.MethodInfo setter = p.GetSetMethod(true);
                    if (setter != null)
                    {
                        setter.Invoke(obj, new object[] { value });
                        return;
                    }
                }
            }
            catch
            {
            }
            try
            {
                System.Reflection.FieldInfo f = obj.GetType().GetField("<" + name + ">k__BackingField",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null)
                {
                    f = obj.GetType().GetField(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                }
                if (f != null)
                {
                    f.SetValue(obj, value);
                }
            }
            catch
            {
            }
        }

        // ------------------------ 音效试听 ------------------------

        /// <summary>
        /// 如果 Mods 目录下存在 EMoneyMod.soundtest.txt，就按品牌把各事件音效依次播一遍。
        /// </summary>
        internal static void RunSoundTestIfRequested()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Mod).Assembly.Location);
                if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "EMoneyMod.soundtest.txt")))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            Thread t = new Thread(delegate ()
            {
                try
                {
                    Thread.Sleep(15000);
                    EnsureMap();

                    string[] names = { "Nanaco", "Edy", "Transport", "Waon", "Paseli", "iD", "Sapica" };
                    string[] events = { "刷卡(0)", "成功(1)", "失败(2)", "重试(3)" };

                    ModLog.Debug("[EMoneyMod][Sound] === 开始音效试听 ===");
                    for (int b = 0; b < BrandCount; b++)
                    {
                        for (int e = 0; e < 4; e++)
                        {
                            string id = ByEvent[b] == null ? null : ByEvent[b][e];
                            if (string.IsNullOrEmpty(id))
                            {
                                continue;
                            }
                            ModLog.Debug("[EMoneyMod][Sound] 现在播放: " + id
                                + "   (" + names[b] + " / " + events[e] + ")");
                            PlayId(id);
                            Thread.Sleep(1400);
                        }
                    }
                    ModLog.Debug("[EMoneyMod][Sound] === 试听结束 ===");
                }
                catch (Exception e)
                {
                    ModLog.Warning("[EMoneyMod][Sound] 试听异常: " + e.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
