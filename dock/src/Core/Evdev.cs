using System;
using System.Collections.Generic;
using System.Globalization;

namespace Launcher.Core
{
    /// <summary>
    /// Decodes Linux <c>struct input_event</c> records read from /dev/input/eventN and reports
    /// key presses. The record is a timeval (two longs), then type (u16), code (u16) and value
    /// (s32): 16 bytes on the 32-bit ARM TV, 24 on a 64-bit desktop. A read may end in the
    /// middle of a record, so leftovers are kept for the next call. Presses only: releases
    /// (value 0) and auto-repeat (value 2) are ignored.
    /// </summary>
    internal sealed class EvdevReader
    {
        public const int EvSyn = 0;
        public const int EvKey = 1;
        private readonly int _record;
        private readonly int _typeOffset;
        private readonly byte[] _carry;
        private int _carried;

        public EvdevReader() : this(IntPtr.Size == 4 ? 16 : 24) { }

        public EvdevReader(int recordSize)
        {
            if (recordSize != 16 && recordSize != 24) throw new ArgumentOutOfRangeException("recordSize");
            _record = recordSize;
            _typeOffset = recordSize - 8;
            _carry = new byte[recordSize];
        }

        public int RecordSize { get { return _record; } }

        public List<int> Feed(byte[] data, int count)
        {
            var presses = new List<int>();
            Feed(data, count, presses);
            return presses;
        }

        /// <summary>Allocation-free variant: appends the presses to a list the caller reuses.</summary>
        public void Feed(byte[] data, int count, List<int> presses)
        {
            int i = 0;
            while (i < count)
            {
                // complete a record left over from the previous read first
                int need = _record - _carried;
                int take = Math.Min(need, count - i);
                Buffer.BlockCopy(data, i, _carry, _carried, take);
                _carried += take;
                i += take;
                if (_carried < _record) break;
                _carried = 0;
                Decode(_carry, presses);
            }
        }

        private void Decode(byte[] rec, List<int> into)
        {
            int type = rec[_typeOffset] | (rec[_typeOffset + 1] << 8);
            int code = rec[_typeOffset + 2] | (rec[_typeOffset + 3] << 8);
            int value = rec[_typeOffset + 4] | (rec[_typeOffset + 5] << 8) | (rec[_typeOffset + 6] << 16) | (rec[_typeOffset + 7] << 24);
            if (type == EvKey && value == 1) into.Add(code);
        }
    }

    internal enum EvdevEventAction { None, Forward, ForwardAndNotify, HomePress, Suppress }

    /// <summary>
    /// Stateful, allocation-free record filter for an exclusively grabbed remote (<c>EVIOCGRAB</c>):
    /// swallows Home key presses, repeats and releases before they reach <c>/dev/uinput</c>, forwards
    /// all other keys and sync frames, and tracks pressed keys for clean release on teardown.
    /// </summary>
    internal sealed class EvdevFilter
    {
        private readonly int _record;
        private readonly int _typeOffset;
        private readonly HashSet<int> _homeCodes;
        private readonly HashSet<ushort> _pressedForwarded = new HashSet<ushort>();
        private readonly HashSet<ushort> _pressedHome = new HashSet<ushort>();
        private readonly byte[] _carry;
        private int _carried;

        public EvdevFilter(HashSet<int> homeCodes) : this(homeCodes, IntPtr.Size == 4 ? 16 : 24) { }

        public EvdevFilter(HashSet<int> homeCodes, int recordSize)
        {
            if (recordSize != 16 && recordSize != 24) throw new ArgumentOutOfRangeException("recordSize");
            _record = recordSize;
            _typeOffset = recordSize - 8;
            _homeCodes = homeCodes ?? new HashSet<int>();
            _carry = new byte[recordSize];
        }

        public int RecordSize { get { return _record; } }
        public byte[] CurrentRecord { get { return _carry; } }

        /// <summary>
        /// Consumes the next complete record from <paramref name="data"/> starting at <paramref name="offset"/>.
        /// When true is returned, <see cref="CurrentRecord"/> holds the <see cref="RecordSize"/>-byte record.
        /// </summary>
        public bool Next(byte[] data, int count, ref int offset, out EvdevEventAction action, out ushort code)
        {
            action = EvdevEventAction.None;
            code = 0;
            if (offset >= count) return false;
            int need = _record - _carried;
            int take = Math.Min(need, count - offset);
            Buffer.BlockCopy(data, offset, _carry, _carried, take);
            _carried += take;
            offset += take;
            if (_carried < _record) return false;
            _carried = 0;
            action = Evaluate(_carry, out code);
            return true;
        }

