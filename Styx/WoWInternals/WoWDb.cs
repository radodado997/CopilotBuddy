using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GreenMagic;
using Styx.Patchables;

namespace Styx.WoWInternals
{
    public class WoWDb
    {
        #region Fields

        private static readonly Dictionary<ClientDb, DbTable> _tables = new Dictionary<ClientDb, DbTable>();
        // Guards _tables and _initialized.  The walk can now also be retried from a lookup
        // (see EnsureInitialized), so a reader must not observe the dictionary while the
        // writer adds to it or clears it after a failed attempt.
        private static readonly object _sync = new object();
        // Static flag: true once init succeeded. False allows retry if WoW wasn't attached on first call.
        private static bool _initialized = false;
        // Environment.TickCount64 of the last walk attempt; throttles the retry path so a
        // layout mismatch cannot turn every lookup into a 4096-iteration remote walk.
        private static long _lastInitAttemptTick = long.MinValue;
        private const int InitRetryIntervalMs = 1000;

        #endregion

        #region Constructor

        internal WoWDb()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Runs the registration-table walk if it has not succeeded yet.  Called from the
        /// constructor and, if that first attempt failed, from every lookup: StyxWoW.Db
        /// caches this instance, so an instance built before the client was readable (or
        /// during a walk failure) would otherwise return empty results for the rest of the
        /// session even after the DBC tables became available.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (_initialized) return;

            var wow = ObjectManager.Wow;
            if (wow == null) return;   // not attached yet - nothing to walk

            lock (_sync)
            {
                if (_initialized) return;

                long now = Environment.TickCount64;
                // Check the sentinel first: `now - long.MinValue` overflows (the result wraps
                // negative in unchecked arithmetic), which would make the very first attempt
                // look like a recent one and skip the walk forever.
                if (_lastInitAttemptTick != long.MinValue &&
                    now - _lastInitAttemptTick < InitRetryIntervalMs)
                {
                    return;             // retried recently; do not hammer the client
                }
                _lastInitAttemptTick = now;

                InitializeCore(wow);
            }
        }

        /// <summary>
        /// Discards the cached table pointers. Called after the bot re-attaches to a
        /// different client process (auto-relogin): every <see cref="DbTable"/> holds an
        /// address inside the old process, so keeping them would make every DBC lookup read
        /// unmapped memory in the new one.
        /// </summary>
        internal static void ResetForNewProcess()
        {
            lock (_sync)
            {
                _tables.Clear();
                _initialized = false;
                _lastInitAttemptTick = long.MinValue;
            }
        }

        private static void InitializeCore(Memory wow)
        {

            // _initialized is only set once the registration walk actually reached the
            // RET terminator.  Otherwise a layout mismatch would latch a half-populated
            // table set forever (the old code published _initialized *before* the walk).
            try
            {
                // Base address for ClientDb_RegisterBase
                uint addr = (uint)GlobalOffsets.ClientDb_RegisterBase; // 0x6337D0
                
                // Walk through the registration table.
                // Bounded: a failed read returns 0 (never 0xC3), so if the client layout
                // does not match the hardcoded offsets this walk used to loop forever
                // reading unmapped memory. Each real entry is 17 bytes; 4096 entries is
                // far beyond any 3.3.5a build and acts as a hard stop.
                bool reachedTerminator = false;
                for (int guard = 0; guard < 4096; guard++)
                {
                    byte opcode = wow.Read<byte>(addr);
                    if (opcode == 0xC3) // RET instruction = end of list
                    {
                        reachedTerminator = true;
                        break;
                    }

                    uint tableId = wow.Read<uint>(addr + 1);
                    int tablePtr = wow.Read<int>(addr + 11);
                    IntPtr pointer = new IntPtr(tablePtr + 24);
                    
                    if (!_tables.ContainsKey((ClientDb)tableId))
                    {
                        _tables.Add((ClientDb)tableId, new DbTable(pointer));
                    }
                    
                    addr += 17; // Each entry is 17 bytes
                }

                if (!reachedTerminator)
                {
                    // No terminator within the bound: treat the whole walk as failed,
                    // discard the partial results and leave _initialized false so a later
                    // WoWDb instance can retry against a valid layout.
                    int partial = _tables.Count;
                    _tables.Clear();
                    Helpers.Logging.WriteDebug("[WoWDb] ClientDb table walk never reached the RET terminator; discarded {0} partial tables, will retry", partial);
                    return;
                }

                _initialized = true;

                // Diagnostics must not invalidate a good initialisation.
                try
                {
                    Helpers.Logging.WriteDebug($"[WoWDb] Loaded {_tables.Count} DBC tables");
                    int logged = 0;
                    foreach (var kvp in _tables)
                    {
                        if (logged++ < 5)
                            Helpers.Logging.WriteDebug($"[WoWDb]   Key={(int)kvp.Key} (0x{(int)kvp.Key:X8}) Rows={kvp.Value.NumRows}");
                    }

                    // Check if Lock DBC is accessible
                    DbTable? lockDb;
                    lock (_sync)
                    {
                        _tables.TryGetValue(ClientDb.Lock, out lockDb);
                    }
                    Helpers.Logging.WriteDebug($"[WoWDb] Lock DBC lookup: {(lockDb != null ? $"FOUND (rows={lockDb.NumRows})" : "NOT FOUND")}");
                    Helpers.Logging.WriteDebug($"[WoWDb] ClientDb.Lock enum value = {(int)ClientDb.Lock} (0x{(int)ClientDb.Lock:X8})");
                }
                catch (Exception ex)
                {
                    Helpers.Logging.WriteDebug("[WoWDb] Diagnostics failed: {0}", ex.Message);
                }
            }
            catch (Exception)
            {
                // Table initialization failed - discard partial results so the next
                // WoWDb instance retries instead of using a half-built table set.
                _tables.Clear();
                _initialized = false;
            }
        }

