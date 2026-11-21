using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Restocker.Market;

// Fork: CashFlow's SqliteDataProvider copies Price unchanged and TabRetainerSales sums it
// once per stack. The history amount is seller proceeds after fees (also documented by
// AscendedLedger's RetainerHistoryCaptureService). Never tax it again or multiply by quantity.
public sealed record RetainerHistorySale(uint ItemId, uint NetGil, uint UnixSeconds, int Quantity,
    bool IsHq, bool IsMannequin, string Buyer)
{
    public const int RecordSize = 52;
    public const int MaxRecords = 20;
    private const int EntriesOffset = 8;
    public const int PacketSize = EntriesOffset + MaxRecords * RecordSize;

    public static List<RetainerHistorySale> Parse(ReadOnlySpan<byte> packet, Action<string>? log = null)
    {
        var sales = new List<RetainerHistorySale>();
        for (var i = 0; i < MaxRecords; i++)
        {
            var offset = EntriesOffset + i * RecordSize;
            if (offset + RecordSize > packet.Length) break;
            var row = packet.Slice(offset, RecordSize);
            var item = BinaryPrimitives.ReadUInt32LittleEndian(row);
            if (item == 0) break;
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
            if (quantity is 0 or > int.MaxValue)
            {
                log?.Invoke($"[Restocky] history: skipped row={i} item={item} invalid quantity={quantity}");
                continue;
            }
            var encodedHq = item is > 1_000_000 and < 2_000_000;
            if (encodedHq) item -= 1_000_000;
            var name = row.Slice(19, 32);
            var end = name.IndexOf((byte)0);
            sales.Add(new(item, BinaryPrimitives.ReadUInt32LittleEndian(row[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]), (int)quantity, encodedHq || row[16] != 0,
                row[18] != 0, Encoding.UTF8.GetString(end < 0 ? name : name[..end])));
        }
        return sales;
    }
}
