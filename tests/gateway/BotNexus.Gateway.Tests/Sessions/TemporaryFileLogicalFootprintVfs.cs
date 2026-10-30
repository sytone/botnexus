using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Test-only, non-default VFS shim. Measures simultaneous logical EOF, not allocated
/// disk blocks, cumulative writes, sorter memory, or main database/journal/WAL bytes.
/// ABI: https://www.sqlite.org/c3ref/vfs.html and /c3ref/io_methods.html.
/// Supports controlled pinned SQLite IO under a trusted-borrow contract, not arbitrary
/// VFS extensions, adversarial misuse, physical allocation measurement, or a security sandbox.
/// Forwarded callbacks use the parent's own VFS/file pointer. Borrowed connections must
/// not be retained beyond their lease, externally closed/disposed/reopened, or have their
/// connection string, VFS, or provider mutated. Do not create or retain raw SQLite statements,
/// backups, native handles, or VFS pointers beyond the lease. Dispose caller commands/readers
/// before the lease, and leases before this observer. Owned leases and the live-statement
/// guard do not detect all raw native resources; callers must honor this contract.
/// </summary>
internal sealed class TemporaryFileLogicalFootprintVfs : IDisposable
{
    private const int Ok = 0;
    private const int IoError = 10;
    private const int NotFound = 12;
    private const int FileOffset = 16; // Alignment for parent sqlite3_file, including 64-bit fields.
    private static readonly List<TemporaryFileLogicalFootprintVfs> Quarantined = [];
    private readonly object _gate = new();
    private readonly List<Delegate> _roots = [];
    private readonly List<nint> _allocations = [];
    private readonly Dictionary<nint, FileState> _files = [];
    private readonly HashSet<int> _scratchFlags = [];
    private readonly Accounting _accounting = new();
    private readonly nint _library;
    private readonly nint _parent;
    private readonly nint _vfs;
    private readonly Vfs _parentLayout;
    private readonly Register _unregister;
    private readonly Action? _beforeScratchSize;
    private bool _uncertainNativeLifetime;
    private bool _disposed;
    private bool _registered;
    private int _activeConnections;
    private int _scratchOpens;
    private int _scratchCloses;

