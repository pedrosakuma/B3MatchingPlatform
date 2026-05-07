using System.Buffers.Binary;
using System.Text;
using B3.Exchange.Core;
using B3.Exchange.Matching;

namespace B3.Exchange.Persistence;

/// <summary>
/// Issue #266: compact binary codec for <see cref="ChannelStateSnapshot"/>.
///
/// <para><b>Wire layout (little-endian):</b>
/// <list type="bullet">
///   <item>4 bytes magic <c>"B3SS"</c> (0x42 0x33 0x53 0x53).</item>
///   <item>uint16 schema version (mirrors
///   <see cref="ChannelStateSnapshot.CurrentVersion"/>; older files load
///   via the JSON path so this rev only tracks binary-side bumps).</item>
///   <item>byte channel number.</item>
///   <item>uint32 sequence number.</item>
///   <item>uint16 sequence version.</item>
///   <item>int64 LastAppliedSeq (issue #269 WAL anchor).</item>
///   <item>EngineStateSnapshot block:
///     <list type="number">
///       <item>int64 NextOrderId, uint32 NextTradeId, uint32 RptSeq.</item>
///       <item>uvarint phase count, then (int64 securityId + byte phase)
///       per entry.</item>
///       <item>uvarint book count, then per book: int64 securityId +
///       uvarint order count + (resting-order record) per order.</item>
///       <item>uvarint stop count (sentinel 0 covers both null and empty —
///       the engine treats them identically), then (resting-stop record)
///       per entry.</item>
///     </list>
///   </item>
///   <item>uvarint owner count, then (owner record) per entry.</item>
/// </list></para>
///
/// <para><b>Resting order record:</b> int64 OrderId, len-prefixed UTF-8
/// ClOrdId, byte Side, int64 PriceMantissa, int64 RemainingQuantity,
/// uint32 EnteringFirm, uint64 InsertTimestampNanos, byte Tif, int64
/// MaxFloor, int64 HiddenQuantity.</para>
///
/// <para><b>Resting stop record:</b> int64 OrderId, len-prefixed UTF-8
/// ClOrdId, int64 SecurityId, byte Side, byte StopType, byte Tif, int64
/// StopPxMantissa, int64 LimitPriceMantissa, int64 Quantity, uint32
/// EnteringFirm, uint64 EnteredAtNanos.</para>
///
/// <para><b>Owner record:</b> int64 OrderId, len-prefixed UTF-8
/// SessionValue, uint32 Firm, uint64 ClOrdId, byte Side, int64 SecurityId.</para>
///
/// <para>Strings use a uvarint byte-length prefix followed by raw UTF-8
/// bytes; the empty string is encoded as a single 0x00 byte. Lists use
/// uvarint counts. There is no trailing checksum: the persister already
/// writes via tmp+fsync+rename so a crash mid-write never leaves a
/// partial slot, and length-prefixed framing makes truncation
/// self-detecting (length walks past the buffer ⇒ throw).</para>
///
/// <para><b>What this format intentionally is NOT:</b> a long-term wire
/// protocol exposed to clients. It is an internal on-disk encoding owned
/// by the host. The schema-evolution policy from issue #272 still
/// applies — bump <see cref="ChannelStateSnapshot.CurrentVersion"/> and
/// register a migration when the layout changes. The JSON loader
/// remains available so an operator can roll back by flipping config
/// and restarting; old binary files are still recognised by magic.</para>
/// </summary>
public static class BinaryChannelStateSnapshotCodec
{
    /// <summary>4-byte little-endian magic identifying the binary format
    /// (<c>'B','3','S','S'</c>). Used by the file persister to
    /// auto-detect binary vs JSON snapshots on load.</summary>
    public static ReadOnlySpan<byte> Magic => "B3SS"u8;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="leadingBytes"/> starts
    /// with <see cref="Magic"/>. The persister calls this on the first
    /// few bytes of every candidate snapshot file before deciding
    /// whether to dispatch to binary or JSON deserialization.
    /// </summary>
    public static bool LooksLikeBinarySnapshot(ReadOnlySpan<byte> leadingBytes)
        => leadingBytes.Length >= Magic.Length && leadingBytes[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encode(ChannelStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var w = new BinaryBufferWriter();
        w.WriteRaw(Magic);
        w.WriteUInt16((ushort)snapshot.Version);
        w.WriteByte(snapshot.ChannelNumber);
        w.WriteUInt32(snapshot.SequenceNumber);
        w.WriteUInt16(snapshot.SequenceVersion);
        w.WriteInt64(snapshot.LastAppliedSeq);

        var engine = snapshot.Engine;
        w.WriteInt64(engine.NextOrderId);
        w.WriteUInt32(engine.NextTradeId);
        w.WriteUInt32(engine.RptSeq);

        w.WriteUVarInt((ulong)engine.Phases.Count);
        foreach (var p in engine.Phases)
        {
            w.WriteInt64(p.SecurityId);
            w.WriteByte((byte)p.Phase);
        }

        w.WriteUVarInt((ulong)engine.Books.Count);
        foreach (var b in engine.Books)
        {
            w.WriteInt64(b.SecurityId);
            w.WriteUVarInt((ulong)b.Orders.Count);
            foreach (var o in b.Orders) WriteRestingOrder(w, o);
        }

        // null and empty stops are operationally identical to the
        // engine; collapse both to an empty list on the wire.
        var stops = engine.Stops;
        if (stops is null)
        {
            w.WriteUVarInt(0);
        }
        else
        {
            w.WriteUVarInt((ulong)stops.Count);
            foreach (var s in stops) WriteRestingStop(w, s);
        }

        w.WriteUVarInt((ulong)snapshot.Owners.Count);
        foreach (var o in snapshot.Owners) WriteOwner(w, o);

        return w.ToArray();
    }

    public static ChannelStateSnapshot Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new BinaryBufferReader(bytes);
        var magic = r.ReadRaw(Magic.Length);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException(
                "binary snapshot does not start with the expected B3SS magic");

        int version = r.ReadUInt16();
        byte channelNumber = r.ReadByte();
        uint sequenceNumber = r.ReadUInt32();
        ushort sequenceVersion = r.ReadUInt16();
        long lastAppliedSeq = r.ReadInt64();

        long nextOrderId = r.ReadInt64();
        uint nextTradeId = r.ReadUInt32();
        uint rptSeq = r.ReadUInt32();

        ulong phaseCount = r.ReadUVarInt();
        var phases = new EngineStateSnapshot.PhaseEntry[phaseCount];
        for (ulong i = 0; i < phaseCount; i++)
        {
            long securityId = r.ReadInt64();
            byte phase = r.ReadByte();
            phases[i] = new EngineStateSnapshot.PhaseEntry(securityId, (TradingPhase)phase);
        }

        ulong bookCount = r.ReadUVarInt();
        var books = new EngineStateSnapshot.BookSnapshot[bookCount];
        for (ulong i = 0; i < bookCount; i++)
        {
            long securityId = r.ReadInt64();
            ulong orderCount = r.ReadUVarInt();
            var orders = new RestingOrderRecord[orderCount];
            for (ulong j = 0; j < orderCount; j++) orders[j] = ReadRestingOrder(ref r);
            books[i] = new EngineStateSnapshot.BookSnapshot(securityId, orders);
        }

        ulong stopCount = r.ReadUVarInt();
        IReadOnlyList<RestingStopRecord>? stops;
        if (stopCount == 0)
        {
            // Empty list (semantically identical to the null the engine
            // emits when no stops are present); pick null so encode→
            // decode→encode is byte-stable for snapshots whose engine
            // produced null.
            stops = null;
        }
        else
        {
            var arr = new RestingStopRecord[stopCount];
            for (ulong i = 0; i < stopCount; i++) arr[i] = ReadRestingStop(ref r);
            stops = arr;
        }

        ulong ownerCount = r.ReadUVarInt();
        var owners = new OrderOwnerSnapshot[ownerCount];
        for (ulong i = 0; i < ownerCount; i++) owners[i] = ReadOwner(ref r);

        if (!r.AtEnd)
            throw new InvalidDataException(
                $"binary snapshot has {r.Remaining} trailing bytes past the encoded payload");

        var engine = new EngineStateSnapshot(nextOrderId, nextTradeId, rptSeq, phases, books, stops);
        return new ChannelStateSnapshot(version, channelNumber, sequenceNumber, sequenceVersion, engine, owners)
        {
            LastAppliedSeq = lastAppliedSeq,
        };
    }

