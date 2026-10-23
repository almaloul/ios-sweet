// LZFSE and LZVN decoders, ported from Apple's reference implementation
// (https://github.com/lzfse/lzfse, BSD-3-Clause, Copyright (c) 2015-2016 Apple Inc.).
// Whole-buffer decoding only: the caller supplies the complete compressed stream.
using System;
using System.Buffers.Binary;

namespace SweetEmu.Core.Compression;

public static class Lzfse
{
    const uint EndOfStream = 0x24787662;   // bvx$
    const uint Uncompressed = 0x2d787662;  // bvx-
    const uint CompressedV1 = 0x31787662;  // bvx1
    const uint CompressedV2 = 0x32787662;  // bvx2
    const uint CompressedLzvn = 0x6e787662; // bvxn

    const int LSymbols = 20, MSymbols = 20, DSymbols = 64, LiteralSymbols = 256;
    const int LStates = 64, MStates = 64, DStates = 256, LiteralStates = 1024;
    const int MatchesPerBlock = 10000, LiteralsPerBlock = 4 * MatchesPerBlock;

    static readonly byte[] LExtraBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8 };
    static readonly int[] LBase = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60 };
    static readonly byte[] MExtraBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11 };
    static readonly int[] MBase = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312 };
    static readonly byte[] DExtraBits = {
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
        8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15 };
    static readonly int[] DBase = {
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52, 60, 76, 92, 108,
        124, 156, 188, 220, 252, 316, 380, 444, 508, 636, 764, 892, 1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580,
        4092, 5116, 6140, 7164, 8188, 10236, 12284, 14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148, 57340, 65532, 81916, 98300, 114684,
        131068, 163836, 196604, 229372 };

    static readonly sbyte[] FreqNbits = {
        2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14, 2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14 };
    static readonly sbyte[] FreqValue = {
        0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1, 0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1 };

    /// <summary>Decompress a complete LZFSE stream into dst. Returns the number of bytes written.</summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        var lit = new byte[LiteralsPerBlock + 64];
        var litDec = new int[LiteralStates];
        var lDec = new ValueEntry[LStates];
        var mDec = new ValueEntry[MStates];
        var dDec = new ValueEntry[DStates];
        var freq = new ushort[LSymbols + MSymbols + DSymbols + LiteralSymbols];

        while (true)
        {
            if (s + 4 > src.Length) throw new InvalidDataException("LZFSE: truncated stream");
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src[s..]);
            switch (magic)
            {
                case EndOfStream:
                    return d;
                case Uncompressed:
                {
                    int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[(s + 4)..]);
                    s += 8;
                    Check(s + n <= src.Length && d + n <= dst.Length, "raw block");
                    src.Slice(s, n).CopyTo(dst[d..]);
                    s += n;
                    d += n;
                    break;
                }
                case CompressedLzvn:
                {
                    int raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[(s + 4)..]);
                    int payload = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[(s + 8)..]);
                    s += 12;
                    Check(s + payload <= src.Length && d + raw <= dst.Length, "lzvn block");
                    int got = Lzvn.Decode(src.Slice(s, payload), dst, d, raw);
                    Check(got == raw, "lzvn block size");
                    s += payload;
                    d += raw;
                    break;
                }
                case CompressedV1:
                case CompressedV2:
                    DecodeBlock(src, ref s, dst, ref d, magic, lit, litDec, lDec, mDec, dDec, freq);
                    break;
                default:
                    throw new InvalidDataException($"LZFSE: bad block magic 0x{magic:x8}");
            }
        }
    }

    /// <summary>Decompress into a new array of exactly expectedSize bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> src, int expectedSize)
    {
        var dst = new byte[expectedSize];
        int n = Decode(src, dst);
        if (n != expectedSize) throw new InvalidDataException($"LZFSE: {n} bytes, expected {expectedSize}");
        return dst;
    }

    struct ValueEntry
    {
        public byte TotalBits, ValueBits;
        public short Delta;
        public int VBase;
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidDataException("LZFSE: corrupt " + what);
    }

    static void DecodeBlock(ReadOnlySpan<byte> src, ref int s, Span<byte> dst, ref int d, uint magic,
        byte[] lit, int[] litDec, ValueEntry[] lDec, ValueEntry[] mDec, ValueEntry[] dDec, ushort[] freq)
    {
        int nRaw, nLiterals, nMatches, nLitPayload, nLmdPayload, literalBits, lmdBits, headerSize;
        var litState = new int[4];
        int lState, mState, dState;
        Array.Clear(freq);

        if (magic == CompressedV2)
        {
            Check(s + 32 <= src.Length, "v2 header");
            nRaw = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[(s + 4)..]);
            ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(src[(s + 8)..]);
            ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(src[(s + 16)..]);
            ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(src[(s + 24)..]);
            nLiterals = (int)Field(v0, 0, 20);
            nLitPayload = (int)Field(v0, 20, 20);
            nMatches = (int)Field(v0, 40, 20);
            literalBits = (int)Field(v0, 60, 3) - 7;
            for (int i = 0; i < 4; i++) litState[i] = (int)Field(v1, 10 * i, 10);
            nLmdPayload = (int)Field(v1, 40, 20);
            lmdBits = (int)Field(v1, 60, 3) - 7;
            headerSize = (int)(uint)v2;
            lState = (int)Field(v2, 32, 10);
            mState = (int)Field(v2, 42, 10);
            dState = (int)Field(v2, 52, 10);
            Check(headerSize >= 32 && s + headerSize <= src.Length, "v2 header size");
            int p = s + 32, end = s + headerSize;
            if (p != end)
            {
                uint accum = 0;
                int accumBits = 0;
                for (int i = 0; i < freq.Length; i++)
                {
                    while (p < end && accumBits + 8 <= 32)
                    {
                        accum |= (uint)src[p++] << accumBits;
                        accumBits += 8;
                    }
                    uint b = accum & 31;
                    int nb = FreqNbits[b];
                    int value = nb == 8 ? 8 + (int)((accum >> 4) & 0xf)
                        : nb == 14 ? 24 + (int)((accum >> 4) & 0x3ff) : FreqValue[b];
                    Check(nb <= accumBits, "freq table");
                    freq[i] = (ushort)value;
                    accum >>= nb;
                    accumBits -= nb;
                }
                Check(accumBits < 8 && p == end, "freq table end");
            }
        }
        else
        {
            headerSize = 772;
            Check(s + headerSize <= src.Length, "v1 header");
            var h = src[s..];
            nRaw = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            nLiterals = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
            nMatches = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            nLitPayload = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            nLmdPayload = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            literalBits = BinaryPrimitives.ReadInt32LittleEndian(h[28..]);
            for (int i = 0; i < 4; i++) litState[i] = BinaryPrimitives.ReadUInt16LittleEndian(h[(32 + 2 * i)..]);
            lmdBits = BinaryPrimitives.ReadInt32LittleEndian(h[40..]);
            lState = BinaryPrimitives.ReadUInt16LittleEndian(h[44..]);
            mState = BinaryPrimitives.ReadUInt16LittleEndian(h[46..]);
            dState = BinaryPrimitives.ReadUInt16LittleEndian(h[48..]);
            for (int i = 0; i < freq.Length; i++) freq[i] = BinaryPrimitives.ReadUInt16LittleEndian(h[(50 + 2 * i)..]);
        }

        Check(nLiterals <= LiteralsPerBlock && nMatches <= MatchesPerBlock, "block counts");
        Check(litState.All(x => x < LiteralStates) && lState < LStates && mState < MStates && dState < DStates, "states");
        s += headerSize;
        Check(s + nLitPayload + nLmdPayload <= src.Length, "block payload");

        var lFreq = freq.AsSpan(0, LSymbols);
        var mFreq = freq.AsSpan(LSymbols, MSymbols);
        var dFreq = freq.AsSpan(LSymbols + MSymbols, DSymbols);
        var litFreq = freq.AsSpan(LSymbols + MSymbols + DSymbols, LiteralSymbols);
        Check(SumOk(lFreq, LStates) && SumOk(mFreq, MStates) && SumOk(dFreq, DStates) && SumOk(litFreq, LiteralStates), "freq sums");
        InitDecoder(LiteralStates, litFreq, litDec);
        InitValueDecoder(LStates, lFreq, LExtraBits, LBase, lDec);
        InitValueDecoder(MStates, mFreq, MExtraBits, MBase, mDec);
        InitValueDecoder(DStates, dFreq, DExtraBits, DBase, dDec);

        // Literals: read backwards from the end of the literal payload.
        {
            // The reference only bounds the reader by the start of the whole input.
            int buf = s + nLitPayload;
            var inp = new BitIn(src, 0);
            inp.Init(literalBits, ref buf);
            int st0 = litState[0], st1 = litState[1], st2 = litState[2], st3 = litState[3];
            for (int i = 0; i < nLiterals; i += 4)
            {
                inp.Flush(ref buf);
                lit[i + 0] = DecodeSym(ref st0, litDec, ref inp);
                lit[i + 1] = DecodeSym(ref st1, litDec, ref inp);
                lit[i + 2] = DecodeSym(ref st2, litDec, ref inp);
                lit[i + 3] = DecodeSym(ref st3, litDec, ref inp);
            }
            s += nLitPayload;
        }

        // L, M, D triplets.
        {
            int buf = s + nLmdPayload;
            var inp = new BitIn(src, s);   // init is bounded by the LMD payload start
            inp.Init(lmdBits, ref buf);
            inp = inp.WithStart(0);       // flushes by the whole input
            int litPos = 0;
            int D = -1;
            int dStart = d;
            for (int k = 0; k < nMatches; k++)
            {
                inp.Flush(ref buf);
                int L = DecodeValue(ref lState, lDec, ref inp);
                int M = DecodeValue(ref mState, mDec, ref inp);
                int newD = DecodeValue(ref dState, dDec, ref inp);
                D = newD != 0 ? newD : D;
                Check(litPos + L <= lit.Length && d + L + M <= dst.Length, "match length");
                Check(D > 0 && D <= d + L, "match distance");
                lit.AsSpan(litPos, L).CopyTo(dst[d..]);
                d += L;
                litPos += L;
                if (D >= M)
                {
                    dst.Slice(d - D, M).CopyTo(dst[d..]);
                }
                else
                {
                    for (int i = 0; i < M; i++) dst[d + i] = dst[d + i - D];
                }
                d += M;
            }
            s += nLmdPayload;
            Check(d - dStart == nRaw, "block size");
        }
    }

    static bool SumOk(ReadOnlySpan<ushort> f, int n)
    {
        int sum = 0;
        foreach (var x in f) sum += x;
        return sum <= n;
    }

    static ulong Field(ulong v, int off, int n) => (v >> off) & ((1UL << n) - 1);

    static int Clz(int x) => System.Numerics.BitOperations.LeadingZeroCount((uint)x);

    // Decoder entry packed like the reference: delta << 16 | symbol << 8 | k.
    static void InitDecoder(int nstates, ReadOnlySpan<ushort> freq, int[] t)
    {
        int nClz = Clz(nstates), pos = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            int k = Clz(f) - nClz;
            int j0 = ((2 * nstates) >> k) - f;
            for (int j = 0; j < f; j++)
            {
                int kk, delta;
                if (j < j0)
                {
                    kk = k;
                    delta = ((f + j) << k) - nstates;
                }
                else
                {
                    kk = k - 1;
                    delta = (j - j0) << (k - 1);
                }
                t[pos++] = (delta << 16) | (i << 8) | (kk & 0xff);
            }
        }
    }

    static void InitValueDecoder(int nstates, ReadOnlySpan<ushort> freq, byte[] vbits, int[] vbase, ValueEntry[] t)
    {
        int nClz = Clz(nstates), pos = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            int k = Clz(f) - nClz;
            int j0 = ((2 * nstates) >> k) - f;
            for (int j = 0; j < f; j++)
            {
                var e = new ValueEntry { ValueBits = vbits[i], VBase = vbase[i] };
                if (j < j0)
                {
                    e.TotalBits = (byte)(k + e.ValueBits);
                    e.Delta = (short)(((f + j) << k) - nstates);
                }
                else
                {
                    e.TotalBits = (byte)(k - 1 + e.ValueBits);
                    e.Delta = (short)((j - j0) << (k - 1));
                }
                t[pos++] = e;
            }
        }
    }

    static byte DecodeSym(ref int state, int[] table, ref BitIn inp)
    {
        int e = table[state];
        state = (e >> 16) + (int)inp.Pull(e & 0xff);
        return (byte)(e >> 8);
    }

    static int DecodeValue(ref int state, ValueEntry[] table, ref BitIn inp)
    {
        var e = table[state];
        uint bits = (uint)inp.Pull(e.TotalBits);
        state = e.Delta + (int)(bits >> e.ValueBits);
        return e.VBase + (int)(bits & ((1u << e.ValueBits) - 1));
    }

    /// FSE bit stream read backwards, 64-bit accumulator (56..63 bits after a flush).
    ref struct BitIn
    {
        readonly ReadOnlySpan<byte> src;
        readonly int start;
        ulong accum;
        int nbits;

        public BitIn(ReadOnlySpan<byte> src, int start)
        {
            this.src = src;
            this.start = start;
            accum = 0;
            nbits = 0;
        }

        public BitIn WithStart(int newStart)
        {
            var b = new BitIn(src, newStart) { accum = accum, nbits = nbits };
            return b;
        }

        public void Init(int n, ref int buf)
        {
            if (n != 0)
            {
                Check(buf >= start + 8, "bit stream init");
                buf -= 8;
                accum = BinaryPrimitives.ReadUInt64LittleEndian(src[buf..]);
                nbits = n + 64;
            }
            else
            {
                Check(buf >= start + 7, "bit stream init");
                buf -= 7;
                accum = 0;
                for (int i = 6; i >= 0; i--) accum = (accum << 8) | src[buf + i];
                nbits = 56;
            }
            Check(nbits >= 56 && nbits < 64 && (accum >> nbits) == 0, "bit stream init");
        }

        public void Flush(ref int buf)
        {
            int n = (63 - nbits) & ~7;
            if (n == 0) return;
            int b = buf - (n >> 3);
            Check(b >= start, "bit stream underflow");
            buf = b;
            // 8 bytes from b; near the end of the buffer only n/8 of them are needed.
            ulong incoming = 0;
            int avail = Math.Min(8, src.Length - b);
            for (int i = avail - 1; i >= 0; i--) incoming = (incoming << 8) | src[b + i];
            accum = (accum << n) | (incoming & ((1UL << n) - 1));
            nbits += n;
        }

        public ulong Pull(int n)
        {
            if (n == 0) return 0;
            nbits -= n;
            ulong result = accum >> nbits;
            accum &= nbits == 0 ? 0 : (1UL << nbits) - 1;
            return result;
        }
    }
}