    private string Name { get; } = $"logical-footprint-{Guid.NewGuid():N}";
    internal long CurrentOpenTemporaryLogicalBytes => _accounting.CurrentOpenTemporaryLogicalBytes;
    internal long PeakOpenTemporaryLogicalBytes => _accounting.PeakOpenTemporaryLogicalBytes;
    internal int ScratchOpenCount { get { lock (_gate) return _scratchOpens; } }
    internal int ScratchCloseCount { get { lock (_gate) return _scratchCloses; } }
    internal int OpenFileCount { get { lock (_gate) return _files.Count; } }
    internal int ActiveConnectionCount { get { lock (_gate) return _activeConnections; } }
    internal nint DefaultVfsPointer { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return Export<Find>("sqlite3_vfs_find")(0); } } }
    internal int[] ScratchFlags { get { lock (_gate) return [.. _scratchFlags]; } }
    internal string LifetimeDiagnostics
    {
        get
        {
            lock (_gate)
                return $"open_files={_files.Count}; active_connections={_activeConnections}; scratch_opens={_scratchOpens}; scratch_closes={_scratchCloses}; "
                    + $"uncertain={_uncertainNativeLifetime}; registered={_registered}; disposed={_disposed}; "
                    + $"observation_error={_accounting.Error}; handles=["
                    + string.Join(";", _files.Select(pair => $"wrapper=0x{pair.Key:x},parent=0x{pair.Value.Parent:x},scratch={pair.Value.Scratch}")) + "]";
        }
    }

    internal TemporaryFileLogicalFootprintVfs(Action? beforeScratchSize = null)
    {
        _beforeScratchSize = beforeScratchSize;
        // Initialize the configured provider, never replace it. Resolve the SAME import
        // name/assembly as SQLitePCLRaw's pinned provider, not an OS sqlite3 library.
        using (var initialize = new SqliteConnection("Data Source=:memory:;Pooling=False")) initialize.Open();
        if (SQLitePCL.raw.GetNativeLibraryName() != "e_sqlite3")
            throw new InvalidOperationException("Observer requires the pinned e_sqlite3 provider.");
        var providerAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3");
        _library = NativeLibrary.Load("e_sqlite3", providerAssembly, null);
        try
        {
            _unregister = Export<Register>("sqlite3_vfs_unregister");
            var version = Export<NoArgs>("sqlite3_libversion_number")();
            if (version != 3053003 || version != SQLitePCL.raw.sqlite3_libversion_number())
                throw new InvalidOperationException("Native SQLite version does not match pinned 3.53.3.");
            _parent = Export<Find>("sqlite3_vfs_find")(0);
            if (_parent == 0) throw new InvalidOperationException("No parent SQLite VFS.");
            // Read only the published prefix before accessing version-specific members.
            var parentVersion = Marshal.ReadInt32(_parent);
            if (parentVersion is < 1 or > 3) throw new InvalidOperationException("Unsupported VFS ABI version.");
            var bytes = parentVersion switch { 1 => Offset<Vfs>(nameof(Vfs.CurrentTime64)), 2 => Offset<Vfs>(nameof(Vfs.SetSystemCall)), _ => Marshal.SizeOf<Vfs>() };
            var layout = Allocate(Marshal.SizeOf<Vfs>());
            Copy(_parent, layout, bytes);
            _parentLayout = Marshal.PtrToStructure<Vfs>(layout);
            if (_parentLayout.FileSize < IntPtr.Size || _parentLayout.FileSize > 1024 * 1024)
                throw new InvalidOperationException("Invalid parent sqlite3_file size.");
            var wrapper = _parentLayout;
            wrapper.FileSize = checked(FileOffset + wrapper.FileSize);
            wrapper.Next = 0;
            wrapper.Name = Marshal.StringToCoTaskMemUTF8(Name);
            _allocations.Add(wrapper.Name);
            wrapper.AppData = 0;
            wrapper.Open = Root<Open>((_, name, file, flags, output) => Guard(() => OpenFile(name, file, flags, output)));
            wrapper.Delete = Root<V3>((_, name, sync) => Guard(() => Fn<V3>(_parentLayout.Delete)(_parent, name, sync)));
            wrapper.Access = Root<V4>((_, name, flags, output) => Guard(() => Fn<V4>(_parentLayout.Access)(_parent, name, flags, output)));
            wrapper.FullPath = Root<V4>((_, name, size, output) => Guard(() => Fn<V4>(_parentLayout.FullPath)(_parent, name, size, output)));
            wrapper.DlOpen = Optional<Pointer2>(_parentLayout.DlOpen, (_, name) => GuardPointer(() => Fn<Pointer2>(_parentLayout.DlOpen)(_parent, name)));
            wrapper.DlError = Optional<Void3>(_parentLayout.DlError, (_, size, output) => GuardVoid(() => Fn<Void3>(_parentLayout.DlError)(_parent, size, output)));
            wrapper.DlSym = Optional<Pointer3>(_parentLayout.DlSym, (_, handle, name) => GuardPointer(() => Fn<Pointer3>(_parentLayout.DlSym)(_parent, handle, name)));
            wrapper.DlClose = Optional<Void2>(_parentLayout.DlClose, (_, handle) => GuardVoid(() => Fn<Void2>(_parentLayout.DlClose)(_parent, handle)));
            wrapper.Randomness = Root<VBuffer>((_, size, output) => Guard(() => Fn<VBuffer>(_parentLayout.Randomness)(_parent, size, output), 0));
            wrapper.Sleep = Root<FInt>((_, micros) => Guard(() => Fn<FInt>(_parentLayout.Sleep)(_parent, micros), 0));
            wrapper.CurrentTime = Root<FPointer>((_, output) => Guard(() => Fn<FPointer>(_parentLayout.CurrentTime)(_parent, output)));
            wrapper.LastError = Optional<VBuffer>(_parentLayout.LastError, (_, size, output) => Guard(() => Fn<VBuffer>(_parentLayout.LastError)(_parent, size, output), 0));
            wrapper.CurrentTime64 = Optional<FPointer>(_parentLayout.CurrentTime64, (_, output) => Guard(() => Fn<FPointer>(_parentLayout.CurrentTime64)(_parent, output)));
            wrapper.SetSystemCall = Optional<VCall>(_parentLayout.SetSystemCall, (_, name, call) => Guard(() => Fn<VCall>(_parentLayout.SetSystemCall)(_parent, name, call)));
            wrapper.GetSystemCall = Optional<Pointer2>(_parentLayout.GetSystemCall, (_, name) => GuardPointer(() => Fn<Pointer2>(_parentLayout.GetSystemCall)(_parent, name)));
            wrapper.NextSystemCall = Optional<Pointer2>(_parentLayout.NextSystemCall, (_, name) => GuardPointer(() => Fn<Pointer2>(_parentLayout.NextSystemCall)(_parent, name)));
            _vfs = Allocate(Marshal.SizeOf<Vfs>());
            Marshal.StructureToPtr(wrapper, _vfs, false);
            var rc = Export<RegisterDefault>("sqlite3_vfs_register")(_vfs, 0);
            if (rc != Ok) throw new InvalidOperationException($"VFS registration failed: {rc}.");
            _registered = true;
        }
        catch
        {
            ReleaseAllocations();
            NativeLibrary.Free(_library);
            throw;
        }
    }

    internal static nint CaptureDefaultVfsPointer()
    {
        // Independent of any observer, so tests can compare before/during/after
        // registrations without calling into a disposed observer's native library.
        using var initialize = new SqliteConnection("Data Source=:memory:;Pooling=False");
        initialize.Open();
        if (SQLitePCL.raw.GetNativeLibraryName() != "e_sqlite3")
            throw new InvalidOperationException("Expected pinned e_sqlite3 provider.");
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3");
        var library = NativeLibrary.Load("e_sqlite3", assembly, null);
        try { return Fn<Find>(NativeLibrary.GetExport(library, "sqlite3_vfs_find"))(0); }
        finally { NativeLibrary.Free(library); }
    }

    /// <summary>
    /// Opens an owned lease for controlled tests. Its connection is a trusted borrow:
    /// do not retain it beyond the lease, externally close/dispose/reopen it, or mutate
    /// its connection string, VFS, or provider. Do not create or retain raw SQLite statements,
    /// backups, native handles, or VFS pointers beyond the lease. Dispose commands/readers
    /// before the lease and the lease before this observer; not all raw resources are detected.
    /// </summary>
    internal ConnectionLease OpenConnection(string dataSource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dataSource, Pooling = false, Vfs = Name
            }.ToString());
            // Count BEFORE Open: memory DB/pager retains pVfs even without xOpen.
            _activeConnections++;
            try
            {
                connection.Open();
                VerifySameNativeLibrary(connection);
                return new ConnectionLease(this, connection);
            }
            catch
            {
                // Release the lease only after managed/native disposal succeeds.
                try { connection.Dispose(); _activeConnections--; }
                catch { _uncertainNativeLifetime = true; throw; }
                throw;
            }
        }
    }

    internal sealed class ConnectionLease : IDisposable
    {
        private readonly TemporaryFileLogicalFootprintVfs _owner;
        private SqliteConnection? _connection;
        internal ConnectionLease(TemporaryFileLogicalFootprintVfs owner, SqliteConnection connection)
        {
            _owner = owner;
            _connection = connection;
        }
        /// <summary>
        /// Trusted borrow valid only within this lease. Do not retain it beyond the lease,
        /// externally close/dispose/reopen it, or mutate its connection string, VFS, or provider.
        /// Do not create or retain raw SQLite statements, backups, native handles, or VFS
        /// pointers beyond the lease. Dispose caller commands/readers before disposing the
        /// lease; the live-statement guard does not detect all raw native resources.
        /// </summary>
        internal SqliteConnection Connection => _connection ?? throw new ObjectDisposedException(nameof(ConnectionLease));
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_connection is null) return;
                var handle = _connection.Handle;
                // sqlite3_close_v2 can defer native destruction while a statement
                // survives. Refuse rather than releasing roots for a zombie pager.
                if (handle is not null && _owner.Export<NextStatement>("sqlite3_next_stmt")(handle.DangerousGetHandle(), 0) != 0)
                    throw new InvalidOperationException("Dispose statements/readers before the connection lease.");
                _connection.Dispose();
                _connection = null;
                _owner._activeConnections--;
            }
        }
    }

    internal static bool IsScratch(int flags) =>
        (flags & (0x100 | 0x800 | 0x80000)) == 0 && (flags & (8 | 0x200 | 0x400 | 0x1000 | 0x2000)) != 0;

    internal void EnsureValid() => _accounting.EnsureValid();

    internal void Reset()
    {
        lock (_gate)
        {
            _accounting.Reset();
            _scratchOpens = 0;
            _scratchCloses = 0;
            _scratchFlags.Clear();
        }
    }

    internal void VerifySameNativeLibrary(SqliteConnection connection)
    {
        if (ReadConnectionVfsPointer(connection) != _vfs)
            throw new InvalidOperationException("Connection does not use this exact native VFS registration.");
        EnsureValid();
    }

    internal nint ReadConnectionVfsPointer(SqliteConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handle = connection.Handle ?? throw new InvalidOperationException("Connection is not open.");
        var output = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            // SQLITE_FCNTL_VFS_POINTER comes from the connection's actual native handle.
            // It must return our exact registered pointer, proving library AND selection.
            var control = Export<DbControl>("sqlite3_file_control");
            Marshal.WriteIntPtr(output, 0);
            if (control(handle.DangerousGetHandle(), "main", 27, output) != Ok)
                throw new InvalidOperationException("Cannot read connection VFS pointer.");
            return Marshal.ReadIntPtr(output);
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    private int OpenFile(nint name, nint file, int flags, nint output)
    {
        lock (_gate)
        {
            Marshal.WriteIntPtr(file, 0);
            var parentFile = file + FileOffset;
            var abortAttempted = false;
            try
            {
                var rc = Fn<Open>(_parentLayout.Open)(_parent, name, parentFile, flags, output);
                var methods = Marshal.ReadIntPtr(parentFile);
                if (rc != Ok && IsScratch(flags)) _accounting.Invalidate($"Scratch xOpen failed: {rc}.");
                if (methods == 0)
                {
                    if (rc == Ok) throw new InvalidOperationException("Successful xOpen returned no IO methods.");
                    return rc; // Parent guarantees no xClose is required.
                }
                var version = Marshal.ReadInt32(methods);
                if (version is < 1 or > 3) throw new InvalidOperationException("Unsupported IO methods ABI.");
                var storage = Allocate(Marshal.SizeOf<Io>());
                Copy(methods, storage, version switch { 1 => Offset<Io>(nameof(Io.ShmMap)), 2 => Offset<Io>(nameof(Io.Fetch)), _ => Marshal.SizeOf<Io>() });
                var state = new FileState(parentFile, Marshal.PtrToStructure<Io>(storage), rc == Ok && IsScratch(flags));
                var io = Wrap(file, state);
                var wrappedMethods = Allocate(Marshal.SizeOf<Io>());
                Marshal.StructureToPtr(io, wrappedMethods, false);
                _files.Add(file, state);
                Marshal.WriteIntPtr(file, wrappedMethods);
                // Retain pMethods until WE close any failed/partial open. In pinned
                // sqlite3OsOpenMalloc, error frees this wrapper without calling xClose.
                // Returning IOERR with pMethods populated would strand the parent and
                // leave _files pointing at SQLite-freed memory.
                if (state.Scratch)
                {
                    _accounting.Open(file);
                    _scratchOpens++;
                    _scratchFlags.Add(flags);
                    if (!Refresh(file, state))
                    {
                        abortAttempted = true;
                        AbortOpen(file, parentFile);
                        return IoError;
                    }
                }
                if (rc != Ok)
                {
                    abortAttempted = true;
                    AbortOpen(file, parentFile);
                }
                return rc;
            }
            catch (Exception ex)
            {
                // Record the ORIGINAL observation failure before attempting teardown.
                // Do not call Refresh again during this unwind: the observer is broken.
                _accounting.Invalidate(ex.GetType().Name + ": " + ex.Message);
                if (!abortAttempted) AbortOpen(file, parentFile);
                else
                {
                    // Even cleanup's finally may fail unexpectedly. Once native close
                    // was attempted, never infer that retrying that pointer is safe.
                    _uncertainNativeLifetime = true;
                    _files.Remove(file);
                    Marshal.WriteIntPtr(file, 0);
                }
                return IoError;
            }
        }
    }

    private void AbortOpen(nint file, nint parentFile)
    {
        // xOpen has not transferred ownership to SQLite. Close synchronously while
        // its caller-owned wrapper storage is still valid, then clear pMethods on
        // every return path. Never revisit this file from Dispose after SQLite frees it.
        try
        {
            var methods = Marshal.ReadIntPtr(parentFile);
            if (methods != 0)
            {
                var alreadyUncertain = _uncertainNativeLifetime;
                _uncertainNativeLifetime = true;
                var closePointer = Marshal.ReadIntPtr(methods, Offset<Io>(nameof(Io.Close)));
                var rc = Fn<F0>(closePointer)(parentFile);
                if (rc != Ok)
                {
                    _accounting.Invalidate($"Parent xClose during failed xOpen returned {rc}; native lifetime uncertain.");
                    return; // Retain roots, never retry a possibly completed close.
                }
                _uncertainNativeLifetime = alreadyUncertain;
            }
            if (_files.Remove(file, out var state) && state.Scratch)
            {
                _accounting.Close(file);
                _scratchCloses++;
            }
        }
        catch (Exception ex)
        {
            _uncertainNativeLifetime = true;
            _accounting.Invalidate("Failed xOpen unwind: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // Never retain/retry a pointer into caller storage which is about to be
            // freed. Uncertainty remains separately latched and pins all roots.
            if (_files.Remove(file, out var abandoned) && abandoned.Scratch)
                _accounting.Abandon(file);
            Marshal.WriteIntPtr(file, 0);
        }
    }

    private Io Wrap(nint file, FileState state)
    {
        var p = state.Parent;
        var original = state.Methods;
        var io = original;
        io.Close = Root<F0>(_ => Guard(() => CloseFile(file, state)));
        io.Read = Root<ReadWrite>((_, buffer, amount, offset) => Guard(() => ReadFile(state, buffer, amount, offset)));
        io.Write = Root<ReadWrite>((_, buffer, amount, offset) => Guard(() => Changed(file, state, () => Fn<ReadWrite>(original.Write)(p, buffer, amount, offset))));
        io.Truncate = Root<FLong>((_, size) => Guard(() => Changed(file, state, () => Fn<FLong>(original.Truncate)(p, size))));
        io.Sync = Root<FInt>((_, flags) => Guard(() => Changed(file, state, () => Fn<FInt>(original.Sync)(p, flags))));
        io.FileSize = Root<FPointer>((_, size) => Guard(() =>
        {
            var rc = Fn<FPointer>(original.FileSize)(p, size);
            if (state.Scratch && rc != Ok) _accounting.Invalidate($"Scratch xFileSize failed: {rc}.");
            return rc;
        }));
        io.Lock = Root<FInt>((_, level) => Guard(() => Fn<FInt>(original.Lock)(p, level)));
        io.Unlock = Root<FInt>((_, level) => Guard(() => Fn<FInt>(original.Unlock)(p, level)));
        io.ReservedLock = Root<FPointer>((_, result) => Guard(() => Fn<FPointer>(original.ReservedLock)(p, result)));
        io.FileControl = Root<Control>((_, operation, argument) => Guard(() => FileControl(file, state, operation, argument)));
        io.SectorSize = Root<F0>(_ => Guard(() => Fn<F0>(original.SectorSize)(p), 4096));
        io.Characteristics = Root<F0>(_ => Guard(() => Fn<F0>(original.Characteristics)(p), 0));
        io.ShmMap = Optional<ShmMap>(original.ShmMap, (_, page, size, extend, result) => Guard(() => Fn<ShmMap>(original.ShmMap)(p, page, size, extend, result)));
        io.ShmLock = Optional<ShmLock>(original.ShmLock, (_, offset, count, flags) => Guard(() => Fn<ShmLock>(original.ShmLock)(p, offset, count, flags)));
        io.ShmBarrier = Optional<Void1>(original.ShmBarrier, _ => GuardVoid(() => Fn<Void1>(original.ShmBarrier)(p)));
        io.ShmUnmap = Optional<FInt>(original.ShmUnmap, (_, delete) => Guard(() => Fn<FInt>(original.ShmUnmap)(p, delete)));
        io.Fetch = Optional<Fetch>(original.Fetch, (_, offset, amount, result) => Guard(() => Fn<Fetch>(original.Fetch)(p, offset, amount, result)));
        io.Unfetch = Optional<Unfetch>(original.Unfetch, (_, offset, pointer) => Guard(() => Fn<Unfetch>(original.Unfetch)(p, offset, pointer)));
        return io;
    }

    private int FileControl(nint file, FileState state, int operation, nint argument)
    {
        // Pointer-returning controls must not reveal the underlying file/VFS and bypass
        // the shim. JOURNAL_POINTER is handled by SQLite core, not the parent file.
        if (operation == 7) { Marshal.WriteIntPtr(argument, file); return Ok; }
        if (operation == 27) { Marshal.WriteIntPtr(argument, _vfs); return Ok; }
        if (operation == 28) return NotFound;
        if (state.Scratch && !IsSupportedScratchControl(operation))
        {
            // GET_HANDLE, SET_HANDLE, FILESTAT, NULL_IO and arbitrary extension controls can
            // mutate EOF outside this shim or expose OS handles. Never forward them.
            _accounting.Invalidate($"unsupported scratch file-control {operation}");
            return IoError;
        }
        // A successful BEGIN_ATOMIC_WRITE restricts subsequent VFS calls to writes
        // and SIZE_HINT until commit/rollback. Do not query xFileSize in that interval.
        // The observer cannot establish exact EOF residency inside such a batch, so
        // invalidate (fail closed) while continuing to forward the parent's protocol.
        lock (_gate)
        {
            var rc = Fn<Control>(state.Methods.FileControl)(state.Parent, operation, argument);
            if (!state.Scratch) return rc;
            if (operation == 31 && rc == Ok)
            {
                state.AtomicWrite = true;
                _accounting.Invalidate("Scratch batch atomic write cannot be measured exactly.");
                return rc;
            }
            if (operation is 32 or 33) state.AtomicWrite = false;
            if (rc != Ok && rc != NotFound)
            {
                _accounting.Invalidate($"Scratch file-control {operation} failed: {rc}.");
                return rc;
            }
            // SIZE_HINT, CHUNK_SIZE, OVERWRITE, SIZE_LIMIT, handle replacement and
            // extension controls may affect EOF: refresh after every normal control.
            return state.AtomicWrite || Refresh(file, state) ? rc : IoError;
        }
    }

    private static bool IsSupportedScratchControl(int operation) => operation is
        1 or 4 or 5 or 6 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 18 or 19 or 20 or 21 or 22
        or 24 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 42 or 44;

    internal void ProbeScratchPointers()
    {
        lock (_gate)
        {
            var pair = _files.Single(pair => pair.Value.Scratch);
            var argument = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(argument, 0);
                if (FileControl(pair.Key, pair.Value, 7, argument) != Ok || Marshal.ReadIntPtr(argument) != pair.Key)
                    throw new InvalidOperationException("Scratch file pointer bypasses wrapper.");
                if (FileControl(pair.Key, pair.Value, 27, argument) != Ok || Marshal.ReadIntPtr(argument) != _vfs)
                    throw new InvalidOperationException("Scratch VFS pointer bypasses wrapper.");
            }
            finally { Marshal.FreeHGlobal(argument); }
        }
    }

    internal int ProbeScratchControl(int operation)
    {
        lock (_gate)
        {
            var pair = _files.Single(pair => pair.Value.Scratch);
            var argument = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                var sentinel = (nint)0x13579;
                Marshal.WriteIntPtr(argument, sentinel);
                var rc = FileControl(pair.Key, pair.Value, operation, argument);
                if (Marshal.ReadIntPtr(argument) != sentinel)
                    throw new InvalidOperationException("Rejected scratch control changed caller output.");
                return rc;
            }
            finally { Marshal.FreeHGlobal(argument); }
        }
    }

    private int ReadFile(FileState state, nint buffer, int amount, long offset)
    {
        var rc = Fn<ReadWrite>(state.Methods.Read)(state.Parent, buffer, amount, offset);
        // SQLITE_IOERR_SHORT_READ is normal zero-fill at EOF; all other errors
        // invalidate evidence even if SQLite recovers and completes the statement.
        if (state.Scratch && rc != Ok && rc != (IoError | (2 << 8)))
            _accounting.Invalidate($"Scratch read failed: {rc}.");
        return rc;
    }

    private int Changed(nint file, FileState state, Func<int> action)
    {
        lock (_gate)
        {
            var rc = action();
            if (!state.Scratch) return rc;
            if (rc != Ok)
            {
                _accounting.Invalidate($"Scratch IO failed: {rc}.");
                return rc;
            }
            return state.AtomicWrite || Refresh(file, state) ? rc : IoError;
        }
    }

    private bool Refresh(nint file, FileState state)
    {
        _beforeScratchSize?.Invoke();
        var size = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            var rc = Fn<FPointer>(state.Methods.FileSize)(state.Parent, size);
            if (rc != Ok) { _accounting.Invalidate($"Scratch xFileSize failed: {rc}."); return false; }
            _accounting.ObserveSize(file, Marshal.ReadInt64(size));
            return true;
        }
        finally { Marshal.FreeHGlobal(size); }
    }

    private int CloseFile(nint file, FileState state)
    {
        lock (_gate)
        {
            // Observation failure MUST NOT prevent parent close and strand callbacks.
            // The guard records invalid evidence but cleanup proceeds independently.
            if (state.Scratch && !state.AtomicWrite) Guard(() => Refresh(file, state) ? Ok : IoError);
            var rc = Fn<F0>(state.Methods.Close)(state.Parent);
            if (rc != Ok) { _accounting.Invalidate($"xClose failed: {rc}; lifetime uncertain."); return rc; }
            if (state.Scratch) { _accounting.Close(file); _scratchCloses++; }
            _files.Remove(file);
            Marshal.WriteIntPtr(file, 0);
            return rc;
        }
    }

    private int Guard(Func<int> action, int error = IoError)
    {
        try { return action(); }
        catch (Exception ex) { _accounting.Invalidate(ex.GetType().Name + ": " + ex.Message); return error; }
    }

    private nint GuardPointer(Func<nint> action)
    {
        try { return action(); }
        catch (Exception ex) { _accounting.Invalidate(ex.GetType().Name + ": " + ex.Message); return 0; }
    }

    private void GuardVoid(Action action)
    {
        try { action(); }
        catch (Exception ex) { _accounting.Invalidate(ex.GetType().Name + ": " + ex.Message); }
    }

    private T Export<T>(string name) where T : Delegate => Fn<T>(NativeLibrary.GetExport(_library, name));
    private static T Fn<T>(nint pointer) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(pointer);
    private nint Root<T>(T callback) where T : Delegate { _roots.Add(callback); return Marshal.GetFunctionPointerForDelegate(callback); }
    private nint Optional<T>(nint original, T callback) where T : Delegate => original == 0 ? 0 : Root(callback);
    private static int Offset<T>(string field) => checked((int)Marshal.OffsetOf<T>(field));
    private nint Allocate(int size)
    {
        var result = Marshal.AllocCoTaskMem(size);
        _allocations.Add(result);
        Marshal.Copy(new byte[size], 0, result, size);
        return result;
    }
    private static void Copy(nint source, nint target, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(source, bytes, 0, size);
        Marshal.Copy(bytes, 0, target, size);
    }
    private void ReleaseAllocations() { foreach (var pointer in _allocations) Marshal.FreeCoTaskMem(pointer); _allocations.Clear(); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_activeConnections != 0)
                throw new InvalidOperationException("VFS has live connection leases; " + LifetimeDiagnostics);
            // Retain a strong global root on uncertain lifetime. A deliberate leak on
            // failed teardown is safer than executing a freed native function pointer.
            if (_uncertainNativeLifetime || _files.Count != 0 || (_registered && _unregister(_vfs) != Ok))
            {
                lock (Quarantined) Quarantined.Add(this);
                throw new InvalidOperationException("VFS teardown unsafe; callbacks and allocations retained. " + LifetimeDiagnostics);
            }
            _registered = false;
            var name = Marshal.PtrToStructure<Vfs>(_vfs).Name;
            if (Export<Find>("sqlite3_vfs_find")(name) != 0)
            {
                lock (Quarantined) Quarantined.Add(this);
                throw new InvalidOperationException("Unregistered VFS is still discoverable; roots retained.");
            }
            _disposed = true;
            ReleaseAllocations();
            NativeLibrary.Free(_library);
            _roots.Clear();
            EnsureValid();
        }
    }

    private sealed record FileState(nint Parent, Io Methods, bool Scratch)
    {
        internal bool AtomicWrite { get; set; }
    }

    /// <summary>Logical-extent accounting, independently testable without native IO.</summary>
    internal sealed class Accounting
    {
        private readonly object _gate = new();
        private readonly Dictionary<nint, long> _sizes = [];
        private string? _error;
        private long _current;
        private long _peak;
        internal long CurrentOpenTemporaryLogicalBytes { get { lock (_gate) { EnsureValid(); return _current; } } }
        internal long PeakOpenTemporaryLogicalBytes { get { lock (_gate) { EnsureValid(); return _peak; } } }
        internal string? Error { get { lock (_gate) return _error; } }
        internal void Invalidate(string error) { lock (_gate) _error ??= error; }
        internal void EnsureValid() { lock (_gate) { if (_error is not null) throw new InvalidOperationException(_error); } }
        internal void Open(nint file)
        {
            lock (_gate)
            {
                if (!_sizes.TryAdd(file, 0)) Fail("Duplicate scratch open.");
            }
        }
        internal void ObserveSize(nint file, long size)
        {
            lock (_gate)
            {
                if (size < 0 || !_sizes.TryGetValue(file, out var previous)) { Fail("Invalid scratch size/handle."); return; }
                try { _current = checked(_current - previous + size); }
                catch (OverflowException) { Fail("Scratch size overflow."); return; }
                _sizes[file] = size;
                _peak = Math.Max(_peak, _current);
            }
        }
        internal void Close(nint file)
        {
            lock (_gate)
            {
                if (!_sizes.Remove(file, out var size)) { Fail("Unknown scratch close."); return; }
                _current -= size;
            }
        }
        internal void Abandon(nint file)
        {
            lock (_gate)
            {
                Invalidate("Scratch native lifetime uncertain; accounting identity abandoned, not a confirmed close.");
                // Retain invalidity and global quarantine, never a caller-freed pointer.
                if (_sizes.Remove(file, out var size)) _current -= size;
            }
        }
        internal void Reset()
        {
            lock (_gate)
            {
                EnsureValid();
                if (_sizes.Count != 0) throw new InvalidOperationException("Cannot reset with live scratch handles.");
                _peak = 0;
            }
        }
        private void Fail(string error) { Invalidate(error); throw new InvalidOperationException(error); }
    }

    // Sequential native ABI, pointer-sized fields and Cdecl on Windows and Unix.
    [StructLayout(LayoutKind.Sequential)]
    private struct Vfs
    {
        internal int Version, FileSize, MaxPath;
        internal nint Next, Name, AppData, Open, Delete, Access, FullPath, DlOpen, DlError, DlSym, DlClose,
            Randomness, Sleep, CurrentTime, LastError, CurrentTime64, SetSystemCall, GetSystemCall, NextSystemCall;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Io
    {
        internal int Version;
        internal nint Close, Read, Write, Truncate, Sync, FileSize, Lock, Unlock, ReservedLock, FileControl,
            SectorSize, Characteristics, ShmMap, ShmLock, ShmBarrier, ShmUnmap, Fetch, Unfetch;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NextStatement(nint database, nint statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Find(nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Register(nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RegisterDefault(nint vfs, int makeDefault);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DbControl(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int operation, nint argument);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(nint vfs, nint name, nint file, int flags, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int V3(nint vfs, nint name, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int V4(nint vfs, nint name, int flags, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VBuffer(nint vfs, int size, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VCall(nint vfs, nint name, nint call);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Pointer2(nint vfs, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Pointer3(nint vfs, nint handle, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Void1(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Void2(nint vfs, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Void3(nint vfs, int size, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int F0(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FInt(nint file, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FLong(nint file, long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FPointer(nint file, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadWrite(nint file, nint buffer, int amount, long offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Control(nint file, int operation, nint argument);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShmMap(nint file, int page, int size, int extend, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShmLock(nint file, int offset, int count, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fetch(nint file, long offset, int amount, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Unfetch(nint file, long offset, nint pointer);
}
