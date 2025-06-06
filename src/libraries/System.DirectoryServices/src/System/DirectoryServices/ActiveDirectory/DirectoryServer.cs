// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.DirectoryServices.ActiveDirectory
{
    public abstract class DirectoryServer : IDisposable
    {
        private bool _disposed;
        internal DirectoryContext context = null!;
        internal string replicaName = null!;
        internal DirectoryEntryManager directoryEntryMgr = null!;

        // internal variables for the public properties
        internal bool siteInfoModified;
        internal string? cachedSiteName;
        internal string? cachedSiteObjectName;
        internal string? cachedServerObjectName;
        internal string? cachedNtdsaObjectName;
        internal Guid cachedNtdsaObjectGuid = Guid.Empty;
        internal ReadOnlyStringCollection? cachedPartitions;

        internal const int DS_REPSYNC_ASYNCHRONOUS_OPERATION = 0x00000001;
        internal const int DS_REPSYNC_ALL_SOURCES = 0x00000010;
        internal const int DS_REPSYNCALL_ID_SERVERS_BY_DN = 0x00000004;
        internal const int DS_REPL_NOTSUPPORTED = 50;
        private ReplicationConnectionCollection? _inbound;
        private ReplicationConnectionCollection? _outbound;

        #region constructors
        protected DirectoryServer()
        {
        }
        #endregion constructors

        #region IDisposable
        ~DirectoryServer() => Dispose(false);

        public void Dispose()
        {
            Dispose(true);

            // Take yourself off of the Finalization queue
            // to prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        // private Dispose method
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // check if this is an explicit Dispose
                // only then clean up the directory entries
                if (disposing)
                {
                    // dispose all directory entries
                    foreach (DirectoryEntry entry in directoryEntryMgr.GetCachedDirectoryEntries())
                    {
                        entry.Dispose();
                    }
                }
                _disposed = true;
            }
        }
        #endregion IDisposable

        #region public methods
        public override string ToString() => Name;

        public void MoveToAnotherSite(string siteName)
        {
            CheckIfDisposed();

            // validate siteName
            ArgumentNullException.ThrowIfNull(siteName);

            if (siteName.Length == 0)
            {
                throw new ArgumentException(SR.EmptyStringParameter, nameof(siteName));
            }

            // the dc is really being moved to a different site
            if (Utils.Compare(SiteName, siteName) != 0)
            {
                DirectoryEntry? newParentEntry = null;
                try
                {
                    // Bind to the target site's server container
                    // Get the distinguished name for the site
                    string parentDN = "CN=Servers,CN=" + siteName + "," + directoryEntryMgr.ExpandWellKnownDN(WellKnownDN.SitesContainer);
                    newParentEntry = DirectoryEntryManager.GetDirectoryEntry(context, parentDN);

                    string serverName = (this is DomainController) ? ((DomainController)this).ServerObjectName : ((AdamInstance)this).ServerObjectName;

                    DirectoryEntry serverEntry = directoryEntryMgr.GetCachedDirectoryEntry(serverName);

                    // force binding (needed otherwise S.DS throw an exception while releasing the COM interface pointer)
                    _ = (string?)PropertyManager.GetPropertyValue(context, serverEntry, PropertyManager.DistinguishedName);

                    // move the object to the servers container of the target site
                    serverEntry.MoveTo(newParentEntry);
                }
                catch (COMException e)
                {
                    throw ExceptionHelper.GetExceptionFromCOMException(context, e);
                }
                finally
                {
                    newParentEntry?.Dispose();
                }

                // remove stale cached directory entries
                // invalidate the cached properties that get affected by this
                siteInfoModified = true;
                cachedSiteName = null;

                if (cachedSiteObjectName != null)
                {
                    directoryEntryMgr.RemoveIfExists(cachedSiteObjectName);
                    cachedSiteObjectName = null;
                }

                if (cachedServerObjectName != null)
                {
                    directoryEntryMgr.RemoveIfExists(cachedServerObjectName);
                    cachedServerObjectName = null;
                }

                if (cachedNtdsaObjectName != null)
                {
                    directoryEntryMgr.RemoveIfExists(cachedNtdsaObjectName);
                    cachedNtdsaObjectName = null;
                }
            }
        }

        public DirectoryEntry GetDirectoryEntry()
        {
            CheckIfDisposed();
            string serverName = (this is DomainController) ? ((DomainController)this).ServerObjectName : ((AdamInstance)this).ServerObjectName;
            return DirectoryEntryManager.GetDirectoryEntry(context, serverName);
        }

        #endregion public methods

        #region public abstract methods

        public abstract void CheckReplicationConsistency();

        public abstract ReplicationCursorCollection GetReplicationCursors(string partition);

        public abstract ReplicationOperationInformation GetReplicationOperationInformation();

        public abstract ReplicationNeighborCollection GetReplicationNeighbors(string partition);

        public abstract ReplicationNeighborCollection GetAllReplicationNeighbors();

        public abstract ReplicationFailureCollection GetReplicationConnectionFailures();

        public abstract ActiveDirectoryReplicationMetadata GetReplicationMetadata(string objectPath);

        public abstract void SyncReplicaFromServer(string partition, string sourceServer);

        public abstract void TriggerSyncReplicaFromNeighbors(string partition);

        public abstract void SyncReplicaFromAllServers(string partition, SyncFromAllServersOptions options);

        #endregion public abstract methods

        #region public properties

        public string Name
        {
            get
            {
                CheckIfDisposed();
                return replicaName;
            }
        }

        public ReadOnlyStringCollection Partitions
        {
            get
            {
                CheckIfDisposed();
                return cachedPartitions ??= new ReadOnlyStringCollection(GetPartitions());
            }
        }

        #endregion public properties

        #region public abstract properties

        public abstract string? IPAddress { get; }

        public abstract string SiteName { get; }

        public abstract SyncUpdateCallback? SyncFromAllServersCallback { get; set; }

        public abstract ReplicationConnectionCollection InboundConnections { get; }

        public abstract ReplicationConnectionCollection OutboundConnections { get; }

        #endregion public abstract properties

        #region private methods

        internal ArrayList GetPartitions()
        {
            ArrayList partitionList = new ArrayList();
            DirectoryEntry? rootDSE = null;
            DirectoryEntry? serverNtdsaEntry = null;

            try
            {
                // get the writable partitions
                rootDSE = DirectoryEntryManager.GetDirectoryEntry(context, WellKnownDN.RootDSE);
                // don't need range retrieval for root dse attributes
                foreach (string partition in rootDSE.Properties[PropertyManager.NamingContexts])
                {
                    partitionList.Add(partition);
                }

                // also the read only partitions
                string ntdsaName = (this is DomainController) ? ((DomainController)this).NtdsaObjectName : ((AdamInstance)this).NtdsaObjectName;
                serverNtdsaEntry = DirectoryEntryManager.GetDirectoryEntry(context, ntdsaName);

                // use range retrieval
                ArrayList propertyNames = new ArrayList();
                propertyNames.Add(PropertyManager.HasPartialReplicaNCs);

                Hashtable? values = null;
                try
                {
                    values = Utils.GetValuesWithRangeRetrieval(serverNtdsaEntry, null, propertyNames, SearchScope.Base);
                }
                catch (COMException e)
                {
                    throw ExceptionHelper.GetExceptionFromCOMException(context, e);
                }

                ArrayList readOnlyPartitions = (ArrayList)values[PropertyManager.HasPartialReplicaNCs.ToLowerInvariant()]!;

                Debug.Assert(readOnlyPartitions != null);
                foreach (string readOnlyPartition in readOnlyPartitions)
                {
                    partitionList.Add(readOnlyPartition);
                }
            }
            catch (COMException e)
            {
                throw ExceptionHelper.GetExceptionFromCOMException(context, e);
            }
            finally
            {
                rootDSE?.Dispose();
                serverNtdsaEntry?.Dispose();
            }
            return partitionList;
        }

        internal void CheckIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        internal DirectoryContext Context => context;

        private static readonly string[] s_name = new string[] { "name" };
        private static readonly string[] s_cn = new string[] { "cn" };
        private static readonly string[] s_objectClassCn = new string[] { "objectClass", "cn" };

        internal unsafe void CheckConsistencyHelper(IntPtr dsHandle, SafeLibraryHandle libHandle)
        {
            // call DsReplicaConsistencyCheck
            var replicaConsistencyCheck = (delegate* unmanaged<IntPtr, int, int, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaConsistencyCheck");
            if (replicaConsistencyCheck == null)
            {
                throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
            }

            int result = replicaConsistencyCheck(dsHandle, 0, 0);

            if (result != 0)
                throw ExceptionHelper.GetExceptionFromErrorCode(result, Name);
        }

        internal unsafe IntPtr GetReplicationInfoHelper(IntPtr dsHandle, int type, int secondaryType, string? partition, ref bool advanced, int context, SafeLibraryHandle libHandle)
        {
            IntPtr info = (IntPtr)0;
            int result = 0;
            bool needToTryAgain = true;

            // first try to use the DsReplicaGetInfo2W API which does not exist on win2k machine
            // call DsReplicaGetInfo2W
            var dsReplicaGetInfo2W = (delegate* unmanaged<IntPtr, int, char*, IntPtr, char*, char*, int, int, IntPtr*, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaGetInfo2W");
            if (dsReplicaGetInfo2W == null)
            {
                // a win2k machine which does not have it.
                var dsReplicaGetInfoW = (delegate* unmanaged<IntPtr, int, char*, IntPtr, IntPtr*, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaGetInfoW");
                if (dsReplicaGetInfoW == null)
                {
                    throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
                }
                fixed (char* partitionPtr = partition)
                {
                    result = dsReplicaGetInfoW(dsHandle, secondaryType, partitionPtr, (IntPtr)0, &info);
                }
                advanced = false;
                needToTryAgain = false;
            }
            else
            {
                fixed (char* partitionPtr = partition)
                {
                    result = dsReplicaGetInfo2W(dsHandle, type, partitionPtr, (IntPtr)0, null, null, 0, context, &info);
                }
            }

            // check the result
            if (needToTryAgain && result == DS_REPL_NOTSUPPORTED)
            {
                // this is the case that client is xp/win2k3, dc is win2k
                var dsReplicaGetInfoW = (delegate* unmanaged<IntPtr, int, char*, IntPtr, IntPtr*, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaGetInfoW");
                if (dsReplicaGetInfoW == null)
                {
                    throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
                }

                fixed (char* partitionPtr = partition)
                {
                    result = dsReplicaGetInfoW(dsHandle, secondaryType, partitionPtr, (IntPtr)0, &info);
                }
                advanced = false;
            }

            if (result != 0)
            {
                if (partition != null)
                {
                    // this is the case of meta data
                    if (type == (int)DS_REPL_INFO_TYPE.DS_REPL_INFO_METADATA_2_FOR_OBJ)
                    {
                        if (result == Interop.Errors.ERROR_DS_DRA_BAD_DN || result == Interop.Errors.ERROR_DS_NAME_UNPARSEABLE)
                            throw new ArgumentException(ExceptionHelper.GetErrorMessage(result, false), "objectPath");

                        DirectoryEntry verifyEntry = DirectoryEntryManager.GetDirectoryEntry(this.context, partition);
                        try
                        {
                            verifyEntry.RefreshCache(s_name);
                        }
                        catch (COMException e)
                        {
                            if (e.ErrorCode == unchecked((int)0x80072020) |          // dir_error on server side
                                   e.ErrorCode == unchecked((int)0x80072030))           // object not exists
                                throw new ArgumentException(SR.DSNoObject, "objectPath");
                            else if (e.ErrorCode == unchecked((int)0x80005000) |          // bad path name
                                   e.ErrorCode == unchecked((int)0x80072032)) // ERROR_DS_INVALID_DN_SYNTAX
                                throw new ArgumentException(SR.DSInvalidPath, "objectPath");
                        }
                    }
                    else
                    {
                        if (!Partitions.Contains(partition))
                            throw new ArgumentException(SR.ServerNotAReplica, nameof(partition));
                    }
                }

                throw ExceptionHelper.GetExceptionFromErrorCode(result, Name);
            }

            return info;
        }

        internal ReplicationCursorCollection ConstructReplicationCursors(IntPtr dsHandle, bool advanced, IntPtr info, string partition, DirectoryServer server, SafeLibraryHandle libHandle)
        {
            int context = 0;
            int count = 0;
            ReplicationCursorCollection collection = new ReplicationCursorCollection(server);

            // construct the collection
            if (advanced)
            {
                // using paging to get all the results
                while (true)
                {
                    try
                    {
                        if (info != (IntPtr)0)
                        {
                            DS_REPL_CURSORS_3 cursors = new DS_REPL_CURSORS_3();
                            Marshal.PtrToStructure(info, cursors);
                            count = cursors.cNumCursors;
                            if (count > 0)
                                collection.AddHelper(partition, cursors, advanced, info);

                            context = cursors.dwEnumerationContext;

                            // we already get all the results or there is no result
                            if (context == -1 || count == 0)
                                break;
                        }
                        else
                        {
                            // should not happen, treat as no result is returned.
                            break;
                        }
                    }
                    finally
                    {
                        FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_CURSORS_3_FOR_NC, info, libHandle);
                    }

                    // get the next batch of results
                    info = GetReplicationInfoHelper(dsHandle, (int)DS_REPL_INFO_TYPE.DS_REPL_INFO_CURSORS_3_FOR_NC, (int)DS_REPL_INFO_TYPE.DS_REPL_INFO_CURSORS_FOR_NC, partition, ref advanced, context, libHandle);
                }
            }
            else
            {
                try
                {
                    if (info != (IntPtr)0)
                    {
                        // structure of DS_REPL_CURSORS_3 and DS_REPL_CURSORS actually are the same
                        DS_REPL_CURSORS cursors = new DS_REPL_CURSORS();
                        Marshal.PtrToStructure(info, cursors);

                        collection.AddHelper(partition, cursors, advanced, info);
                    }
                }
                finally
                {
                    FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_CURSORS_FOR_NC, info, libHandle);
                }
            }

            return collection;
        }

        internal ReplicationOperationInformation ConstructPendingOperations(IntPtr info, DirectoryServer server, SafeLibraryHandle libHandle)
        {
            ReplicationOperationInformation replicationInfo = new ReplicationOperationInformation();
            ReplicationOperationCollection collection = new ReplicationOperationCollection(server);
            replicationInfo.collection = collection;
            int count = 0;

            try
            {
                if (info != (IntPtr)0)
                {
                    DS_REPL_PENDING_OPS operations = new DS_REPL_PENDING_OPS();
                    Marshal.PtrToStructure(info, operations);
                    count = operations.cNumPendingOps;
                    if (count > 0)
                    {
                        collection.AddHelper(operations, info);
                        replicationInfo.startTime = DateTime.FromFileTime(operations.ftimeCurrentOpStarted);
                        replicationInfo.currentOp = collection.GetFirstOperation();
                    }
                }
            }
            finally
            {
                FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_PENDING_OPS, info, libHandle);
            }
            return replicationInfo;
        }

        internal ReplicationNeighborCollection ConstructNeighbors(IntPtr info, DirectoryServer server, SafeLibraryHandle libHandle)
        {
            ReplicationNeighborCollection collection = new ReplicationNeighborCollection(server);
            int count = 0;

            try
            {
                if (info != (IntPtr)0)
                {
                    DS_REPL_NEIGHBORS neighbors = new DS_REPL_NEIGHBORS();
                    Marshal.PtrToStructure(info, neighbors);
                    Debug.Assert(neighbors != null);
                    count = neighbors.cNumNeighbors;
                    if (count > 0)
                        collection.AddHelper(neighbors, info);
                }
            }
            finally
            {
                FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_NEIGHBORS, info, libHandle);
            }
            return collection;
        }

        internal ReplicationFailureCollection ConstructFailures(IntPtr info, DirectoryServer server, SafeLibraryHandle libHandle)
        {
            ReplicationFailureCollection collection = new ReplicationFailureCollection(server);
            int count = 0;

            try
            {
                if (info != (IntPtr)0)
                {
                    DS_REPL_KCC_DSA_FAILURES failures = new DS_REPL_KCC_DSA_FAILURES();
                    Marshal.PtrToStructure(info, failures);
                    count = failures.cNumEntries;
                    if (count > 0)
                        collection.AddHelper(failures, info);
                }
            }
            finally
            {
                FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_KCC_DSA_CONNECT_FAILURES, info, libHandle);
            }
            return collection;
        }

        internal ActiveDirectoryReplicationMetadata ConstructMetaData(bool advanced, IntPtr info, DirectoryServer server, SafeLibraryHandle libHandle)
        {
            ActiveDirectoryReplicationMetadata collection = new ActiveDirectoryReplicationMetadata(server);
            int count = 0;

            if (advanced)
            {
                try
                {
                    if (info != (IntPtr)0)
                    {
                        DS_REPL_OBJ_META_DATA_2 objMetaData = new DS_REPL_OBJ_META_DATA_2();
                        Marshal.PtrToStructure(info, objMetaData);

                        count = objMetaData.cNumEntries;
                        if (count > 0)
                        {
                            collection.AddHelper(count, info, true);
                        }
                    }
                }
                finally
                {
                    FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_METADATA_2_FOR_OBJ, info, libHandle);
                }
            }
            else
            {
                try
                {
                    DS_REPL_OBJ_META_DATA objMetadata = new DS_REPL_OBJ_META_DATA();
                    Marshal.PtrToStructure(info, objMetadata);

                    count = objMetadata.cNumEntries;
                    if (count > 0)
                    {
                        collection.AddHelper(count, info, false);
                    }
                }
                finally
                {
                    FreeReplicaInfo(DS_REPL_INFO_TYPE.DS_REPL_INFO_METADATA_FOR_OBJ, info, libHandle);
                }
            }

            return collection;
        }

        internal bool SyncAllCallbackRoutine(IntPtr data, IntPtr update)
        {
            if (SyncFromAllServersCallback == null)
            {
                // user does not specify callback, resume the DsReplicaSyncAll execution
                return true;
            }
            else
            {
                // user specifies callback
                // our callback is invoked, update should not be NULL, do assertion here
                Debug.Assert(update != (IntPtr)0);

                DS_REPSYNCALL_UPDATE syncAllUpdate = new DS_REPSYNCALL_UPDATE();
                Marshal.PtrToStructure(update, syncAllUpdate);

                // get the event type
                SyncFromAllServersEvent eventType = syncAllUpdate.eventType;

                // get the error information
                IntPtr temp = syncAllUpdate.pErrInfo;
                SyncFromAllServersOperationException? exception = null;

                if (temp != (IntPtr)0)
                {
                    // error information is available
                    exception = ExceptionHelper.CreateSyncAllException(temp, true);
                    if (exception == null)
                    {
                        // this is the special case that we ignore the failure when SyncAllOptions.CheckServerAlivenessOnly is specified
                        return true;
                    }
                }

                string? targetName = null;
                string? sourceName = null;

                temp = syncAllUpdate.pSync;
                if (temp != (IntPtr)0)
                {
                    DS_REPSYNCALL_SYNC sync = new DS_REPSYNCALL_SYNC();
                    Marshal.PtrToStructure(temp, sync);

                    targetName = Marshal.PtrToStringUni(sync.pszDstId);
                    sourceName = Marshal.PtrToStringUni(sync.pszSrcId);
                }

                // invoke the client callback
                SyncUpdateCallback clientCallback = SyncFromAllServersCallback;

                return clientCallback(eventType, targetName, sourceName, exception);
            }
        }

        internal unsafe void SyncReplicaAllHelper(IntPtr handle, SyncReplicaFromAllServersCallback syncAllCallback, string partition, SyncFromAllServersOptions option, SyncUpdateCallback? callback, SafeLibraryHandle libHandle)
        {
            void* pErrors = null;

            if (!Partitions.Contains(partition))
                throw new ArgumentException(SR.ServerNotAReplica, nameof(partition));

            // we want to return the dn instead of DNS guid
            // call DsReplicaSyncAllW
            var dsReplicaSyncAllW = (delegate* unmanaged<IntPtr, char*, int, IntPtr, IntPtr, void**, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaSyncAllW");
            if (dsReplicaSyncAllW == null)
            {
                throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
            }

            int result;
            fixed (char* partitionPtr = partition)
            {
                IntPtr syncAllFunctionPointer = Marshal.GetFunctionPointerForDelegate(syncAllCallback);
                result = dsReplicaSyncAllW(handle, partitionPtr, (int)option | DS_REPSYNCALL_ID_SERVERS_BY_DN, syncAllFunctionPointer, (IntPtr)0, &pErrors);
                GC.KeepAlive(syncAllCallback);
            }

            try
            {
                // error happens during the synchronization
                if (pErrors is not null)
                {
                    SyncFromAllServersOperationException? e = ExceptionHelper.CreateSyncAllException((IntPtr)pErrors, false);
                    if (e == null)
                        return;
                    else
                        throw e;
                }
                else
                {
                    // API does not return error infor occurred during synchronization, but result is not success.
                    if (result != 0)
                        throw new SyncFromAllServersOperationException(ExceptionHelper.GetErrorMessage(result, false));
                }
            }
            finally
            {
                // release the memory
                if (pErrors is not null)
                    global::Interop.Kernel32.LocalFree(pErrors);
            }
        }

        private unsafe void FreeReplicaInfo(DS_REPL_INFO_TYPE type, IntPtr value, SafeLibraryHandle libHandle)
        {
            if (value != (IntPtr)0)
            {
                // call DsReplicaFreeInfo
                var dsReplicaFreeInfo = (delegate* unmanaged<int, IntPtr, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaFreeInfo");
                if (dsReplicaFreeInfo == null)
                {
                    throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
                }
                dsReplicaFreeInfo((int)type, value);
            }
        }

        internal unsafe void SyncReplicaHelper(IntPtr dsHandle, bool isADAM, string partition, string? sourceServer, int option, SafeLibraryHandle libHandle)
        {
            int structSize = sizeof(Guid);
            IntPtr unmanagedGuid = 0;
            Guid guid = Guid.Empty;
            AdamInstance? adamServer = null;
            DomainController? dcServer = null;

            unmanagedGuid = Marshal.AllocHGlobal(structSize);
            try
            {
                if (sourceServer != null)
                {
                    DirectoryContext newContext = Utils.GetNewDirectoryContext(sourceServer, DirectoryContextType.DirectoryServer, context);
                    if (isADAM)
                    {
                        adamServer = AdamInstance.GetAdamInstance(newContext);
                        guid = adamServer.NtdsaObjectGuid;
                    }
                    else
                    {
                        dcServer = DomainController.GetDomainController(newContext);
                        guid = dcServer.NtdsaObjectGuid;
                    }

                    Marshal.StructureToPtr(guid, unmanagedGuid, false);
                }

                // call DsReplicaSyncW
                var dsReplicaSyncW = (delegate* unmanaged<IntPtr, char*, IntPtr, int, int>)global::Interop.Kernel32.GetProcAddress(libHandle, "DsReplicaSyncW");
                if (dsReplicaSyncW == null)
                {
                    throw ExceptionHelper.GetExceptionFromErrorCode(Marshal.GetLastPInvokeError());
                }

                fixed (char* partitionPtr = partition)
                {
                    int result = dsReplicaSyncW(dsHandle, partitionPtr, unmanagedGuid, (int)option);

                    // check the result
                    if (result != 0)
                    {
                        if (!Partitions.Contains(partition))
                            throw new ArgumentException(SR.ServerNotAReplica, nameof(partition));

                        string? serverDownName = null;
                        // this is the error returned when the server that we want to sync from is down
                        if (result == Interop.Errors.RPC_S_SERVER_UNAVAILABLE)
                            serverDownName = sourceServer;
                        // this is the error returned when the server that we want to get synced is down
                        else if (result == Interop.Errors.RPC_S_CALL_FAILED)
                            serverDownName = Name;

                        throw ExceptionHelper.GetExceptionFromErrorCode(result, serverDownName);
                    }
                }
            }
            finally
            {
                if (unmanagedGuid != (IntPtr)0)
                    Marshal.FreeHGlobal(unmanagedGuid);

                adamServer?.Dispose();
                dcServer?.Dispose();
            }
        }

        internal ReplicationConnectionCollection GetInboundConnectionsHelper()
        {
            if (_inbound == null)
            {
                // construct the replicationconnection collection
                _inbound = new ReplicationConnectionCollection();
                DirectoryContext newContext = Utils.GetNewDirectoryContext(Name, DirectoryContextType.DirectoryServer, context);

                // this is the first time that user tries to retrieve this property, so get it from the directory
                string serverName = (this is DomainController) ? ((DomainController)this).ServerObjectName : ((AdamInstance)this).ServerObjectName;
                string srchDN = "CN=NTDS Settings," + serverName;
                DirectoryEntry de = DirectoryEntryManager.GetDirectoryEntry(Utils.GetNewDirectoryContext(Name, DirectoryContextType.DirectoryServer, context), srchDN);

                ADSearcher adSearcher = new ADSearcher(de,
                                                      "(&(objectClass=nTDSConnection)(objectCategory=nTDSConnection))",
                                                      s_cn,
                                                      SearchScope.OneLevel);
                SearchResultCollection? srchResults = null;

                try
                {
                    srchResults = adSearcher.FindAll();
                    foreach (SearchResult r in srchResults)
                    {
                        ReplicationConnection con = new ReplicationConnection(newContext, r.GetDirectoryEntry(), (string)PropertyManager.GetSearchResultPropertyValue(r, PropertyManager.Cn)!);
                        _inbound.Add(con);
                    }
                }
                catch (COMException e)
                {
                    throw ExceptionHelper.GetExceptionFromCOMException(newContext, e);
                }
                finally
                {
                    srchResults?.Dispose();
                    de.Dispose();
                }
            }

            return _inbound;
        }

        internal ReplicationConnectionCollection GetOutboundConnectionsHelper()
        {
            // this is the first time that user tries to retrieve this property, so get it from the directory
            if (_outbound == null)
            {
                // search base is the site container
                string siteName = (this is DomainController) ? ((DomainController)this).SiteObjectName : ((AdamInstance)this).SiteObjectName;
                DirectoryEntry de = DirectoryEntryManager.GetDirectoryEntry(Utils.GetNewDirectoryContext(Name, DirectoryContextType.DirectoryServer, context), siteName);

                string serverName = (this is DomainController) ? ((DomainController)this).ServerObjectName : ((AdamInstance)this).ServerObjectName;
                ADSearcher adSearcher = new ADSearcher(de,
                                                               "(&(objectClass=nTDSConnection)(objectCategory=nTDSConnection)(fromServer=CN=NTDS Settings," + serverName + "))",
                                                               s_objectClassCn,
                                                               SearchScope.Subtree);

                SearchResultCollection? results = null;
                DirectoryContext newContext = Utils.GetNewDirectoryContext(Name, DirectoryContextType.DirectoryServer, context);

                try
                {
                    results = adSearcher.FindAll();
                    _outbound = new ReplicationConnectionCollection();

                    foreach (SearchResult result in results)
                    {
                        ReplicationConnection con = new ReplicationConnection(newContext, result.GetDirectoryEntry(), (string)result.Properties["cn"][0]!);
                        _outbound.Add(con);
                    }
                }
                catch (COMException e)
                {
                    throw ExceptionHelper.GetExceptionFromCOMException(newContext, e);
                }
                finally
                {
                    results?.Dispose();
                    de.Dispose();
                }
            }

            return _outbound;
        }

        #endregion private methods
    }
}
