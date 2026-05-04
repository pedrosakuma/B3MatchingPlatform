namespace B3.Umdf.WireEncoder;

/// <summary>
/// Wire-protocol byte offsets and block lengths for the B3 UMDF messages
/// the encoder library and the synthetic publisher emit.
///
/// All values come straight from the SBE-generated reader structs in
/// <c>B3.Umdf.Sbe</c> and reflect the V16 schema (latest). Publishers that
/// claim a lower <c>Version</c> in the SbeMessageHeader but write the V16
/// physical body still get correctly read by the V16 reader iff the
/// MessageHeader.BlockLength field matches the bytes actually written.
/// </summary>
public static class WireOffsets
{
    public const int PacketHeaderSize = 16;
    public const int FramingHeaderSize = 4;
    public const int SbeMessageHeaderSize = 8;

    // PacketHeader (Pack=1): byte channel, byte reserved, ushort seqVersion,
    // uint sequenceNumber@4, ulong sendingTime@8.
    public const int PacketHeaderChannelOffset = 0;
    public const int PacketHeaderReservedOffset = 1;
    public const int PacketHeaderSequenceVersionOffset = 2;
    public const int PacketHeaderSequenceNumberOffset = 4;
    public const int PacketHeaderSendingTimeOffset = 8;

    // FramingHeader: ushort messageLength@0, ushort encodingType@2.
    public const int FramingHeaderMessageLengthOffset = 0;
    public const int FramingHeaderEncodingTypeOffset = 2;

    // ---- Order_MBO_50 (V6 body, accepted by V16 reader) ----
    public const int OrderBlockLength = 56;
    public const int OrderBodySecurityIdOffset = 0;        // long
    public const int OrderBodyMatchEventIndicatorOffset = 8; // byte
    public const int OrderBodyMdUpdateActionOffset = 9;    // byte
    public const int OrderBodyMdEntryTypeOffset = 10;      // byte
    public const int OrderBodyMdEntryPxOffset = 12;        // long mantissa
    public const int OrderBodyMdEntrySizeOffset = 20;      // long
    public const int OrderBodyMdInsertTimestampOffset = 36; // ulong nanos
    public const int OrderBodySecondaryOrderIdOffset = 44;  // long
    public const int OrderBodyRptSeqOffset = 52;           // uint

    // ---- DeleteOrder_MBO_51 (V16 body — V15 added TransactTime@32 / MDEntryPx@44) ----
    public const int DeleteOrderBlockLength = 52;
    public const int DeleteOrderBodySecurityIdOffset = 0;          // long
    public const int DeleteOrderBodyMdEntryTypeOffset = 10;        // byte
    public const int DeleteOrderBodyMdEntrySizeOffset = 16;        // long
    public const int DeleteOrderBodySecondaryOrderIdOffset = 24;   // long
    public const int DeleteOrderBodyTransactTimeOffset = 32;       // ulong nanos (V15+)
    public const int DeleteOrderBodyRptSeqOffset = 40;             // uint
    public const int DeleteOrderBodyMdEntryPxOffset = 44;          // PriceOptional (V15+, may be NULL)

    // ---- Trade_53 (V16) ----
    public const int TradeBlockLength = 56;
    public const int TradeBodySecurityIdOffset = 0;        // long
    public const int TradeBodyMatchEventIndicatorOffset = 8; // byte
    public const int TradeBodyTradingSessionIdOffset = 9;  // byte
    public const int TradeBodyTradeConditionOffset = 10;   // byte
    public const int TradeBodyMdEntryPxOffset = 12;        // long mantissa
    public const int TradeBodyMdEntrySizeOffset = 20;      // long
    public const int TradeBodyTradeIdOffset = 28;          // uint
    public const int TradeBodyMdEntryBuyerOffset = 32;     // uint (0 = NULL)
    public const int TradeBodyMdEntrySellerOffset = 36;    // uint (0 = NULL)
    public const int TradeBodyTradeDateOffset = 40;        // ushort (LocalMktDate)
    public const int TradeBodyTrdSubTypeOffset = 42;       // byte (255 = NULL)
    public const int TradeBodyTransactTimeOffset = 44;     // ulong nanos
    public const int TradeBodyRptSeqOffset = 52;           // uint

