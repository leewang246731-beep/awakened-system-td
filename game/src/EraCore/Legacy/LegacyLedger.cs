using System.Text;
using EraCore.Persistence;

namespace EraCore.Legacy;

/// <summary>
/// One recorded era transition (T-074): the committed jump with its levy
/// outcome summary — the campaign-domain payload carries the record so the
/// transition history survives load (A5 family: persisted facts only).
/// </summary>
/// <param name="FromEra">Era index left behind.</param>
/// <param name="ToEra">Era index entered.</param>
/// <param name="FundGained">Fund units the 3:1 fold deposited at this jump.</param>
public sealed record LegacyTransitionRecord(int FromEra, int ToEra, long FundGained);

/// <summary>
/// Aggregate persistent ledger of the C-20 legacy subsystem (T-074): the
/// wonder board (跃迁硬条件), the knowledge-scroll board (F.3 同节点 ≤1), the
/// memorial aura board (取最大 ≤3), the civilization-fund ledger (传承商店),
/// the diggable abstract list (F.7 降级产物) and the transition history.
/// Encodes to/from one opaque payload that travels through the K-7 campaign
/// document as a layout snapshot (payload format owned by C-04/C-20); the
/// byte encoding is deterministic (same state ⇒ same bytes), which both the
/// rollback journal's exact restore and the determinism tests rely on.
/// </summary>
public sealed class LegacyLedger
{
    private static readonly byte[] Magic = { (byte)'E', (byte)'1', (byte)'L', (byte)'G' };

    private const byte FormatVersion = 1;

    private readonly ArchaeologyTable _table;
    private readonly HeritageShopTable _shop;
    private readonly HashSet<string> _wondersBuilt = new(StringComparer.Ordinal);
    private readonly List<string> _abstractList = new();
    private readonly List<LegacyTransitionRecord> _transitions = new();

    private MemorialAuraBoard _memorials;
    private KnowledgeScrollBoard _scrolls;
    private CivilizationFundLedger _fund;

    /// <summary>Reserved layout-snapshot slot id of the ledger payload inside the campaign document.</summary>
    public const string LedgerSlotId = "legacy:campaign-ledger";

