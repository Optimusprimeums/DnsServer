/*
Technitium DNS Server
Copyright (C) 2026  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Auth;
using DnsServerCore.Dns;
using DnsServerCore.Dns.Dnssec;
using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Zones;
using DnsServerCore.HttpApi;
using DnsServerCore.HttpApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Cluster
{
    sealed class ClusterManager : IDisposable
    {
        #region variables

        const ushort HEARTBEAT_REFRESH_INTERVAL_SECONDS = 30;
        const ushort HEARTBEAT_RETRY_INTERVAL_SECONDS = 10;
        const ushort CONFIG_REFRESH_INTERVAL_SECONDS = 900;
        const ushort CONFIG_RETRY_INTERVAL_SECONDS = 60;

        readonly DnsWebService _dnsWebService;

        string _clusterDomain;
        ushort _heartbeatRefreshIntervalSeconds;
        ushort _heartbeatRetryIntervalSeconds;
        ushort _configRefreshIntervalSeconds;
        ushort _configRetryIntervalSeconds;
        DateTime _configLastSynced;
        DateTime _configLastModified = DateTime.UtcNow; //primary only; startup time makes secondaries check once after a restart

        //auto failover; cluster wide options set on primary
        const ushort AUTO_FAILOVER_TIMEOUT_SECONDS = 120;
        const int AUTO_FAILOVER_TIMER_INTERVAL = 10000;
        bool _autoFailoverEnabled;
        string _autoFailoverSuccessorNodeName;
        ushort _autoFailoverTimeoutSeconds = AUTO_FAILOVER_TIMEOUT_SECONDS;
        IReadOnlyList<IPAddress> _autoFailoverWitnessAddresses; //2-node tie breaker; null = successor's default gateway
        DateTime _autoFailoverPausedUntil; //local clock; set from primary for planned maintenance
        const int LIVENESS_PROBE_TIMEOUT = 3000;
        readonly Timer _autoFailoverTimer;
        int _autoFailoverInProgress;
        readonly Lock _voteLock = new Lock();
        string _voteGrantedTo;
        DateTime _voteGrantedOn;
        readonly Dictionary<int, DateTime> _staleNodeNotifiedOn = new Dictionary<int, DateTime>();
        const int STALE_NODE_NOTIFY_INTERVAL_SECONDS = 300;

        //set when another node in this cluster has taken over as primary while this primary was unreachable
        volatile bool _fenced;
        string _fencedByNodeName;
        uint _primaryTerm; //incremented on every promotion; a Primary is fenced only by a Primary with a higher term
        readonly Dictionary<int, DateTime> _fenceRejectedLoggedOn = new Dictionary<int, DateTime>();

        IReadOnlyDictionary<int, ClusterNode> _clusterNodes;

        readonly SemaphoreSlim _configRefreshLock = new SemaphoreSlim(1, 1);
        readonly Timer _configRefreshTimer;
        bool _configRefreshTimerTriggered;
        IReadOnlyCollection<string> _configRefreshIncludeZones;
        const int CONFIG_REFRESH_TIMER_INTERVAL = 5000;

        readonly Timer _notifyAllSecondaryNodesTimer;
        volatile bool _notifyAllSecondaryNodesTimerTriggered;
        const int NOTIFY_ALL_SECONDARY_NODES_TIMER_INTERVAL = 5000;

        readonly Timer _clusterUpdateForSecondaryNodeChangesTimer;
        bool _clusterUpdateForSecondaryNodeChangesTimerTriggered;
        const int CLUSTER_UPDATE_FOR_SECONDARY_NODE_CHANGES_TIMER_INTERVAL = 5000;

        readonly Lock _saveLock = new Lock();
        bool _pendingSave;
        readonly Timer _saveTimer;
        const int SAVE_TIMER_INITIAL_INTERVAL = 5000;

        volatile int _recordUpdateForMemberZonesId;

        #endregion

        #region constructor

        public ClusterManager(DnsWebService dnsWebService)
        {
            _dnsWebService = dnsWebService;

            _configRefreshTimer = new Timer(ConfigRefreshTimerCallbackAsync);
            _notifyAllSecondaryNodesTimer = new Timer(NotifyAllSecondaryNodesTimerCallbackAsync);
            _clusterUpdateForSecondaryNodeChangesTimer = new Timer(ClusterUpdateForSecondaryNodeChangesTimerCallbackAsync);
            _autoFailoverTimer = new Timer(AutoFailoverTimerCallbackAsync, null, AUTO_FAILOVER_TIMER_INTERVAL, AUTO_FAILOVER_TIMER_INTERVAL);

            _saveTimer = new Timer(delegate (object state)
            {
                lock (_saveLock)
                {
                    if (_pendingSave)
                    {
                        try
                        {
                            SaveConfigFileInternal();
                            _pendingSave = false;
                        }
                        catch (Exception ex)
                        {
                            _dnsWebService.LogManager.Write(ex);
                        }
                    }
                }
            });

            LoadConfigFile();

            if (ClusterInitialized)
            {
                //find and fix Cluster API token
                bool foundClusterApiToken = false;

                foreach (UserSession session in _dnsWebService.AuthManager.Sessions)
                {
                    if (session.Type == UserSessionType.ClusterApiToken)
                    {
                        foundClusterApiToken = true;
                        break;
                    }
                }

                if (!foundClusterApiToken)
                {
                    Group administrators = _dnsWebService.AuthManager.GetGroup(Group.ADMINISTRATORS);

                    foreach (UserSession session in _dnsWebService.AuthManager.Sessions)
                    {
                        if ((session.Type == UserSessionType.ApiToken) && _clusterDomain.Equals(session.TokenName, StringComparison.OrdinalIgnoreCase) && session.User.IsMemberOfGroup(administrators))
                        {
                            session.UpgradeToClusterApiToken();
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region IDisposable

        bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _configRefreshTimer?.Dispose();
            _notifyAllSecondaryNodesTimer?.Dispose();
            _clusterUpdateForSecondaryNodeChangesTimer?.Dispose();
            _autoFailoverTimer?.Dispose();

            DisposeAllNodes();

            lock (_saveLock)
            {
                _saveTimer?.Dispose();

                if (_pendingSave)
                {
                    try
                    {
                        SaveConfigFileInternal();
                    }
                    catch (Exception ex)
                    {
                        _dnsWebService.LogManager.Write(ex);
                    }
                    finally
                    {
                        _pendingSave = false;
                    }
                }
            }

            _configRefreshLock?.Dispose();

            _disposed = true;
        }

        #endregion

        #region config

        private void LoadConfigFile()
        {
            string configFile = Path.Combine(_dnsWebService.ConfigFolder, "cluster.config");

            try
            {
                DisposeAllNodes(); //dispose existing nodes, if any

                using (FileStream fS = new FileStream(configFile, FileMode.Open, FileAccess.Read))
                {
                    ReadConfigFrom(fS);
                }

                InitializeHeartbeatTimerFor(_clusterNodes);
                UpdateConfigRefreshTimer();

                _dnsWebService.LogManager.Write("DNS Server Cluster config file was loaded: " + configFile);
            }
            catch (FileNotFoundException)
            {
                //do nothing
            }
            catch (Exception ex)
            {
                _dnsWebService.LogManager.Write("DNS Server encountered an error while loading the Cluster config file: " + configFile, ex);
            }
        }

        public void LoadConfig(Stream s)
        {
            lock (_saveLock)
            {
                DisposeAllNodes(); //dispose existing nodes, if any

                ReadConfigFrom(s);

                InitializeHeartbeatTimerFor(_clusterNodes);
                UpdateConfigRefreshTimer();

                //save config file
                SaveConfigFileInternal();
                _pendingSave = false;
            }
        }

        private void UpdateConfigRefreshTimer(int refreshInterval = CONFIG_REFRESH_TIMER_INTERVAL)
        {
            //ensure that the new refresh interval is applied using lock
            _configRefreshLock.Wait();
            try
            {
                if (ClusterInitialized && (GetSelfNode().Type == ClusterNodeType.Secondary))
                    _configRefreshTimer.Change(refreshInterval, Timeout.Infinite); //start config refresh timer only for secondary nodes
                else
                    _configRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            finally
            {
                _configRefreshLock.Release();
            }
        }

        private void StopConfigRefreshTimer()
        {
            //ensure that the timer is stopped using lock
            _configRefreshLock.Wait();
            try
            {
                _configRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            finally
            {
                _configRefreshLock.Release();
            }
        }

        private void SaveConfigFileInternal()
        {
            if (!ClusterInitialized)
                throw new InvalidOperationException();

            string tmpConfigFile = Path.Combine(_dnsWebService.ConfigFolder, "cluster.tmp");
            string configFile = Path.Combine(_dnsWebService.ConfigFolder, "cluster.config");

            using (FileStream fS = new FileStream(tmpConfigFile, FileMode.Create, FileAccess.Write))
            {
                WriteConfigTo(fS);
            }

            File.Move(tmpConfigFile, configFile, true);

            _dnsWebService.LogManager.Write("DNS Server Cluster config file was saved: " + configFile);
        }

        private void SaveConfigFile(bool immediately = false)
        {
            if (!ClusterInitialized)
                throw new InvalidOperationException();

            lock (_saveLock)
            {
                if (immediately)
                {
                    SaveConfigFileInternal();
                    _pendingSave = false;
                    return;
                }

                if (_pendingSave)
                    return;

                _pendingSave = true;
                _saveTimer.Change(SAVE_TIMER_INITIAL_INTERVAL, Timeout.Infinite);
            }
        }

        private void UnloadAndDeleteConfigFile()
        {
            StopConfigRefreshTimer();

            DisposeAllNodes(); //dispose existing nodes, if any

            lock (_saveLock)
            {
                //unload
                _clusterDomain = null;
                _configLastSynced = default;
                _clusterNodes = null;

                _autoFailoverEnabled = false;
                _autoFailoverSuccessorNodeName = null;
                _autoFailoverTimeoutSeconds = AUTO_FAILOVER_TIMEOUT_SECONDS;
                _autoFailoverWitnessAddresses = null;
                _autoFailoverPausedUntil = default;
                _fenced = false;
                _fencedByNodeName = null;
                _primaryTerm = 0;

                //delete config file
                string configFile = Path.Combine(_dnsWebService.ConfigFolder, "cluster.config");

                try
                {
                    if (File.Exists(configFile))
                    {
                        File.Delete(configFile);

                        _dnsWebService.LogManager.Write("DNS Server Cluster config file was deleted: " + configFile);
                    }
                }
                catch (Exception ex)
                {
                    _dnsWebService.LogManager.Write("DNS Server encountered an error while deleting the Cluster config file: " + configFile, ex);
                }

                _pendingSave = false;
            }
        }

        private void ReadConfigFrom(Stream s)
        {
            if (Encoding.ASCII.GetString(s.ReadExactly(2)) != "CL") //format
                throw new InvalidDataException("DNS Server Cluster config file format is invalid.");

            BinaryReader bR = new BinaryReader(s);

            int version = bR.ReadByte();
            switch (version)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                    _clusterDomain = bR.ReadString();
                    _heartbeatRefreshIntervalSeconds = bR.ReadUInt16();
                    _heartbeatRetryIntervalSeconds = bR.ReadUInt16();
                    _configRefreshIntervalSeconds = bR.ReadUInt16();
                    _configRetryIntervalSeconds = bR.ReadUInt16();
                    _configLastSynced = s.ReadDateTime();

                    Dictionary<int, ClusterNode> clusterNodes = null;
                    int count = bR.ReadByte();

                    if (count > 0)
                    {
                        clusterNodes = new Dictionary<int, ClusterNode>(count);

                        for (int i = 0; i < count; i++)
                        {
                            ClusterNode node = new ClusterNode(this, bR);
                            clusterNodes.TryAdd(node.Id, node);
                        }
                    }

                    _clusterNodes = clusterNodes;

                    if (version >= 2)
                    {
                        _autoFailoverEnabled = bR.ReadBoolean();

                        _autoFailoverSuccessorNodeName = bR.ReadString();
                        if (_autoFailoverSuccessorNodeName.Length == 0)
                            _autoFailoverSuccessorNodeName = null;

                        _autoFailoverTimeoutSeconds = bR.ReadUInt16();

                        _fenced = bR.ReadBoolean();

                        _fencedByNodeName = bR.ReadString();
                        if (_fencedByNodeName.Length == 0)
                            _fencedByNodeName = null;
                    }
                    else
                    {
                        _autoFailoverEnabled = false;
                        _autoFailoverSuccessorNodeName = null;
                        _autoFailoverTimeoutSeconds = AUTO_FAILOVER_TIMEOUT_SECONDS;
                    }

                    if (version >= 3)
                    {
                        int witnessCount = bR.ReadByte();
                        if (witnessCount > 0)
                        {
                            IPAddress[] witnessAddresses = new IPAddress[witnessCount];

                            for (int i = 0; i < witnessCount; i++)
                                witnessAddresses[i] = IPAddressExtensions.ReadFrom(bR);

                            _autoFailoverWitnessAddresses = witnessAddresses;
                        }
                        else
                        {
                            _autoFailoverWitnessAddresses = null;
                        }

                        _autoFailoverPausedUntil = s.ReadDateTime();
                    }
                    else
                    {
                        _autoFailoverWitnessAddresses = null;
                        _autoFailoverPausedUntil = default;
                    }

                    if (version >= 4)
                        _primaryTerm = bR.ReadUInt32();
                    else
                        _primaryTerm = 0;

                    break;

                default:
                    throw new InvalidDataException("DNS Server Cluster config version not supported.");
            }
        }

        private void WriteConfigTo(Stream s)
        {
            BinaryWriter bW = new BinaryWriter(s);

            bW.Write(Encoding.ASCII.GetBytes("CL")); //format
            bW.Write((byte)4); //version

            bW.Write(_clusterDomain);
            bW.Write(_heartbeatRefreshIntervalSeconds);
            bW.Write(_heartbeatRetryIntervalSeconds);
            bW.Write(_configRefreshIntervalSeconds);
            bW.Write(_configRetryIntervalSeconds);
            s.WriteDateTime(_configLastSynced);

            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            if (clusterNodes is null)
            {
                bW.Write((byte)0);
            }
            else
            {
                bW.Write(Convert.ToByte(clusterNodes.Count));

                foreach (KeyValuePair<int, ClusterNode> node in clusterNodes)
                    node.Value.WriteTo(bW);
            }

            bW.Write(_autoFailoverEnabled);
            bW.Write(_autoFailoverSuccessorNodeName ?? "");
            bW.Write(_autoFailoverTimeoutSeconds);

            bW.Write(_fenced);
            bW.Write(_fencedByNodeName ?? "");

            IReadOnlyList<IPAddress> witnessAddresses = _autoFailoverWitnessAddresses;
            if (witnessAddresses is null)
            {
                bW.Write((byte)0);
            }
            else
            {
                bW.Write(Convert.ToByte(witnessAddresses.Count));

                foreach (IPAddress witnessAddress in witnessAddresses)
                    witnessAddress.WriteTo(bW);
            }

            s.WriteDateTime(_autoFailoverPausedUntil);

            bW.Write(_primaryTerm);
        }

        #endregion

        #region private

        private void DisposeAllNodes()
        {
            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            if (clusterNodes is not null)
            {
                foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                    clusterNode.Value.Dispose();
            }
        }

        private static void InitializeHeartbeatTimerFor(IReadOnlyDictionary<int, ClusterNode> clusterNodes)
        {
            //start heartbeat timers for all nodes except self node
            foreach (KeyValuePair<int, ClusterNode> node in clusterNodes)
            {
                if (node.Value.State == ClusterNodeState.Self)
                    continue;

                node.Value.InitializeHeartbeatTimer();
            }
        }

        private void UpdateHeartbeatTimerForAllClusterNodes()
        {
            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            if (clusterNodes is not null)
            {
                foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                {
                    if (clusterNode.Value.State == ClusterNodeState.Self)
                        continue;

                    clusterNode.Value.UpdateHeartbeatTimer();
                }
            }
        }

        private void DeleteAllClusterConfig()
        {
            //delete cluster catalog zone
            string clusterCatalogDomain = "cluster-catalog." + _clusterDomain;

            AuthZoneInfo clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain);
            if (clusterCatalogZoneInfo is not null)
            {
                if (_dnsWebService.DnsServer.AuthZoneManager.DeleteZone(clusterCatalogZoneInfo, true))
                    _dnsWebService.AuthManager.RemoveAllPermissions(PermissionSection.Zones, clusterCatalogDomain);
            }

            //remove TSIG key for cluster catalog zone
            {
                IReadOnlyDictionary<string, TsigKey> existingKeys = _dnsWebService.DnsServer.TsigKeys;
                if (existingKeys is not null)
                {
                    Dictionary<string, TsigKey> updatedKeys = new Dictionary<string, TsigKey>(existingKeys);
                    updatedKeys.Remove(clusterCatalogDomain);

                    _dnsWebService.DnsServer.TsigKeys = updatedKeys;
                }
            }

            //delete cluster API token
            {
                foreach (UserSession session in _dnsWebService.AuthManager.Sessions)
                {
                    if (session.Type == UserSessionType.ClusterApiToken)
                        _dnsWebService.AuthManager.DeleteSession(session.Token);
                }
            }

            //finalize
            if (_dnsWebService.DnsServer.ServerDomain.EndsWith("." + _clusterDomain, StringComparison.OrdinalIgnoreCase))
                _dnsWebService.DnsServer.ServerDomain = _dnsWebService.DnsServer.ServerDomain.Substring(0, _dnsWebService.DnsServer.ServerDomain.Length - (_clusterDomain.Length + 1));

            //save all changes
            _dnsWebService.DnsServer.SaveConfigFile();
            _dnsWebService.AuthManager.SaveConfigFile();

            UnloadAndDeleteConfigFile();
        }

        #endregion

        #region primary node

        public void InitializeCluster(string clusterDomain, IReadOnlyList<IPAddress> primaryNodeIpAddresses, UserSession session)
        {
            if (ClusterInitialized)
                throw new DnsServerException("Failed to initialize Cluster: the Cluster is already initialized.");

            if (!_dnsWebService.IsWebServiceTlsEnabled)
                throw new InvalidOperationException();

            if (session.User.Type != UserType.Local)
                throw new DnsServerException("Failed to initialize Cluster: only a local administrator user can initialize cluster. Please login with a local administrator user account and try again.");

            clusterDomain = clusterDomain.ToLowerInvariant();

            //create self node
            string serverDomain = _dnsWebService.DnsServer.ServerDomain;
            if (!serverDomain.EndsWith("." + clusterDomain, StringComparison.OrdinalIgnoreCase))
            {
                int x = serverDomain.IndexOf('.');
                if (x < 0)
                    serverDomain = serverDomain + "." + clusterDomain;
                else
                    serverDomain = string.Concat(serverDomain.AsSpan(0, x), ".", clusterDomain);
            }

            Uri primaryNodeUrl = new Uri($"https://{serverDomain}:{_dnsWebService.WebServiceTlsPort}/");

            ClusterNode selfPrimaryNode = new ClusterNode(this, RandomNumberGenerator.GetInt32(int.MaxValue), primaryNodeUrl, primaryNodeIpAddresses, ClusterNodeType.Primary, ClusterNodeState.Self);

            //create cluster primary zone
            AuthZoneInfo clusterZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterDomain);
            if (clusterZoneInfo is null)
            {
                clusterZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.CreatePrimaryZone(clusterDomain);
                if (clusterZoneInfo is null)
                    throw new DnsServerException($"Failed to initialize Cluster: failed to create the Cluster zone '{clusterDomain}'. Please try again.");
            }
            else if (clusterZoneInfo.Type != AuthZoneType.Primary)
            {
                throw new DnsServerException($"Failed to initialize Cluster: the zone '{clusterZoneInfo.Name}' already exists and is not a Primary zone. Please delete the existing zone or use a different Cluster domain name.");
            }

            //create cluster catalog zone
            string clusterCatalogDomain = "cluster-catalog." + clusterDomain;

            AuthZoneInfo clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain);
            if (clusterCatalogZoneInfo is null)
            {
                clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.CreateCatalogZone(clusterCatalogDomain);
                if (clusterCatalogZoneInfo is null)
                    throw new DnsServerException($"Failed to initialize Cluster: failed to create the Cluster Catalog zone '{clusterCatalogDomain}'. Please try again.");
            }
            else if (clusterCatalogZoneInfo.Type != AuthZoneType.Catalog)
            {
                throw new DnsServerException($"Failed to initialize Cluster: the zone '{clusterCatalogZoneInfo.Name}' already exists and is not a Catalog zone. Please delete the existing zone or use a different Cluster domain name.");
            }

            //set cluster primary zone permissions
            _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.ADMINISTRATORS), PermissionFlag.ViewModifyDelete);
            _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.DNS_ADMINISTRATORS), PermissionFlag.View);

            //set cluster catalog zone permissions
            _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterCatalogZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.ADMINISTRATORS), PermissionFlag.ViewModifyDelete);
            _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterCatalogZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.DNS_ADMINISTRATORS), PermissionFlag.View);

            //ensure cluster zone is a member of cluster catalog zone
            if (clusterZoneInfo.CatalogZoneName is null)
                _dnsWebService.DnsServer.AuthZoneManager.AddCatalogMemberZone(clusterCatalogZoneInfo.Name, clusterZoneInfo);
            else if (!clusterZoneInfo.CatalogZoneName.Equals(clusterCatalogZoneInfo.Name, StringComparison.OrdinalIgnoreCase))
                _dnsWebService.DnsServer.AuthZoneManager.ChangeCatalogMemberZoneOwnership(clusterZoneInfo, clusterCatalogZoneInfo.Name);

            //sign cluster zone
            if (clusterZoneInfo.ApexZone.DnssecStatus == AuthZoneDnssecStatus.Unsigned)
            {
                DnssecPrivateKey kskPrivateKey = DnssecPrivateKey.Create(DnssecAlgorithm.ECDSAP256SHA256, DnssecPrivateKeyType.KeySigningKey);
                DnssecPrivateKey zskPrivateKey = DnssecPrivateKey.Create(DnssecAlgorithm.ECDSAP256SHA256, DnssecPrivateKeyType.ZoneSigningKey);
                zskPrivateKey.RolloverDays = 90;

                _dnsWebService.DnsServer.AuthZoneManager.SignPrimaryZone(clusterZoneInfo.Name, kskPrivateKey, zskPrivateKey, 3600, false);
            }

            //create TSIG key for cluster catalog zone if it does not exist
            {
                IReadOnlyDictionary<string, TsigKey> existingKeys = _dnsWebService.DnsServer.TsigKeys;
                if (existingKeys is null)
                {
                    Dictionary<string, TsigKey> updatedKeys = new Dictionary<string, TsigKey>();
                    updatedKeys[clusterCatalogDomain] = new TsigKey(clusterCatalogDomain, TsigAlgorithm.HMAC_SHA256);

                    _dnsWebService.DnsServer.TsigKeys = updatedKeys;
                }
                else if (!existingKeys.ContainsKey(clusterCatalogDomain))
                {
                    Dictionary<string, TsigKey> updatedKeys = new Dictionary<string, TsigKey>(existingKeys);
                    updatedKeys[clusterCatalogDomain] = new TsigKey(clusterCatalogDomain, TsigAlgorithm.HMAC_SHA256);

                    _dnsWebService.DnsServer.TsigKeys = updatedKeys;
                }
            }

            //create cluster API token if it does not exist
            {
                List<UserSession> userSessions = _dnsWebService.AuthManager.GetSessions(session.User);
                bool apiTokenExists = false;

                foreach (UserSession existingSession in userSessions)
                {
                    if (existingSession.Type == UserSessionType.ClusterApiToken)
                    {
                        apiTokenExists = true;
                        break;
                    }
                }

                if (!apiTokenExists)
                    _dnsWebService.AuthManager.CreateSession(UserSessionType.ClusterApiToken, clusterZoneInfo.Name, session.User.Username, session.LastSeenRemoteAddress, session.LastSeenUserAgent);
            }

            //dispose existing nodes, if any
            DisposeAllNodes();

            //initialize cluster
            _clusterNodes = new Dictionary<int, ClusterNode>(1)
            {
                [selfPrimaryNode.Id] = selfPrimaryNode
            };

            _clusterDomain = clusterZoneInfo.Name;
            _heartbeatRefreshIntervalSeconds = HEARTBEAT_REFRESH_INTERVAL_SECONDS;
            _heartbeatRetryIntervalSeconds = HEARTBEAT_RETRY_INTERVAL_SECONDS;
            _configRefreshIntervalSeconds = CONFIG_REFRESH_INTERVAL_SECONDS;
            _configRetryIntervalSeconds = CONFIG_RETRY_INTERVAL_SECONDS;

            //update cluster primary zone and save zone file
            FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl); //find existing record TTL values
            RemoveAllClusterPrimaryZoneNSRecords(); //remove all existing NS records
            AddClusterPrimaryZoneRecordsFor(selfPrimaryNode, nsTtl, aTtl, _dnsWebService.WebServiceTlsCertificate);

            //update cluster catalog zone ACLs, TSIG key name and save zone file
            UpdateClusterCatalogZoneOptions(clusterCatalogZoneInfo);

            //finalize
            _dnsWebService.DnsServer.ServerDomain = selfPrimaryNode.Name;

            //save all changes
            _dnsWebService.DnsServer.SaveConfigFile(true);
            _dnsWebService.AuthManager.SaveConfigFile(true);
            SaveConfigFile();
        }

        public void DeleteCluster(bool forceDelete)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to delete Cluster: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to delete Cluster: only a Primary node can delete the Cluster.");

            if (!forceDelete && (_clusterNodes.Count > 1))
                throw new DnsServerException("Failed to delete Cluster: please remove all Secondary nodes before deleting the Cluster.");

            DeleteAllClusterConfig();
        }

        public ClusterNode JoinCluster(int secondaryNodeId, Uri secondaryNodeUrl, IReadOnlyList<IPAddress> secondaryNodeIpAddresses, X509Certificate2 secondaryNodeCertificate)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to add Secondary node: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to add Secondary node: only a Primary node can add a Secondary node to the Cluster.");

            string secondaryNodeDomain = secondaryNodeUrl.Host.ToLowerInvariant();

            if (!secondaryNodeDomain.EndsWith("." + _clusterDomain, StringComparison.OrdinalIgnoreCase))
                throw new DnsServerException("Failed to add Secondary node: the Secondary node domain name must be a subdomain of the Cluster domain name.");

            IReadOnlyDictionary<int, ClusterNode> existingClusterNodes = _clusterNodes;

            //validate for duplicate names
            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
            {
                if (existingClusterNode.Value.Name.Equals(secondaryNodeUrl.Host, StringComparison.OrdinalIgnoreCase))
                    throw new DnsServerException("Failed to add Secondary node: A node with the same DNS Server Domain Name already exists in the Cluster. Please try again after changing the Secondary node's DNS Server Domain Name.");
            }

            //add secondary node to cluster nodes
            ClusterNode secondaryNode = new ClusterNode(this, secondaryNodeId, secondaryNodeUrl, secondaryNodeIpAddresses, ClusterNodeType.Secondary, ClusterNodeState.Unknown);
            Dictionary<int, ClusterNode> updatedClusterNodes = new Dictionary<int, ClusterNode>(existingClusterNodes.Count + 1);

            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
                updatedClusterNodes[existingClusterNode.Value.Id] = existingClusterNode.Value;

            if (!updatedClusterNodes.TryAdd(secondaryNode.Id, secondaryNode))
                throw new DnsServerException("Failed to add Secondary node: node ID already exists in the Cluster. Please try again.");

            if (updatedClusterNodes.Count > 255)
                throw new DnsServerException("Failed to add Secondary node: a maximum of 255 nodes are supported by the Cluster.");

            IReadOnlyDictionary<int, ClusterNode> originalValue = Interlocked.CompareExchange(ref _clusterNodes, updatedClusterNodes, existingClusterNodes);
            if (!ReferenceEquals(originalValue, existingClusterNodes))
                throw new DnsServerException("Failed to add Secondary node: please try again.");

            secondaryNode.InitializeHeartbeatTimer();

            //update cluster zone and save zone file
            FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl); //find existing record TTL values
            AddClusterPrimaryZoneRecordsFor(secondaryNode, nsTtl, aTtl, secondaryNodeCertificate);

            //update cluster catalog zone ACLs and save zone file
            UpdateClusterCatalogZoneOptions();

            //save all changes
            SaveConfigFile();

            //notify all secondary nodes
            TriggerNotifyAllSecondaryNodes();

            //trigger NS and SOA update for member zones
            TriggerRecordUpdateForClusterCatalogMemberZones();

            return secondaryNode;
        }

        public async Task<ClusterNode> AskSecondaryNodeToLeaveClusterAsync(int secondaryNodeId)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to ask Secondary node to leave: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to ask Secondary node to leave: only a Primary node can ask a Secondary node to leave the Cluster.");

            //find existing secondary node
            if (!_clusterNodes.TryGetValue(secondaryNodeId, out ClusterNode secondaryNode))
                throw new DnsServerException("Failed to ask Secondary node to leave: the specified node does not exist in the Cluster.");

            if (secondaryNode.Type == ClusterNodeType.Primary)
                throw new DnsServerException("Failed to ask Secondary node to leave: the specified node is the Cluster Primary node and cannot be removed.");

            //ask secondary node to leave the cluster
            await secondaryNode.AskSecondaryNodeToLeaveClusterAsync();

            return secondaryNode;
        }

        public ClusterNode DeleteSecondaryNode(int secondaryNodeId)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to delete Secondary node: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to delete Secondary node: only a Primary node can delete a Secondary node from the Cluster.");

            //find existing secondary node
            IReadOnlyDictionary<int, ClusterNode> existingClusterNodes = _clusterNodes;

            if (!existingClusterNodes.TryGetValue(secondaryNodeId, out ClusterNode secondaryNode))
                throw new DnsServerException("Failed to delete Secondary node: the specified node does not exist in the Cluster.");

            if (secondaryNode.Type == ClusterNodeType.Primary)
                throw new DnsServerException("Failed to delete Secondary node: the specified node is the Cluster Primary node and cannot be deleted.");

            //delete secondary node from cluster nodes
            Dictionary<int, ClusterNode> updatedClusterNodes = new Dictionary<int, ClusterNode>(existingClusterNodes.Count - 1);

            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
            {
                if (existingClusterNode.Key == secondaryNodeId)
                    continue;

                updatedClusterNodes[existingClusterNode.Key] = existingClusterNode.Value;
            }

            IReadOnlyDictionary<int, ClusterNode> originalValue = Interlocked.CompareExchange(ref _clusterNodes, updatedClusterNodes, existingClusterNodes);
            if (!ReferenceEquals(originalValue, existingClusterNodes))
                throw new InvalidOperationException("Failed to delete Secondary node: please try again.");

            secondaryNode.Dispose();

            //update cluster zone and save zone file
            RemoveClusterPrimaryZoneRecordsFor(secondaryNode);

            //update cluster catalog zone ACLs and save zone file
            UpdateClusterCatalogZoneOptions();

            //save all changes
            SaveConfigFile();

            //notify all secondary nodes
            TriggerNotifyAllSecondaryNodes();

            //trigger NS and SOA update for member zones
            TriggerRecordUpdateForClusterCatalogMemberZones();

            return secondaryNode;
        }

        public ClusterNode UpdateSecondaryNode(int secondaryNodeId, Uri secondaryNodeUrl, IReadOnlyList<IPAddress> secondaryNodeIpAddresses, X509Certificate2 secondaryNodeCertificate)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to update Secondary node: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to update Secondary node: only a Primary node can update a Secondary node's details in the Cluster.");

            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;

            if (!clusterNodes.TryGetValue(secondaryNodeId, out ClusterNode secondaryNode))
                throw new DnsServerException("Failed to update Secondary node: the specified node does not exist in the Cluster.");

            if (secondaryNode.Type != ClusterNodeType.Secondary)
                throw new DnsServerException("Failed to update Secondary node: the specified node to update must be a Secondary node.");

            //validate for duplicate names
            foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
            {
                if (clusterNode.Key == secondaryNodeId)
                    continue; //skip self

                if (clusterNode.Value.Name.Equals(secondaryNodeUrl.Host, StringComparison.OrdinalIgnoreCase))
                    throw new DnsServerException("Failed to update Secondary node: A node with the same DNS Server Domain Name already exists in the Cluster. Please try again after changing the Secondary node's DNS Server Domain Name.");
            }

            bool secondaryNodeDomainChanged = !secondaryNode.Name.Equals(secondaryNodeUrl.Host, StringComparison.OrdinalIgnoreCase);

            //find existing record TTL values
            FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl);

            //update cluster zone to remove existing records for secondary node
            RemoveClusterPrimaryZoneRecordsFor(secondaryNode);

            //update secondary node URL and IP address
            secondaryNode.UpdateNode(secondaryNodeUrl, secondaryNodeIpAddresses);

            //update cluster zone to add updated records for secondary node and save zone file
            AddClusterPrimaryZoneRecordsFor(secondaryNode, nsTtl, aTtl, secondaryNodeCertificate);

            //update cluster catalog zone ACLs and save zone file
            UpdateClusterCatalogZoneOptions();

            //save all changes
            SaveConfigFile();

            //notify all secondary nodes
            TriggerNotifyAllSecondaryNodes();

            //trigger NS and SOA update for member zones only if secondary node domain name has changed
            if (secondaryNodeDomainChanged)
                TriggerRecordUpdateForClusterCatalogMemberZones();

            return secondaryNode;
        }

        public Task TransferConfigAsync(Stream zipStream, DateTime ifModifiedSince, ICollection<string> includeZones)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to transfer configuration: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to transfer configuration: only the Primary node can transfer the configuration.");

            if (_fenced)
                throw new DnsServerException("Failed to transfer configuration: this Primary node is fenced since node '" + _fencedByNodeName + "' has taken over as the Primary node.");

            return _dnsWebService.BackupConfigAsync(zipStream: zipStream,
                                                    authConfig: true,
                                                    clusterConfig: false,
                                                    webServiceSettings: false,
                                                    dnsSettings: true,
                                                    logSettings: false,
                                                    zones: true,
                                                    allowedZones: true,
                                                    blockedZones: true,
                                                    blockLists: true,
                                                    apps: true,
                                                    scopes: false,
                                                    stats: false,
                                                    logs: false,
                                                    isConfigTransfer: true,
                                                    ifModifiedSince: ifModifiedSince,
                                                    includeZones: includeZones);
        }

        public void UpdateClusterOptions(ushort heartbeatRefreshIntervalSeconds, ushort heartbeatRetryIntervalSeconds, ushort configRefreshIntervalSeconds, ushort configRetryIntervalSeconds)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to update Cluster options: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to update Cluster options: only the Primary node can update the Cluster options.");

            if ((heartbeatRefreshIntervalSeconds < 10) || (heartbeatRefreshIntervalSeconds > 300))
                throw new ArgumentOutOfRangeException(nameof(heartbeatRefreshIntervalSeconds));

            if ((heartbeatRetryIntervalSeconds < 10) || (heartbeatRetryIntervalSeconds > 300))
                throw new ArgumentOutOfRangeException(nameof(heartbeatRetryIntervalSeconds));

            if ((configRefreshIntervalSeconds < 30) || (configRefreshIntervalSeconds > 3600))
                throw new ArgumentOutOfRangeException(nameof(configRefreshIntervalSeconds));

            if ((configRetryIntervalSeconds < 30) || (configRetryIntervalSeconds > 3600))
                throw new ArgumentOutOfRangeException(nameof(configRetryIntervalSeconds));

            if (configRefreshIntervalSeconds <= heartbeatRefreshIntervalSeconds)
                throw new ArgumentException("Failed to update Cluster options: The config refresh interval must be greater than the heartbeat refresh interval.");

            bool changed = false;

            if (_heartbeatRefreshIntervalSeconds != heartbeatRefreshIntervalSeconds)
            {
                _heartbeatRefreshIntervalSeconds = heartbeatRefreshIntervalSeconds;
                changed = true;
            }

            if (_heartbeatRetryIntervalSeconds != heartbeatRetryIntervalSeconds)
            {
                _heartbeatRetryIntervalSeconds = heartbeatRetryIntervalSeconds;
                changed = true;
            }

            if (_configRefreshIntervalSeconds != configRefreshIntervalSeconds)
            {
                _configRefreshIntervalSeconds = configRefreshIntervalSeconds;
                changed = true;
            }

            if (_configRetryIntervalSeconds != configRetryIntervalSeconds)
            {
                _configRetryIntervalSeconds = configRetryIntervalSeconds;
                changed = true;
            }

            if (changed)
            {
                //apply new interval to all cluster nodes immediately
                UpdateHeartbeatTimerForAllClusterNodes();

                //save changes
                SaveConfigFile();

                //trigger notify to all secondary nodes
                TriggerNotifyAllSecondaryNodes();
            }
        }

        public string ValidateAutoFailoverOptions(bool enabled, string successorNodeName, ushort timeoutSeconds, ushort heartbeatRefreshIntervalSeconds)
        {
            //returns normalized successor node name; throws if the options cannot be applied
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to update Cluster auto failover options: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to update Cluster auto failover options: only the Primary node can update the Cluster options.");

            if (string.IsNullOrWhiteSpace(successorNodeName))
            {
                successorNodeName = null;
            }
            else if (TryGetClusterNode(successorNodeName, out ClusterNode successorNode) && (successorNode.Type == ClusterNodeType.Secondary))
            {
                successorNodeName = successorNode.Name;
            }
            else
            {
                if (enabled)
                    throw new DnsServerException("Failed to update Cluster auto failover options: the successor must be an existing Secondary node: " + successorNodeName);

                successorNodeName = null; //stale successor while disabled; clear it instead of blocking the save
            }

            if (!enabled)
                return successorNodeName;

            if (successorNodeName is null)
                throw new DnsServerException("Failed to update Cluster auto failover options: a successor node must be selected.");

            //2-node clusters use a witness (default: successor's gateway) instead of a majority vote

            if ((timeoutSeconds < 60) || (timeoutSeconds > 3600))
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Auto failover timeout must be between 60 and 3600 seconds.");

            if (timeoutSeconds < (heartbeatRefreshIntervalSeconds * 2))
                throw new ArgumentException("Failed to update Cluster auto failover options: the timeout must be at least twice the heartbeat refresh interval.");

            return successorNodeName;
        }

        public void PauseAutoFailover(int minutes)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to pause auto failover: the Cluster is not initialized.");

            if (GetSelfNode().Type != ClusterNodeType.Primary)
                throw new DnsServerException("Failed to pause auto failover: only the Primary node can pause auto failover.");

            if ((minutes < 0) || (minutes > 1440))
                throw new ArgumentOutOfRangeException(nameof(minutes), "Pause duration must be between 0 and 1440 minutes.");

            _autoFailoverPausedUntil = (minutes == 0) ? default : DateTime.UtcNow.AddMinutes(minutes);

            SaveConfigFile(true);

            //push to secondaries right away so the successor knows before the primary goes down for maintenance
            TriggerNotifyAllSecondaryNodes(0);

            if (minutes == 0)
                _dnsWebService.LogManager.Write("Cluster auto failover was resumed.");
            else
                _dnsWebService.LogManager.Write("Cluster auto failover was paused for " + minutes + " minute(s) until " + _autoFailoverPausedUntil.ToString("u") + ".");
        }

        public bool IsAutoFailoverPaused
        { get { return DateTime.UtcNow < _autoFailoverPausedUntil; } }

        public int AutoFailoverPauseRemainingSeconds
        {
            get
            {
                double remaining = (_autoFailoverPausedUntil - DateTime.UtcNow).TotalSeconds;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        public void ValidateWitnessAddresses(IReadOnlyList<IPAddress> witnessAddresses)
        {
            if (witnessAddresses is null)
                return;

            if (witnessAddresses.Count > 10)
                throw new DnsServerException("Failed to update Cluster auto failover options: at most 10 witness addresses can be specified.");

            foreach (IPAddress witnessAddress in witnessAddresses)
            {
                if (IPAddress.IsLoopback(witnessAddress) || witnessAddress.Equals(IPAddress.Any) || witnessAddress.Equals(IPAddress.IPv6Any))
                    throw new DnsServerException("Failed to update Cluster auto failover options: invalid witness address: " + witnessAddress.ToString());

                foreach (KeyValuePair<int, ClusterNode> clusterNode in _clusterNodes)
                {
                    foreach (IPAddress nodeAddress in clusterNode.Value.IPAddresses)
                    {
                        if (nodeAddress.Equals(witnessAddress))
                            throw new DnsServerException("Failed to update Cluster auto failover options: the witness must be a separate device (such as your router), not a Cluster node: " + witnessAddress.ToString());
                    }
                }
            }
        }

        public void UpdateAutoFailoverOptions(bool enabled, string successorNodeName, ushort timeoutSeconds, IReadOnlyList<IPAddress> witnessAddresses)
        {
            successorNodeName = ValidateAutoFailoverOptions(enabled, successorNodeName, timeoutSeconds, _heartbeatRefreshIntervalSeconds);
            ValidateWitnessAddresses(witnessAddresses);

            if ((witnessAddresses is not null) && (witnessAddresses.Count == 0))
                witnessAddresses = null;

            if ((timeoutSeconds < 60) || (timeoutSeconds > 3600))
                timeoutSeconds = _autoFailoverTimeoutSeconds; //keep a valid value when disabled

            bool witnessesSame = (witnessAddresses is null) ? (_autoFailoverWitnessAddresses is null) : ((_autoFailoverWitnessAddresses is not null) && witnessAddresses.HasSameItems(_autoFailoverWitnessAddresses));

            if ((_autoFailoverEnabled == enabled) && string.Equals(_autoFailoverSuccessorNodeName, successorNodeName, StringComparison.OrdinalIgnoreCase) && (_autoFailoverTimeoutSeconds == timeoutSeconds) && witnessesSame)
                return;

            _autoFailoverEnabled = enabled;
            _autoFailoverSuccessorNodeName = successorNodeName;
            _autoFailoverTimeoutSeconds = timeoutSeconds;
            _autoFailoverWitnessAddresses = witnessAddresses;

            SaveConfigFile();
            TriggerNotifyAllSecondaryNodes();
        }

        public ClusterPrimaryReachability GetPrimaryReachability(string candidateNodeName)
        {
            //used by successor to collect votes; answers from this node's own heartbeat view of the primary
            if (!ClusterInitialized)
                throw new DnsServerException("The Cluster is not initialized.");

            ClusterNode primaryNode = GetPrimaryNode();

            ClusterPrimaryReachability reachability = new ClusterPrimaryReachability();
            reachability.PrimaryNodeId = primaryNode.Id;

            if (primaryNode.LastSeen != default)
                reachability.PrimaryLastSeen = primaryNode.LastSeen;

            if (primaryNode.State == ClusterNodeState.Self)
            {
                reachability.PrimaryReachable = true;
                reachability.PrimaryLastSeen = DateTime.UtcNow;
            }
            else if (IsPrimaryNodeReachable(primaryNode))
            {
                reachability.PrimaryReachable = true;
            }
            else
            {
                //primary looks down; grant the vote only to the successor in this node's view and only once per window
                reachability.PrimaryReachable = !TryGrantVote(candidateNodeName);
            }

            return reachability;
        }

        private bool TryGrantVote(string candidateNodeName)
        {
            if (string.IsNullOrEmpty(candidateNodeName) || !_autoFailoverEnabled || IsAutoFailoverPaused || !candidateNodeName.Equals(_autoFailoverSuccessorNodeName, StringComparison.OrdinalIgnoreCase))
                return false;

            lock (_voteLock)
            {
                DateTime utcNow = DateTime.UtcNow;

                if ((_voteGrantedTo is not null) && (utcNow < _voteGrantedOn.AddSeconds(_autoFailoverTimeoutSeconds)) && !_voteGrantedTo.Equals(candidateNodeName, StringComparison.OrdinalIgnoreCase))
                    return false; //already voted for another candidate in this window

                _voteGrantedTo = candidateNodeName;
                _voteGrantedOn = utcNow;
                return true;
            }
        }

        private bool IsPrimaryNodeReachable(ClusterNode primaryNode)
        {
            DateTime lastSeen = primaryNode.LastSeen;

            //not contacted yet since this node started; count from start time so a fresh node does not vote too early
            if (lastSeen == default)
                lastSeen = _dnsWebService.UpTimeStamp;

            return (primaryNode.State != ClusterNodeState.Unreachable) || (DateTime.UtcNow < lastSeen.AddSeconds(_autoFailoverTimeoutSeconds));
        }

        private async void AutoFailoverTimerCallbackAsync(object state)
        {
            if (Interlocked.CompareExchange(ref _autoFailoverInProgress, 1, 0) != 0)
                return;

            try
            {
                if (!_autoFailoverEnabled || !ClusterInitialized || (_autoFailoverSuccessorNodeName is null))
                    return;

                ClusterNode selfNode = GetSelfNode();
                if ((selfNode.Type != ClusterNodeType.Secondary) || !selfNode.Name.Equals(_autoFailoverSuccessorNodeName, StringComparison.OrdinalIgnoreCase))
                    return; //only the designated successor acts

                ClusterNode primaryNode = GetPrimaryNode();
                if (IsPrimaryNodeReachable(primaryNode))
                    return;

                if (IsAutoFailoverPaused)
                    return; //planned maintenance on the primary

                //heartbeat only proves the HTTPS API is down; if the DNS or web port still accepts connections the primary is alive
                if (await IsPrimaryNodeProcessAliveAsync(primaryNode))
                {
                    _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' heartbeat is failing but it still accepts connections on its DNS or web service port; not promoting.");
                    return;
                }

                //this node's own vote follows the same one-candidate-per-window rule as other voters
                if (!TryGrantVote(selfNode.Name))
                    return;

                IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
                int totalNodes = clusterNodes.Count;

                if (totalNodes == 2)
                {
                    //no other voter exists; use the witness to prove this node is not the isolated one
                    IReadOnlyList<IPAddress> witnessAddresses = _autoFailoverWitnessAddresses ?? GetDefaultGatewayAddresses();
                    if (witnessAddresses.Count == 0)
                    {
                        _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' is unreachable but no witness address is configured and no default gateway was found; not promoting.");
                        return;
                    }

                    IPAddress reachableWitness = await FindReachableWitnessAsync(witnessAddresses);
                    if (reachableWitness is null)
                    {
                        _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' is unreachable but this node cannot reach any witness (" + witnessAddresses.Join() + ") either, so this node is likely the isolated one; not promoting.");
                        return;
                    }

                    _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' is unreachable for more than " + _autoFailoverTimeoutSeconds + " seconds and does not accept connections, while witness " + reachableWitness.ToString() + " is reachable; promoting this node to Primary.");
                }
                else
                {
                    if (!await HasPrimaryDownQuorumAsync(selfNode, primaryNode, clusterNodes))
                        return;
                }

                //promotion stops the config refresh timer under its lock, which waits for any in-flight sync to finish
                await PromoteToPrimaryNodeAsync(true);

                //successor is now the primary; require admin to choose a new successor
                _autoFailoverEnabled = false;
                _autoFailoverSuccessorNodeName = null;
                SaveConfigFile();
                TriggerNotifyAllSecondaryNodes();

                _dnsWebService.LogManager.Write("Cluster auto failover: this node was promoted to Primary successfully. Auto failover was disabled; select a new successor node to enable it again. The former Primary node will be fenced when it comes back and must be rejoined as a Secondary node.");
            }
            catch (Exception ex)
            {
                _dnsWebService.LogManager.Write("Cluster auto failover failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _autoFailoverInProgress, 0);
            }
        }

        private async Task<bool> HasPrimaryDownQuorumAsync(ClusterNode selfNode, ClusterNode primaryNode, IReadOnlyDictionary<int, ClusterNode> clusterNodes)
        {
            //collect votes from other secondary nodes; this node counts as one vote
            {
                int totalNodes = clusterNodes.Count;
                int votesPrimaryDown = 1;

                List<Task<ClusterPrimaryReachability>> voteTasks = new List<Task<ClusterPrimaryReachability>>();

                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                    {
                        if ((clusterNode.Value.State == ClusterNodeState.Self) || (clusterNode.Value.Type == ClusterNodeType.Primary))
                            continue;

                        voteTasks.Add(clusterNode.Value.GetPrimaryReachabilityAsync(selfNode.Name, cts.Token));
                    }

                    foreach (Task<ClusterPrimaryReachability> voteTask in voteTasks)
                    {
                        try
                        {
                            ClusterPrimaryReachability vote = await voteTask;

                            //vote counts only if the voter agrees on which node is the primary
                            if ((vote.PrimaryNodeId == primaryNode.Id) && !vote.PrimaryReachable)
                                votesPrimaryDown++;
                        }
                        catch
                        {
                            //unreachable voter does not count
                        }
                    }
                }

                int quorum = (totalNodes / 2) + 1;

                if (votesPrimaryDown < quorum)
                {
                    _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' is unreachable from this node but only " + votesPrimaryDown + " of " + totalNodes + " nodes agree (quorum " + quorum + "); not promoting.");
                    return false;
                }

                _dnsWebService.LogManager.Write("Cluster auto failover: Primary node '" + primaryNode.ToString() + "' is unreachable for more than " + _autoFailoverTimeoutSeconds + " seconds and " + votesPrimaryDown + " of " + totalNodes + " nodes agree; promoting this node to Primary.");
                return true;
            }
        }

        private static async Task<bool> TryConnectAsync(IPAddress address, int port, bool refusedMeansReachable)
        {
            using (Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            using (CancellationTokenSource cts = new CancellationTokenSource(LIVENESS_PROBE_TIMEOUT))
            {
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token);
                    return true;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    return refusedMeansReachable; //host answered with a reset so it is up, but nothing listens on the port
                }
                catch
                {
                    return false;
                }
            }
        }

        private static async Task<bool> IsPrimaryNodeProcessAliveAsync(ClusterNode primaryNode)
        {
            //a running DNS server accepts TCP on port 53 and on its web service port; a refused connect means the server is down
            List<Task<bool>> probes = new List<Task<bool>>();

            foreach (IPAddress address in primaryNode.IPAddresses)
            {
                probes.Add(TryConnectAsync(address, 53, false));
                probes.Add(TryConnectAsync(address, primaryNode.Url.Port, false));
            }

            foreach (bool alive in await Task.WhenAll(probes))
            {
                if (alive)
                    return true;
            }

            return false;
        }

        private static IReadOnlyList<IPAddress> GetDefaultGatewayAddresses()
        {
            List<IPAddress> gateways = new List<IPAddress>();

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if ((networkInterface.OperationalStatus != OperationalStatus.Up) || (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback))
                    continue;

                foreach (GatewayIPAddressInformation gateway in networkInterface.GetIPProperties().GatewayAddresses)
                {
                    IPAddress address = gateway.Address;

                    if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal)
                        continue;

                    if (!gateways.Contains(address))
                        gateways.Add(address);
                }
            }

            return gateways;
        }

        private static async Task<IPAddress> FindReachableWitnessAsync(IReadOnlyList<IPAddress> witnessAddresses)
        {
            foreach (IPAddress witnessAddress in witnessAddresses)
            {
                //ping first; many routers answer it
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using (Ping ping = new Ping())
                        {
                            PingReply reply = await ping.SendPingAsync(witnessAddress, LIVENESS_PROBE_TIMEOUT);
                            if (reply.Status == IPStatus.Success)
                                return witnessAddress;
                        }
                    }
                    catch
                    { }
                }

                //ICMP may be blocked; any TCP answer (accept or reset) on common router ports also proves reachability
                foreach (int port in new int[] { 53, 80, 443 })
                {
                    if (await TryConnectAsync(witnessAddress, port, true))
                        return witnessAddress;
                }
            }

            return null;
        }

        public void CheckPrimaryConsistency(ClusterNode remoteNode, ClusterInfo remoteClusterInfo)
        {
            if (!ClusterInitialized || !remoteClusterInfo.ClusterInitialized || (remoteClusterInfo.ClusterNodes is null))
                return;

            if (!string.Equals(remoteClusterInfo.ClusterDomain, _clusterDomain, StringComparison.OrdinalIgnoreCase))
                return;

            ClusterNode selfNode = GetSelfNode();
            if (selfNode.Type != ClusterNodeType.Primary)
                return;

            ClusterInfo.ClusterNodeInfo remotePrimaryNodeInfo = null;

            foreach (ClusterInfo.ClusterNodeInfo nodeInfo in remoteClusterInfo.ClusterNodes)
            {
                if (nodeInfo.Type.Equals(nameof(ClusterNodeType.Primary), StringComparison.OrdinalIgnoreCase))
                {
                    remotePrimaryNodeInfo = nodeInfo;
                    break;
                }
            }

            if ((remotePrimaryNodeInfo is null) || (remotePrimaryNodeInfo.Id == selfNode.Id))
                return; //remote agrees this node is primary

            if (_clusterNodes.ContainsKey(remotePrimaryNodeInfo.Id))
            {
                //remote follows another member of this cluster as primary: that node was promoted while this node was unreachable
                if (!_fenced)
                {
                    //fence only when the other Primary itself confirms that it was promoted after this node became Primary;
                    //a Secondary's view (stale, misled or compromised) is never enough to stop this Primary
                    uint remotePrimaryTerm = remoteClusterInfo.PrimaryTerm ?? 0;

                    if ((remotePrimaryNodeInfo.Id != remoteNode.Id) || (remotePrimaryTerm <= _primaryTerm))
                    {
                        lock (_fenceRejectedLoggedOn)
                        {
                            DateTime utcNow = DateTime.UtcNow;

                            if (_fenceRejectedLoggedOn.TryGetValue(remoteNode.Id, out DateTime loggedOn) && (utcNow < loggedOn.AddSeconds(STALE_NODE_NOTIFY_INTERVAL_SECONDS)))
                                return;

                            _fenceRejectedLoggedOn[remoteNode.Id] = utcNow;
                        }

                        if (remotePrimaryNodeInfo.Id != remoteNode.Id)
                            _dnsWebService.LogManager.Write("Cluster: node '" + remoteNode.ToString() + "' reports that '" + remotePrimaryNodeInfo.Name + "' is the Primary node. This node stays Primary unless '" + remotePrimaryNodeInfo.Name + "' itself confirms a newer promotion.");
                        else
                            _dnsWebService.LogManager.Write("WARNING! Cluster: node '" + remoteNode.ToString() + "' also claims to be the Primary node, but with Primary term " + remotePrimaryTerm + " which isn't newer than this node's term " + _primaryTerm + ". Not fencing; check the Cluster and remove the stale Primary node.");

                        return;
                    }

                    _fencedByNodeName = remotePrimaryNodeInfo.Name;
                    _fenced = true;

                    //persist so the node stays fenced across restarts
                    SaveConfigFile();

                    _dnsWebService.LogManager.Write("Cluster: this Primary node is now FENCED. Node '" + remoteNode.ToString() + "' reports that '" + remotePrimaryNodeInfo.Name + "' is the Primary node. This node will stop notifying and serving configuration to Secondary nodes. Delete the Cluster on this node and join it to '" + remotePrimaryNodeInfo.Name + "' as a Secondary node.");
                }
            }
            else if (!_fenced)
            {
                //remote still follows a former primary that this node replaced; tell it about this primary (rate limited)
                lock (_staleNodeNotifiedOn)
                {
                    DateTime utcNow = DateTime.UtcNow;

                    if (_staleNodeNotifiedOn.TryGetValue(remoteNode.Id, out DateTime notifiedOn) && (utcNow < notifiedOn.AddSeconds(STALE_NODE_NOTIFY_INTERVAL_SECONDS)))
                        return;

                    _staleNodeNotifiedOn[remoteNode.Id] = utcNow;
                }

                _dnsWebService.LogManager.Write("Cluster: node '" + remoteNode.ToString() + "' still follows former Primary node '" + remotePrimaryNodeInfo.Name + "'; notifying it of the current Primary node.");

                _ = remoteNode.NotifySecondaryNodeAsync(selfNode);
            }
        }

        private void FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl)
        {
            //try get existing NS record TTL
            IReadOnlyList<DnsResourceRecord> existingNSRecords = _dnsWebService.DnsServer.AuthZoneManager.GetRecords(_clusterDomain, _clusterDomain, DnsResourceRecordType.NS);
            if (existingNSRecords.Count > 0)
            {
                DnsResourceRecord existingNSRecord = existingNSRecords[0];

                nsTtl = existingNSRecord.TTL;

                string nsDomain = (existingNSRecord.RDATA as DnsNSRecordData).NameServer;

                if (nsDomain.EndsWith("." + _clusterDomain, StringComparison.OrdinalIgnoreCase))
                {
                    IReadOnlyList<DnsResourceRecord> existingRecords = _dnsWebService.DnsServer.AuthZoneManager.GetRecords(_clusterDomain, nsDomain, DnsResourceRecordType.A);
                    if (existingRecords.Count == 0)
                        existingRecords = _dnsWebService.DnsServer.AuthZoneManager.GetRecords(_clusterDomain, nsDomain, DnsResourceRecordType.AAAA);

                    if (existingRecords.Count > 0)
                        aTtl = existingRecords[0].TTL;
                    else
                        aTtl = _dnsWebService.DnsServer.AuthZoneManager.DefaultRecordTtl;
                }
                else
                {
                    aTtl = _dnsWebService.DnsServer.AuthZoneManager.DefaultRecordTtl;
                }
            }
            else
            {
                nsTtl = _dnsWebService.DnsServer.AuthZoneManager.DefaultNsRecordTtl;
                aTtl = _dnsWebService.DnsServer.AuthZoneManager.DefaultRecordTtl;
            }
        }

        private void RemoveAllClusterPrimaryZoneNSRecords()
        {
            //remove all existing NS records
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecords(_clusterDomain, _clusterDomain, DnsResourceRecordType.NS);
        }

        private void RemoveClusterPrimaryZoneRecordsFor(ClusterNode node)
        {
            //remove NS record
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecord(_clusterDomain, _clusterDomain, DnsResourceRecordType.NS, new DnsNSRecordData(node.Name));

            //remove A/AAAA records
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecords(_clusterDomain, node.Name, DnsResourceRecordType.A);
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecords(_clusterDomain, node.Name, DnsResourceRecordType.AAAA);

            //remove CNAME records if manually added by user
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecords(_clusterDomain, node.Name, DnsResourceRecordType.CNAME);

            //remove PTR record
            foreach (IPAddress ipAddress in node.IPAddresses)
            {
                string ptrDomain = Zone.GetReverseZone(ipAddress, ipAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

                AuthZoneInfo reverseZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.FindAuthZoneInfo(ptrDomain);
                if (reverseZoneInfo is not null)
                {
                    if ((reverseZoneInfo.Type == AuthZoneType.Primary) || (reverseZoneInfo.Type == AuthZoneType.Forwarder))
                    {
                        _dnsWebService.DnsServer.AuthZoneManager.DeleteRecord(reverseZoneInfo.Name, ptrDomain, DnsResourceRecordType.PTR, new DnsPTRRecordData(node.Name));
                        _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(reverseZoneInfo.Name);
                    }
                }
            }

            //remove TLSA DANE-EE record
            _dnsWebService.DnsServer.AuthZoneManager.DeleteRecords(_clusterDomain, $"_{node.Url.Port}._tcp.{node.Name}", DnsResourceRecordType.TLSA);

            //save zone file
            _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(_clusterDomain);
        }

        private void AddClusterPrimaryZoneRecordsFor(ClusterNode node, uint nsTtl, uint aTtl, X509Certificate2 certificate)
        {
            const string recordComments = "Cluster managed record. Do not update or delete.";

            if (node.Type == ClusterNodeType.Primary)
            {
                //update SOA record
                IReadOnlyList<DnsResourceRecord> existingSoaRecords = _dnsWebService.DnsServer.AuthZoneManager.GetRecords(_clusterDomain, _clusterDomain, DnsResourceRecordType.SOA);
                DnsResourceRecord existingSoaRecord = existingSoaRecords[0];
                DnsSOARecordData existingSoa = existingSoaRecord.RDATA as DnsSOARecordData;

                DnsSOARecordData newSoa = new DnsSOARecordData(node.Name, existingSoa.ResponsiblePerson, existingSoa.Serial, existingSoa.Refresh, existingSoa.Retry, existingSoa.Expire, existingSoa.Minimum);
                DnsResourceRecord newSoaRecord = new DnsResourceRecord(_clusterDomain, DnsResourceRecordType.SOA, DnsClass.IN, existingSoaRecord.TTL, newSoa);

                _dnsWebService.DnsServer.AuthZoneManager.SetRecord(_clusterDomain, newSoaRecord);
            }

            //add NS record
            DnsResourceRecord nsRecord = new DnsResourceRecord(_clusterDomain, DnsResourceRecordType.NS, DnsClass.IN, nsTtl, new DnsNSRecordData(node.Name));

            GenericRecordInfo nsRecordInfo = nsRecord.GetAuthGenericRecordInfo();
            nsRecordInfo.LastModified = DateTime.UtcNow;
            nsRecordInfo.Comments = recordComments;

            _dnsWebService.DnsServer.AuthZoneManager.AddRecord(_clusterDomain, nsRecord);

            //set A/AAAA record
            List<DnsResourceRecord> ipv4AddressRecords = new List<DnsResourceRecord>(node.IPAddresses.Count);
            List<DnsResourceRecord> ipv6AddressRecords = new List<DnsResourceRecord>(node.IPAddresses.Count);

            foreach (IPAddress ipAddress in node.IPAddresses)
            {
                DnsResourceRecord record;

                switch (ipAddress.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        record = new DnsResourceRecord(node.Name, DnsResourceRecordType.A, DnsClass.IN, aTtl, new DnsARecordData(ipAddress));
                        ipv4AddressRecords.Add(record);
                        break;

                    case AddressFamily.InterNetworkV6:
                        record = new DnsResourceRecord(node.Name, DnsResourceRecordType.AAAA, DnsClass.IN, aTtl, new DnsAAAARecordData(ipAddress));
                        ipv6AddressRecords.Add(record);
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                GenericRecordInfo recordInfo = record.GetAuthGenericRecordInfo();
                recordInfo.LastModified = DateTime.UtcNow;
                recordInfo.Comments = recordComments;
            }

            if (ipv4AddressRecords.Count > 0)
                _dnsWebService.DnsServer.AuthZoneManager.SetRecords(_clusterDomain, ipv4AddressRecords);

            if (ipv6AddressRecords.Count > 0)
                _dnsWebService.DnsServer.AuthZoneManager.SetRecords(_clusterDomain, ipv6AddressRecords);

            //set PTR record
            foreach (IPAddress ipAddress in node.IPAddresses)
            {
                string ptrDomain = Zone.GetReverseZone(ipAddress, ipAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

                AuthZoneInfo reverseZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.FindAuthZoneInfo(ptrDomain);
                if (reverseZoneInfo is not null)
                {
                    if ((reverseZoneInfo.Type == AuthZoneType.Primary) || (reverseZoneInfo.Type == AuthZoneType.Forwarder))
                    {
                        DnsResourceRecord ptrRecord = new DnsResourceRecord(ptrDomain, DnsResourceRecordType.PTR, DnsClass.IN, aTtl, new DnsPTRRecordData(node.Name));

                        GenericRecordInfo ptrRecordInfo = ptrRecord.GetAuthGenericRecordInfo();
                        ptrRecordInfo.LastModified = DateTime.UtcNow;
                        ptrRecordInfo.Comments = recordComments;

                        _dnsWebService.DnsServer.AuthZoneManager.SetRecord(reverseZoneInfo.Name, ptrRecord);
                        _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(reverseZoneInfo.Name);
                    }
                }
            }

            //set TLSA DANE-EE record
            DnsResourceRecord tlsaRecord = new DnsResourceRecord($"_{node.Url.Port}._tcp.{node.Name}", DnsResourceRecordType.TLSA, DnsClass.IN, aTtl, new DnsTLSARecordData(DnsTLSACertificateUsage.DANE_EE, DnsTLSASelector.SPKI, DnsTLSAMatchingType.SHA2_256, certificate));

            GenericRecordInfo tlsaRecordInfo = tlsaRecord.GetAuthGenericRecordInfo();
            tlsaRecordInfo.LastModified = DateTime.UtcNow;
            tlsaRecordInfo.Comments = recordComments;

            _dnsWebService.DnsServer.AuthZoneManager.SetRecord(_clusterDomain, tlsaRecord);

            //save zone file
            _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(_clusterDomain);
        }

        public void UpdateClusterRecordsFor(AuthZoneInfo zoneInfo)
        {
            if (zoneInfo.Type != AuthZoneType.Primary)
                throw new InvalidOperationException();

            //set NS records for cluster
            IReadOnlyList<DnsResourceRecord> existingNSRecords = zoneInfo.ApexZone.GetRecords(DnsResourceRecordType.NS);
            uint ttl;

            if (existingNSRecords.Count > 0)
                ttl = existingNSRecords[0].TTL;
            else
                ttl = _dnsWebService.DnsServer.AuthZoneManager.DefaultNsRecordTtl;

            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            DnsResourceRecord[] nsRecords = new DnsResourceRecord[clusterNodes.Count];
            int i = 0;

            foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                nsRecords[i++] = new DnsResourceRecord(zoneInfo.Name, DnsResourceRecordType.NS, DnsClass.IN, ttl, new DnsNSRecordData(clusterNode.Value.Name));

            //set NS record
            _dnsWebService.DnsServer.AuthZoneManager.SetRecords(zoneInfo.Name, nsRecords);

            //ensure correct SOA primary name server
            IReadOnlyList<DnsResourceRecord> existingSoaRecords = zoneInfo.ApexZone.GetRecords(DnsResourceRecordType.SOA);
            if (existingSoaRecords.Count > 0)
            {
                DnsResourceRecord existingSoaRecord = existingSoaRecords[0];
                DnsSOARecordData existingSoa = existingSoaRecord.RDATA as DnsSOARecordData;

                //set SOA record
                _dnsWebService.DnsServer.AuthZoneManager.SetRecords(zoneInfo.Name, [new DnsResourceRecord(zoneInfo.Name, DnsResourceRecordType.SOA, DnsClass.IN, existingSoaRecord.TTL, new DnsSOARecordData(_dnsWebService.DnsServer.ServerDomain, existingSoa.ResponsiblePerson, existingSoa.Serial, existingSoa.Refresh, existingSoa.Retry, existingSoa.Expire, existingSoa.Minimum))]);
            }
        }

        private void TriggerRecordUpdateForClusterCatalogMemberZones()
        {
            int id = RandomNumberGenerator.GetInt32(int.MaxValue);
            _recordUpdateForMemberZonesId = id;

            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                try
                {
                    //get cluster catalog zone info
                    AuthZoneInfo clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo("cluster-catalog." + _clusterDomain);
                    if ((clusterCatalogZoneInfo is null) || (clusterCatalogZoneInfo.Type != AuthZoneType.Catalog))
                        throw new InvalidOperationException();

                    //get all member zone names for cluster catalog zone
                    IReadOnlyCollection<string> memberZoneNames = (clusterCatalogZoneInfo.ApexZone as CatalogZone).GetAllMemberZoneNames();

                    foreach (string memberZoneName in memberZoneNames)
                    {
                        if (_recordUpdateForMemberZonesId != id)
                            return; //stop current update since another update has been triggered

                        //get member zone info
                        AuthZoneInfo memberZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(memberZoneName);
                        if ((memberZoneInfo is null) || (memberZoneInfo.Type != AuthZoneType.Primary))
                            continue; //process is only for primary zones

                        //update NS and SOA records for the member zone
                        UpdateClusterRecordsFor(memberZoneInfo);

                        //save zone file
                        _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(memberZoneName);
                    }

                    _dnsWebService.LogManager.Write("The Cluster Catalog member zones NS and SOA records were successfully updated to reflect the Cluster changes.");
                }
                catch (Exception ex)
                {
                    _dnsWebService.LogManager.Write(ex);
                }
            });
        }

        private void UpdateClusterCatalogZoneOptions()
        {
            string clusterCatalogDomain = "cluster-catalog." + _clusterDomain;

            AuthZoneInfo clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain);
            if (clusterCatalogZoneInfo is null)
                throw new InvalidOperationException();

            UpdateClusterCatalogZoneOptions(clusterCatalogZoneInfo);
        }

        private void UpdateClusterCatalogZoneOptions(AuthZoneInfo clusterCatalogZoneInfo)
        {
            //set cluster catalog zone options for Zone Transfer ACLs and notify addresses
            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;

            List<NetworkAccessControl> zoneTransferACL = new List<NetworkAccessControl>(clusterNodes.Count * 2);
            List<IPAddress> notifyNameServers = new List<IPAddress>(clusterNodes.Count * 2);

            foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
            {
                if (clusterNode.Value.Type == ClusterNodeType.Primary)
                    continue;

                foreach (IPAddress ipAddress in clusterNode.Value.IPAddresses)
                    zoneTransferACL.Add(new NetworkAccessControl(ipAddress, (byte)(ipAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)));

                notifyNameServers.AddRange(clusterNode.Value.IPAddresses);
            }

            clusterCatalogZoneInfo.ZoneTransferNetworkACL = zoneTransferACL;
            clusterCatalogZoneInfo.NotifyNameServers = notifyNameServers;

            clusterCatalogZoneInfo.ZoneTransfer = AuthZoneTransfer.UseSpecifiedNetworkACL;
            clusterCatalogZoneInfo.Notify = AuthZoneNotify.SpecifiedNameServers;

            //set cluster catalog zone options for zone transfer TSIG key names
            IReadOnlySet<string> existingKeyNames = clusterCatalogZoneInfo.ZoneTransferTsigKeyNames;
            if (existingKeyNames is null)
            {
                HashSet<string> updatedKeyNames = [clusterCatalogZoneInfo.Name];

                clusterCatalogZoneInfo.ZoneTransferTsigKeyNames = updatedKeyNames;
            }
            else if (!existingKeyNames.Contains(clusterCatalogZoneInfo.Name))
            {
                HashSet<string> updatedKeyNames = [.. existingKeyNames, clusterCatalogZoneInfo.Name];

                clusterCatalogZoneInfo.ZoneTransferTsigKeyNames = updatedKeyNames;
            }

            _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(clusterCatalogZoneInfo.Name);
        }

        public void TriggerNotifyAllSecondaryNodesIfPrimarySelfNode()
        {
            if (GetSelfNode().Type == ClusterNodeType.Primary)
                TriggerNotifyAllSecondaryNodes();
        }

        public void TriggerNotifyAllSecondaryNodes(int notifyInterval = NOTIFY_ALL_SECONDARY_NODES_TIMER_INTERVAL)
        {
            //record change time so secondaries that miss the notify still detect it on their next heartbeat
            _configLastModified = DateTime.UtcNow;

            if (_notifyAllSecondaryNodesTimerTriggered)
                return;

            _notifyAllSecondaryNodesTimer.Change(notifyInterval, Timeout.Infinite);
            _notifyAllSecondaryNodesTimerTriggered = true;
        }

        private async void NotifyAllSecondaryNodesTimerCallbackAsync(object state)
        {
            _notifyAllSecondaryNodesTimerTriggered = false; //reset immediately to allow triggerring notify again since the current notify task may take longer to complete due to one or more nodes being down or not responding fast enough causing lost notify for working nodes

            try
            {
                IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
                if ((clusterNodes is null) || (clusterNodes.Count < 1))
                    return; //cluster not initialized

                ClusterNode primaryNode = null;

                foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                {
                    if (clusterNode.Value.Type == ClusterNodeType.Primary)
                    {
                        primaryNode = clusterNode.Value;
                        break;
                    }
                }

                if ((primaryNode is null) || (primaryNode.State != ClusterNodeState.Self))
                    throw new InvalidOperationException();

                if (_fenced)
                {
                    _dnsWebService.LogManager.Write("Cluster notify was skipped: this Primary node is fenced since node '" + _fencedByNodeName + "' has taken over as the Primary node.");
                    return;
                }

                List<Task> tasks = new List<Task>(clusterNodes.Count);

                foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
                {
                    if (clusterNode.Value.Type == ClusterNodeType.Primary)
                        continue; //skip primary cluster node

                    tasks.Add(clusterNode.Value.NotifySecondaryNodeAsync(primaryNode));
                }

                await Task.WhenAll(tasks); //notify node call does error logging
            }
            catch (Exception ex)
            {
                _dnsWebService.LogManager.Write(ex);
            }
        }

        #endregion

        #region secondary node

        public async Task InitializeAndJoinClusterAsync(IReadOnlyList<IPAddress> secondaryNodeIpAddresses, Uri primaryNodeUrl, string primaryNodeUsername, string primaryNodePassword, string primaryNodeTotp = null, IReadOnlyList<IPAddress> primaryNodeIpAddresses = null, bool ignoreCertificateErrors = false, CancellationToken cancellationToken = default)
        {
            if (ClusterInitialized)
                throw new DnsServerException("Failed to join Cluster: the Cluster is already initialized.");

            if (!_dnsWebService.IsWebServiceTlsEnabled)
                throw new InvalidOperationException();

            if (IPAddress.TryParse(primaryNodeUrl.Host, out _))
                throw new DnsServerException("Failed to join Cluster: the Primary Node URL must use the domain name of the Primary node and not its IP address.");

            if (primaryNodeIpAddresses is null)
            {
                try
                {
                    IReadOnlyList<IPAddress> ipAddresses = await DnsClient.ResolveIPAsync(_dnsWebService.DnsServer, primaryNodeUrl.Host, _dnsWebService.DnsServer.IPv6Mode, cancellationToken);
                    if (ipAddresses.Count < 1)
                        throw new DnsServerException($"Failed to join Cluster: the domain name '{primaryNodeUrl.Host}' does not have an A/AAAA record configured.");

                    primaryNodeIpAddresses = ipAddresses;
                }
                catch (Exception ex)
                {
                    throw new DnsServerException($"Failed to join Cluster: the Primary node domain name '{primaryNodeUrl.Host}' could not be resolved to an IP address. Please specify the Primary node IP address manually.", ex);
                }
            }

            //login to primary node API
            using HttpApiClient primaryNodeApiClient = new HttpApiClient(primaryNodeUrl, _dnsWebService.DnsServer.Proxy, _dnsWebService.DnsServer.IPv6Mode, ignoreCertificateErrors, new InternalDnsClient(_dnsWebService.DnsServer, primaryNodeIpAddresses));

            try
            {
                _ = await primaryNodeApiClient.LoginAsync(primaryNodeUsername, primaryNodePassword, primaryNodeTotp, false, cancellationToken);
            }
            catch (TwoFactorAuthRequiredHttpApiClientException ex)
            {
                throw new TwoFactorAuthRequiredWebServiceException("Failed to join Cluster: two-factor authentication is required by the Primary node user account.", ex);
            }

            try
            {
                //get cluster info
                ClusterInfo primaryNodeClusterInfo = await primaryNodeApiClient.GetClusterStateAsync(cancellationToken: cancellationToken);

                //do validations
                if (!primaryNodeClusterInfo.ClusterInitialized)
                    throw new DnsServerException("Failed to join Cluster: the Primary node does not have a Cluster initialized.");

                string clusterCatalogDomain = "cluster-catalog." + primaryNodeClusterInfo.ClusterDomain;

                if (_dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain) is not null)
                    throw new DnsServerException($"Failed to join Cluster: the zone '{clusterCatalogDomain}' already exists. Please delete the '{clusterCatalogDomain}' zone and try again.");

                if (_dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(primaryNodeClusterInfo.ClusterDomain) is not null)
                    throw new DnsServerException($"Failed to join Cluster: the zone '{primaryNodeClusterInfo.ClusterDomain}' already exists. Please delete the '{primaryNodeClusterInfo.ClusterDomain}' zone and try again.");

                //create self node
                string serverDomain = _dnsWebService.DnsServer.ServerDomain;
                if (!serverDomain.EndsWith("." + primaryNodeClusterInfo.ClusterDomain, StringComparison.OrdinalIgnoreCase))
                {
                    int x = serverDomain.IndexOf('.');
                    if (x < 0)
                        serverDomain = serverDomain + "." + primaryNodeClusterInfo.ClusterDomain;
                    else
                        serverDomain = string.Concat(serverDomain.AsSpan(0, x), ".", primaryNodeClusterInfo.ClusterDomain);
                }

                Uri secondaryNodeUrl = new Uri($"https://{serverDomain}:{_dnsWebService.WebServiceTlsPort}/");

                ClusterNode selfSecondaryNode = new ClusterNode(this, RandomNumberGenerator.GetInt32(int.MaxValue), secondaryNodeUrl, secondaryNodeIpAddresses, ClusterNodeType.Secondary, ClusterNodeState.Self);

                //join cluster
                primaryNodeClusterInfo = await primaryNodeApiClient.JoinClusterAsync(selfSecondaryNode.Id, secondaryNodeUrl, secondaryNodeIpAddresses, _dnsWebService.WebServiceTlsCertificate, cancellationToken);

                //initialize cluster
                Dictionary<int, ClusterNode> clusterNodes = new Dictionary<int, ClusterNode>(primaryNodeClusterInfo.ClusterNodes.Count + 1);

                clusterNodes[selfSecondaryNode.Id] = selfSecondaryNode;

                foreach (ClusterInfo.ClusterNodeInfo nodeInfo in primaryNodeClusterInfo.ClusterNodes)
                {
                    if (nodeInfo.Id == selfSecondaryNode.Id)
                        continue; //skip self node

                    ClusterNode node = new ClusterNode(this, nodeInfo);
                    clusterNodes[node.Id] = node;
                }

                DisposeAllNodes(); //dispose existing nodes, if any

                _clusterNodes = clusterNodes;

                _clusterDomain = primaryNodeClusterInfo.ClusterDomain;
                _heartbeatRefreshIntervalSeconds = primaryNodeClusterInfo.HeartbeatRefreshIntervalSeconds;
                _heartbeatRetryIntervalSeconds = primaryNodeClusterInfo.HeartbeatRetryIntervalSeconds;
                _configRefreshIntervalSeconds = primaryNodeClusterInfo.ConfigRefreshIntervalSeconds;
                _configRetryIntervalSeconds = primaryNodeClusterInfo.ConfigRetryIntervalSeconds;

                try
                {
                    //sync entire config from primary node first to get TSIG keys for secondary catalog zone transfer
                    _configLastSynced = DateTime.UnixEpoch; //reset last sync time to ensure full sync
                    await SyncConfigFromAsync(primaryNodeApiClient, cancellationToken: cancellationToken);

                    //create cluster secondary catalog zone
                    AuthZoneInfo clusterSecondaryCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.CreateSecondaryCatalogZone(clusterCatalogDomain, primaryNodeIpAddresses.Convert(delegate (IPAddress ipAddress) { return new NameServerAddress(primaryNodeUrl.Host, ipAddress); }), DnsTransportProtocol.Tcp, clusterCatalogDomain);
                    if (clusterSecondaryCatalogZoneInfo is null)
                        throw new DnsServerException($"Failed to join Cluster: the zone '{clusterCatalogDomain}' already exists. Please delete the '{clusterCatalogDomain}' zone and try again.");

                    //set cluster secondary catalog zone permissions
                    _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterSecondaryCatalogZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.ADMINISTRATORS), PermissionFlag.ViewModifyDelete);
                    _dnsWebService.AuthManager.SetPermission(PermissionSection.Zones, clusterSecondaryCatalogZoneInfo.Name, _dnsWebService.AuthManager.GetGroup(Group.DNS_ADMINISTRATORS), PermissionFlag.View);
                }
                catch
                {
                    try
                    {
                        await primaryNodeApiClient.DeleteSecondaryNodeAsync(selfSecondaryNode.Id, cancellationToken);
                    }
                    catch
                    { }

                    try
                    {
                        DeleteAllClusterConfig();
                    }
                    catch (Exception ex)
                    {
                        _dnsWebService.LogManager.Write(ex);
                    }

                    throw;
                }

                //initialize heartbeat timer for all nodes here since config sync and zone transfers needs to occur first for DANE validation to work
                InitializeHeartbeatTimerFor(clusterNodes);

                //start config refresh timer to refresh as per config refresh interval as config was just synced
                UpdateConfigRefreshTimer(_configRefreshIntervalSeconds * 1000);

                //finalize
                _dnsWebService.DnsServer.ServerDomain = selfSecondaryNode.Name;

                //save all changes
                _dnsWebService.DnsServer.SaveConfigFile();
                _dnsWebService.AuthManager.SaveConfigFile();
                SaveConfigFile();
            }
            finally
            {
                try
                {
                    //logout from primary node
                    await primaryNodeApiClient.LogoutAsync(cancellationToken);
                }
                catch
                { }
            }
        }

        public async Task LeaveClusterAsync(bool forceLeave)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to leave Cluster: the Cluster is not initialized.");

            ClusterNode primaryNode = GetPrimaryNode();

            if (primaryNode.State == ClusterNodeState.Self)
                throw new DnsServerException("Failed to leave Cluster: a Primary self node cannot leave the Cluster.");

            ClusterNode secondaryNode = GetSelfNode();

            if (secondaryNode.Type != ClusterNodeType.Secondary)
                throw new DnsServerException("Failed to leave Cluster: only Secondary nodes can leave the Cluster.");

            if (!forceLeave)
            {
                //delete self node from cluster on primary node
                await primaryNode.DeleteSecondaryNodeAsync(secondaryNode);
            }

            //delete all cluster config
            DeleteAllClusterConfig();
        }

        public async Task<ClusterNode> UpdatePrimaryNodeAsync(Uri primaryNodeUrl, IReadOnlyList<IPAddress> primaryNodeIpAddresses = null, int primaryNodeId = -1, CancellationToken cancellationToken = default)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to update Primary node: the Cluster is not initialized.");

            if (primaryNodeIpAddresses is null)
            {
                try
                {
                    IReadOnlyList<IPAddress> ipAddresses = await DnsClient.ResolveIPAsync(_dnsWebService.DnsServer, primaryNodeUrl.Host, _dnsWebService.DnsServer.IPv6Mode, cancellationToken);
                    if (ipAddresses.Count < 1)
                        throw new DnsServerException($"The domain name '{primaryNodeUrl.Host}' does not have an A/AAAA record configured.");

                    primaryNodeIpAddresses = ipAddresses;
                }
                catch (Exception ex)
                {
                    throw new DnsServerException($"Failed to update Primary node: the Primary node domain name '{primaryNodeUrl.Host}' could not be resolved to an IP address.", ex);
                }
            }

            ClusterNode primaryNode;

            if (primaryNodeId < 0)
                primaryNode = GetPrimaryNode();
            else if (!_clusterNodes.TryGetValue(primaryNodeId, out primaryNode))
                throw new DnsServerException("Failed to update Primary node: the specified Primary node ID does not exists in the Cluster.");

            if (primaryNode.State == ClusterNodeState.Self)
                throw new DnsServerException("Failed to update Primary node: the specified node is the self node and cannot be updated this way.");

            if (primaryNode.Type == ClusterNodeType.Secondary)
            {
                //secondary node was promoted to primary node
                ClusterNode formerPrimaryNode = GetPrimaryNode();

                //dispose former primary node immediately to stop heartbeat
                formerPrimaryNode.Dispose();

                //remove former primary node from cluster nodes
                IReadOnlyDictionary<int, ClusterNode> existingClusterNodes = _clusterNodes;
                Dictionary<int, ClusterNode> updatedClusterNodes = new Dictionary<int, ClusterNode>(existingClusterNodes.Count - 1);

                foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
                {
                    if (existingClusterNode.Key == formerPrimaryNode.Id)
                        continue;

                    updatedClusterNodes[existingClusterNode.Key] = existingClusterNode.Value;
                }

                //update cluster nodes
                _clusterNodes = updatedClusterNodes;

                //promote secondary node to primary immediately
                primaryNode.PromoteToPrimaryNode();

                //ensure to save changes
                SaveConfigFile();
            }

            //validate for duplicate names
            foreach (KeyValuePair<int, ClusterNode> clusterNode in _clusterNodes)
            {
                if (clusterNode.Key == primaryNode.Id)
                    continue; //skip self

                if (clusterNode.Value.Name.Equals(primaryNodeUrl.Host, StringComparison.OrdinalIgnoreCase))
                    throw new DnsServerException("Failed to update Primary node: the Primary node's domain name already exists in the Cluster. Please try again after changing the Primary DNS Server's domain name.");
            }

            //get cluster secondary catalog zone
            string clusterCatalogDomain = "cluster-catalog." + _clusterDomain;

            AuthZoneInfo clusterSecondaryCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain);
            if (clusterSecondaryCatalogZoneInfo is null)
                throw new DnsServerException($"Failed to update Primary node: the Cluster Secondary Catalog zone '{clusterCatalogDomain}' does not exists.");

            //update primary node
            primaryNode.UpdateNode(primaryNodeUrl, primaryNodeIpAddresses);

            //update cluster catalog zone's primary name server
            clusterSecondaryCatalogZoneInfo.PrimaryNameServerAddresses = primaryNodeIpAddresses.Convert(delegate (IPAddress ipAddress)
            {
                int port = 53;

                if (clusterSecondaryCatalogZoneInfo.PrimaryNameServerAddresses is not null)
                {
                    foreach (NameServerAddress primaryNameServerAddress in clusterSecondaryCatalogZoneInfo.PrimaryNameServerAddresses)
                    {
                        if ((primaryNameServerAddress.IPEndPoint is not null) && primaryNameServerAddress.IPEndPoint.Address.Equals(ipAddress))
                        {
                            port = primaryNameServerAddress.Port;
                            break;
                        }
                    }
                }

                return new NameServerAddress(primaryNodeUrl.Host, new IPEndPoint(ipAddress, port));
            });

            //save all changes
            _dnsWebService.DnsServer.AuthZoneManager.SaveZoneFile(clusterSecondaryCatalogZoneInfo.Name);
            SaveConfigFile();

            //trigger config and zone refresh
            _ = InitializeConfigRefreshAsync(CONFIG_REFRESH_TIMER_INTERVAL);

            return primaryNode;
        }

        public void CheckConfigFreshnessFromPrimaryNode(ClusterInfo primaryNodeClusterInfo)
        {
            if (!ClusterInitialized || (GetSelfNode().Type != ClusterNodeType.Secondary))
                return;

            if (primaryNodeClusterInfo.ConfigLastModified is null)
                return; //primary node does not support this

            //last synced time comes from an HTTP date header with seconds precision so compare at that precision;
            //a change in the same second as the last sync triggers one extra sync, which is harmless
            DateTime configLastModified = primaryNodeClusterInfo.ConfigLastModified.Value.ToUniversalTime();
            configLastModified = configLastModified.AddTicks(-(configLastModified.Ticks % TimeSpan.TicksPerSecond));

            if (configLastModified >= _configLastSynced)
                _ = InitializeConfigRefreshAsync(CONFIG_REFRESH_TIMER_INTERVAL);
        }

        public void TriggerRefreshForConfig(IReadOnlyCollection<string> configRefreshIncludeZones = null)
        {
            //do validation
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to refresh configuration: the Cluster is not initialized.");

            ClusterNode primaryNode = GetPrimaryNode();

            if (primaryNode.State == ClusterNodeState.Self)
                throw new DnsServerException("Failed to refresh configuration: only Secondary nodes can sync configuration from Primary nodes.");

            _ = InitializeConfigRefreshAsync(CONFIG_REFRESH_TIMER_INTERVAL, configRefreshIncludeZones);
        }

        public void TriggerResyncForConfig()
        {
            //do validation
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to resync configuration: the Cluster is not initialized.");

            ClusterNode primaryNode = GetPrimaryNode();

            if (primaryNode.State == ClusterNodeState.Self)
                throw new DnsServerException("Failed to resync configuration: only Secondary nodes can sync configuration from Primary nodes.");

            _configLastSynced = DateTime.UnixEpoch; //to ensure complete config resync

            //trigger immediate config refresh
            _ = InitializeConfigRefreshAsync(0);
        }

        private async Task InitializeConfigRefreshAsync(int refreshInterval, IReadOnlyCollection<string> configRefreshIncludeZones = null)
        {
            await _configRefreshLock.WaitAsync();
            try
            {
                if (configRefreshIncludeZones is not null)
                {
                    if (_configRefreshIncludeZones is null)
                        _configRefreshIncludeZones = configRefreshIncludeZones;
                    else
                        _configRefreshIncludeZones = [.. _configRefreshIncludeZones, .. configRefreshIncludeZones];
                }

                if (_configRefreshTimerTriggered)
                    return;

                _configRefreshTimer.Change(refreshInterval, Timeout.Infinite);
                _configRefreshTimerTriggered = true;
            }
            finally
            {
                _configRefreshLock.Release();
            }
        }

        private async void ConfigRefreshTimerCallbackAsync(object state)
        {
            bool success = false;

            await _configRefreshLock.WaitAsync();
            try
            {
                ClusterNode primaryNode = GetPrimaryNode();

                if (primaryNode.State == ClusterNodeState.Self)
                    throw new InvalidOperationException();

                //update cluster options
                UpdateClusterFromPrimaryNode(await primaryNode.GetClusterStateAsync());

                //sync config from primary node
                await primaryNode.SyncConfigAsync(_configRefreshIncludeZones);

                success = true;
            }
            catch (Exception ex)
            {
                _dnsWebService.LogManager.Write("Failed to sync server configuration from the Primary node.", ex);
            }
            finally
            {
                if (success)
                {
                    _configRefreshTimerTriggered = false;
                    _configRefreshIncludeZones = null;
                }

                try
                {
                    //do not re-arm once this node is the primary (e.g. promoted while this callback was queued)
                    if (ClusterInitialized && (GetSelfNode().Type == ClusterNodeType.Primary))
                    {
                        _configRefreshTimerTriggered = false;
                        _configRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    else
                    {
                        _configRefreshTimer.Change(success ? _configRefreshIntervalSeconds * 1000 : _configRetryIntervalSeconds * 1000, Timeout.Infinite);
                    }
                }
                catch (ObjectDisposedException)
                { }

                try
                {
                    _configRefreshLock.Release();
                }
                catch (ObjectDisposedException)
                { }
            }
        }

        public async Task SyncConfigFromAsync(HttpApiClient primaryNodeApiClient, IReadOnlyCollection<string> includeZones = null, CancellationToken cancellationToken = default)
        {
            string tmpFile = Path.GetTempFileName();
            try
            {
                await using (FileStream configZipStream = new FileStream(tmpFile, FileMode.Create, FileAccess.ReadWrite))
                {
                    //get config from primary node
                    (Stream, DateTime) response = await primaryNodeApiClient.TransferConfigFromPrimaryNodeAsync(_configLastSynced, includeZones, cancellationToken);

                    await using (Stream stream = response.Item1)
                    {
                        //copy stream with idle timeout
                        await stream.CopyToAsync(configZipStream, TimeSpan.FromSeconds(60), cancellationToken);
                    }

                    //dynamically load config
                    configZipStream.Position = 0;

                    await _dnsWebService.RestoreConfigAsync(zipStream: configZipStream,
                                                            authConfig: true,
                                                            clusterConfig: false,
                                                            webServiceSettings: false,
                                                            dnsSettings: true,
                                                            logSettings: false,
                                                            zones: true,
                                                            allowedZones: true,
                                                            blockedZones: true,
                                                            blockLists: true,
                                                            apps: true,
                                                            scopes: false,
                                                            stats: false,
                                                            logs: false,
                                                            deleteExistingFiles: false,
                                                            isConfigTransfer: true);

                    _configLastSynced = response.Item2;

                    //save config
                    SaveConfigFile();
                }

                _dnsWebService.LogManager.Write("Server configuration was synced from the Primary node successfully.");
            }
            finally
            {
                try
                {
                    File.Delete(tmpFile);
                }
                catch (Exception ex)
                {
                    _dnsWebService.LogManager.Write(ex);
                }
            }
        }

        private void TriggerClusterUpdateForSecondaryNodeChanges()
        {
            if (_clusterUpdateForSecondaryNodeChangesTimerTriggered)
                return;

            _clusterUpdateForSecondaryNodeChangesTimer.Change(CLUSTER_UPDATE_FOR_SECONDARY_NODE_CHANGES_TIMER_INTERVAL, Timeout.Infinite);
            _clusterUpdateForSecondaryNodeChangesTimerTriggered = true;
        }

        private async void ClusterUpdateForSecondaryNodeChangesTimerCallbackAsync(object state)
        {
            try
            {
                ClusterNode primaryNode = GetPrimaryNode();

                if (primaryNode.State == ClusterNodeState.Self)
                    throw new InvalidOperationException();

                ClusterNode secondaryNode = GetSelfNode();

                if (secondaryNode.Type != ClusterNodeType.Secondary)
                    throw new InvalidOperationException();

                UpdateClusterFromPrimaryNode(await primaryNode.UpdateSecondaryNodeAsync(secondaryNode, _dnsWebService.WebServiceTlsCertificate));

                _dnsWebService.LogManager.Write("DNS Server updated this Secondary node's details on the Primary node successfully.");
            }
            catch (Exception ex)
            {
                _dnsWebService.LogManager.Write("DNS Server failed to update this Secondary node's details on the Primary node.", ex);
            }
            finally
            {
                _clusterUpdateForSecondaryNodeChangesTimerTriggered = false;
            }
        }

        public void UpdateClusterFromPrimaryNode(ClusterInfo primaryNodeClusterInfo)
        {
            IReadOnlyDictionary<int, ClusterNode> existingClusterNodes = _clusterNodes;

            //follow the primary's term so a promotion of this node always gets a newer term than the primary it replaces
            uint primaryTerm = primaryNodeClusterInfo.PrimaryTerm ?? 0;
            if (primaryTerm > _primaryTerm)
            {
                _primaryTerm = primaryTerm;
                SaveConfigFile();
            }

            //validation
            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
            {
                if (existingClusterNode.Value.Type == ClusterNodeType.Primary)
                {
                    if (existingClusterNode.Value.State == ClusterNodeState.Self)
                        throw new InvalidOperationException(); //this is a self primary node itself

                    break;
                }
            }

            List<ClusterNode> clusterNodesToAdd = new List<ClusterNode>();
            List<ClusterNode> clusterNodesToRemove = new List<ClusterNode>();

            foreach (ClusterInfo.ClusterNodeInfo clusterNodeInfo in primaryNodeClusterInfo.ClusterNodes)
            {
                if (existingClusterNodes.TryGetValue(clusterNodeInfo.Id, out ClusterNode existingClusterNode))
                {
                    if (existingClusterNode.State == ClusterNodeState.Self)
                        continue; //skip self node

                    //update existing cluster node
                    existingClusterNode.UpdateNode(clusterNodeInfo);
                }
                else
                {
                    //add new cluster node
                    clusterNodesToAdd.Add(new ClusterNode(this, clusterNodeInfo));
                }
            }

            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
            {
                bool found = false;

                foreach (ClusterInfo.ClusterNodeInfo clusterNodeInfo in primaryNodeClusterInfo.ClusterNodes)
                {
                    if (existingClusterNode.Key == clusterNodeInfo.Id)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    clusterNodesToRemove.Add(existingClusterNode.Value);
            }

            bool saveConfig = false;

            if ((clusterNodesToAdd.Count > 0) || (clusterNodesToRemove.Count > 0))
            {
                Dictionary<int, ClusterNode> updatedClusterNodes = new Dictionary<int, ClusterNode>(existingClusterNodes.Count + clusterNodesToAdd.Count - clusterNodesToRemove.Count);

                foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
                {
                    if (clusterNodesToRemove.Contains(existingClusterNode.Value))
                        continue; //skip removed node

                    updatedClusterNodes[existingClusterNode.Key] = existingClusterNode.Value;
                }

                foreach (ClusterNode clusterNode in clusterNodesToAdd)
                    updatedClusterNodes[clusterNode.Id] = clusterNode;

                //verify if node is part of the cluster
                {
                    bool foundSelfNode = false;

                    foreach (KeyValuePair<int, ClusterNode> clusterNode in updatedClusterNodes)
                    {
                        if (clusterNode.Value.State == ClusterNodeState.Self)
                        {
                            foundSelfNode = true;
                            break;
                        }
                    }

                    if (!foundSelfNode)
                    {
                        //this node is not part of the cluster anymore
                        //delete all cluster config
                        DeleteAllClusterConfig();

                        _dnsWebService.LogManager.Write("Failed to sync Cluster config: this Secondary node is not part of the Cluster anymore.");
                        return;
                    }
                }

                //dispose all removed nodes
                foreach (ClusterNode removedNodes in clusterNodesToRemove)
                    removedNodes.Dispose();

                _clusterNodes = updatedClusterNodes;

                InitializeHeartbeatTimerFor(updatedClusterNodes);
                saveConfig = true;
            }

            if (primaryNodeClusterInfo.HeartbeatRefreshIntervalSeconds != _heartbeatRefreshIntervalSeconds)
            {
                _heartbeatRefreshIntervalSeconds = primaryNodeClusterInfo.HeartbeatRefreshIntervalSeconds;
                UpdateHeartbeatTimerForAllClusterNodes(); //apply new interval to all cluster nodes immediately
                saveConfig = true;
            }

            if (primaryNodeClusterInfo.HeartbeatRetryIntervalSeconds != _heartbeatRetryIntervalSeconds)
            {
                _heartbeatRetryIntervalSeconds = primaryNodeClusterInfo.HeartbeatRetryIntervalSeconds;
                saveConfig = true;
            }

            if (primaryNodeClusterInfo.ConfigRefreshIntervalSeconds != _configRefreshIntervalSeconds)
            {
                _configRefreshIntervalSeconds = primaryNodeClusterInfo.ConfigRefreshIntervalSeconds;
                UpdateConfigRefreshTimer(_configRefreshIntervalSeconds * 1000); //apply new interval to config refresh timer immediately
                saveConfig = true;
            }

            if (primaryNodeClusterInfo.ConfigRetryIntervalSeconds != _configRetryIntervalSeconds)
            {
                _configRetryIntervalSeconds = primaryNodeClusterInfo.ConfigRetryIntervalSeconds;
                saveConfig = true;
            }

            //auto failover options; a primary without support reports disabled
            if ((primaryNodeClusterInfo.AutoFailoverEnabled != _autoFailoverEnabled) || !string.Equals(primaryNodeClusterInfo.AutoFailoverSuccessorNodeName, _autoFailoverSuccessorNodeName, StringComparison.OrdinalIgnoreCase) || ((primaryNodeClusterInfo.AutoFailoverTimeoutSeconds > 0) && (primaryNodeClusterInfo.AutoFailoverTimeoutSeconds != _autoFailoverTimeoutSeconds)))
            {
                _autoFailoverEnabled = primaryNodeClusterInfo.AutoFailoverEnabled;
                _autoFailoverSuccessorNodeName = primaryNodeClusterInfo.AutoFailoverSuccessorNodeName;

                if (primaryNodeClusterInfo.AutoFailoverTimeoutSeconds > 0)
                    _autoFailoverTimeoutSeconds = primaryNodeClusterInfo.AutoFailoverTimeoutSeconds;

                saveConfig = true;
            }

            {
                IReadOnlyList<IPAddress> witnessAddresses = null;

                if ((primaryNodeClusterInfo.AutoFailoverWitnessAddresses is not null) && (primaryNodeClusterInfo.AutoFailoverWitnessAddresses.Length > 0))
                    witnessAddresses = primaryNodeClusterInfo.AutoFailoverWitnessAddresses.Convert(IPAddress.Parse);

                bool witnessesSame = (witnessAddresses is null) ? (_autoFailoverWitnessAddresses is null) : ((_autoFailoverWitnessAddresses is not null) && witnessAddresses.HasSameItems(_autoFailoverWitnessAddresses));
                if (!witnessesSame)
                {
                    _autoFailoverWitnessAddresses = witnessAddresses;
                    saveConfig = true;
                }
            }

            if (primaryNodeClusterInfo.AutoFailoverPauseRemainingSeconds is not null)
            {
                //remaining seconds instead of an absolute time avoids clock skew between nodes
                int remaining = primaryNodeClusterInfo.AutoFailoverPauseRemainingSeconds.Value;
                DateTime pausedUntil = remaining > 0 ? DateTime.UtcNow.AddSeconds(remaining) : default;

                bool wasPaused = IsAutoFailoverPaused;
                bool isPaused = remaining > 0;

                if ((wasPaused != isPaused) || (isPaused && (Math.Abs((pausedUntil - _autoFailoverPausedUntil).TotalSeconds) > 30)))
                {
                    _autoFailoverPausedUntil = pausedUntil;
                    saveConfig = true;

                    if (isPaused)
                        _dnsWebService.LogManager.Write("Cluster auto failover was paused by the Primary node for " + remaining + " second(s).");
                    else
                        _dnsWebService.LogManager.Write("Cluster auto failover was resumed by the Primary node.");
                }
            }

            //save changes
            if (saveConfig)
                SaveConfigFile();
        }

        public async Task PromoteToPrimaryNodeAsync(bool forceDeletePrimary)
        {
            if (!ClusterInitialized)
                throw new DnsServerException("Failed to promote to Primary node: the Cluster is not initialized.");

            //do validation
            ClusterNode selfNewPrimaryNode = GetSelfNode();
            if (selfNewPrimaryNode.Type != ClusterNodeType.Secondary)
                throw new DnsServerException("Failed to promote to Primary node: only Secondary nodes can be promoted to Primary nodes.");

            string clusterCatalogDomain = "cluster-catalog." + _clusterDomain;

            AuthZoneInfo clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(clusterCatalogDomain);
            if (clusterCatalogZoneInfo is null)
                throw new DnsServerException("Failed to promote to Primary node: the Cluster Secondary Catalog zone does not exist.");

            AuthZoneInfo clusterZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(_clusterDomain);
            if (clusterZoneInfo is null)
                throw new DnsServerException("Failed to promote to Primary node: the Cluster Secondary zone does not exist.");

            //stop cluster config refresh timer
            StopConfigRefreshTimer();

            //resync config and delete current primary node from the cluster immediately
            ClusterNode existingPrimaryNode = GetPrimaryNode();

            if (!forceDeletePrimary)
            {
                //resync complete config from current primary node to ensure all data is synced
                _configLastSynced = DateTime.UnixEpoch; //to ensure complete config resync
                await existingPrimaryNode.SyncConfigAsync();

                //delete current cluster primary node
                await existingPrimaryNode.DeleteClusterAsync(true);
            }

            //dispose primary node immediately to stop heartbeat
            existingPrimaryNode.Dispose();

            //remove primary node from cluster nodes
            IReadOnlyDictionary<int, ClusterNode> existingClusterNodes = _clusterNodes;
            Dictionary<int, ClusterNode> updatedClusterNodes = new Dictionary<int, ClusterNode>(existingClusterNodes.Count - 1);

            foreach (KeyValuePair<int, ClusterNode> existingClusterNode in existingClusterNodes)
            {
                if (existingClusterNode.Key == existingPrimaryNode.Id)
                    continue;

                updatedClusterNodes[existingClusterNode.Key] = existingClusterNode.Value;
            }

            //update cluster nodes
            _clusterNodes = updatedClusterNodes;

            //promote self node to primary immediately
            selfNewPrimaryNode.PromoteToPrimaryNode();

            //a new term lets the former primary recognize this promotion as newer than its own when it comes back
            _primaryTerm++;

            //convert cluster secondary catalog zone to catalog zone along with all its member zones
            if (clusterCatalogZoneInfo.Type == AuthZoneType.SecondaryCatalog)
                clusterCatalogZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.ConvertZoneTypeTo(clusterCatalogZoneInfo.Name, AuthZoneType.Catalog);

            //get converted primary cluster zone info
            clusterZoneInfo = _dnsWebService.DnsServer.AuthZoneManager.GetAuthZoneInfo(_clusterDomain);
            if (clusterZoneInfo is null)
                throw new DnsServerException("Failed to promote to Primary node: the Cluster Primary zone does not exist.");

            //sign cluster zone in case when DNSSEC private keys were not available during ConvertZoneTypeTo() operation
            if (clusterZoneInfo.ApexZone.DnssecStatus == AuthZoneDnssecStatus.Unsigned)
            {
                DnssecPrivateKey kskPrivateKey = DnssecPrivateKey.Create(DnssecAlgorithm.ECDSAP256SHA256, DnssecPrivateKeyType.KeySigningKey);
                DnssecPrivateKey zskPrivateKey = DnssecPrivateKey.Create(DnssecAlgorithm.ECDSAP256SHA256, DnssecPrivateKeyType.ZoneSigningKey);
                zskPrivateKey.RolloverDays = 90;

                _dnsWebService.DnsServer.AuthZoneManager.SignPrimaryZone(clusterZoneInfo.Name, kskPrivateKey, zskPrivateKey, 3600, false);
            }

            //find existing record TTL values
            FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl);

            //remove old primary node records from cluster primary zone and save zone file
            if (existingPrimaryNode is not null)
                RemoveClusterPrimaryZoneRecordsFor(existingPrimaryNode);

            //update cluster primary zone for new primary node
            AddClusterPrimaryZoneRecordsFor(selfNewPrimaryNode, nsTtl, aTtl, _dnsWebService.WebServiceTlsCertificate);

            //update cluster catalog zone ACLs, TSIG key name and save zone file
            UpdateClusterCatalogZoneOptions(clusterCatalogZoneInfo);

            //save all changes immediately before sending notify
            SaveConfigFile(true);

            //notify all secondary nodes as a primary node immediately
            TriggerNotifyAllSecondaryNodes(0);

            //trigger NS and SOA update for member zones
            TriggerRecordUpdateForClusterCatalogMemberZones();
        }

        #endregion

        #region public

        public ClusterNode GetPrimaryNode()
        {
            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            if (clusterNodes is null)
                throw new InvalidOperationException();

            foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
            {
                if (clusterNode.Value.Type == ClusterNodeType.Primary)
                    return clusterNode.Value;
            }

            throw new InvalidOperationException();
        }

        public ClusterNode GetSelfNode()
        {
            IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;
            if (clusterNodes is null)
                throw new InvalidOperationException();

            foreach (KeyValuePair<int, ClusterNode> clusterNode in clusterNodes)
            {
                if (clusterNode.Value.State == ClusterNodeState.Self)
                    return clusterNode.Value;
            }

            throw new InvalidOperationException();
        }

        public bool TryGetClusterNode(string nodeName, out ClusterNode clusterNode)
        {
            foreach (KeyValuePair<int, ClusterNode> node in _clusterNodes)
            {
                if (node.Value.Name.Equals(nodeName, StringComparison.OrdinalIgnoreCase))
                {
                    clusterNode = node.Value;
                    return true;
                }
            }

            clusterNode = null;
            return false;
        }

        public bool IsClusterPrimaryZone(string zoneName)
        {
            return (zoneName is not null) && zoneName.Equals(_clusterDomain, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsClusterCatalogZone(string zoneName)
        {
            return (zoneName is not null) && zoneName.Equals("cluster-catalog." + _clusterDomain, StringComparison.OrdinalIgnoreCase);
        }

        public ClusterNode UpdateSelfNodeIPAddresses(IReadOnlyList<IPAddress> ipAddresses)
        {
            ClusterNode selfNode = GetSelfNode();

            switch (selfNode.Type)
            {
                case ClusterNodeType.Primary:
                    //find existing record TTL values
                    FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl);

                    //update cluster zone to remove current self node records
                    RemoveClusterPrimaryZoneRecordsFor(selfNode);

                    //update self node
                    selfNode.UpdateSelfNodeIPAddresses(ipAddresses);

                    //update cluster zone to add updated self node records
                    AddClusterPrimaryZoneRecordsFor(selfNode, nsTtl, aTtl, _dnsWebService.WebServiceTlsCertificate);

                    //update cluster catalog zone ACLs and save zone file
                    UpdateClusterCatalogZoneOptions();

                    //save all changes immediately before sending notify
                    SaveConfigFile(true);

                    //notify all secondary nodes immediately
                    TriggerNotifyAllSecondaryNodes(0);
                    break;

                case ClusterNodeType.Secondary:
                    //update self node
                    selfNode.UpdateSelfNodeIPAddresses(ipAddresses);

                    //save all changes
                    SaveConfigFile();

                    //trigger cluster node update on primary node
                    TriggerClusterUpdateForSecondaryNodeChanges();
                    break;
            }

            return selfNode;
        }

        public void UpdateSelfNodeUrlAndCertificate()
        {
            ClusterNode selfNode = GetSelfNode();

            //validation
            foreach (KeyValuePair<int, ClusterNode> clusterNode in _clusterNodes)
            {
                if (clusterNode.Key == selfNode.Id)
                    continue; //skip self

                if (clusterNode.Value.Name.Equals(_dnsWebService.DnsServer.ServerDomain, StringComparison.OrdinalIgnoreCase))
                    throw new DnsServerException("Failed to update self node URL: the node's domain name already exists in the Cluster. Please try again after changing the DNS Server's domain name.");
            }

            switch (selfNode.Type)
            {
                case ClusterNodeType.Primary:
                    //find existing record TTL values
                    FindExistingRecordTtlValues(out uint nsTtl, out uint aTtl);

                    //update cluster zone to remove current self node records
                    RemoveClusterPrimaryZoneRecordsFor(selfNode);

                    //update self node
                    selfNode.UpdateSelfNodeUrl();

                    //update cluster zone to add updated self node records
                    AddClusterPrimaryZoneRecordsFor(selfNode, nsTtl, aTtl, _dnsWebService.WebServiceTlsCertificate);

                    //save all changes
                    SaveConfigFile();

                    _dnsWebService.LogManager.Write("Primary node '" + selfNode.ToString() + "' URL was updated successfully.");

                    //notify all secondary nodes
                    TriggerNotifyAllSecondaryNodes();
                    break;

                case ClusterNodeType.Secondary:
                    //update self node
                    selfNode.UpdateSelfNodeUrl();

                    //save all changes
                    SaveConfigFile();

                    _dnsWebService.LogManager.Write("Secondary node '" + selfNode.ToString() + "' URL was updated successfully.");

                    //trigger cluster node update on primary node
                    TriggerClusterUpdateForSecondaryNodeChanges();
                    break;
            }
        }

        #endregion

        #region properties

        public DnsWebService DnsWebService
        { get { return _dnsWebService; } }

        public bool ClusterInitialized
        {
            get
            {
                IReadOnlyDictionary<int, ClusterNode> clusterNodes = _clusterNodes;

                return (clusterNodes is not null) && (clusterNodes.Count > 0);
            }
        }

        public string ClusterDomain
        { get { return _clusterDomain; } }

        public ushort HeartbeatRefreshIntervalSeconds
        { get { return _heartbeatRefreshIntervalSeconds; } }

        public ushort HeartBeatRetryIntervalSeconds
        { get { return _heartbeatRetryIntervalSeconds; } }

        public ushort ConfigRefreshIntervalSeconds
        { get { return _configRefreshIntervalSeconds; } }

        public ushort ConfigRetryIntervalSeconds
        { get { return _configRetryIntervalSeconds; } }

        public DateTime ConfigLastSynced
        { get { return _configLastSynced; } }

        public DateTime ConfigLastModified
        { get { return _configLastModified; } }

        public bool AutoFailoverEnabled
        { get { return _autoFailoverEnabled; } }

        public string AutoFailoverSuccessorNodeName
        { get { return _autoFailoverSuccessorNodeName; } }

        public ushort AutoFailoverTimeoutSeconds
        { get { return _autoFailoverTimeoutSeconds; } }

        public IReadOnlyList<IPAddress> AutoFailoverWitnessAddresses
        { get { return _autoFailoverWitnessAddresses; } }

        public bool Fenced
        { get { return _fenced; } }

        public string FencedByNodeName
        { get { return _fencedByNodeName; } }

        public uint PrimaryTerm
        { get { return _primaryTerm; } }

        public IReadOnlyDictionary<int, ClusterNode> ClusterNodes
        { get { return _clusterNodes; } }

        #endregion
    }
}