    // ---- SnapshotFullRefresh_Header_30 ----
    public const int SnapHeaderBlockLength = 32;
    public const int SnapHeaderBodySecurityIdOffset = 0;          // long
    public const int SnapHeaderBodyTotNumReportsOffset = 12;      // uint
    public const int SnapHeaderBodyTotNumBidsOffset = 16;         // uint
    public const int SnapHeaderBodyTotNumOffersOffset = 20;       // uint
    public const int SnapHeaderBodyTotNumStatsOffset = 24;        // ushort
    public const int SnapHeaderBodyLastRptSeqOffset = 28;         // uint (0 = NULL)

    // ---- SnapshotFullRefresh_Orders_MBO_71 (group-based) ----
    public const int SnapOrdersHeaderBlockLength = 8;          // SecurityID only as block-level
    public const int SnapOrdersGroupSizeEncodingSize = 3;      // BlockLength(ushort) + NumInGroup(byte)
    public const int SnapOrdersEntrySize = 42;                 // per-entry (V16 layout)

    // ---- TradeBust_57 (V16) ----
    // Body layout (BLOCK_LENGTH=48): securityID@0 (long), securityExchange@8
    // shares offset with matchEventIndicator@8 (byte) — encoder writes the
    // MEI and leaves securityExchange/tradingSessionID as 0; tradingSessionID@9
    // (byte); mDEntryPx@12 (long mantissa); mDEntrySize@20 (long); tradeID@28
    // (uint); tradeDate@32 (ushort); transactTime@36 (ulong nanos); rptSeq@44
    // (uint, 0 = NULL).
    public const int TradeBustBlockLength = 48;
    public const int TradeBustBodySecurityIdOffset = 0;
    public const int TradeBustBodyMatchEventIndicatorOffset = 8;
    public const int TradeBustBodyTradingSessionIdOffset = 9;
    public const int TradeBustBodyMdEntryPxOffset = 12;
    public const int TradeBustBodyMdEntrySizeOffset = 20;
    public const int TradeBustBodyTradeIdOffset = 28;
    public const int TradeBustBodyTradeDateOffset = 32;
    public const int TradeBustBodyTransactTimeOffset = 36;
    public const int TradeBustBodyRptSeqOffset = 44;

    // ---- Sequence_2 (V16) ----
    // Body layout (BLOCK_LENGTH=4): nextSeqNo@0 (uint).
    public const int SequenceBlockLength = 4;
    public const int SequenceBodyNextSeqNoOffset = 0;

    // ---- SequenceReset_1 (V16) ----
    // Body has no fields (BLOCK_LENGTH=0).
    public const int SequenceResetBlockLength = 0;

    // ---- EmptyBook_9 (V16) ----
    // Body layout (BLOCK_LENGTH=20): securityID@0 (long); securityExchange@8
    // shares offset with matchEventIndicator@8 (byte); mDEntryTimestamp@12
    // (ulong nanos). The MDUpdateAction (NEW) and MDEntryType (EMPTY_BOOK)
    // are template-level constants — not encoded into the body.
    public const int EmptyBookBlockLength = 20;
    public const int EmptyBookBodySecurityIdOffset = 0;
    public const int EmptyBookBodyMatchEventIndicatorOffset = 8;
    public const int EmptyBookBodyMdEntryTimestampOffset = 12;

    // ---- MassDeleteOrders_MBO_52 (V16) ----
    // Body layout (BLOCK_LENGTH=28): securityID@0 (long); securityExchange@8
    // shares offset with matchEventIndicator@8 (byte); mDUpdateAction@9
    // (always DELETE_THRU); mDEntryType@10 (BID or OFFER); transactTime@16
    // (ulong nanos); rptSeq@24 (uint, 0 = NULL).
    public const int MassDeleteOrdersBlockLength = 28;
    public const int MassDeleteOrdersBodySecurityIdOffset = 0;
    public const int MassDeleteOrdersBodyMatchEventIndicatorOffset = 8;
    public const int MassDeleteOrdersBodyMdUpdateActionOffset = 9;
    public const int MassDeleteOrdersBodyMdEntryTypeOffset = 10;
    public const int MassDeleteOrdersBodyTransactTimeOffset = 16;
    public const int MassDeleteOrdersBodyRptSeqOffset = 24;