public static class Lzvn
{
    /// <summary>
    /// Decode an LZVN stream into dst[dstPos..dstPos+maxOut]. Matches may reach back to dst[0].
    /// Returns the number of bytes produced (stops at the end-of-stream opcode or maxOut).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst, int dstPos, int maxOut)
    {
        int s = 0, d = dstPos, end = dstPos + maxOut;
        int D = 0;
        while (s < src.Length)
        {
            byte opc = src[s];
            int L, M;
            switch (Kind[opc])
            {
                case Op.SmlD:
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    Need(src, s + 2 + L);
                    D = ((opc & 7) << 8) | src[s + 1];
                    s += 2;
                    break;
                case Op.MedD:
                {
                    L = (opc >> 3) & 3;
                    Need(src, s + 3 + L);
                    int opc23 = src[s + 1] | (src[s + 2] << 8);
                    M = (((opc & 7) << 2) | (opc23 & 3)) + 3;
                    D = opc23 >> 2;
                    s += 3;
                    break;
                }
                case Op.LrgD:
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    Need(src, s + 3 + L);
                    D = src[s + 1] | (src[s + 2] << 8);
                    s += 3;
                    break;
                case Op.PreD:
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    Need(src, s + 1 + L);
                    s += 1;
                    break;
                case Op.SmlL:
                    L = opc & 0xf;
                    M = 0;
                    s += 1;
                    break;
                case Op.LrgL:
                    Need(src, s + 2);
                    L = src[s + 1] + 16;
                    M = 0;
                    s += 2;
                    break;
                case Op.SmlM:
                    L = 0;
                    M = opc & 0xf;
                    s += 1;
                    break;
                case Op.LrgM:
                    Need(src, s + 2);
                    L = 0;
                    M = src[s + 1] + 16;
                    s += 2;
                    break;
                case Op.Nop:
                    s += 1;
                    continue;
                case Op.Eos:
                    return d - dstPos;
                default:
                    throw new InvalidDataException($"LZVN: undefined opcode 0x{opc:x2}");
            }
            if (L > 0)
            {
                Need(src, s + L);
                if (d + L > end) throw new InvalidDataException("LZVN: output overflow");
                src.Slice(s, L).CopyTo(dst[d..]);
                s += L;
                d += L;
            }
            if (M > 0)
            {
                if (D == 0 || D > d) throw new InvalidDataException("LZVN: bad match distance");
                if (d + M > end) throw new InvalidDataException("LZVN: output overflow");
                if (D >= M) dst.Slice(d - D, M).CopyTo(dst[d..]);
                else for (int i = 0; i < M; i++) dst[d + i] = dst[d + i - D];
                d += M;
            }
        }
        return d - dstPos;
    }

