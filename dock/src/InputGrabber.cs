using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Launcher.Core;
using Launcher.Native;

namespace Launcher
{
    /// <summary>
    /// Exclusively grabs physical remote evdev nodes (<c>EVIOCGRAB</c>) on a dedicated poll thread
    /// and forwards every key except Home to a virtual <c>/dev/uinput</c> device. Because the input
    /// thread never touches EFL or runs shell commands, remote navigation never stalls even if the UI
    /// or an app launch is busy. Falls back gracefully to passive reading if <c>/dev/uinput</c> or
    /// <c>EVIOCGRAB</c> is unavailable or paused for Vetro's Remapper.
    /// </summary>
    internal sealed class InputGrabber : IDisposable
    {
        public const byte TagHome = 1;
        public const byte TagNav = 2;
        public const byte TagRescan = 3;

        private const int KeyMaximum = 0x2ff;
        private const int UinputUserDeviceSize = 1116;
        private const int UinputNameBytes = 80;

        private sealed class GrabbedNode : IDisposable
        {
            public readonly InputDeviceInfo Info;
            public readonly EvdevFilter Filter;
            public int InputFd;
            public int UinputFd;
            public bool Grabbed;

            public GrabbedNode(InputDeviceInfo info, HashSet<int> homeCodes)
            {
                Info = info;
                Filter = new EvdevFilter(homeCodes);
                InputFd = -1;
                UinputFd = -1;
            }

            public void Dispose()
            {
                if (UinputFd >= 0)
                {
                    try
                    {
                        ushort[] held = Filter.DrainPressedForwarded();
                        if (held.Length > 0)
                        {
                            foreach (ushort code in held)
                            {
                                byte[] rel = Filter.BuildKeyEvent(code, 0);
                                WriteBytes(UinputFd, rel, rel.Length);
                            }
                            byte[] syn = Filter.BuildSyncEvent();
                            WriteBytes(UinputFd, syn, syn.Length);
                        }
                    }
                    catch (Exception) { }
                    Libc.ioctl_void(UinputFd, (UIntPtr)Libc.UI_DEV_DESTROY);
                    Libc.close(UinputFd);
                    UinputFd = -1;
                }
                if (Grabbed && InputFd >= 0)
                {
                    Libc.ioctl_int(InputFd, (UIntPtr)Libc.EVIOCGRAB, 0);
                    Grabbed = false;
                }
                if (InputFd >= 0)
                {
                    Libc.close(InputFd);
                    InputFd = -1;
                }
            }
        }

        private readonly HashSet<int> _homeCodes;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private readonly Dictionary<string, InputDeviceInfo> _desired = new Dictionary<string, InputDeviceInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, GrabbedNode> _active = new Dictionary<string, GrabbedNode>(StringComparer.Ordinal);
        private readonly HashSet<string> _failedNodes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _fallbackNodes = new HashSet<string>(StringComparer.Ordinal);
        private readonly ManualResetEventSlim _ungrabbedEvent = new ManualResetEventSlim(true);

        private int _eventReadFd = -1, _eventWriteFd = -1;
        private int _wakeReadFd = -1, _wakeWriteFd = -1;
        private bool _grabWanted = true;
        private bool _stopping;
        private Thread _thread;

        public int EventReadFd { get { return _eventReadFd; } }

        public InputGrabber(HashSet<int> homeCodes, Action<string> log)
        {
            _homeCodes = homeCodes;
            _log = log;
        }

        public bool Start()
        {
            int[] evPipe = new int[2];
            int[] wakePipe = new int[2];
            if (Libc.pipe(evPipe) != 0)
            {
                _log("grabber: pipe failed errno=" + Marshal.GetLastWin32Error());
                return false;
            }
            if (Libc.pipe(wakePipe) != 0)
            {
                Libc.close(evPipe[0]); Libc.close(evPipe[1]);
                _log("grabber: wake pipe failed errno=" + Marshal.GetLastWin32Error());
                return false;
            }
            SetNonBlocking(evPipe[0]);
            SetNonBlocking(evPipe[1]);
            SetNonBlocking(wakePipe[0]);
            SetNonBlocking(wakePipe[1]);
            _eventReadFd = evPipe[0];
            _eventWriteFd = evPipe[1];
            _wakeReadFd = wakePipe[0];
            _wakeWriteFd = wakePipe[1];

            _thread = new Thread(RunLoop) { IsBackground = true, Name = "vetro-input-grabber" };
            _thread.Start();
            return true;
        }