        #endregion

        #region Indexer
        public DbTable? this[ClientDb db]
        {
            get
            {
                // Retry a failed/early initialisation before answering, so the cached
                // StyxWoW.Db instance starts returning real tables once the client is
                // readable.  A no-op once the walk has succeeded.
                EnsureInitialized();

                lock (_sync)
                {
                    DbTable? table;
                    return _tables.TryGetValue(db, out table) ? table : null;
                }
            }
        }

        #endregion

        #region Nested Classes
        public class DbTable
        {
            private readonly IntPtr _tablePtr;
            private readonly DbTableHeader _header;

            internal DbTable(IntPtr tablePtr)
            {
                _tablePtr = tablePtr;
                var wow = ObjectManager.Wow;
                _header = wow != null ? wow.ReadStruct<DbTableHeader>((uint)tablePtr.ToInt32() - 24) : default;
            }
            public bool IsLoaded => _header.IsLoaded != 0;
            public int NumRows => _header.NumRows;
            public int MaxIndex => _header.MaxIndex;
            public int MinIndex => _header.MinIndex;
            public Row? GetRow(uint index)
            {
                var wow = ObjectManager.Wow;
                if (wow == null) return null;
                
                if (index < MinIndex || index > MaxIndex)
                    return null;

                uint offset = (uint)_header.RowArrayPtr.ToInt32();
                offset += (index - (uint)MinIndex) * 4;
                
                uint rowPtr = wow.Read<uint>(offset);
                if (rowPtr == 0)
                    return null;

                return new Row(new IntPtr(unchecked((int)rowPtr)));
            }

            public Row? GetLocalizedRow(int index)
            {
                var wow = ObjectManager.Wow;
                if (wow == null)
                    return null;

                var header = wow.ReadStruct<DbTableHeader>((uint)_tablePtr.ToInt32() - 24);
                if (index < header.MinIndex || index > header.MaxIndex)
                    return null;

                uint rowPtr = wow.Read<uint>((uint)(header.RowArrayPtr.ToInt32() + 4 * (index - header.MinIndex)));
                if (rowPtr == 0)
                    return null;

                IntPtr buffer;
                if (wow.Read<byte>((uint)GlobalOffsets.ClientDb_IsCompressed) == 1)
                {
                    buffer = Unpack(rowPtr, 704);
                }
                else
                {
                    byte[] bytes = wow.ReadBytes(rowPtr, 704);
                    buffer = Marshal.AllocHGlobal(704);
                    Marshal.Copy(bytes, 0, buffer, 704);
                }
                return new Row(buffer, true);
            }

            private static IntPtr Unpack(uint source, int size)
            {
                var wow = ObjectManager.Wow;
                byte[] bytes = new byte[20480];
                bytes[0] = wow.Read<byte>(source);
                int written = 1;
                uint src = source + 1;
                while (written < size)
                {
                    bytes[written++] = wow.Read<byte>(src);
                    if (wow.Read<byte>(src) == wow.Read<byte>(src - 1))
                    {
                        byte repeat = wow.Read<byte>(src + 1);
                        while (repeat != 0)
                        {
                            repeat--;
                            bytes[written++] = wow.Read<byte>(src);
                        }
                        src += 2;
                        if (written < size)
                            bytes[written++] = wow.Read<byte>(src);
                    }
                    src++;
                }
                IntPtr buffer = Marshal.AllocHGlobal(size);
                Marshal.Copy(bytes, 0, buffer, size);
                return buffer;
            }
        }
        public class Row
        {
            private readonly IntPtr _address;
            private readonly bool _ownsMemory;

            internal Row(IntPtr address)
            {
                _address = address;
                _ownsMemory = false;
            }

            internal Row(IntPtr address, bool ownsMemory) : this(address)
            {
                _ownsMemory = ownsMemory;
            }
            public bool IsValid => _address != IntPtr.Zero;
            public T? GetField<T>(uint index)
            {
                try
                {
                    var wow = ObjectManager.Wow;
                    if (wow == null || _ownsMemory)
                        return default;

                    if (typeof(T) == typeof(string))
                    {
                        uint strPtr = wow.Read<uint>((uint)_address.ToInt32() + index * 4);
                        object result = wow.Read<string>(strPtr);
                        return (T)result;
                    }
                    
                    return wow.Read<T>((uint)_address.ToInt32() + index * 4);
                }
                catch
                {
                    return default;
                }
            }
            public void SetField<T>(uint index, T value)
            {
                var wow = ObjectManager.Wow;
                if (wow != null)
                    wow.Write((uint)_address.ToInt32() + index * 4, value);
            }
            public T GetStruct<T>() where T : struct
            {
                try
                {
                    if (_ownsMemory)
                        return Marshal.PtrToStructure<T>(_address);

                    var wow = ObjectManager.Wow;
                    if (wow == null)
                        return default;
                    return wow.ReadStruct<T>((uint)_address.ToInt32());
                }
                catch
                {
                    return default;
                }
            }
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        internal struct DbTableHeader
        {
            private readonly IntPtr _reserved0;
            public int IsLoaded;
            public int NumRows;
            public int MaxIndex;
            public int MinIndex;
            public int RecordSize;
            private readonly IntPtr _reserved1;
            public int FieldCount;
            public IntPtr RowArrayPtr;
        }

        #endregion
    }
}