    private static void WriteRestingOrder(BinaryBufferWriter w, RestingOrderRecord o)
    {
        w.WriteInt64(o.OrderId);
        w.WriteString(o.ClOrdId);
        w.WriteByte((byte)o.Side);
        w.WriteInt64(o.PriceMantissa);
        w.WriteInt64(o.RemainingQuantity);
        w.WriteUInt32(o.EnteringFirm);
        w.WriteUInt64(o.InsertTimestampNanos);
        w.WriteByte((byte)o.Tif);
        w.WriteInt64(o.MaxFloor);
        w.WriteInt64(o.HiddenQuantity);
    }

    private static RestingOrderRecord ReadRestingOrder(ref BinaryBufferReader r)
    {
        long orderId = r.ReadInt64();
        string clOrdId = r.ReadString();
        byte side = r.ReadByte();
        long price = r.ReadInt64();
        long qty = r.ReadInt64();
        uint firm = r.ReadUInt32();
        ulong ts = r.ReadUInt64();
        byte tif = r.ReadByte();
        long maxFloor = r.ReadInt64();
        long hidden = r.ReadInt64();
        return new RestingOrderRecord(orderId, clOrdId, (Side)side, price, qty, firm, ts,
            (TimeInForce)tif, maxFloor, hidden);
    }