        private static void SetNonBlocking(int fd)
        {
            int flags = Libc.fcntl(fd, Libc.F_GETFL, 0);
            if (flags >= 0) Libc.fcntl(fd, Libc.F_SETFL, flags | Libc.O_NONBLOCK);
        }

        public void SetGrabWanted(bool wanted)
        {
            lock (_sync)
            {
                if (_grabWanted == wanted) return;
                _grabWanted = wanted;
                _fallbackNodes.Clear();
                if (!wanted && _active.Count > 0) _ungrabbedEvent.Reset();
            }
            Wake();
            if (!wanted) _ungrabbedEvent.Wait(250);
        }

        public void UpdateDevices(List<InputDeviceInfo> physicalDevices)
        {
            lock (_sync)
            {
                _desired.Clear();
                if (physicalDevices != null)
                    foreach (InputDeviceInfo d in physicalDevices)
                        if (!d.IsVetroForwarded) _desired[d.Node] = d;
            }
            Wake();
        }

        public bool ShouldWatchPassively(InputDeviceInfo device)
        {
            if (device.IsVetroForwarded) return true;
            lock (_sync) return !_grabWanted || _fallbackNodes.Contains(device.Node);
        }

        private void Wake()
        {
            if (_wakeWriteFd < 0) return;
            byte[] b = { 1 };
            Libc.write(_wakeWriteFd, b, (UIntPtr)1);
        }

        private void NotifyMain(byte tag, ushort code)
        {
            if (_eventWriteFd < 0) return;
            byte[] msg = { tag, (byte)code, (byte)(code >> 8), 0 };
            Libc.write(_eventWriteFd, msg, (UIntPtr)4);
        }

