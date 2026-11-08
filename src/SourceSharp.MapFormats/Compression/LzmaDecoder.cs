//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Compression;

/// <summary>
/// Decodes a raw LZMA stream (the LZMA1 algorithm, no container) whose
/// unpacked size is known up front, which is how the engine stores
/// compressed map lumps.
/// </summary>
/// <remarks>
/// <para>
/// A direct reading of the published LZMA specification's reference decoder:
/// a binary range coder over adaptive probabilities, literals coded with
/// <c>lc</c>/<c>lp</c> context bits, matches and the four repeat distances,
/// and the twelve-state machine that chooses between them. Integer arithmetic
/// only, so the output is the same on every machine; no mutable state
/// outlives a call.
/// </para>
/// <para>
/// Read side only. The engine decompresses compressed lumps, game lump
/// entries and pak entries on load, and so do the reference compile tools,
/// so a shipped map (Team Fortress 2's are compressed lump by lump) must
/// decode here to be checked, diffed, recompiled or mined for content. The
/// compilers never write compressed output, so there is no encoder.
/// </para>
/// <para>
/// Decoding stops when the declared number of bytes has been produced. An
/// end marker before that point is accepted only if it falls exactly at the
/// end; anything else that does not fit the stream (a distance past the
/// output so far, input that runs out, a corrupt range coder) throws
/// <see cref="InvalidDataException"/> rather than returning short or
/// garbage output.
/// </para>
/// </remarks>
public static class LzmaDecoder
{
    /// <summary>The size of the properties block: the lc/lp/pb byte and a 32-bit dictionary size.</summary>
    public const int PropertiesSize = 5;

    private const int NumBitModelTotalBits = 11;
    private const int BitModelTotal = 1 << NumBitModelTotalBits;
    private const int NumMoveBits = 5;
    private const uint TopValue = 1u << 24;
    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int StartPosModelIndex = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int MatchMinLen = 2;