        private EvdevEventAction Evaluate(byte[] rec, out ushort code)
        {
            int type = rec[_typeOffset] | (rec[_typeOffset + 1] << 8);
            code = (ushort)(rec[_typeOffset + 2] | (rec[_typeOffset + 3] << 8));
            int value = rec[_typeOffset + 4] | (rec[_typeOffset + 5] << 8) | (rec[_typeOffset + 6] << 16) | (rec[_typeOffset + 7] << 24);
            if (type == EvdevReader.EvSyn) return EvdevEventAction.Forward;
            if (type != EvdevReader.EvKey) return EvdevEventAction.None;

            if (_homeCodes.Contains(code))
            {
                if (value == 1)
                {
                    _pressedHome.Add(code);
                    return EvdevEventAction.HomePress;
                }
                if (value == 0) _pressedHome.Remove(code);
                return EvdevEventAction.Suppress;
            }

            if (value == 0)
            {
                _pressedForwarded.Remove(code);
                return EvdevEventAction.Forward;
            }
            _pressedForwarded.Add(code);
            return value == 1 ? EvdevEventAction.ForwardAndNotify : EvdevEventAction.Forward;
        }

        public ushort[] DrainPressedForwarded()
        {
            var keys = new ushort[_pressedForwarded.Count];
            _pressedForwarded.CopyTo(keys);
            _pressedForwarded.Clear();
            _pressedHome.Clear();
            return keys;
        }

        public byte[] BuildKeyEvent(ushort code, int value)
        {
            var rec = new byte[_record];
            rec[_typeOffset] = EvdevReader.EvKey;
            rec[_typeOffset + 2] = (byte)code;
            rec[_typeOffset + 3] = (byte)(code >> 8);
            rec[_typeOffset + 4] = (byte)value;
            rec[_typeOffset + 5] = (byte)(value >> 8);
            rec[_typeOffset + 6] = (byte)(value >> 16);
            rec[_typeOffset + 7] = (byte)(value >> 24);
            return rec;
        }

        public byte[] BuildSyncEvent() { return new byte[_record]; }
    }

    internal sealed class InputDeviceInfo
    {
        public readonly string Node;
        public readonly string Name;
        public readonly string Phys;
        public readonly ushort Bus;
        public readonly ushort Vendor;
        public readonly ushort Product;
        public readonly ushort Version;
        public readonly bool IsVetroForwarded;

        public InputDeviceInfo(string node, string name, string phys, ushort bus, ushort vendor, ushort product, ushort version, bool isVetroForwarded)
        {
            Node = node;
            Name = name;
            Phys = phys ?? "";
            Bus = bus;
            Vendor = vendor;
            Product = product;
            Version = version;
            IsVetroForwarded = isVetroForwarded;
        }
    }

    internal static class HomeKeyMap
    {
        public static HashSet<int> Parse(string xkbLayoutText, int extraCode)
        {
            var codes = new HashSet<int>();
            if (extraCode > 0 && extraCode < 768) codes.Add(extraCode);
            if (string.IsNullOrEmpty(xkbLayoutText)) return codes;
            foreach (string raw in xkbLayoutText.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                int code;
                if (parts.Length >= 2 && parts[0] == "XF86Home" && int.TryParse(parts[1], out code) && code > 0 && code < 768)
                    codes.Add(code);
            }
            return codes;
        }
    }

    /// <summary>Finds the remote-control input nodes in the text of /proc/bus/input/devices.</summary>
    internal static class InputDevices
    {
        public const string DockForwardedPhys = "vetro-dock-forwarded";
        public const string RemapperForwardedPhys = "vetro-forwarded-remote";

        // Vetro's filter takes the real remote exclusively (EVIOCGRAB) and re-emits the keys it lets through on its own
        // node under the real remote's name, so these keywords match that node too while the physical one goes silent.
        private static readonly string[] Remote = { "smart control", "rc device", "mcirb", "smart remote", "tv remote" };
        private static readonly string[] Excluded = { "keyboard", "mouse", "panel" };

