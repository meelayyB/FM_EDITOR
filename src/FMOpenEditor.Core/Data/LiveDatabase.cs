using System.Buffers.Binary;
using System.Text;
using FMOpenEditor.Core.Definitions;
using FMOpenEditor.Core.Memory;
using FMOpenEditor.Core.Model;
using FMOpenEditor.Core.Research;

namespace FMOpenEditor.Core.Data;

/// <summary>Reads and writes a running game's memory, driven entirely by a <see cref="GameDefinition"/>.</summary>
public sealed class LiveDatabase : IGameDatabase
{
    private readonly IMemoryAccess _memory;
    private readonly GameDefinition _def;
    private readonly nint _moduleBase;
    private readonly long _moduleSize;
    private readonly IDisposable? _owner;

    private readonly Dictionary<string, nint> _roots = [];
    private readonly Dictionary<EntityKind, List<Entity>> _entities = [];
    private readonly Dictionary<EntityKind, Dictionary<int, Entity>> _byId = [];
    private readonly Dictionary<nint, Entity> _byAddress = [];
    private List<RttiClass>? _rttiClasses;

    public string DisplayName { get; }
    public bool IsLive => true;
    public GameDefinition Definition => _def;

    /// <summary>Problems found while resolving roots and collections; non-empty means partial support.</summary>
    public List<string> Warnings { get; } = [];

    public LiveDatabase(IMemoryAccess memory, GameDefinition definition, nint moduleBase, long moduleSize,
        string displayName, IDisposable? owner = null)
    {
        _memory = memory;
        _def = definition;
        _moduleBase = moduleBase;
        _moduleSize = moduleSize;
        _owner = owner;
        DisplayName = displayName;
        ResolveRoots();
        Refresh();
    }

    public DateOnly GameDate
    {
        get
        {
            if (_def.GameDate is { IsMapped: true } f && _def.GameDateRoot is { } r && _roots.TryGetValue(r, out var root))
            {
                if (ReadField(root, f, ValueKind.Date) is DateOnly d) return d;
            }
            return DateOnly.FromDateTime(DateTime.Today);
        }
    }

    private void ResolveRoots()
    {
        foreach (var (name, root) in _def.Roots)
        {
            try
            {
                nint addr;
                if (!string.IsNullOrWhiteSpace(root.Pattern))
                {
                    var match = SignatureScanner.Find(_memory, _moduleBase, _moduleSize, root.Pattern)
                                ?? throw new InvalidOperationException("signature not found");
                    addr = SignatureScanner.ResolveRipRelative(_memory, match, root.DispOffset, root.InstructionLength);
                }
                else if (!string.IsNullOrWhiteSpace(root.ModuleOffset))
                {
                    addr = _moduleBase + (nint)GameDefinition.ParseOffset(root.ModuleOffset);
                }
                else
                {
                    Warnings.Add($"Root '{name}' is not mapped yet.");
                    continue;
                }

                foreach (var hop in root.Deref) addr = _memory.ReadPointer(addr + (nint)GameDefinition.ParseOffset(hop));
                _roots[name] = addr;
            }
            catch (Exception ex)
            {
                Warnings.Add($"Root '{name}': {ex.Message}");
            }
        }
    }

