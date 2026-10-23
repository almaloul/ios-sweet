// Property lists: XML and binary (bplist00), read and write.
// Values: PDict, List<object>, string, long, ulong (only above long.MaxValue), double,
// bool, byte[], DateTime, PUid.
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Xml;

namespace SweetEmu.Core;

/// <summary>Insertion-ordered string-keyed dictionary (plist dict).</summary>
public sealed class PDict : IEnumerable<KeyValuePair<string, object>>
{
    readonly List<string> keys = new();
    readonly Dictionary<string, object> map = new();

    public int Count => keys.Count;
    public IEnumerable<string> Keys => keys;

    public object this[string key]
    {
        get => map[key];
        set
        {
            if (!map.ContainsKey(key)) keys.Add(key);
            map[key] = value;
        }
    }

    public bool ContainsKey(string key) => map.ContainsKey(key);
    public bool TryGetValue(string key, out object value) => map.TryGetValue(key, out value!);
    public object? Get(string key) => map.TryGetValue(key, out var v) ? v : null;
    public PDict? Dict(string key) => Get(key) as PDict;
    public string? Str(string key) => Get(key) as string;
    public List<object>? Array(string key) => Get(key) as List<object>;

    public bool Remove(string key)
    {
        if (!map.Remove(key)) return false;
        keys.Remove(key);
        return true;
    }