    /// <param name="table">Archaeology content table.</param>
    /// <param name="shop">Heritage-shop shelf content.</param>
    /// <exception cref="ArgumentNullException">table or shop is null.</exception>
    public LegacyLedger(ArchaeologyTable table, HeritageShopTable shop)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(shop);
        _table = table;
        _shop = shop;
        _memorials = new MemorialAuraBoard(table);
        _scrolls = new KnowledgeScrollBoard(table);
        _fund = new CivilizationFundLedger(shop);
    }

    /// <summary>Memorial aura board (F-059 同类取最大、至多 3 座生效).</summary>
    public MemorialAuraBoard Memorials => _memorials;

    /// <summary>Knowledge-scroll board (F.3 同一科技节点至多 1 张).</summary>
    public KnowledgeScrollBoard Scrolls => _scrolls;

    /// <summary>Civilization-fund ledger (传承商店).</summary>
    public CivilizationFundLedger Fund => _fund;

    /// <summary>Wonder ids marked built, in mark order.</summary>
    public IReadOnlyList<string> BuiltWonderIds => _wondersBuilt.ToList();

    /// <summary>Diggable abstract-list entries (F.7 ruins + cap skips; archaeology site feed).</summary>
    public IReadOnlyList<string> AbstractList => _abstractList;

    /// <summary>Committed transitions, oldest first.</summary>
    public IReadOnlyList<LegacyTransitionRecord> Transitions => _transitions;

    /// <summary>Marks one wonder built (the transition precondition input).</summary>
    /// <exception cref="ArgumentException">wonderId blank.</exception>
    public void MarkWonderBuilt(string wonderId)
    {
        if (string.IsNullOrWhiteSpace(wonderId))
        {
            throw new ArgumentException("Wonder id must not be blank.", nameof(wonderId));
        }

        _wondersBuilt.Add(wonderId.Trim());
    }

    /// <summary>True when the wonder is marked built.</summary>
    public bool IsWonderBuilt(string wonderId) => _wondersBuilt.Contains(wonderId);

    /// <summary>
    /// Ingests the mapping pass's abstract list (diggable sites); duplicate
    /// entries are skipped (replay of the same transition stays idempotent).
    /// </summary>
    /// <exception cref="ArgumentNullException">entries is null.</exception>
    public void InheritAbstractList(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (string entry in entries)
        {
            if (!_abstractList.Contains(entry))
            {
                _abstractList.Add(entry);
            }
        }
    }

    /// <summary>Records one committed transition.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a legal +1 era step inside 1..6.</exception>
    public void RecordTransition(int fromEra, int toEra, long fundGained)
    {
        if (fromEra is < 1 or > 6 || toEra is < 1 or > 6 || toEra != fromEra + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fromEra), $"{fromEra}→{toEra} is not a legal era step.");
        }

        _transitions.Add(new LegacyTransitionRecord(fromEra, toEra, fundGained));
    }

    /// <summary>FNV-1a hash over the encoded ledger bytes (determinism anchor).</summary>
    public long ComputeStateHash()
    {
        byte[] payload = Encode();
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (byte b in payload)
            {
                hash ^= b;
                hash *= prime;
            }

            return unchecked((long)hash);
        }
    }

    /// <summary>
    /// Encodes the full ledger state to the deterministic payload bytes
    /// (the K-7 layout-payload carrier, slot <see cref="LedgerSlotId"/>).
    /// </summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);

        WriteStringList(writer, _fund.RedeemedIds);
        writer.Write(_fund.Balance);

        WriteStringList(writer, _scrolls.ReadScrollIds);

        IReadOnlyList<MemorialRegistration> memorials = _memorials.Memorials;
        writer.Write(memorials.Count);
        foreach (MemorialRegistration memorial in memorials)
        {
            writer.Write(memorial.Ordinal);
            writer.Write(memorial.Auras.Count);
            foreach ((string kind, double value) in memorial.Auras.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteString(writer, kind);
                writer.Write(value);
            }
        }

        writer.Write(_wondersBuilt.Count);
        foreach (string wonder in _wondersBuilt)
        {
            WriteString(writer, wonder);
        }

        WriteStringList(writer, _abstractList);

        writer.Write(_transitions.Count);
        foreach (LegacyTransitionRecord record in _transitions)
        {
            writer.Write(record.FromEra);
            writer.Write(record.ToEra);
            writer.Write(record.FundGained);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Resets THIS ledger's state from a payload (the load path and the
    /// transaction journal's exact-restore path; the content tables stay
    /// bound). The payload is staged into fresh boards first — a damaged or
    /// inconsistent payload leaves this instance untouched.
    /// </summary>
    /// <exception cref="ArgumentNullException">payload is null.</exception>
    /// <exception cref="SaveCorruptException">Payload is damaged or internally inconsistent.</exception>
    public void DecodeInto(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            {
                throw new SaveCorruptException("Bad magic: not an EraCore legacy ledger.", storedVersion: 0);
            }

            byte version = reader.ReadByte();
            if (version != FormatVersion)
            {
                throw new SaveCorruptException($"Legacy ledger format version {version} is not {FormatVersion}.", version);
            }

            var redeemed = ReadStringList(reader, version);
            long balance = reader.ReadInt64();
            var scrollsRead = ReadStringList(reader, version);

            // ---- stage fresh boards (RegisterMemorial/Read/Redeem validate as they replay)
            var stagedMemorials = new MemorialAuraBoard(_table);
            int memorialCount = ReadCount(reader, version);
            for (int i = 0; i < memorialCount; i++)
            {
                int ordinal = reader.ReadInt32();
                int auraCount = ReadCount(reader, version);
                var auras = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int a = 0; a < auraCount; a++)
                {
                    string kind = ReadString(reader, version);
                    auras[kind] = reader.ReadDouble();
                }

                MemorialRegistration registration = stagedMemorials.RegisterMemorial(auras);
                if (registration.Ordinal != ordinal)
                {
                    throw new SaveCorruptException(
                        $"Memorial ordinal {ordinal} breaks registration order ({registration.Ordinal}).", version);
                }
            }

            var stagedScrolls = new KnowledgeScrollBoard(_table);
            foreach (string scroll in scrollsRead)
            {
                _ = stagedScrolls.Read(scroll);
            }

            var stagedFund = new CivilizationFundLedger(_shop, 0);
            foreach (string item in redeemed)
            {
                HeritageShopItem? row = _shop.Items.FirstOrDefault(entry => entry.Id == item)
                    ?? throw new SaveCorruptException($"Ledger references heritage-shop item '{item}' this build does not know.", version);
                stagedFund.DepositFund(row.PriceFund); // 先按价回充再兑付=复演兑付校验而不二次扣款
                _ = stagedFund.Redeem(item);
            }

            stagedFund.DepositFund(balance - stagedFund.Balance); // 存量余额权威：差额入账

            var wonders = new HashSet<string>(StringComparer.Ordinal);
            int wonderCount = ReadCount(reader, version);
            for (int i = 0; i < wonderCount; i++)
            {
                wonders.Add(ReadString(reader, version));
            }

            var abstractList = ReadStringList(reader, version);
            var transitions = new List<LegacyTransitionRecord>();
            int transitionCount = ReadCount(reader, version);
            for (int i = 0; i < transitionCount; i++)
            {
                transitions.Add(new LegacyTransitionRecord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64()));
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new SaveCorruptException("Trailing bytes after the ledger payload.", version);
            }

            // ---- validated end-to-end: commit
            _memorials = stagedMemorials;
            _scrolls = stagedScrolls;
            _fund = stagedFund;
            _wondersBuilt.Clear();
            foreach (string wonder in wonders)
            {
                _wondersBuilt.Add(wonder);
            }

            _abstractList.Clear();
            _abstractList.AddRange(abstractList);
            _transitions.Clear();
            _transitions.AddRange(transitions);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ObjectDisposedException or DecoderFallbackException)
        {
            throw new SaveCorruptException($"Ledger payload truncated at {payload.Length} bytes.", storedVersion: 0, ex);
        }
        catch (SaveCorruptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SaveCorruptException($"Ledger payload references content this build does not know: {ex.Message}", FormatVersion, ex);
        }
    }

    // ------------------------------------------------------------ codec helpers

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, byte version)
    {
        int length = reader.ReadInt32();
        if (length <= 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new SaveCorruptException($"String length {length} exceeds the remaining payload.", version);
        }

        byte[] bytes = reader.ReadBytes(length);
        return bytes.Length == length ? Encoding.UTF8.GetString(bytes) : throw new SaveCorruptException("String truncated.", version);
    }

    private static void WriteStringList(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (string value in values)
        {
            WriteString(writer, value);
        }
    }

    private static List<string> ReadStringList(BinaryReader reader, byte version)
    {
        int count = ReadCount(reader, version);
        var values = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(ReadString(reader, version));
        }

        return values;
    }

    private static int ReadCount(BinaryReader reader, byte version)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new SaveCorruptException($"Count {count} exceeds the remaining payload.", version);
        }

        return count;
    }
}