        private static bool Any(string lower, string[] words)
        {
            foreach (string w in words) if (lower.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // "event" followed by digits only, as a whole word of the Handlers line.
        private static string EventNode(string handlers)
        {
            foreach (string t in handlers.Split(' '))
            {
                if (t.Length <= 5 || !t.StartsWith("event", StringComparison.Ordinal)) continue;
                bool digits = true;
                for (int i = 5; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') { digits = false; break; }
                if (digits) return "/dev/input/" + t;
            }
            return null;
        }

        public static bool HasAnyKeyCapability(string sysfsKeyMask)
        {
            if (sysfsKeyMask == null) return true;
            foreach (char c in sysfsKeyMask)
                if ((c >= '1' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) return true;
            return false;
        }

        public static bool HasKeyBit(string sysfsKeyMask, int code, int wordBits = 0)
        {
            if (string.IsNullOrWhiteSpace(sysfsKeyMask) || code < 0 || code >= 768) return false;
            string[] chunks = sysfsKeyMask.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (chunks.Length == 0) return false;
            if (wordBits <= 0)
            {
                bool anyWide = false;
                foreach (string chunk in chunks) if (chunk.Length > 8) { anyWide = true; break; }
                if (anyWide) wordBits = 64;
                else if (chunks.Length > 12) wordBits = 32;
                else wordBits = IntPtr.Size == 8 ? 64 : 32;
            }
            int wordIndex = code / wordBits;
            int bitIndex = code % wordBits;
            int pos = chunks.Length - 1 - wordIndex;
            if (pos < 0 || pos >= chunks.Length) return false;
            ulong word;
            if (!ulong.TryParse(chunks[pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out word)) return false;
            return ((word >> bitIndex) & 1UL) != 0;
        }

        /// <summary>Vetro's remote selection, which also matches Remapper's forwarded node while skipping the dock's own forwarded node.</summary>
        public static List<string> RemoteNodes(string procBusInputDevices)
        {
            var devices = RemoteDevices(procBusInputDevices);
            var nodes = new List<string>(devices.Count);
            foreach (InputDeviceInfo d in devices) nodes.Add(d.Node);
            return nodes;
        }

        public static List<InputDeviceInfo> RemoteDevices(string procBusInputDevices)
        {
            var devices = new List<InputDeviceInfo>();
            string name = null, node = null, phys = null;
            ushort bus = 0, vendor = 0, product = 0, version = 0;
            foreach (string raw in (procBusInputDevices ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    Flush(devices, name, node, phys, bus, vendor, product, version);
                    name = null; node = null; phys = null; bus = 0; vendor = 0; product = 0; version = 0;
                    continue;
                }
                if (line.StartsWith("I: ", StringComparison.Ordinal))
                    ParseIdentity(line, out bus, out vendor, out product, out version);
                else if (line.StartsWith("N: Name=\"", StringComparison.Ordinal) && line.EndsWith("\"", StringComparison.Ordinal) && line.Length >= 10)
                    name = line.Substring(9, line.Length - 10);
                else if (line.StartsWith("P: Phys=", StringComparison.Ordinal))
                    phys = line.Substring(8).Trim();
                else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
                    node = EventNode(line.Substring(12));
            }
            Flush(devices, name, node, phys, bus, vendor, product, version);
            return devices;
        }

        private static void ParseIdentity(string line, out ushort bus, out ushort vendor, out ushort product, out ushort version)
        {
            bus = 0; vendor = 0; product = 0; version = 0;
            foreach (string token in line.Substring(3).Split(' '))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                string key = token.Substring(0, eq), val = token.Substring(eq + 1);
                ushort parsed;
                if (!ushort.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) continue;
                if (key == "Bus") bus = parsed;
                else if (key == "Vendor") vendor = parsed;
                else if (key == "Product") product = parsed;
                else if (key == "Version") version = parsed;
            }
        }

        private static void Flush(List<InputDeviceInfo> into, string name, string node, string phys, ushort bus, ushort vendor, ushort product, ushort version)
        {
            if (name == null || node == null) return;
            if (string.Equals(phys, DockForwardedPhys, StringComparison.Ordinal)) return;
            string lower = name.ToLowerInvariant();
            if (Any(lower, Remote) && !Any(lower, Excluded))
                into.Add(new InputDeviceInfo(node, name, phys, bus, vendor, product, version, string.Equals(phys, RemapperForwardedPhys, StringComparison.Ordinal)));
        }
    }
}