    static void Need(ReadOnlySpan<byte> src, int n)
    {
        if (n > src.Length) throw new InvalidDataException("LZVN: truncated");
    }

    enum Op : byte { Udef, SmlD, MedD, LrgD, PreD, SmlL, LrgL, SmlM, LrgM, Nop, Eos }

    static readonly Op[] Kind = BuildKinds();

    static Op[] BuildKinds()
    {
        var k = new Op[256];
        for (int i = 0; i < 256; i++)
        {
            int lo = i & 7;
            if (i >= 0xF0) k[i] = i == 0xF0 ? Op.LrgM : Op.SmlM;
            else if (i >= 0xE0) k[i] = i == 0xE0 ? Op.LrgL : Op.SmlL;
            else if (i >= 0xD0) k[i] = Op.Udef;
            else if (i >= 0xA0 && i <= 0xBF) k[i] = Op.MedD;
            else if (i >= 0x70 && i <= 0x7F) k[i] = Op.Udef;
            else if (lo == 7) k[i] = Op.LrgD;
            else if (lo == 6)
            {
                k[i] = i switch
                {
                    0x06 => Op.Eos,
                    0x0E or 0x16 => Op.Nop,
                    < 0x40 => Op.Udef,
                    _ => Op.PreD,
                };
            }
            else k[i] = Op.SmlD;
        }
        return k;
    }
}