    public void Refresh()
    {
        _entities.Clear();
        _byId.Clear();
        _byAddress.Clear();
        var rttiRecords = ScanRttiCollections();
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            var list = new List<Entity>();
            var ids = new Dictionary<int, Entity>();
            if (_def.Collections.TryGetValue(kind.ToString(), out var col))
            {
                IEnumerable<nint>? records = null;
                if (col.Layout == CollectionLayout.RttiInstances) records = rttiRecords.GetValueOrDefault(kind.ToString());
                else if (_roots.TryGetValue(col.Root, out var root)) records = EnumerateRecords(root, col);
                if (records is not null)
                try
                {
                    var idField = FieldsFor(kind)?.GetValueOrDefault("Id");
                    var index = 0;
                    foreach (var addr in records)
                    {
                        if (col.Filter is { IsMapped: true } filter && ReadRaw(addr, filter) != col.FilterValue) continue;
                        var id = idField is { IsMapped: true } ? (int)ReadRaw(addr, idField) : index;
                        index++;
                        var e = new Entity(kind, id, addr);
                        list.Add(e);
                        ids.TryAdd(id, e);
                        _byAddress.TryAdd(addr, e);
                    }
                }
                catch (Exception ex)
                {
                    Warnings.Add($"{kind} list: {ex.Message}");
                }
            }
            _entities[kind] = list;
            _byId[kind] = ids;
        }
    }

    /// <summary>
    /// Resolves every <see cref="CollectionLayout.RttiInstances"/> collection with one RTTI lookup and one
    /// pass over the heap. Keyed by entity kind name; each list is in address order without duplicates.
    /// </summary>
    private Dictionary<string, List<nint>> ScanRttiCollections()
    {
        var result = new Dictionary<string, List<nint>>();
        var wanted = _def.Collections.Where(c => c.Value.Layout == CollectionLayout.RttiInstances).ToList();
        if (wanted.Count == 0) return result;

        try
        {
            _rttiClasses ??= RttiScanner.FindClasses(_memory, _moduleBase, _moduleSize);
        }
        catch (Exception ex)
        {
            Warnings.Add($"RTTI: {ex.Message}");
            return result;
        }
        var byName = _rttiClasses.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());

        var vtablesByKind = new Dictionary<string, List<nint>>();
        foreach (var (kind, col) in wanted)
        {
            var list = vtablesByKind[kind] = [];
            foreach (var src in col.Classes ?? [])
            {
                if (!byName.TryGetValue(src.Class, out var cls)) { Warnings.Add($"{kind} list: class {src.Class} not found."); continue; }
                var offset = (int)GameDefinition.ParseOffset(src.VtableOffset);
                var vt = cls.Vtables.FirstOrDefault(v => v.ObjectOffset == offset);
                if (vt is null) { Warnings.Add($"{kind} list: {src.Class} has no vtable at offset 0x{offset:X}."); continue; }
                list.Add(vt.Address);
            }
        }

        var all = vtablesByKind.Values.SelectMany(v => v).Distinct().Select(v => new RttiVtable(v, 0, 0)).ToList();
        var max = wanted.Max(c => c.Value.MaxCount);
        var scan = RttiScanner.FindInstances(_memory, all, _moduleBase, _moduleSize, max);
        foreach (var (kind, vtables) in vtablesByKind)
        {
            var records = vtables.SelectMany(v => scan.Samples.GetValueOrDefault(v) ?? []).Distinct().Order().ToList();
            var cap = _def.Collections[kind].MaxCount;
            if (records.Count > cap) { Warnings.Add($"{kind} list: {records.Count:N0} objects, more than the {cap:N0} allowed."); records = records[..cap]; }
            result[kind] = records;
        }
        return result;
    }

    private IEnumerable<nint> EnumerateRecords(nint root, CollectionDef col)
    {
        switch (col.Layout)
        {
            case CollectionLayout.PointerRange:
            {
                var begin = _memory.ReadPointer(root + (nint)GameDefinition.ParseOffset(col.BeginOffset));
                var end = _memory.ReadPointer(root + (nint)GameDefinition.ParseOffset(col.EndOffset));
                var count = (end - begin) / 8;
                if (count < 0 || count > col.MaxCount) throw new InvalidDataException($"implausible count {count}");
                for (var i = 0; i < count; i++)
                {
                    var p = _memory.ReadPointer(begin + i * 8);
                    if (p != 0) yield return p;
                }
                break;
            }
            case CollectionLayout.PointerArray:
            case CollectionLayout.InlineArray:
            {
                var begin = _memory.ReadPointer(root + (nint)GameDefinition.ParseOffset(col.BeginOffset));
                var count = _memory.ReadInt32(root + (nint)GameDefinition.ParseOffset(col.CountOffset ?? "0x8"));
                if (count < 0 || count > col.MaxCount) throw new InvalidDataException($"implausible count {count}");
                for (var i = 0; i < count; i++)
                {
                    if (col.Layout == CollectionLayout.InlineArray)
                    {
                        yield return begin + i * col.Stride;
                        continue;
                    }
                    var p = _memory.ReadPointer(begin + i * 8);
                    if (p != 0) yield return p;
                }
                break;
            }
        }
    }

    public IReadOnlyList<Entity> GetAll(EntityKind kind) => _entities.GetValueOrDefault(kind) ?? [];

    public Entity? Find(EntityKind kind, int id) =>
        _byId.TryGetValue(kind, out var map) && map.TryGetValue(id, out var e) ? e : null;

    private Dictionary<string, FieldDef>? FieldsFor(EntityKind kind) => _def.Structs.GetValueOrDefault(kind.ToString());

    private FieldDef? Field(EntityKind kind, string key) =>
        FieldsFor(kind)?.GetValueOrDefault(key) is { IsMapped: true } f ? f : null;

    public bool Supports(EntityKind kind, string key) => Field(kind, key) is not null;

    public bool CanWrite(EntityKind kind, string key) => Field(kind, key) is { ReadOnly: false };

    public object? Read(Entity entity, string key)
    {
        var field = Field(entity.Kind, key) ?? throw new NotSupportedException($"{entity.Kind}.{key} is not mapped for {_def.Version}.");
        var value = ReadField(entity.Address, field, PropertyCatalog.Get(entity.Kind, key).Kind);
        if (value is string { Length: > 0 } || key is not ("FirstName" or "LastName" or "CommonName")) return value;
        return ReadConsolePersonName(entity, key) ?? value;
    }

    /// <summary>
    /// FM27 Console / Game Pass can expose a different base subobject through RTTI than the Steam build. The name
    /// table itself is unchanged, so try the confirmed name-entry slots relative to each base the Console build has
    /// been observed to expose. This is deliberately read-only; writes still use the definition's verified address.
    /// </summary>
    private string? ReadConsolePersonName(Entity entity, string key)
    {
        var part = key switch { "FirstName" => 0, "LastName" => 8, "CommonName" => 16, _ => -1 };
        if (part < 0 || entity.Kind is not (EntityKind.Player or EntityKind.Staff)) return null;

        // db::PERSON base, complete player object, Console player-array subobject, complete staff object.
        var bases = entity.Kind == EntityKind.Player
            ? new[] { 0x50, 0x2D8, 0x228 }
            : new[] { 0x50, 0x150 };
        foreach (var offset in bases)
        {
            try
            {
                var entry = _memory.ReadPointer(entity.Address + offset + part);
                if (entry == 0) continue;
                var str = _memory.ReadPointer(entry);
                if (str == 0) continue;
                var length = _memory.ReadInt32(str);
                if (length is <= 0 or > 256) continue;
                var name = Encoding.UTF8.GetString(_memory.ReadBytes(str + 4, length));
                if (name.Any(char.IsLetter) && !name.Any(char.IsControl)) return name;
            }
            catch
            {
                // A candidate that is not this build's base simply is not readable.
            }
        }
        return null;
    }

    public void Write(Entity entity, string key, object? value)
    {
        var field = Field(entity.Kind, key) ?? throw new NotSupportedException($"{entity.Kind}.{key} is not mapped for {_def.Version}.");
        var prop = PropertyCatalog.Get(entity.Kind, key);
        if (field.ReadOnly) throw new NotSupportedException($"{prop.Label} can be read but not changed in this version.");
        var addr = FieldAddress(entity.Address, field)
                   ?? throw new NotSupportedException($"{prop.Label} is empty for this record, so there is nothing to edit in place.");

        switch (field.Type)
        {
            case FieldType.FmDate:
                var d = value as DateOnly? ?? default;
                var dateBytes = new byte[4];
                var dayBase = _def.DateDayBase;
                var emptyYear = dayBase == 1 ? 1900 : 0;
                BinaryPrimitives.WriteUInt16LittleEndian(dateBytes, (ushort)(value is null ? dayBase : d.DayOfYear - 1 + dayBase));
                BinaryPrimitives.WriteUInt16LittleEndian(dateBytes.AsSpan(2), (ushort)(value is null ? emptyYear : d.Year));
                _memory.WriteChecked(addr, dateBytes);
                break;
            case FieldType.Utf16Inline:
                var text = ((string?)value ?? "")[..Math.Min(((string?)value ?? "").Length, field.MaxLength - 1)];
                var textBytes = new byte[field.MaxLength * 2];
                Encoding.Unicode.GetBytes(text, textBytes);
                _memory.WriteChecked(addr, textBytes);
                break;
            case FieldType.Utf16Ptr or FieldType.Utf8Ptr:
                // Growing a heap string means allocating in the game; overwrite in place when it fits.
                var target = _memory.ReadPointer(addr);
                var enc = field.Type == FieldType.Utf16Ptr ? Encoding.Unicode : Encoding.UTF8;
                var current = _memory.ReadString(target, enc, field.MaxLength);
                var next = (string?)value ?? "";
                if (next.Length > current.Length)
                    throw new NotSupportedException($"{prop.Label} can be at most {current.Length} characters in a live game.");
                var bytes = enc.GetBytes(next + "\0");
                _memory.WriteChecked(target, bytes);
                break;
            case FieldType.FmString:
            {
                // Same rule as other heap strings: rewrite in place, never longer than what is there.
                var str = _memory.ReadPointer(addr);
                if (str == 0) throw new NotSupportedException($"{prop.Label} is empty for this record, so there is nothing to edit in place.");
                var currentBytes = _memory.ReadInt32(str);
                var newBytes = Encoding.UTF8.GetBytes((string?)value ?? "");
                if (currentBytes < 0 || newBytes.Length > currentBytes)
                    throw new NotSupportedException($"{prop.Label} can be at most {currentBytes} bytes (UTF-8) in a live game.");
                var block = new byte[4 + currentBytes + 1];
                BinaryPrimitives.WriteInt32LittleEndian(block, newBytes.Length);
                newBytes.CopyTo(block, 4);
                _memory.WriteChecked(str, block);
                break;
            }
            case FieldType.EntityPtr:
                var refKind = prop.RefKind ?? throw new InvalidDataException($"{key} has no reference kind.");
                var ptr = value is int id && Find(refKind, id) is { } refEntity ? refEntity.Address : 0;
                _memory.WriteChecked(addr, BitConverter.GetBytes((long)ptr));
                break;
            case FieldType.Bool when !string.IsNullOrWhiteSpace(field.Mask):
            {
                // One flag in a shared byte: change that bit and leave its neighbours alone.
                var mask = (byte)GameDefinition.ParseOffset(field.Mask);
                var flags = _memory.ReadByte(addr);
                var on = value is not null && Values.ToDouble(value) != 0;
                _memory.WriteChecked(addr, [(byte)(on ? flags | mask : flags & ~mask)]);
                break;
            }
            case var _ when field.Values is { Count: > 0 } stored:
            {
                var index = value is null ? -1 : (int)Values.ToDouble(value);
                if (index < 0 || index >= stored.Count) throw new NotSupportedException($"{prop.Label} has no choice number {index}.");
                WriteNumber(addr, field, stored[index]);
                break;
            }
            default:
                WriteNumber(addr, field, value is null ? 0 : Values.ToDouble(value) * field.Scale);
                break;
        }
    }

    /// <summary>The field's address, or null when a pointer along its <see cref="FieldDef.Path"/> is empty.</summary>
    private nint? FieldAddress(nint recordBase, FieldDef field)
    {
        var addr = recordBase;
        if (field.Path is { } path)
        {
            foreach (var hop in path)
            {
                addr = _memory.ReadPointer(addr + (nint)GameDefinition.ParseOffset(hop));
                if (addr == 0) return null;
            }
        }
        return addr + (nint)GameDefinition.ParseOffset(field.Offset!);
    }

    private long ReadRaw(nint recordBase, FieldDef field) =>
        FieldAddress(recordBase, field) is { } addr ? (long)ReadNumber(addr, field.Type) : 0;

    private object? ReadField(nint recordBase, FieldDef field, ValueKind kind)
    {
        if (FieldAddress(recordBase, field) is not { } addr) return null;
        switch (field.Type)
        {
            case FieldType.FmDate:
                var day = _memory.ReadUInt16(addr) - _def.DateDayBase;
                var year = _memory.ReadUInt16(addr + 2);
                if (year is < 1 or > 9999 || day < 0) return null;
                if (_def.DateDayBase == 1 && year <= 1900) return null;
                return new DateOnly(year, 1, 1).AddDays(Math.Min(day, 365));
            case FieldType.FmString:
                var str = _memory.ReadPointer(addr);
                if (str == 0) return null;
                var length = _memory.ReadInt32(str);
                if (length <= 0 || length > field.MaxLength * 4) return length == 0 ? "" : null;
                return Encoding.UTF8.GetString(_memory.ReadBytes(str + 4, length));
            case FieldType.Utf16Ptr:
                return _memory.ReadString(_memory.ReadPointer(addr), Encoding.Unicode, field.MaxLength);
            case FieldType.Utf8Ptr:
                return _memory.ReadString(_memory.ReadPointer(addr), Encoding.UTF8, field.MaxLength);
            case FieldType.Utf16Inline:
                return _memory.ReadString(addr, Encoding.Unicode, field.MaxLength);
            case FieldType.EntityPtr:
                var p = _memory.ReadPointer(addr);
                return p != 0 && _byAddress.TryGetValue(p, out var e) ? e.Id : null;
        }

        if (field.Type == FieldType.Bool && !string.IsNullOrWhiteSpace(field.Mask))
            return (_memory.ReadByte(addr) & (byte)GameDefinition.ParseOffset(field.Mask)) != 0;

        if (field.Values is { Count: > 0 } stored)
        {
            var index = stored.IndexOf((long)ReadNumber(addr, field.Type));
            return index < 0 ? null : index;
        }

        var shown = ReadNumber(addr, field.Type) / field.Scale;
        return kind switch
        {
            ValueKind.Boolean => shown != 0,
            ValueKind.Money => (long)Math.Round(shown),
            ValueKind.Decimal => shown,
            ValueKind.Reference => shown < 0 ? null : (int)shown,
            _ => (int)Math.Round(shown),
        };
    }

    private double ReadNumber(nint addr, FieldType type) => type switch
    {
        FieldType.U8 or FieldType.Bool => _memory.ReadByte(addr),
        FieldType.I8 => (sbyte)_memory.ReadByte(addr),
        FieldType.U16 => _memory.ReadUInt16(addr),
        FieldType.I16 => _memory.ReadInt16(addr),
        FieldType.I32 => _memory.ReadInt32(addr),
        FieldType.U32 => _memory.ReadUInt32(addr),
        FieldType.I64 => _memory.ReadInt64(addr),
        FieldType.F32 => _memory.ReadSingle(addr),
        FieldType.F64 => _memory.ReadDouble(addr),
        _ => throw new NotSupportedException($"{type} is not numeric."),
    };

    private void WriteNumber(nint addr, FieldDef field, double raw)
    {
        var r = Math.Round(raw);
        byte[] bytes = field.Type switch
        {
            FieldType.U8 or FieldType.Bool => [(byte)Math.Clamp(r, 0, 255)],
            FieldType.I8 => [(byte)(sbyte)Math.Clamp(r, -128, 127)],
            FieldType.U16 => BitConverter.GetBytes((ushort)Math.Clamp(r, 0, ushort.MaxValue)),
            FieldType.I16 => BitConverter.GetBytes((short)Math.Clamp(r, short.MinValue, short.MaxValue)),
            FieldType.I32 => BitConverter.GetBytes((int)Math.Clamp(r, int.MinValue, int.MaxValue)),
            FieldType.U32 => BitConverter.GetBytes((uint)Math.Clamp(r, 0, uint.MaxValue)),
            FieldType.I64 => BitConverter.GetBytes((long)r),
            FieldType.F32 => BitConverter.GetBytes((float)raw),
            FieldType.F64 => BitConverter.GetBytes(raw),
            _ => throw new NotSupportedException($"{field.Type} is not numeric."),
        };
        _memory.WriteChecked(addr, bytes);
    }

    public void Dispose() => _owner?.Dispose();
}