    private static void WriteRestingStop(BinaryBufferWriter w, RestingStopRecord s)
    {
        w.WriteInt64(s.OrderId);
        w.WriteString(s.ClOrdId);
        w.WriteInt64(s.SecurityId);
        w.WriteByte((byte)s.Side);
        w.WriteByte((byte)s.StopType);
        w.WriteByte((byte)s.Tif);
        w.WriteInt64(s.StopPxMantissa);
        w.WriteInt64(s.LimitPriceMantissa);
        w.WriteInt64(s.Quantity);
        w.WriteUInt32(s.EnteringFirm);
        w.WriteUInt64(s.EnteredAtNanos);
    }

    private static RestingStopRecord ReadRestingStop(ref BinaryBufferReader r)
    {
        long orderId = r.ReadInt64();
        string clOrdId = r.ReadString();
        long securityId = r.ReadInt64();
        byte side = r.ReadByte();
        byte stopType = r.ReadByte();
        byte tif = r.ReadByte();
        long stopPx = r.ReadInt64();
        long limit = r.ReadInt64();
        long qty = r.ReadInt64();
        uint firm = r.ReadUInt32();
        ulong enteredAt = r.ReadUInt64();
        return new RestingStopRecord(orderId, clOrdId, securityId, (Side)side,
            (OrderType)stopType, (TimeInForce)tif, stopPx, limit, qty, firm, enteredAt);
    }

    private static void WriteOwner(BinaryBufferWriter w, OrderOwnerSnapshot o)
    {
        w.WriteInt64(o.OrderId);
        w.WriteString(o.SessionValue);
        w.WriteUInt32(o.Firm);
        w.WriteUInt64(o.ClOrdId);
        w.WriteByte((byte)o.Side);
        w.WriteInt64(o.SecurityId);
    }

    private static OrderOwnerSnapshot ReadOwner(ref BinaryBufferReader r)
    {
        long orderId = r.ReadInt64();
        string session = r.ReadString();
        uint firm = r.ReadUInt32();
        ulong clOrdId = r.ReadUInt64();
        byte side = r.ReadByte();
        long securityId = r.ReadInt64();
        return new OrderOwnerSnapshot(orderId, session, firm, clOrdId, (Side)side, securityId);
    }
}

internal sealed class BinaryBufferWriter
{
    private byte[] _buf = new byte[1024];
    private int _len;

    public byte[] ToArray()
    {
        var arr = new byte[_len];
        Buffer.BlockCopy(_buf, 0, arr, 0, _len);
        return arr;
    }

    private Span<byte> Reserve(int n)
    {
        if (_len + n > _buf.Length)
        {
            int newCap = _buf.Length;
            while (newCap < _len + n) newCap *= 2;
            Array.Resize(ref _buf, newCap);
        }
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));
    public void WriteByte(byte b) => Reserve(1)[0] = b;
    public void WriteUInt16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), v);
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);
    public void WriteInt64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), v);

    public void WriteUVarInt(ulong v)
    {
        // Standard 7-bit varint (LEB128, unsigned).
        while (v >= 0x80)
        {
            WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        WriteByte((byte)v);
    }

    public void WriteString(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            WriteUVarInt(0);
            return;
        }
        int byteCount = Encoding.UTF8.GetByteCount(s);
        WriteUVarInt((ulong)byteCount);
        var dst = Reserve(byteCount);
        Encoding.UTF8.GetBytes(s, dst);
    }
}

internal ref struct BinaryBufferReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public BinaryBufferReader(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public bool AtEnd => _pos == _buf.Length;
    public int Remaining => _buf.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _buf.Length)
            throw new InvalidDataException(
                $"binary snapshot truncated: need {n} bytes at offset {_pos} but only {_buf.Length - _pos} remain");
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public ReadOnlySpan<byte> ReadRaw(int n) => Take(n);
    public byte ReadByte() => Take(1)[0];
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadUVarInt()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("uvarint overflow in binary snapshot");
        }
    }

    public string ReadString()
    {
        ulong length = ReadUVarInt();
        if (length == 0) return string.Empty;
        if (length > int.MaxValue)
            throw new InvalidDataException($"binary snapshot string length {length} exceeds Int32.MaxValue");
        var bytes = Take((int)length);
        return Encoding.UTF8.GetString(bytes);
    }
}