    /// <summary>Decodes a stream.</summary>
    /// <param name="properties">The five properties bytes: <c>(pb * 5 + lp) * 9 + lc</c>, then the dictionary size.</param>
    /// <param name="compressed">The raw LZMA stream after the properties.</param>
    /// <param name="unpackedSize">How many bytes it decodes to.</param>
    /// <returns>Exactly <paramref name="unpackedSize"/> bytes.</returns>
    /// <exception cref="InvalidDataException">The properties or the stream are not valid LZMA.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> properties, ReadOnlySpan<byte> compressed, int unpackedSize)
    {
        if (properties.Length < PropertiesSize)
        {
            throw new InvalidDataException($"LZMA properties are {PropertiesSize} bytes, not {properties.Length}");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(unpackedSize);

        int d = properties[0];
        if (d >= 9 * 5 * 5)
        {
            throw new InvalidDataException($"LZMA properties byte {d} is out of range");
        }

        int lc = d % 9;
        d /= 9;
        int lp = d % 5;
        int pb = d / 5;
        uint dictSize = (uint)(properties[1] | (properties[2] << 8) | (properties[3] << 16) | (properties[4] << 24));

        Decoder decoder = new(compressed, lc, lp, pb, dictSize, unpackedSize);
        decoder.Run();
        return decoder.Output;
    }

    /// <summary>One decode's state. A ref struct so it can hold the input span.</summary>
    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> _input;
        private readonly int _lc;
        private readonly int _lp;
        private readonly int _pb;
        private readonly uint _dictSize;
        private readonly ushort[] _literal;
        private readonly ushort[] _isMatch = Probs(NumStates << NumPosBitsMax);
        private readonly ushort[] _isRep = Probs(NumStates);
        private readonly ushort[] _isRepG0 = Probs(NumStates);
        private readonly ushort[] _isRepG1 = Probs(NumStates);
        private readonly ushort[] _isRepG2 = Probs(NumStates);
        private readonly ushort[] _isRep0Long = Probs(NumStates << NumPosBitsMax);
        private readonly ushort[] _posSlot = Probs(NumLenToPosStates << 6);
        private readonly ushort[] _posDecoders = Probs(1 + NumFullDistances - EndPosModelIndex);
        private readonly ushort[] _align = Probs(1 << NumAlignBits);
        private readonly ushort[] _lenProbs = LenProbs();
        private readonly ushort[] _repLenProbs = LenProbs();
        private int _inPos;
        private uint _range;
        private uint _code;
        private int _outPos;

        public Decoder(ReadOnlySpan<byte> input, int lc, int lp, int pb, uint dictSize, int unpackedSize)
        {
            _input = input;
            _lc = lc;
            _lp = lp;
            _pb = pb;
            _dictSize = dictSize;
            _literal = Probs(0x300 << (lc + lp));
            Output = new byte[unpackedSize];
        }

        public byte[] Output { get; }

        // Length decoder layout in one array: choice, choice2, 16 low trees
        // of 8, 16 mid trees of 8, one high tree of 256.
        private const int LenChoice = 0;
        private const int LenChoice2 = 1;
        private const int LenLow = 2;
        private const int LenMid = LenLow + (16 << 3);
        private const int LenHigh = LenMid + (16 << 3);

        private static ushort[] LenProbs() => Probs(LenHigh + 256);

        private static ushort[] Probs(int count)
        {
            ushort[] probs = new ushort[count];
            Array.Fill(probs, (ushort)(BitModelTotal / 2));
            return probs;
        }

        public void Run()
        {
            if (_input.Length < 5 || _input[0] != 0)
            {
                throw new InvalidDataException("the LZMA stream does not start with a range coder header");
            }

            _code = ((uint)_input[1] << 24) | ((uint)_input[2] << 16) | ((uint)_input[3] << 8) | _input[4];
            _range = 0xFFFFFFFF;
            _inPos = 5;
            if (_code == _range)
            {
                throw new InvalidDataException("the LZMA range coder header is corrupt");
            }

            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int state = 0;
            int posMask = (1 << _pb) - 1;

            while (_outPos < Output.Length)
            {
                int posState = _outPos & posMask;
                if (Bit(_isMatch, (state << NumPosBitsMax) + posState) == 0)
                {
                    Literal(state, rep0);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                int len;
                if (Bit(_isRep, state) != 0)
                {
                    if (_outPos == 0)
                    {
                        throw new InvalidDataException("an LZMA repeat match before any output");
                    }

                    if (Bit(_isRepG0, state) == 0)
                    {
                        if (Bit(_isRep0Long, (state << NumPosBitsMax) + posState) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            Put(Output[_outPos - (int)rep0 - 1]);
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (Bit(_isRepG1, state) == 0)
                        {
                            dist = rep1;
                        }
                        else
                        {
                            if (Bit(_isRepG2, state) == 0)
                            {
                                dist = rep2;
                            }
                            else
                            {
                                dist = rep3;
                                rep3 = rep2;
                            }

                            rep2 = rep1;
                        }

                        rep1 = rep0;
                        rep0 = dist;
                    }

                    len = Length(_repLenProbs, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    len = Length(_lenProbs, posState);
                    state = state < 7 ? 7 : 10;
                    rep0 = Distance(len);
                    if (rep0 == 0xFFFFFFFF)
                    {
                        // An end marker: only acceptable where the data ends.
                        throw new InvalidDataException(
                            $"the LZMA stream ends after {_outPos} of {Output.Length} bytes");
                    }

                    if (rep0 >= _dictSize || rep0 >= (uint)_outPos)
                    {
                        throw new InvalidDataException(
                            $"an LZMA match reaches {rep0 + 1} bytes back from position {_outPos}");
                    }
                }

                len += MatchMinLen;
                int from = _outPos - (int)rep0 - 1;
                int count = Math.Min(len, Output.Length - _outPos);
                for (int i = 0; i < count; i++)
                {
                    Output[_outPos++] = Output[from + i];
                }
            }
        }

        private void Put(byte value) => Output[_outPos++] = value;

        private void Literal(int state, uint rep0)
        {
            int prev = _outPos > 0 ? Output[_outPos - 1] : 0;
            int litState = ((_outPos & ((1 << _lp) - 1)) << _lc) + (prev >> (8 - _lc));
            int baseIndex = 0x300 * litState;
            int symbol = 1;

            if (state >= 7)
            {
                int matchByte = Output[_outPos - (int)rep0 - 1];
                do
                {
                    int matchBit = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    int bit = Bit(_literal, baseIndex + ((1 + matchBit) << 8) + symbol);
                    symbol = (symbol << 1) | bit;
                    if (matchBit != bit)
                    {
                        break;
                    }
                }
                while (symbol < 0x100);
            }

            while (symbol < 0x100)
            {
                symbol = (symbol << 1) | Bit(_literal, baseIndex + symbol);
            }

            Put((byte)(symbol - 0x100));
        }

        private int Length(ushort[] probs, int posState)
        {
            if (Bit(probs, LenChoice) == 0)
            {
                return Tree(probs, LenLow + (posState << 3), 3);
            }

            if (Bit(probs, LenChoice2) == 0)
            {
                return 8 + Tree(probs, LenMid + (posState << 3), 3);
            }

            return 16 + Tree(probs, LenHigh, 8);
        }

        private uint Distance(int len)
        {
            int lenState = Math.Min(len, NumLenToPosStates - 1);
            int posSlot = Tree(_posSlot, lenState << 6, 6);
            if (posSlot < StartPosModelIndex)
            {
                return (uint)posSlot;
            }

            int directBits = (posSlot >> 1) - 1;
            uint dist = (uint)((2 | (posSlot & 1)) << directBits);
            if (posSlot < EndPosModelIndex)
            {
                dist += (uint)ReverseTree(_posDecoders, (int)dist - posSlot, directBits);
            }
            else
            {
                dist += DirectBits(directBits - NumAlignBits) << NumAlignBits;
                dist += (uint)ReverseTree(_align, 0, NumAlignBits);
            }

            return dist;
        }

        /// <summary>A bit tree of <paramref name="bits"/> levels rooted at <paramref name="offset"/> (whose index 0 is unused).</summary>
        private int Tree(ushort[] probs, int offset, int bits)
        {
            int m = 1;
            for (int i = 0; i < bits; i++)
            {
                m = (m << 1) + Bit(probs, offset + m);
            }

            return m - (1 << bits);
        }

        private int ReverseTree(ushort[] probs, int offset, int bits)
        {
            int m = 1;
            int symbol = 0;
            for (int i = 0; i < bits; i++)
            {
                int bit = Bit(probs, offset + m);
                m = (m << 1) + bit;
                symbol |= bit << i;
            }

            return symbol;
        }

        private uint DirectBits(int bits)
        {
            uint result = 0;
            for (; bits > 0; bits--)
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0 - (_code >> 31);
                _code += _range & t;
                if (_code == _range)
                {
                    throw new InvalidDataException("the LZMA range coder is corrupt");
                }

                Normalize();
                result = (result << 1) + (t + 1);
            }

            return result;
        }

        private int Bit(ushort[] probs, int index)
        {
            uint v = probs[index];
            uint bound = (_range >> NumBitModelTotalBits) * v;
            int symbol;
            if (_code < bound)
            {
                v += (BitModelTotal - v) >> NumMoveBits;
                _range = bound;
                symbol = 0;
            }
            else
            {
                v -= v >> NumMoveBits;
                _code -= bound;
                _range -= bound;
                symbol = 1;
            }

            probs[index] = (ushort)v;
            Normalize();
            return symbol;
        }

        private void Normalize()
        {
            if (_range >= TopValue)
            {
                return;
            }

            if (_inPos >= _input.Length)
            {
                throw new InvalidDataException("the LZMA stream ends before its data does");
            }

            _range <<= 8;
            _code = (_code << 8) | _input[_inPos++];
        }
    }
}

/// <summary>
/// The engine's LZMA container for compressed map lumps: the four bytes
/// <c>LZMA</c>, the unpacked size, the packed size (both 32-bit little
/// endian), the five LZMA properties bytes, then the raw stream.
/// </summary>
public static class ValveLzma
{
    /// <summary>The header's size: magic, two sizes and the properties.</summary>
    public const int HeaderSize = 4 + 4 + 4 + LzmaDecoder.PropertiesSize;

    /// <summary>Whether bytes start with the container's magic.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>True for <c>LZMA</c> first.</returns>
    public static bool IsCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'L' && data[1] == (byte)'Z' && data[2] == (byte)'M' && data[3] == (byte)'A';

    /// <summary>Unpacks a container.</summary>
    /// <param name="data">The container, header first.</param>
    /// <returns>The unpacked bytes, exactly as many as the header declares.</returns>
    /// <exception cref="InvalidDataException">The header or stream is not valid.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!IsCompressed(data) || data.Length < HeaderSize)
        {
            throw new InvalidDataException("not an LZMA lump: the header is missing");
        }

        uint actual = BitConverterLe(data[4..]);
        uint packed = BitConverterLe(data[8..]);
        if (actual > int.MaxValue || packed > (uint)(data.Length - HeaderSize))
        {
            throw new InvalidDataException(
                $"the LZMA header declares {packed} packed bytes in a {data.Length - HeaderSize}-byte payload");
        }

        return LzmaDecoder.Decode(data[12..HeaderSize], data.Slice(HeaderSize, (int)packed), (int)actual);
    }

    private static uint BitConverterLe(ReadOnlySpan<byte> b) =>
        (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
}

/// <summary>
/// LZMA as a Source pak stores it (ZIP method 14, APPNOTE 5.8.8 framing):
/// two SDK version bytes, a 16-bit properties size, the properties, then the
/// raw stream; the unpacked size comes from the zip directory.
/// </summary>
public static class ZipLzma
{
    /// <summary>Unpacks a method-14 entry's payload.</summary>
    /// <param name="payload">The entry's stored bytes, framing first.</param>
    /// <param name="unpackedSize">The size the zip directory declares.</param>
    /// <returns>Exactly <paramref name="unpackedSize"/> bytes.</returns>
    /// <exception cref="InvalidDataException">
    /// The framing is short, declares a properties size other than five (the
    /// engine's decoder refuses those too), or the stream is not valid.
    /// </exception>
    public static byte[] Decompress(ReadOnlySpan<byte> payload, int unpackedSize)
    {
        const int Preamble = 4;
        if (payload.Length < Preamble + LzmaDecoder.PropertiesSize)
        {
            throw new InvalidDataException(
                $"an LZMA zip entry is {payload.Length} bytes, too short for its {Preamble + LzmaDecoder.PropertiesSize}-byte framing");
        }

        int propertiesSize = payload[2] | (payload[3] << 8);
        if (propertiesSize != LzmaDecoder.PropertiesSize)
        {
            throw new InvalidDataException(
                $"an LZMA zip entry declares {propertiesSize} properties bytes; the engine accepts only {LzmaDecoder.PropertiesSize}");
        }

        return LzmaDecoder.Decode(
            payload.Slice(Preamble, LzmaDecoder.PropertiesSize),
            payload[(Preamble + LzmaDecoder.PropertiesSize)..],
            unpackedSize);
    }
}