    public void Add(string key, object value) => this[key] = value;

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        foreach (var k in keys) yield return new(k, map[k]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public readonly record struct PUid(ulong Value);

public enum PlistFormat { Xml, Binary }

public static class Plist
{
    public static object Read(byte[] data) => Read(data, out _);

    public static object Read(byte[] data, out PlistFormat format)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 6) == "bplist")
        {
            format = PlistFormat.Binary;
            return BinaryReaderImpl.Read(data);
        }
        format = PlistFormat.Xml;
        return ReadXml(data);
    }

    public static byte[] Write(object root, PlistFormat format) =>
        format == PlistFormat.Binary ? BinaryWriterImpl.Write(root) : WriteXml(root);

    // ---------------- XML ----------------

    static object ReadXml(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true };
        using var r = XmlReader.Create(new MemoryStream(data), settings);
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.Name == "plist")
            {
                if (r.IsEmptyElement) return new PDict();
                r.Read();
                return ReadXmlValue(r);
            }
        }
        throw new InvalidDataException("plist: no <plist> element");
    }

    // Positioned on a value element; returns with the reader after its end tag.
    static object ReadXmlValue(XmlReader r)
    {
        while (r.NodeType != XmlNodeType.Element)
        {
            if (!r.Read()) throw new InvalidDataException("plist: unexpected end");
        }
        string name = r.Name;
        bool empty = r.IsEmptyElement;
        switch (name)
        {
            case "dict":
            {
                var d = new PDict();
                r.Read();
                if (empty) return d;
                while (true)
                {
                    SkipNonElements(r);
                    if (r.NodeType == XmlNodeType.EndElement)
                    {
                        r.Read();
                        return d;
                    }
                    if (r.Name != "key") throw new InvalidDataException("plist: expected <key>");
                    string key = r.ReadElementContentAsString();
                    d[key] = ReadXmlValue(r);
                }
            }
            case "array":
            {
                var a = new List<object>();
                r.Read();
                if (empty) return a;
                while (true)
                {
                    SkipNonElements(r);
                    if (r.NodeType == XmlNodeType.EndElement)
                    {
                        r.Read();
                        return a;
                    }
                    a.Add(ReadXmlValue(r));
                }
            }
            case "true":
            case "false":
                r.Read();
                if (!empty) r.Read();
                return name == "true";
            default:
            {
                string text = empty ? "" : r.ReadElementContentAsString();
                if (empty) r.Read();
                return name switch
                {
                    "string" => text,
                    "integer" => ParseInteger(text.Trim()),
                    "real" => double.Parse(text.Trim(), CultureInfo.InvariantCulture),
                    "data" => Convert.FromBase64String(new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray())),
                    "date" => DateTime.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    _ => throw new InvalidDataException($"plist: unknown element <{name}>"),
                };
            }
        }
    }

    static object ParseInteger(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (long)Convert.ToUInt64(s[2..], 16);
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
        return ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    static void SkipNonElements(XmlReader r)
    {
        while (r.NodeType != XmlNodeType.Element && r.NodeType != XmlNodeType.EndElement)
        {
            if (!r.Read()) throw new InvalidDataException("plist: unexpected end");
        }
    }

    static byte[] WriteXml(object root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteXmlValue(sb, root, 0);
        sb.Append("</plist>\n");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    static void WriteXmlValue(StringBuilder sb, object v, int indent)
    {
        string pad = new('\t', indent);
        switch (v)
        {
            case PDict d:
                if (d.Count == 0)
                {
                    sb.Append(pad).Append("<dict/>\n");
                    break;
                }
                sb.Append(pad).Append("<dict>\n");
                foreach (var (k, val) in d)
                {
                    sb.Append(pad).Append('\t').Append("<key>").Append(Esc(k)).Append("</key>\n");
                    WriteXmlValue(sb, val, indent + 1);
                }
                sb.Append(pad).Append("</dict>\n");
                break;
            case List<object> a:
                if (a.Count == 0)
                {
                    sb.Append(pad).Append("<array/>\n");
                    break;
                }
                sb.Append(pad).Append("<array>\n");
                foreach (var x in a) WriteXmlValue(sb, x, indent + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            case string s:
                sb.Append(pad).Append("<string>").Append(Esc(s)).Append("</string>\n");
                break;
            case bool b:
                sb.Append(pad).Append(b ? "<true/>\n" : "<false/>\n");
                break;
            case long or int or ulong or uint or short or ushort or byte:
                sb.Append(pad).Append("<integer>").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case double or float:
                sb.Append(pad).Append("<real>").Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case byte[] data:
                sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>\n");
                break;
            case DateTime dt:
                sb.Append(pad).Append("<date>").Append(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</date>\n");
                break;
            case PUid u:
                sb.Append(pad).Append("<dict><key>CF$UID</key><integer>").Append(u.Value).Append("</integer></dict>\n");
                break;
            default:
                throw new ArgumentException($"plist: cannot write {v?.GetType().Name ?? "null"}");
        }
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---------------- binary ----------------

    static class BinaryReaderImpl
    {
        public static object Read(byte[] b)
        {
            if (b.Length < 40) throw new InvalidDataException("bplist: too short");
            var t = b.AsSpan(b.Length - 32);
            int offSize = t[6], refSize = t[7];
            long numObjects = BinaryPrimitives.ReadInt64BigEndian(t[8..]);
            long top = BinaryPrimitives.ReadInt64BigEndian(t[16..]);
            long table = BinaryPrimitives.ReadInt64BigEndian(t[24..]);
            var offsets = new long[numObjects];
            for (long i = 0; i < numObjects; i++) offsets[i] = (long)UInt(b, (int)(table + i * offSize), offSize);
            var cache = new object?[numObjects];
            return Obj(b, offsets, refSize, top, cache, 0);
        }

        static ulong UInt(byte[] b, int at, int size)
        {
            ulong v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | b[at + i];
            return v;
        }

        static object Obj(byte[] b, long[] offsets, int refSize, long idx, object?[] cache, int depth)
        {
            if (depth > 512) throw new InvalidDataException("bplist: nesting too deep");
            if (cache[idx] is { } c && c is not PDict && c is not List<object>) return c;
            int at = (int)offsets[idx];
            byte marker = b[at];
            int hi = marker >> 4, lo = marker & 0xF;

            int Count(ref int p)
            {
                if (lo != 0xF) return lo;
                byte m = b[p];
                int n = 1 << (m & 0xF);
                long v = (long)UInt(b, p + 1, n);
                p += 1 + n;
                return (int)v;
            }

            int pos = at + 1;
            object result;
            switch (hi)
            {
                case 0x0:
                    result = lo switch { 0x8 => false, 0x9 => true, _ => throw new InvalidDataException("bplist: null/fill unsupported") };
                    break;
                case 0x1:
                {
                    int n = 1 << lo;
                    if (n == 16)
                    {
                        // 128-bit: keep the low 64 bits as unsigned
                        ulong low = UInt(b, pos + 8, 8);
                        result = low > long.MaxValue ? low : (long)low;
                    }
                    else
                    {
                        ulong v = UInt(b, pos, n);
                        result = n == 8 ? (long)v : (long)v;
                    }
                    break;
                }
                case 0x2:
                    result = lo == 2 ? BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(pos)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(pos));
                    result = Convert.ToDouble(result);
                    break;
                case 0x3:
                    result = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(pos)));
                    break;
                case 0x4:
                {
                    int n = Count(ref pos);
                    result = b.AsSpan(pos, n).ToArray();
                    break;
                }
                case 0x5:
                {
                    int n = Count(ref pos);
                    result = Encoding.ASCII.GetString(b, pos, n);
                    break;
                }
                case 0x6:
                {
                    int n = Count(ref pos);
                    result = Encoding.BigEndianUnicode.GetString(b, pos, 2 * n);
                    break;
                }
                case 0x7:
                {
                    int n = Count(ref pos);
                    result = Encoding.UTF8.GetString(b, pos, n);
                    break;
                }
                case 0x8:
                    result = new PUid(UInt(b, pos, lo + 1));
                    break;
                case 0xA:
                case 0xC:
                {
                    int n = Count(ref pos);
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++)
                        list.Add(Obj(b, offsets, refSize, (long)UInt(b, pos + i * refSize, refSize), cache, depth + 1));
                    result = list;
                    break;
                }
                case 0xD:
                {
                    int n = Count(ref pos);
                    var d = new PDict();
                    for (int i = 0; i < n; i++)
                    {
                        var k = Obj(b, offsets, refSize, (long)UInt(b, pos + i * refSize, refSize), cache, depth + 1);
                        var v = Obj(b, offsets, refSize, (long)UInt(b, pos + (n + i) * refSize, refSize), cache, depth + 1);
                        d[k as string ?? throw new InvalidDataException("bplist: non-string key")] = v;
                    }
                    result = d;
                    break;
                }
                default:
                    throw new InvalidDataException($"bplist: unknown marker 0x{marker:x2}");
            }
            cache[idx] = result;
            return result;
        }
    }

    static class BinaryWriterImpl
    {
        public static byte[] Write(object root)
        {
            var objs = new List<object>();
            var stringIds = new Dictionary<string, int>();
            Flatten(root, objs, stringIds);
            int n = objs.Count;
            int refSize = n < 256 ? 1 : n < 65536 ? 2 : 4;

            var ms = new MemoryStream();
            ms.Write("bplist00"u8);
            var offsets = new long[n];
            // Children refs are assigned in Flatten order: map object -> index via reference identity.
            var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < n; i++)
            {
                if (objs[i] is PDict or List<object>) index[objs[i]] = i;
            }

            int RefOf(object v)
            {
                if (v is string s) return stringIds[s];
                if (v is PDict or List<object>) return index[v];
                return scalarIds[v];
            }

            for (int i = 0; i < n; i++)
            {
                offsets[i] = ms.Position;
                var o = objs[i];
                switch (o)
                {
                    case PDict d:
                        Marker(ms, 0xD, d.Count);
                        foreach (var k in d.Keys) Ref(ms, stringIds[k], refSize);
                        foreach (var (_, v) in d) Ref(ms, RefOf(v), refSize);
                        break;
                    case List<object> a:
                        Marker(ms, 0xA, a.Count);
                        foreach (var v in a) Ref(ms, RefOf(v), refSize);
                        break;
                    case ScalarBox box:
                        WriteScalar(ms, box.Value);
                        break;
                    case string s:
                        WriteString(ms, s);
                        break;
                }
            }

            long tableAt = ms.Position;
            long maxOff = tableAt;
            int offSize = maxOff < 256 ? 1 : maxOff < 65536 ? 2 : maxOff < (1L << 32) ? 4 : 8;
            foreach (var off in offsets) BE(ms, (ulong)off, offSize);
            var trailer = new byte[32];
            trailer[6] = (byte)offSize;
            trailer[7] = (byte)refSize;
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(8), n);
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(16), 0);
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(24), tableAt);
            ms.Write(trailer);
            return ms.ToArray();
        }

        // Scalars are boxed one object per occurrence.
        sealed class ScalarBox(object v)
        {
            public object Value { get; } = v;
        }

        [ThreadStatic] static Dictionary<object, int>? scalarIdsTs;
        static Dictionary<object, int> scalarIds => scalarIdsTs!;

        static void Flatten(object root, List<object> objs, Dictionary<string, int> stringIds)
        {
            scalarIdsTs = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<object>();
            void Add(object v)
            {
                switch (v)
                {
                    case string s:
                        if (!stringIds.ContainsKey(s))
                        {
                            stringIds[s] = objs.Count;
                            objs.Add(s);
                        }
                        break;
                    case PDict or List<object>:
                        objs.Add(v);
                        queue.Enqueue(v);
                        break;
                    default:
                        scalarIds[v] = objs.Count;
                        objs.Add(new ScalarBox(v));
                        break;
                }
            }
            Add(root);
            while (queue.Count > 0)
            {
                var o = queue.Dequeue();
                if (o is PDict d)
                {
                    foreach (var k in d.Keys) Add(k);
                    foreach (var (_, v) in d) Add(v);
                }
                else if (o is List<object> a)
                {
                    foreach (var v in a) Add(v);
                }
            }
        }

        static void Marker(Stream s, int type, int count)
        {
            if (count < 15) s.WriteByte((byte)((type << 4) | count));
            else
            {
                s.WriteByte((byte)((type << 4) | 0xF));
                WriteInt(s, count);
            }
        }

        static void WriteInt(Stream s, long v)
        {
            if (v >= 0 && v < 256)
            {
                s.WriteByte(0x10);
                s.WriteByte((byte)v);
            }
            else if (v >= 0 && v < 65536)
            {
                s.WriteByte(0x11);
                BE(s, (ulong)v, 2);
            }
            else if (v >= 0 && v < (1L << 32))
            {
                s.WriteByte(0x12);
                BE(s, (ulong)v, 4);
            }
            else
            {
                s.WriteByte(0x13);
                BE(s, (ulong)v, 8);
            }
        }

        static void WriteScalar(Stream s, object v)
        {
            switch (v)
            {
                case bool b:
                    s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                    break;
                case ulong u when u > long.MaxValue:
                    s.WriteByte(0x14);
                    BE(s, 0, 8);
                    BE(s, u, 8);
                    break;
                case long or int or ulong or uint or short or ushort or byte:
                    WriteInt(s, Convert.ToInt64(v));
                    break;
                case double or float:
                    s.WriteByte(0x23);
                    Span<byte> d = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(d, Convert.ToDouble(v));
                    s.Write(d);
                    break;
                case byte[] data:
                    Marker(s, 0x4, data.Length);
                    s.Write(data);
                    break;
                case DateTime dt:
                    s.WriteByte(0x33);
                    Span<byte> t = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(t, (dt.ToUniversalTime() - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                    s.Write(t);
                    break;
                case PUid u:
                    int n = u.Value < 256 ? 1 : u.Value < 65536 ? 2 : u.Value < (1UL << 32) ? 4 : 8;
                    s.WriteByte((byte)(0x80 | (n - 1)));
                    BE(s, u.Value, n);
                    break;
                default:
                    throw new ArgumentException($"bplist: cannot write {v.GetType().Name}");
            }
        }

        static void WriteString(Stream s, string str)
        {
            if (str.All(c => c < 128))
            {
                Marker(s, 0x5, str.Length);
                s.Write(Encoding.ASCII.GetBytes(str));
            }
            else
            {
                Marker(s, 0x6, str.Length);
                s.Write(Encoding.BigEndianUnicode.GetBytes(str));
            }
        }

        static void Ref(Stream s, int r, int size) => BE(s, (ulong)r, size);

        static void BE(Stream s, ulong v, int size)
        {
            for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }
    }
}