    // ---- SecurityStatus_3 (V16) ----
    // Body layout (BLOCK_LENGTH=36): securityID@0 (long); securityExchange@8
    // shares offset with matchEventIndicator@8 (byte); tradingSessionID@9
    // (byte); securityTradingStatus@10 (byte); securityTradingEvent@11
    // (byte, 255 = NULL); tradeDate@12 (ushort, LocalMktDate); pad@14;
    // tradSesOpenTime@16 (ulong nanos); transactTime@24 (ulong nanos);
    // rptSeq@32 (uint, 0 = NULL).
    public const int SecurityStatusBlockLength = 36;
    public const int SecurityStatusBodySecurityIdOffset = 0;
    public const int SecurityStatusBodyMatchEventIndicatorOffset = 8;
    public const int SecurityStatusBodyTradingSessionIdOffset = 9;
    public const int SecurityStatusBodySecurityTradingStatusOffset = 10;
    public const int SecurityStatusBodySecurityTradingEventOffset = 11;
    public const int SecurityStatusBodyTradeDateOffset = 12;
    public const int SecurityStatusBodyTradSesOpenTimeOffset = 16;
    public const int SecurityStatusBodyTransactTimeOffset = 24;
    public const int SecurityStatusBodyRptSeqOffset = 32;

    // ---- SecurityGroupPhase_10 (V16) ----
    // Body layout (BLOCK_LENGTH=32): securityGroup@0 (8-byte char array);
    // matchEventIndicator@8 (byte); tradingSessionID@9 (byte);
    // tradingSessionSubID@10 (byte); securityTradingEvent@11 (byte,
    // 255 = NULL); tradeDate@12 (ushort, LocalMktDate); pad@14;
    // tradSesOpenTime@16 (ulong nanos); transactTime@24 (ulong nanos).
    public const int SecurityGroupPhaseBlockLength = 32;
    public const int SecurityGroupPhaseBodySecurityGroupOffset = 0;
    public const int SecurityGroupPhaseBodySecurityGroupLength = 8;
    public const int SecurityGroupPhaseBodyMatchEventIndicatorOffset = 8;
    public const int SecurityGroupPhaseBodyTradingSessionIdOffset = 9;
    public const int SecurityGroupPhaseBodyTradingSessionSubIdOffset = 10;
    public const int SecurityGroupPhaseBodySecurityTradingEventOffset = 11;
    public const int SecurityGroupPhaseBodyTradeDateOffset = 12;
    public const int SecurityGroupPhaseBodyTradSesOpenTimeOffset = 16;
    public const int SecurityGroupPhaseBodyTransactTimeOffset = 24;

    // ---- ChannelReset_11 (V16) ----
    // Body: MatchEventIndicator @0 (4 bytes — UMDF wire treats the bitset as
    // a 4-byte block, see schema offset="4" on MDEntryTimestamp), then
    // MDEntryTimestamp @4 (8 bytes ulong nanos). Total 12 bytes.
    public const int ChannelResetBlockLength = 12;
    public const int ChannelResetBodyMatchEventIndicatorOffset = 0;
    public const int ChannelResetBodyMdEntryTimestampOffset = 4;

    // ---- SecurityDefinition_12 (V16) ----
    public const int SecDefBlockLength = 230;
    public const int SecDefSecurityIdOffset = 0;
    public const int SecDefSecurityExchangeOffset = 8;
    public const int SecDefSymbolOffset = 16;
    public const int SecDefSecurityTypeOffset = 37;
    public const int SecDefTotNoRelatedSymOffset = 40;
    public const int SecDefSecurityValidityTimestampOffset = 76;
    public const int SecDefMaturityDateOffset = 140;
    public const int SecDefIsinNumberOffset = 164;

    // SecDef body emits three empty repeating-group headers
    // (NoUnderlyings, NoLegs, NoInstrAttribs) so consumer ReadGroups paths stay safe.
    public const int GroupSizeEncodingSize = 3;
    public const int SecDefBodyTotal = SecDefBlockLength + GroupSizeEncodingSize * 3;
}