        private void RunLoop()
        {
            var readBuf = new byte[16 * 32];
            var wakeBuf = new byte[64];
            var activeList = new List<GrabbedNode>();

            try
            {
                while (true)
                {
                    if (!SyncDevices(activeList)) break;

                    var pfds = new Libc.PollFd[1 + activeList.Count];
                    pfds[0] = new Libc.PollFd { fd = _wakeReadFd, events = Libc.POLLIN };
                    for (int i = 0; i < activeList.Count; i++)
                        pfds[1 + i] = new Libc.PollFd { fd = activeList[i].InputFd, events = Libc.POLLIN };

                    int ready = Libc.poll(pfds, (UIntPtr)pfds.Length, -1);
                    if (ready < 0)
                    {
                        if (Marshal.GetLastWin32Error() == Libc.EINTR) continue;
                        _log("grabber: poll error errno=" + Marshal.GetLastWin32Error());
                        break;
                    }

                    if ((pfds[0].revents & Libc.POLLIN) != 0)
                    {
                        while ((long)Libc.read(_wakeReadFd, wakeBuf, (UIntPtr)wakeBuf.Length) > 0) { }
                    }

                    bool lostAny = false;
                    for (int i = 0; i < activeList.Count; i++)
                    {
                        short rev = pfds[1 + i].revents;
                        GrabbedNode node = activeList[i];
                        if ((rev & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0)
                        {
                            DropGrabbed(node);
                            lostAny = true;
                            continue;
                        }
                        if ((rev & Libc.POLLIN) == 0) continue;

                        while (true)
                        {
                            long n = (long)Libc.read(node.InputFd, readBuf, (UIntPtr)readBuf.Length);
                            if (n < 0)
                            {
                                int err = Marshal.GetLastWin32Error();
                                if (err == Libc.EAGAIN || err == Libc.EINTR) break;
                                DropGrabbed(node);
                                lostAny = true;
                                break;
                            }
                            if (n == 0)
                            {
                                DropGrabbed(node);
                                lostAny = true;
                                break;
                            }
                            int offset = 0;
                            EvdevEventAction action;
                            ushort code;
                            while (node.Filter.Next(readBuf, (int)n, ref offset, out action, out code))
                            {
                                switch (action)
                                {
                                    case EvdevEventAction.HomePress:
                                        NotifyMain(TagHome, code);
                                        break;
                                    case EvdevEventAction.ForwardAndNotify:
                                        WriteBytes(node.UinputFd, node.Filter.CurrentRecord, node.Filter.RecordSize);
                                        NotifyMain(TagNav, code);
                                        break;
                                    case EvdevEventAction.Forward:
                                        WriteBytes(node.UinputFd, node.Filter.CurrentRecord, node.Filter.RecordSize);
                                        break;
                                }
                            }
                        }
                    }

                    if (lostAny) NotifyMain(TagRescan, 0);
                }
            }
            catch (Exception ex)
            {
                _log("grabber: fatal " + ex.Message);
            }
            finally
            {
                ReleaseAll();
                NotifyMain(TagRescan, 0);
            }
        }

        private bool SyncDevices(List<GrabbedNode> activeList)
        {
            List<InputDeviceInfo> toAdd = null;
            List<GrabbedNode> toRemove = null;
            bool stopping;
            bool grabWanted;

            lock (_sync)
            {
                stopping = _stopping;
                grabWanted = _grabWanted;

                foreach (var kv in _active)
                {
                    if (stopping || !grabWanted || !_desired.ContainsKey(kv.Key))
                    {
                        if (toRemove == null) toRemove = new List<GrabbedNode>();
                        toRemove.Add(kv.Value);
                    }
                }
                if (toRemove != null)
                {
                    foreach (GrabbedNode n in toRemove) _active.Remove(n.Info.Node);
                }

                if (!stopping && grabWanted)
                {
                    foreach (var kv in _desired)
                    {
                        if (!_active.ContainsKey(kv.Key) && !_fallbackNodes.Contains(kv.Key))
                        {
                            if (toAdd == null) toAdd = new List<InputDeviceInfo>();
                            toAdd.Add(kv.Value);
                        }
                    }
                }
            }

            if (toRemove != null)
            {
                foreach (GrabbedNode n in toRemove)
                {
                    n.Dispose();
                    _log("grabber: released " + n.Info.Node);
                }
            }

            bool fallbackNeeded = false;
            if (toAdd != null)
            {
                foreach (InputDeviceInfo info in toAdd)
                {
                    GrabbedNode grabbed = TryGrab(info);
                    if (grabbed != null)
                    {
                        lock (_sync)
                        {
                            if (_stopping || !_grabWanted) grabbed.Dispose();
                            else { _active[info.Node] = grabbed; _failedNodes.Remove(info.Node); _fallbackNodes.Remove(info.Node); }
                        }
                    }
                    else
                    {
                        lock (_sync) _fallbackNodes.Add(info.Node);
                        fallbackNeeded = true;
                    }
                }
            }

            lock (_sync)
            {
                activeList.Clear();
                foreach (var kv in _active) activeList.Add(kv.Value);
                if (_active.Count == 0) _ungrabbedEvent.Set();
            }

            if (fallbackNeeded) NotifyMain(TagRescan, 0);
            return !stopping;
        }

        private void DropGrabbed(GrabbedNode node)
        {
            lock (_sync)
            {
                _active.Remove(node.Info.Node);
                _desired.Remove(node.Info.Node);
                _fallbackNodes.Remove(node.Info.Node);
            }
            node.Dispose();
            _log("grabber: lost " + node.Info.Node);
        }

        private void ReleaseAll()
        {
            var copy = new List<GrabbedNode>();
            lock (_sync)
            {
                foreach (var kv in _active) copy.Add(kv.Value);
                _active.Clear();
                _ungrabbedEvent.Set();
            }
            foreach (GrabbedNode n in copy) n.Dispose();
        }

        private GrabbedNode TryGrab(InputDeviceInfo info)
        {
            var node = new GrabbedNode(info, _homeCodes);
            try
            {
                node.InputFd = Libc.open(info.Node, Libc.O_RDONLY | Libc.O_NONBLOCK);
                if (node.InputFd < 0)
                {
                    LogOnce(info.Node, "grabber: cannot open " + info.Node + " errno=" + Marshal.GetLastWin32Error());
                    node.Dispose();
                    return null;
                }

                node.UinputFd = OpenUinput(info);
                if (node.UinputFd < 0)
                {
                    LogOnce(info.Node, "grabber: uinput unavailable for " + info.Node + " errno=" + Marshal.GetLastWin32Error());
                    node.Dispose();
                    return null;
                }

                if (Libc.ioctl_int(node.InputFd, (UIntPtr)Libc.EVIOCGRAB, 1) != 0)
                {
                    LogOnce(info.Node, "grabber: EVIOCGRAB failed on " + info.Node + " errno=" + Marshal.GetLastWin32Error());
                    node.Dispose();
                    return null;
                }
                node.Grabbed = true;
                _log("grabber: exclusively guarding Home on " + info.Node + " (" + info.Name + ")");
                return node;
            }
            catch (Exception ex)
            {
                LogOnce(info.Node, "grabber: setup error on " + info.Node + ": " + ex.Message);
                node.Dispose();
                return null;
            }
        }

        private void LogOnce(string node, string message)
        {
            lock (_sync) if (!_failedNodes.Add(node)) return;
            _log(message);
        }

        private static int OpenUinput(InputDeviceInfo source)
        {
            int fd = Libc.open("/dev/uinput", Libc.O_WRONLY | Libc.O_NONBLOCK);
            if (fd < 0) return -1;
            try
            {
                if (Libc.ioctl_int(fd, (UIntPtr)Libc.UI_SET_EVBIT, EvdevReader.EvSyn) != 0) { Libc.close(fd); return -1; }
                if (Libc.ioctl_int(fd, (UIntPtr)Libc.UI_SET_EVBIT, EvdevReader.EvKey) != 0) { Libc.close(fd); return -1; }
                for (int code = 0; code <= KeyMaximum; code++)
                {
                    if (Libc.ioctl_int(fd, (UIntPtr)Libc.UI_SET_KEYBIT, code) != 0) { Libc.close(fd); return -1; }
                }
                UIntPtr setPhys = (UIntPtr)(IntPtr.Size == 8 ? Libc.UI_SET_PHYS_64 : Libc.UI_SET_PHYS_32);
                if (Libc.ioctl_str(fd, setPhys, InputDevices.DockForwardedPhys) != 0) { Libc.close(fd); return -1; }

                var def = new byte[UinputUserDeviceSize];
                byte[] nameBytes = Encoding.UTF8.GetBytes(source.Name ?? "Vetro Remote");
                int copyLen = Math.Min(nameBytes.Length, UinputNameBytes - 1);
                Buffer.BlockCopy(nameBytes, 0, def, 0, copyLen);
                ushort bus = source.Bus == 0 ? (ushort)6 : source.Bus;
                WriteUInt16(def, 80, bus);
                WriteUInt16(def, 82, source.Vendor);
                WriteUInt16(def, 84, source.Product);
                WriteUInt16(def, 86, source.Version);
                if (!WriteBytes(fd, def, def.Length)) { Libc.close(fd); return -1; }
                if (Libc.ioctl_void(fd, (UIntPtr)Libc.UI_DEV_CREATE) != 0) { Libc.close(fd); return -1; }
                return fd;
            }
            catch
            {
                Libc.close(fd);
                return -1;
            }
        }

        private static void WriteUInt16(byte[] dst, int offset, ushort value)
        {
            dst[offset] = (byte)value;
            dst[offset + 1] = (byte)(value >> 8);
        }

        private static bool WriteBytes(int fd, byte[] buf, int count)
        {
            long n = (long)Libc.write(fd, buf, (UIntPtr)count);
            return n == count;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_stopping) return;
                _stopping = true;
            }
            Wake();
            if (_thread != null && _thread.IsAlive) _thread.Join(500);
            if (_wakeReadFd >= 0) { Libc.close(_wakeReadFd); _wakeReadFd = -1; }
            if (_wakeWriteFd >= 0) { Libc.close(_wakeWriteFd); _wakeWriteFd = -1; }
            if (_eventReadFd >= 0) { Libc.close(_eventReadFd); _eventReadFd = -1; }
            if (_eventWriteFd >= 0) { Libc.close(_eventWriteFd); _eventWriteFd = -1; }
        }
    }
}
