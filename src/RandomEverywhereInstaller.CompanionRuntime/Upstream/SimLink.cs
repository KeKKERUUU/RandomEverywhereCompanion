// SimConnect link with the simulator.
//  - keeps the local variable L:AUDIOINHIBIT at 1 (option "Auto set default audio inhibit", on by default);
//  - publishes the state of the voice pack system in L:HMC_VOICE_STATUS, L:HMC_VOICEPACK_VERSION and L:HMC_APP_VERSION
//    (see docs/architecture/VOICEPACKS.md), so a mission can read it with a wait_for.
// The unmodified x64 SimConnect.dll is bundled; an explicit SDK override remains supported.
// The project copies it next to the worker and embeds that payload in the distribution EXE.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HPGMissionCompanion
{
    internal static class SimLink
    {
        public const string Variable = "L:AUDIOINHIBIT";
        public const double Wanted = 1.0;
        public const int PingSeconds = 5;

        public const string VarStatus = "L:HMC_VOICE_STATUS";             // 0 no pack declared, 1 pack ok, 2 pack missing, 3 pack older than the mission needs
        public const string VarPackVersion = "L:HMC_VOICEPACK_VERSION";   // version of the declared pack, 0 when missing
        public const string VarAppVersion = "L:HMC_APP_VERSION";          // version of this program (0.4 -> 0.4)

        // the mission publishes its context for the radio rule (see docs/architecture/VOICEPACKS.md); the program only reads them
        public const string CtxValid = "L:HMC_CTX_VALID";                 // 1 when the mission publishes the two variables below
        public const string CtxHeliOff = "L:HMC_HELI_OFF";                // 1 helicopter completely off
        public const string CtxTechSex = "L:HMC_TECH_SEX";                 // the technician of the scene: 0 male, 1 female (the mission picks one at random in app mode)
        public const string CtxChat = "L:HMC_CHAT_MAX";                    // the highest option number a message may use (the mission setting CREW CHATTER: FULL 6, REDUCED 3, MINIMAL 1); not published or 0 = 6
        public const string CtxMute = "L:HMC_CREW_MUTE";                   // same bits as the line below: crew members who cannot speak (dead, or hurt so badly that they are out of the conversation)
        public const string CtxCrew = "L:HMC_CREW_PRESENT";               // bit 0 pilot, 1 copilot, 2 doctor, 3 HEMS1, 4 HEMS2: aboard
        public const string CtxRadio = "L:H145_SDK_OH_BATTERY_MASTER";    // the aircraft's own variable: 1 battery master on, the radio works (the mission reads it as L:{HXX}_SDK_OH_BATTERY_MASTER, HXX = H145); not read = the opposite of L:HMC_HELI_OFF
        public const string CtxExist = "L:HMC_CREW_EXIST";                // same bits as L:HMC_CREW_PRESENT: the crew members that exist (aboard or outside); 0 = not published
        public const string CtxBase = "L:HMC_AT_BASE";                    // 1 the helicopter is at the departure base, in range of its wifi: the dispatcher reaches the crew radios even with the battery off
        public const string CtxZoneTech = "L:HMC_ZONE_TECH";              // zone of the technician from the helicopter: 1 up to 5 m, 2 up to 30 m, 3 up to 100 m, 4 beyond; 0 = not published
        public const string CtxZoneGround = "L:HMC_ZONE_GROUND";          // the same for the ground unit / hospital staff (actor 9)
        public const string CtxZonePatient = "L:HMC_ZONE_PATIENT";        // the same for the patient / extra (actor 10)

        const uint WatchDefineBase = 300;
        const uint WatchRequestBase = 300;
        const int WatchGroup = 90;
        const uint PeriodSimFrame = 3;
        const uint FlagChanged = 1;

        const uint DefineId = 1;
        const uint RequestId = 1;
        const uint OutputDefineBase = 100;
        const uint ObjectUser = 0;
        const uint DatatypeFloat64 = 4;
        const uint DatatypeString32 = 6;
        const uint CallDefineId = 200;       // ATC ID, ATC AIRLINE, ATC FLIGHT NUMBER of the aircraft: the livery sets them in aircraft.cfg (atc_id, atc_airline, atc_flight_number)
        const uint CallRequestId = 200;
        public static Action<string, string, string> OnAtcStrings;
        const uint PeriodOnce = 1;
        const int RecvQuit = 3;
        const int RecvException = 1;
        const int RecvSimObjectData = 8;
        const int DataOffset = 40;     // SIMCONNECT_RECV_SIMOBJECT_DATA: 12 byte header + 7 dwords, then dwData

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd, uint userEvent, IntPtr hEvent, uint configIndex);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int SimConnect_Close(IntPtr handle);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string unitsName, uint datumType, float epsilon, uint datumId);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags, uint arrayCount, uint unitSize, IntPtr data);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

        static Thread worker;
        static volatile bool stop;
        static volatile bool connected;
        static volatile string status = "SimConnect: starting";
        static Func<bool> enabled;
        static string dllPath;
        static double? inhibitBaseline;
        static bool inhibitWasChanged;

        // values published to the simulator (name -> value); written at every connection, every ping and when they change
        static readonly object outLock = new object();
        static readonly Dictionary<string, double> outputs = new Dictionary<string, double>();
        static volatile bool outDirty;

        // variables the program watches (the LVAR counters of the bridge pack and the context): name, old value, new value, first value after the connection
        static string[] watch = new string[0];
        static volatile int watchVersion;
        public static Action<string, double, double, bool> OnWatch;
        static int exceptionsLogged;

        public static void SetWatch(List<string> names)
        {
            string[] fresh = names.ToArray();
            string[] old = watch;
            bool same = old.Length == fresh.Length;
            for (int i = 0; same && i < fresh.Length; i++) if (old[i] != fresh[i]) same = false;
            if (same) return;
            watch = fresh;
            watchVersion++;      // the open connection is closed and reopened with the new list
        }

        public static string Status { get { return status; } }
        // Session activity and an established simulator link are separate states.
        public static bool Connected { get { return connected; } }

        // Sets a variable that the program publishes to the simulator (only the three HMC variables above).
        public static void Publish(string name, double value)
        {
            lock (outLock)
            {
                double old;
                if (!outputs.TryGetValue(name, out old) || old != value) { outputs[name] = value; outDirty = true; }
            }
        }

        // isEnabled tells whether L:AUDIOINHIBIT has to be kept at 1; the connection and the published variables do not depend on it
        public static void Start(Func<bool> isEnabled)
        {
            if (worker != null) return;
            connected = false;
            enabled = isEnabled;
            inhibitBaseline = null; inhibitWasChanged = false;
            stop = false;
            double app;
            if (!double.TryParse(AppInfo.Version, NumberStyles.Float, CultureInfo.InvariantCulture, out app)) app = 0;
            Publish(VarAppVersion, app);
            lock (outLock) { if (!outputs.ContainsKey(VarStatus)) outputs[VarStatus] = 0; if (!outputs.ContainsKey(VarPackVersion)) outputs[VarPackVersion] = 0; }
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "SimLink";
            worker.Start();
        }

        public static void Stop()
        {
            stop = true;
            Thread t = worker;
            if (t != null && t.IsAlive && !t.Join(5000)) throw new InvalidOperationException("The simulator link did not stop. A second audio mode was not started.");
            worker = null;
            connected = false;
            OnWatch = null;
            OnAtcStrings = null;
        }

        // Prefer an explicit user override, then the bundled copy, then installed SDKs.
        public static string FindDll()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Ctx.BaseDir, "SimConnect.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SimConnect.dll"),
                SdkLib(Environment.GetEnvironmentVariable("MSFS2024_SDK")),
                SdkLib(Environment.GetEnvironmentVariable("MSFS_SDK")),
                SdkLib("C:\\MSFS 2024 SDK"),
                SdkLib("C:\\MSFS SDK")
            };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try { if (File.Exists(c)) return c; } catch (Exception) { }
            }
            return null;
        }

        static string SdkLib(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, "SimConnect SDK\\lib\\SimConnect.dll");
        }

        static void SetStatus(string text)
        {
            if (status == text) return;
            status = text;
        }

        static void Run()
        {
            bool dllLogged = false;
            string lastWait = null;
            while (!stop)
            {
                if (dllPath == null)
                {
                    dllPath = FindDll();
                    if (dllPath == null)
                    {
                        SetStatus("SimConnect: bundled DLL missing");
                        if (!dllLogged) { dllLogged = true; Ctx.Log("[WARN] Bundled SimConnect.dll not found. Repair the application or select an SDK override in Settings. The simulator link is off."); }
                        Sleep(5000);
                        continue;
                    }
                    try { LoadLibraryW(dllPath); } catch (Exception) { }
                    Ctx.Log("[INFO] SimConnect library: " + dllPath);
                }
                IntPtr h = IntPtr.Zero;
                try
                {
                    int rc = SimConnect_Open(out h, AppInfo.Name, IntPtr.Zero, 0, IntPtr.Zero, 0);
                    if (rc != 0 || h == IntPtr.Zero)
                    {
                        string w = "SimConnect: waiting for the simulator";
                        SetStatus(w);
                        if (lastWait != w) { lastWait = w; Ctx.Log("[INFO] " + w + " (checking every " + PingSeconds + " s)"); }
                        Sleep(PingSeconds * 1000);
                        continue;
                    }
                    lastWait = null;
                    Session(h);
                }
                catch (DllNotFoundException)
                {
                    connected = false;
                    dllPath = null;
                    SetStatus("SimConnect: SimConnect.dll cannot be loaded");
                    Ctx.Log("[ERROR] SimConnect.dll cannot be loaded (wrong bitness or missing).");
                    Sleep(10000);
                }
                catch (Exception ex)
                {
                    connected = false;
                    SetStatus("SimConnect: error, retrying");
                    Ctx.Log("[ERROR] SimConnect: " + ex.Message);
                    Sleep(PingSeconds * 1000);
                }
                finally
                {
                    connected = false;
                    if (h != IntPtr.Zero) { try { SimConnect_Close(h); } catch (Exception) { } }
                }
            }
        }

        // the k-th string (STRING32, ANSI, ends at the first zero) of a SIMCONNECT_RECV_SIMOBJECT_DATA of the call sign request
        static string AtcString(IntPtr data, int k)
        {
            try
            {
                string s = Marshal.PtrToStringAnsi(IntPtr.Add(data, DataOffset + 32 * k), 32) ?? "";
                int z = s.IndexOf('\0');
                return (z >= 0 ? s.Substring(0, z) : s).Trim();
            }
            catch (Exception) { return ""; }
        }

        static void Sleep(int ms)
        {
            int left = ms;
            while (left > 0 && !stop) { Thread.Sleep(Math.Min(100, left)); left -= 100; }
        }

        // one connection: ping every PingSeconds, read L:AUDIOINHIBIT and write 1 when it is not the wanted one; publish the HMC variables
        static void Session(IntPtr h)
        {
            if (SimConnect_AddToDataDefinition(h, DefineId, Variable, "number", DatatypeFloat64, 0f, uint.MaxValue) != 0)
            {
                Ctx.Log("[ERROR] SimConnect: cannot define " + Variable);
                return;
            }
            Dictionary<string, uint> outIds = new Dictionary<string, uint>();
            lock (outLock)
            {
                uint n = OutputDefineBase;
                foreach (string name in new List<string>(outputs.Keys))
                {
                    if (SimConnect_AddToDataDefinition(h, n, name, "number", DatatypeFloat64, 0f, uint.MaxValue) == 0) outIds[name] = n;
                    else Ctx.Log("[ERROR] SimConnect: cannot define " + name);
                    n++;
                }
                outDirty = true;
            }
            List<string> allNames = new List<string>(watch);
            if (!allNames.Contains(Variable)) allNames.Add(Variable);      // L:AUDIOINHIBIT is watched too: the simulator resets it to 0 at every flight or mission load and it must be 1 again at once
            string[] names = allNames.ToArray();
            int version = watchVersion;
            int groups = (names.Length + WatchGroup - 1) / WatchGroup;
            double[][] last = new double[groups][];
            bool[] have = new bool[groups];
            for (int g = 0; g < groups; g++)
            {
                int from = g * WatchGroup, count = Math.Min(WatchGroup, names.Length - from);
                last[g] = new double[count];
                for (int i = 0; i < count; i++)
                    if (SimConnect_AddToDataDefinition(h, WatchDefineBase + (uint)g, names[from + i], names[from + i].StartsWith("L:", StringComparison.Ordinal) ? "number" : "enum", DatatypeFloat64, 0f, uint.MaxValue) != 0) Ctx.Log("[ERROR] SimConnect: cannot define " + names[from + i]);
                if (SimConnect_RequestDataOnSimObject(h, WatchRequestBase + (uint)g, WatchDefineBase + (uint)g, ObjectUser, PeriodSimFrame, FlagChanged, 0, 0, 0) != 0)
                    Ctx.Log("[ERROR] SimConnect: cannot watch the variables of group " + (g + 1));
            }
            if (names.Length > 0) Ctx.Log("[INFO] SimConnect: watching " + names.Length + " variables");
            var atcDefined = OnAtcStrings != null &&
                SimConnect_AddToDataDefinition(h, CallDefineId, "ATC ID", null, DatatypeString32, 0f, uint.MaxValue) == 0 &&
                SimConnect_AddToDataDefinition(h, CallDefineId, "ATC AIRLINE", null, DatatypeString32, 0f, uint.MaxValue) == 0 &&
                SimConnect_AddToDataDefinition(h, CallDefineId, "ATC FLIGHT NUMBER", null, DatatypeString32, 0f, uint.MaxValue) == 0;
            var nextAtc = DateTime.UtcNow;
            Ctx.Log("[INFO] SimConnect: connected to the simulator");
            SetStatus("SimConnect: connected, " + Variable + " not read yet");
            connected = true;
            DateTime nextPing = DateTime.UtcNow;
            DateTime lastAnswer = DateTime.UtcNow;
            bool announced = false;
            try
            {
                while (!stop)
                {
                    DateTime now = DateTime.UtcNow;
                    if (watchVersion != version) { Ctx.Log("[INFO] SimConnect: the list of watched variables changed, reconnecting"); return; }
                    bool inhibit = enabled();
                    if (now >= nextPing)
                    {
                        nextPing = now.AddSeconds(PingSeconds);
                        if (inhibit && SimConnect_RequestDataOnSimObject(h, RequestId, DefineId, ObjectUser, PeriodOnce, 0, 0, 0, 0) != 0)
                        {
                            Ctx.Log("[INFO] SimConnect: connection lost");
                            SetStatus("SimConnect: connection lost");
                            return;
                        }
                        outDirty = true;     // the simulator can reset the variables (new flight): write them again at every ping
                        if (!inhibit) { lastAnswer = now; SetStatus("SimConnect: connected (audio inhibit option off)"); }
                    }
                    if (inhibit && (now - lastAnswer).TotalSeconds > 30)
                    {
                        Ctx.Log("[WARN] SimConnect: no answer for 30 s, reconnecting");
                        SetStatus("SimConnect: no answer, reconnecting");
                        return;
                    }
                    if (atcDefined && now >= nextAtc)
                    {
                        nextAtc = now.AddSeconds(30);
                        SimConnect_RequestDataOnSimObject(h, CallRequestId, CallDefineId, ObjectUser, PeriodOnce, 0, 0, 0, 0);
                    }
                    if (outDirty)
                    {
                        outDirty = false;
                        Dictionary<string, double> copy;
                        lock (outLock) { copy = new Dictionary<string, double>(outputs); }
                        foreach (KeyValuePair<string, double> kv in copy)
                        {
                            uint id;
                            if (outIds.TryGetValue(kv.Key, out id) && !Write(h, id, kv.Value, kv.Key))
                            {
                                SetStatus("SimConnect: connection lost");
                                return;
                            }
                        }
                    }
                    IntPtr data; uint size;
                    bool got = false;
                    while (SimConnect_GetNextDispatch(h, out data, out size) == 0 && data != IntPtr.Zero)
                    {
                        got = true;
                        int id = Marshal.ReadInt32(data, 8);
                        if (id == RecvQuit)
                        {
                            Ctx.Log("[INFO] SimConnect: the simulator closed the connection");
                            SetStatus("SimConnect: simulator closed");
                            return;
                        }
                        if (id == RecvException)
                        {
                            if (exceptionsLogged++ < 8) Ctx.Log("[WARN] SimConnect exception " + Marshal.ReadInt32(data, 12) + " (send id " + Marshal.ReadInt32(data, 16) + ", index " + Marshal.ReadInt32(data, 20) + ")");
                        }
                        else if (id == RecvSimObjectData && size >= DataOffset + 96 && Marshal.ReadInt32(data, 12) == (int)CallRequestId)
                        {
                            var callback = OnAtcStrings;
                            if (callback != null)
                                try { callback(AtcString(data, 0), AtcString(data, 1), AtcString(data, 2)); }
                                catch (Exception ex) { Ctx.Log("[ERROR] ATC strings: " + ex.Message); }
                        }
                        else if (id == RecvSimObjectData && Marshal.ReadInt32(data, 12) >= (int)WatchRequestBase && Marshal.ReadInt32(data, 12) < (int)WatchRequestBase + groups)
                        {
                            int g = Marshal.ReadInt32(data, 12) - (int)WatchRequestBase;
                            int n = Math.Min(Marshal.ReadInt32(data, 36), last[g].Length);
                            for (int i = 0; i < n; i++)
                            {
                                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, DataOffset + 8 * i));
                                if (have[g] && v == last[g][i]) continue;
                                double old = last[g][i];
                                last[g][i] = v;
                                if (names[g * WatchGroup + i] == Variable && enabled() && Math.Abs(v - Wanted) > 0.001)
                                {
                                    if (!inhibitBaseline.HasValue) inhibitBaseline = v;
                                    inhibitWasChanged |= Write(h, DefineId, Wanted, Variable);
                                    Ctx.Log("[INFO] SimConnect: " + Variable + " was " + v.ToString("0.###", CultureInfo.InvariantCulture) + ", set to 1 at once");
                                    SetStatus("SimConnect: connected, " + Variable + " set to 1");
                                }
                                Action<string, double, double, bool> cb = OnWatch;
                                if (cb != null) { try { cb(names[g * WatchGroup + i], old, v, !have[g]); } catch (Exception ex) { Ctx.Log("[ERROR] watch: " + ex.Message); } }
                            }
                            have[g] = true;
                        }
                        else if (id == RecvSimObjectData && Marshal.ReadInt32(data, 12) == (int)RequestId)
                        {
                            lastAnswer = DateTime.UtcNow;
                            double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, DataOffset));
                            if (Math.Abs(value - Wanted) > 0.001)
                            {
                                if (!inhibitBaseline.HasValue) inhibitBaseline = value;
                                inhibitWasChanged |= Write(h, DefineId, Wanted, Variable);
                                Ctx.Log("[INFO] SimConnect: " + Variable + " was " + value.ToString("0.###", CultureInfo.InvariantCulture) + ", set to 1");
                                SetStatus("SimConnect: connected, " + Variable + " set to 1");
                            }
                            else
                            {
                                if (!announced) { announced = true; Ctx.Log("[INFO] SimConnect: " + Variable + " is already 1"); }
                                SetStatus("SimConnect: connected, " + Variable + " = 1");
                            }
                        }
                    }
                    if (!got) Thread.Sleep(100);
                }
            }
            finally
            {
                // Restore only a value this session changed; never force the user's prior choice.
                if (stop)
                {
                    if (inhibitWasChanged && inhibitBaseline.HasValue)
                        Write(h, DefineId, inhibitBaseline.Value, Variable);
                    foreach (var name in new[] { VarStatus, VarPackVersion })
                    {
                        uint define;
                        if (outIds.TryGetValue(name, out define)) Write(h, define, 0, name);
                    }
                }
            }
        }

        // false when the connection is gone
        static bool Write(IntPtr h, uint define, double value, string name)
        {
            IntPtr mem = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(mem, BitConverter.DoubleToInt64Bits(value));
                int rc = SimConnect_SetDataOnSimObject(h, define, ObjectUser, 0, 1, 8, mem);
                if (rc != 0) Ctx.Log("[ERROR] SimConnect: writing " + name + " failed (" + rc + ")");
                return rc == 0;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
    }
}
