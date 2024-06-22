﻿/*
Technitium DNS Server
Copyright (C) 2024  Shreyas Zare (shreyas@technitium.com)

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

using DnsServerCore.Dns.Dnssec;
using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Trees;
using DnsServerCore.Dns.Zones;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.ZoneManagers
{
    public sealed class AuthZoneManager : IDisposable
    {
        #region variables

        readonly DnsServer _dnsServer;

        string _serverDomain;
        bool _useSoaSerialDateScheme;

        readonly AuthZoneTree _root = new AuthZoneTree();

        readonly List<AuthZoneInfo> _zoneIndex = new List<AuthZoneInfo>(10);
        readonly ReaderWriterLockSlim _zoneIndexLock = new ReaderWriterLockSlim();

        readonly object _saveLock = new object();
        readonly Dictionary<string, object> _pendingSaveZones = new Dictionary<string, object>();
        readonly Timer _saveTimer;
        const int SAVE_TIMER_INITIAL_INTERVAL = 10000;

        #endregion

        #region constructor

        public AuthZoneManager(DnsServer dnsServer)
        {
            _dnsServer = dnsServer;

            _serverDomain = _dnsServer.ServerDomain;

            _saveTimer = new Timer(delegate (object state)
            {
                lock (_saveLock)
                {
                    List<string> failedZones = new List<string>();

                    foreach (KeyValuePair<string, object> pendingSaveZone in _pendingSaveZones)
                    {
                        try
                        {
                            SaveZoneFileInternal(pendingSaveZone.Key);
                        }
                        catch (Exception ex)
                        {
                            _dnsServer.LogManager.Write(ex);

                            failedZones.Add(pendingSaveZone.Key);
                        }
                    }

                    _pendingSaveZones.Clear();

                    foreach (string zoneName in failedZones)
                        _pendingSaveZones.TryAdd(zoneName, null);

                    if (_pendingSaveZones.Count > 0)
                        _saveTimer.Change(SAVE_TIMER_INITIAL_INTERVAL, Timeout.Infinite);
                }
            });
        }

        #endregion

        #region IDisposable

        bool _disposed;

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                lock (_saveLock)
                {
                    _saveTimer?.Dispose();

                    try
                    {
                        foreach (KeyValuePair<string, object> pendingSaveZone in _pendingSaveZones)
                        {
                            try
                            {
                                SaveZoneFileInternal(pendingSaveZone.Key);
                            }
                            catch (Exception ex)
                            {
                                _dnsServer.LogManager.Write(ex);
                            }
                        }
                    }
                    finally
                    {
                        _pendingSaveZones.Clear();
                    }
                }

                foreach (AuthZoneNode zoneNode in _root)
                    zoneNode.Dispose();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region private

        internal void UpdateServerDomain()
        {
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                string serverDomain = _dnsServer.ServerDomain;

                //update authoritative zone SOA and NS records
                try
                {
                    IReadOnlyList<AuthZoneInfo> zones = GetAllZones();

                    foreach (AuthZoneInfo zone in zones)
                    {
                        if (zone.Type != AuthZoneType.Primary)
                            continue;

                        DnsResourceRecord record = zone.GetApexRecords(DnsResourceRecordType.SOA)[0];
                        DnsSOARecordData soa = record.RDATA as DnsSOARecordData;

                        if (soa.PrimaryNameServer.Equals(_serverDomain, StringComparison.OrdinalIgnoreCase))
                        {
                            SetRecords(zone.Name, record.Name, record.Type, record.TTL, [new DnsSOARecordData(serverDomain, soa.ResponsiblePerson, soa.Serial, soa.Refresh, soa.Retry, soa.Expire, soa.Minimum)]);

                            //update NS records
                            IReadOnlyList<DnsResourceRecord> nsResourceRecords = zone.GetApexRecords(DnsResourceRecordType.NS);

                            foreach (DnsResourceRecord nsResourceRecord in nsResourceRecords)
                            {
                                if ((nsResourceRecord.RDATA as DnsNSRecordData).NameServer.Equals(_serverDomain, StringComparison.OrdinalIgnoreCase))
                                {
                                    UpdateRecord(zone.Name, nsResourceRecord, new DnsResourceRecord(nsResourceRecord.Name, nsResourceRecord.Type, nsResourceRecord.Class, nsResourceRecord.TTL, new DnsNSRecordData(serverDomain)) { Tag = nsResourceRecord.Tag });
                                    break;
                                }
                            }

                            if (zone.Internal)
                                continue; //dont save internal zones to disk

                            try
                            {
                                SaveZoneFile(zone.Name);
                            }
                            catch (Exception ex)
                            {
                                _dnsServer.LogManager?.Write(ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _dnsServer.LogManager?.Write(ex);
                }

                //update server domain
                _serverDomain = serverDomain;
            });
        }

        private ApexZone CreateEmptyZone(AuthZoneInfo zoneInfo)
        {
            ApexZone zone;

            switch (zoneInfo.Type)
            {
                case AuthZoneType.Primary:
                    zone = new PrimaryZone(_dnsServer, zoneInfo);
                    break;

                case AuthZoneType.Secondary:
                    zone = new SecondaryZone(_dnsServer, zoneInfo);
                    break;

                case AuthZoneType.Stub:
                    zone = new StubZone(_dnsServer, zoneInfo);
                    break;

                case AuthZoneType.Forwarder:
                    zone = new ForwarderZone(zoneInfo);
                    break;

                default:
                    throw new InvalidDataException("DNS zone type not supported.");
            }

            if (_root.TryAdd(zone))
                return zone;

            throw new DnsServerException("Zone already exists: " + zoneInfo.Name);
        }

        internal AuthZone GetOrAddSubDomainZone(string zoneName, string domain)
        {
            return _root.GetOrAddSubDomainZone(zoneName, domain, delegate ()
            {
                if (!_root.TryGet(zoneName, out ApexZone apexZone))
                    throw new DnsServerException("Zone was not found for domain: " + domain);

                if (apexZone is PrimaryZone primaryZone)
                    return new PrimarySubDomainZone(primaryZone, domain);
                else if (apexZone is SecondaryZone secondaryZone)
                    return new SecondarySubDomainZone(secondaryZone, domain);
                else if (apexZone is ForwarderZone forwarderZone)
                    return new ForwarderSubDomainZone(forwarderZone, domain);

                throw new DnsServerException("Zone cannot have sub domains.");
            });
        }

        internal IReadOnlyList<AuthZone> GetZoneWithSubDomainZones(string zoneName)
        {
            return _root.GetZoneWithSubDomainZones(zoneName);
        }

        internal AuthZone GetAuthZone(string zoneName, string domain)
        {
            return _root.GetAuthZone(zoneName, domain);
        }

        internal AuthZone FindPreviousSubDomainZone(string zoneName, string domain)
        {
            return _root.FindPreviousSubDomainZone(zoneName, domain);
        }

        internal AuthZone FindNextSubDomainZone(string zoneName, string domain)
        {
            return _root.FindNextSubDomainZone(zoneName, domain);
        }

        internal bool SubDomainExists(string zoneName, string domain)
        {
            return _root.SubDomainExists(zoneName, domain);
        }

        internal void RemoveSubDomainZone(string domain)
        {
            _root.TryRemove(domain, out SubDomainZone _);
        }

        internal static string GetParentZone(string domain)
        {
            int i = domain.IndexOf('.');
            if (i > -1)
                return domain.Substring(i + 1);

            //dont return root zone
            return null;
        }

        private static void ValidateZoneNameFor(string zoneName, string domain)
        {
            if (domain.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase) || (zoneName.Length == 0))
                return;

            throw new DnsServerException("The domain name does not belong to the zone: " + domain);
        }

        private void ResolveCNAME(DnsQuestionRecord question, bool dnssecOk, DnsResourceRecord lastCNAME, List<DnsResourceRecord> answerRecords)
        {
            int queryCount = 0;

            do
            {
                string cnameDomain = (lastCNAME.RDATA as DnsCNAMERecordData).Domain;
                if (lastCNAME.Name.Equals(cnameDomain, StringComparison.OrdinalIgnoreCase))
                    break; //loop detected

                if (!_root.TryGet(cnameDomain, out AuthZoneNode zoneNode))
                    break;

                IReadOnlyList<DnsResourceRecord> records = zoneNode.QueryRecords(question.Type, dnssecOk);
                if (records.Count < 1)
                    break;

                DnsResourceRecord lastRR = records[records.Count - 1];
                if (lastRR.Type != DnsResourceRecordType.CNAME)
                {
                    answerRecords.AddRange(records);
                    break;
                }

                foreach (DnsResourceRecord answerRecord in answerRecords)
                {
                    if (answerRecord.Type != DnsResourceRecordType.CNAME)
                        continue;

                    if (answerRecord.RDATA.Equals(lastRR.RDATA))
                        return; //loop detected
                }

                answerRecords.AddRange(records);

                lastCNAME = lastRR;
            }
            while (++queryCount < DnsServer.MAX_CNAME_HOPS);
        }

        private bool DoDNAMESubstitution(DnsQuestionRecord question, bool dnssecOk, IReadOnlyList<DnsResourceRecord> answer, out IReadOnlyList<DnsResourceRecord> newAnswer)
        {
            DnsResourceRecord dnameRR = answer[0];

            string result = (dnameRR.RDATA as DnsDNAMERecordData).Substitute(question.Name, dnameRR.Name);

            if (DnsClient.IsDomainNameValid(result))
            {
                DnsResourceRecord cnameRR = new DnsResourceRecord(question.Name, DnsResourceRecordType.CNAME, question.Class, dnameRR.TTL, new DnsCNAMERecordData(result));

                List<DnsResourceRecord> list = new List<DnsResourceRecord>(5);

                list.AddRange(answer);
                list.Add(cnameRR);

                ResolveCNAME(question, dnssecOk, cnameRR, list);

                newAnswer = list;
                return true;
            }
            else
            {
                newAnswer = answer;
                return false;
            }
        }

        private List<DnsResourceRecord> GetAdditionalRecords(IReadOnlyList<DnsResourceRecord> refRecords, bool dnssecOk)
        {
            List<DnsResourceRecord> additionalRecords = new List<DnsResourceRecord>(refRecords.Count);

            foreach (DnsResourceRecord refRecord in refRecords)
            {
                switch (refRecord.Type)
                {
                    case DnsResourceRecordType.NS:
                        IReadOnlyList<DnsResourceRecord> glueRecords = refRecord.GetAuthNSRecordInfo().GlueRecords;
                        if (glueRecords is not null)
                        {
                            additionalRecords.AddRange(glueRecords);
                        }
                        else
                        {
                            ResolveAdditionalRecords(refRecord, (refRecord.RDATA as DnsNSRecordData).NameServer, dnssecOk, additionalRecords);
                        }
                        break;

                    case DnsResourceRecordType.MX:
                        ResolveAdditionalRecords(refRecord, (refRecord.RDATA as DnsMXRecordData).Exchange, dnssecOk, additionalRecords);
                        break;

                    case DnsResourceRecordType.SRV:
                        ResolveAdditionalRecords(refRecord, (refRecord.RDATA as DnsSRVRecordData).Target, dnssecOk, additionalRecords);
                        break;

                    case DnsResourceRecordType.SVCB:
                    case DnsResourceRecordType.HTTPS:
                        DnsSVCBRecordData svcb = refRecord.RDATA as DnsSVCBRecordData;
                        string targetName = svcb.TargetName;

                        if (svcb.SvcPriority == 0)
                        {
                            //For AliasMode SVCB RRs, a TargetName of "." indicates that the service is not available or does not exist [draft-ietf-dnsop-svcb-https-12]
                            if ((targetName.Length == 0) || targetName.Equals(refRecord.Name, StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                        else
                        {
                            //For ServiceMode SVCB RRs, if TargetName has the value ".", then the owner name of this record MUST be used as the effective TargetName [draft-ietf-dnsop-svcb-https-12]
                            if (targetName.Length == 0)
                                targetName = refRecord.Name;
                        }

                        ResolveAdditionalRecords(refRecord, targetName, dnssecOk, additionalRecords);
                        break;
                }
            }

            return additionalRecords;
        }

        private void ResolveAdditionalRecords(DnsResourceRecord refRecord, string domain, bool dnssecOk, List<DnsResourceRecord> additionalRecords)
        {
            int count = 0;

            while (count++ < DnsServer.MAX_CNAME_HOPS)
            {
                AuthZone zone = _root.FindZone(domain, out _, out _, out _, out _);
                if ((zone is null) || !zone.IsActive)
                    break;

                if (((refRecord.Type == DnsResourceRecordType.SVCB) || (refRecord.Type == DnsResourceRecordType.HTTPS)) && ((refRecord.RDATA as DnsSVCBRecordData).SvcPriority == 0))
                {
                    //resolve SVCB/HTTPS for Alias mode refRecord
                    IReadOnlyList<DnsResourceRecord> records = zone.QueryRecordsWildcard(refRecord.Type, dnssecOk, domain);
                    if ((records.Count > 0) && (records[0].Type == refRecord.Type) && (records[0].RDATA is DnsSVCBRecordData svcb))
                    {
                        additionalRecords.AddRange(records);

                        string targetName = svcb.TargetName;

                        if (svcb.SvcPriority == 0)
                        {
                            //Alias mode
                            if ((targetName.Length == 0) || targetName.Equals(records[0].Name, StringComparison.OrdinalIgnoreCase))
                                break; //For AliasMode SVCB RRs, a TargetName of "." indicates that the service is not available or does not exist [draft-ietf-dnsop-svcb-https-12]

                            foreach (DnsResourceRecord additionalRecord in additionalRecords)
                            {
                                if (additionalRecord.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                                    return; //loop detected
                            }

                            //continue to resolve SVCB/HTTPS further
                            domain = targetName;
                            refRecord = records[0];
                            continue;
                        }
                        else
                        {
                            //Service mode
                            if (targetName.Length > 0)
                            {
                                //continue to resolve A/AAAA for target name
                                domain = targetName;
                                refRecord = records[0];
                                continue;
                            }

                            //resolve A/AAAA below
                        }
                    }
                }

                bool hasA = false;
                bool hasAAAA = false;

                if ((refRecord.Type == DnsResourceRecordType.SRV) || (refRecord.Type == DnsResourceRecordType.SVCB) || (refRecord.Type == DnsResourceRecordType.HTTPS))
                {
                    foreach (DnsResourceRecord additionalRecord in additionalRecords)
                    {
                        if (additionalRecord.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (additionalRecord.Type)
                            {
                                case DnsResourceRecordType.A:
                                    hasA = true;
                                    break;

                                case DnsResourceRecordType.AAAA:
                                    hasAAAA = true;
                                    break;
                            }
                        }

                        if (hasA && hasAAAA)
                            break;
                    }
                }

                if (!hasA)
                {
                    IReadOnlyList<DnsResourceRecord> records = zone.QueryRecordsWildcard(DnsResourceRecordType.A, dnssecOk, domain);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.A))
                        additionalRecords.AddRange(records);
                }

                if (!hasAAAA)
                {
                    IReadOnlyList<DnsResourceRecord> records = zone.QueryRecordsWildcard(DnsResourceRecordType.AAAA, dnssecOk, domain);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.AAAA))
                        additionalRecords.AddRange(records);
                }

                break;
            }
        }

        private DnsDatagram GetReferralResponse(DnsDatagram request, bool dnssecOk, AuthZone delegationZone, ApexZone apexZone)
        {
            IReadOnlyList<DnsResourceRecord> authority;

            if (delegationZone is StubZone)
            {
                authority = delegationZone.GetRecords(DnsResourceRecordType.NS); //stub zone has no authority so cant query

                //update last used on
                DateTime utcNow = DateTime.UtcNow;

                foreach (DnsResourceRecord record in authority)
                    record.GetAuthGenericRecordInfo().LastUsedOn = utcNow;
            }
            else
            {
                authority = delegationZone.QueryRecords(DnsResourceRecordType.NS, false);

                if (dnssecOk)
                {
                    IReadOnlyList<DnsResourceRecord> dsRecords = delegationZone.QueryRecords(DnsResourceRecordType.DS, true);
                    if (dsRecords.Count > 0)
                    {
                        List<DnsResourceRecord> newAuthority = new List<DnsResourceRecord>(authority.Count + dsRecords.Count);

                        newAuthority.AddRange(authority);
                        newAuthority.AddRange(dsRecords);

                        authority = newAuthority;
                    }
                    else
                    {
                        //add proof of non existence (NODATA) to prove DS record does not exists
                        IReadOnlyList<DnsResourceRecord> nsecRecords;

                        if (apexZone.DnssecStatus == AuthZoneDnssecStatus.SignedWithNSEC3)
                            nsecRecords = _root.FindNSec3ProofOfNonExistenceNoData(delegationZone, apexZone);
                        else
                            nsecRecords = AuthZoneTree.FindNSecProofOfNonExistenceNoData(delegationZone);

                        if (nsecRecords.Count > 0)
                        {
                            List<DnsResourceRecord> newAuthority = new List<DnsResourceRecord>(authority.Count + nsecRecords.Count);

                            newAuthority.AddRange(authority);
                            newAuthority.AddRange(nsecRecords);

                            authority = newAuthority;
                        }
                    }
                }
            }

            IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(authority, dnssecOk);

            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, request.Question, null, authority, additional);
        }

        private DnsDatagram GetForwarderResponse(DnsDatagram request, AuthZone zone, SubDomainZone closestZone, ApexZone forwarderZone)
        {
            IReadOnlyList<DnsResourceRecord> authority = null;

            if (zone is not null)
            {
                if (zone.ContainsNameServerRecords())
                    return GetReferralResponse(request, false, zone, forwarderZone);

                authority = zone.QueryRecords(DnsResourceRecordType.FWD, false);
            }

            if (((authority is null) || (authority.Count == 0)) && (closestZone is not null))
            {
                if (closestZone.ContainsNameServerRecords())
                    return GetReferralResponse(request, false, closestZone, forwarderZone);

                authority = closestZone.QueryRecords(DnsResourceRecordType.FWD, false);
            }

            if ((authority is null) || (authority.Count == 0))
            {
                if (forwarderZone.ContainsNameServerRecords())
                    return GetReferralResponse(request, false, forwarderZone, forwarderZone);

                authority = forwarderZone.QueryRecords(DnsResourceRecordType.FWD, false);
            }

            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, false, false, false, DnsResponseCode.NoError, request.Question, null, authority);
        }

        internal void Flush()
        {
            _zoneIndexLock.EnterWriteLock();
            try
            {
                _root.Clear();
                _zoneIndex.Clear();
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }
        }

        private static List<DnsResourceRecord> CondenseIncrementalZoneTransferRecords(string zoneName, DnsResourceRecord currentSoaRecord, IReadOnlyList<DnsResourceRecord> xfrRecords)
        {
            DnsResourceRecord firstSoaRecord = xfrRecords[0];
            DnsResourceRecord lastSoaRecord = xfrRecords[xfrRecords.Count - 1];

            DnsResourceRecord firstDeletedSoaRecord = null;
            DnsResourceRecord lastAddedSoaRecord = null;

            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedGlueRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> addedGlueRecords = new List<DnsResourceRecord>();

            //read and apply difference sequences
            int index = 1;
            int count = xfrRecords.Count - 1;
            DnsSOARecordData currentSoa = (DnsSOARecordData)currentSoaRecord.RDATA;

            while (index < count)
            {
                //read deleted records
                DnsResourceRecord deletedSoaRecord = xfrRecords[index];
                if ((deletedSoaRecord.Type != DnsResourceRecordType.SOA) || !deletedSoaRecord.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();

                if (firstDeletedSoaRecord is null)
                    firstDeletedSoaRecord = deletedSoaRecord;

                index++;

                while (index < count)
                {
                    DnsResourceRecord record = xfrRecords[index];
                    if (record.Type == DnsResourceRecordType.SOA)
                        break;

                    if (zoneName.Length == 0)
                    {
                        //root zone case
                        switch (record.Type)
                        {
                            case DnsResourceRecordType.A:
                            case DnsResourceRecordType.AAAA:
                                if (!addedGlueRecords.Remove(record))
                                    deletedGlueRecords.Add(record);

                                break;

                            default:
                                if (!addedRecords.Remove(record))
                                    deletedRecords.Add(record);

                                break;
                        }
                    }
                    else
                    {
                        if (record.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || record.Name.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!addedRecords.Remove(record))
                                deletedRecords.Add(record);
                        }
                        else
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.A:
                                case DnsResourceRecordType.AAAA:
                                    if (!addedGlueRecords.Remove(record))
                                        deletedGlueRecords.Add(record);

                                    break;
                            }
                        }
                    }

                    index++;
                }

                //read added records
                DnsResourceRecord addedSoaRecord = xfrRecords[index];
                if (!addedSoaRecord.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();

                lastAddedSoaRecord = addedSoaRecord;

                index++;

                while (index < count)
                {
                    DnsResourceRecord record = xfrRecords[index];
                    if (record.Type == DnsResourceRecordType.SOA)
                        break;

                    if (zoneName.Length == 0)
                    {
                        //root zone case
                        switch (record.Type)
                        {
                            case DnsResourceRecordType.A:
                            case DnsResourceRecordType.AAAA:
                                if (!deletedGlueRecords.Remove(record))
                                    addedGlueRecords.Add(record);

                                break;

                            default:
                                if (!deletedRecords.Remove(record))
                                    addedRecords.Add(record);

                                break;
                        }
                    }
                    else
                    {
                        if (record.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || record.Name.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!deletedRecords.Remove(record))
                                addedRecords.Add(record);
                        }
                        else
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.A:
                                case DnsResourceRecordType.AAAA:
                                    if (!deletedGlueRecords.Remove(record))
                                        addedGlueRecords.Add(record);

                                    break;
                            }
                        }
                    }

                    index++;
                }

                //check sequence soa serial
                DnsSOARecordData deletedSoa = deletedSoaRecord.RDATA as DnsSOARecordData;

                if (currentSoa.Serial != deletedSoa.Serial)
                    throw new InvalidOperationException("Current SOA serial does not match with the IXFR difference sequence deleted SOA.");

                //check next difference sequence
                currentSoa = addedSoaRecord.RDATA as DnsSOARecordData;
            }

            //create condensed records
            List<DnsResourceRecord> condensedRecords = new List<DnsResourceRecord>(2 + 2 + deletedRecords.Count + deletedGlueRecords.Count + addedRecords.Count + addedGlueRecords.Count);

            condensedRecords.Add(firstSoaRecord);

            condensedRecords.Add(firstDeletedSoaRecord);
            condensedRecords.AddRange(deletedRecords);
            condensedRecords.AddRange(deletedGlueRecords);

            condensedRecords.Add(lastAddedSoaRecord);
            condensedRecords.AddRange(addedRecords);
            condensedRecords.AddRange(addedGlueRecords);

            condensedRecords.Add(lastSoaRecord);

            return condensedRecords;
        }

        private void SaveZoneFileInternal(string zoneName)
        {
            zoneName = zoneName.ToLower();

            using (MemoryStream mS = new MemoryStream())
            {
                //serialize zone
                WriteZoneTo(zoneName, mS);

                if (mS.Position == 0)
                    return; //zone was not found

                //write to zone file
                mS.Position = 0;

                using (FileStream fS = new FileStream(Path.Combine(_dnsServer.ConfigFolder, "zones", zoneName + ".zone"), FileMode.Create, FileAccess.Write))
                {
                    mS.CopyTo(fS);
                }
            }

            _dnsServer.LogManager?.Write("Saved zone file for domain: " + (zoneName == "" ? "<root>" : zoneName));
        }

        #endregion

        #region public

        public void LoadAllZoneFiles()
        {
            Flush();

            string zonesFolder = Path.Combine(_dnsServer.ConfigFolder, "zones");
            if (!Directory.Exists(zonesFolder))
                Directory.CreateDirectory(zonesFolder);

            //move zone files to new folder
            {
                string[] oldZoneFiles = Directory.GetFiles(_dnsServer.ConfigFolder, "*.zone");

                foreach (string oldZoneFile in oldZoneFiles)
                    File.Move(oldZoneFile, Path.Combine(zonesFolder, Path.GetFileName(oldZoneFile)));
            }

            //remove old internal zones files
            {
                string[] oldZoneFiles = new string[] { "localhost.zone", "1.0.0.127.in-addr.arpa.zone", "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.ip6.arpa.zone" };

                foreach (string oldZoneFile in oldZoneFiles)
                {
                    string filePath = Path.Combine(zonesFolder, oldZoneFile);

                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch
                        { }
                    }
                }
            }

            //load system zones
            {
                {
                    CreatePrimaryZone("localhost", _dnsServer.ServerDomain, true);
                    SetRecords("localhost", "localhost", DnsResourceRecordType.A, 3600, new DnsResourceRecordData[] { new DnsARecordData(IPAddress.Loopback) });
                    SetRecords("localhost", "localhost", DnsResourceRecordType.AAAA, 3600, new DnsResourceRecordData[] { new DnsAAAARecordData(IPAddress.IPv6Loopback) });
                }

                {
                    string prtDomain = "0.in-addr.arpa";

                    CreatePrimaryZone(prtDomain, _dnsServer.ServerDomain, true);
                }

                {
                    string prtDomain = "255.in-addr.arpa";

                    CreatePrimaryZone(prtDomain, _dnsServer.ServerDomain, true);
                }

                {
                    string ptrZoneName = "127.in-addr.arpa";

                    CreatePrimaryZone(ptrZoneName, _dnsServer.ServerDomain, true);
                    SetRecords(ptrZoneName, "1.0.0.127.in-addr.arpa", DnsResourceRecordType.PTR, 3600, new DnsResourceRecordData[] { new DnsPTRRecordData("localhost") });
                }

                {
                    string ptrZoneName = IPAddress.IPv6Loopback.GetReverseDomain();

                    CreatePrimaryZone(ptrZoneName, _dnsServer.ServerDomain, true);
                    SetRecords(ptrZoneName, ptrZoneName, DnsResourceRecordType.PTR, 3600, new DnsResourceRecordData[] { new DnsPTRRecordData("localhost") });
                }
            }

            //load zone files
            _zoneIndexLock.EnterWriteLock();
            try
            {
                string[] zoneFiles = Directory.GetFiles(zonesFolder, "*.zone");

                foreach (string zoneFile in zoneFiles)
                {
                    try
                    {
                        using (FileStream fS = new FileStream(zoneFile, FileMode.Open, FileAccess.Read))
                        {
                            AuthZoneInfo zoneInfo = LoadZoneFrom(fS, File.GetLastWriteTimeUtc(fS.SafeFileHandle));
                            _zoneIndex.Add(zoneInfo);
                        }

                        _dnsServer.LogManager?.Write("DNS Server successfully loaded zone file: " + zoneFile);
                    }
                    catch (Exception ex)
                    {
                        _dnsServer.LogManager?.Write("DNS Server failed to load zone file: " + zoneFile + "\r\n" + ex.ToString());
                    }
                }

                _zoneIndex.Sort();
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }
        }

        internal AuthZoneInfo CreateSpecialPrimaryZone(string zoneName, DnsSOARecordData soaRecord, DnsNSRecordData ns)
        {
            PrimaryZone apexZone = new PrimaryZone(_dnsServer, zoneName, soaRecord, ns);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        internal void LoadSpecialPrimaryZones(IReadOnlyList<string> zoneNames, DnsSOARecordData soaRecord, DnsNSRecordData ns)
        {
            _zoneIndexLock.EnterWriteLock();
            try
            {
                foreach (string zoneName in zoneNames)
                {
                    PrimaryZone apexZone = new PrimaryZone(_dnsServer, zoneName, soaRecord, ns);

                    if (_root.TryAdd(apexZone))
                    {
                        AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                        _zoneIndex.Add(zoneInfo);
                    }
                }

                _zoneIndex.Sort();
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }
        }

        internal void LoadSpecialPrimaryZones(Func<string> getZoneName, DnsSOARecordData soaRecord, DnsNSRecordData ns)
        {
            _zoneIndexLock.EnterWriteLock();
            try
            {
                string zoneName;

                while (true)
                {
                    zoneName = getZoneName();
                    if (zoneName is null)
                        break;

                    PrimaryZone apexZone = new PrimaryZone(_dnsServer, zoneName, soaRecord, ns);

                    if (_root.TryAdd(apexZone))
                    {
                        AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                        _zoneIndex.Add(zoneInfo);
                    }
                }

                _zoneIndex.Sort();
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }
        }

        public AuthZoneInfo CreatePrimaryZone(string zoneName, string primaryNameServer, bool @internal)
        {
            return CreatePrimaryZone(zoneName, primaryNameServer, @internal, _useSoaSerialDateScheme);
        }

        public AuthZoneInfo CreatePrimaryZone(string zoneName, string primaryNameServer, bool @internal, bool useSoaSerialDateScheme)
        {
            PrimaryZone apexZone = new PrimaryZone(_dnsServer, zoneName, primaryNameServer, @internal, useSoaSerialDateScheme);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        public async Task<AuthZoneInfo> CreateSecondaryZoneAsync(string zoneName, string primaryNameServerAddresses = null, DnsTransportProtocol zoneTransferProtocol = DnsTransportProtocol.Tcp, string tsigKeyName = null, bool validateZone = false)
        {
            SecondaryZone apexZone = await SecondaryZone.CreateAsync(_dnsServer, zoneName, primaryNameServerAddresses, zoneTransferProtocol, tsigKeyName, validateZone);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    apexZone.TriggerRefresh(0);

                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        public async Task<AuthZoneInfo> CreateStubZoneAsync(string zoneName, string primaryNameServerAddresses = null)
        {
            StubZone apexZone = await StubZone.CreateAsync(_dnsServer, zoneName, primaryNameServerAddresses);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    apexZone.TriggerRefresh(0);

                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        public AuthZoneInfo CreateForwarderZone(string zoneName)
        {
            ForwarderZone apexZone = new ForwarderZone(zoneName);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        public AuthZoneInfo CreateForwarderZone(string zoneName, DnsTransportProtocol forwarderProtocol, string forwarder, bool dnssecValidation, DnsForwarderRecordProxyType proxyType, string proxyAddress, ushort proxyPort, string proxyUsername, string proxyPassword, string fwdRecordComments)
        {
            ForwarderZone apexZone = new ForwarderZone(zoneName, forwarderProtocol, forwarder, dnssecValidation, proxyType, proxyAddress, proxyPort, proxyUsername, proxyPassword, fwdRecordComments);

            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryAdd(apexZone))
                {
                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    _zoneIndex.Add(zoneInfo);
                    _zoneIndex.Sort();

                    return zoneInfo;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return null;
        }

        public AuthZoneInfo CloneZone(string zoneName, string sourceZoneName)
        {
            AuthZoneInfo sourceZoneInfo = GetAuthZoneInfo(sourceZoneName, false);
            if (sourceZoneInfo is null)
                throw new DnsServerException("No such zone was found: " + (zoneName.Length == 0 ? "." : zoneName));

            AuthZoneInfo zoneInfo;

            switch (sourceZoneInfo.Type)
            {
                case AuthZoneType.Primary:
                    zoneInfo = CreatePrimaryZone(zoneName, _dnsServer.ServerDomain, false);
                    break;

                case AuthZoneType.Forwarder:
                    zoneInfo = CreateForwarderZone(zoneName);
                    break;

                default:
                    throw new DnsServerException("Cannot clone the zone: source zone must be a primary or conditional forwarder zone.");
            }

            if (zoneInfo is null)
                throw new DnsServerException("Failed to clone the zone: zone already exists.");

            //copy zone options
            zoneInfo.Disabled = sourceZoneInfo.Disabled;

            if (zoneInfo.Type == AuthZoneType.Primary)
            {
                zoneInfo.ZoneTransfer = sourceZoneInfo.ZoneTransfer;
                zoneInfo.ZoneTransferNameServers = sourceZoneInfo.ZoneTransferNameServers;
                zoneInfo.ZoneTransferTsigKeyNames = sourceZoneInfo.ZoneTransferTsigKeyNames;

                zoneInfo.Notify = sourceZoneInfo.Notify;
                zoneInfo.NotifyNameServers = sourceZoneInfo.NotifyNameServers;

                zoneInfo.Update = sourceZoneInfo.Update;
                zoneInfo.UpdateIpAddresses = sourceZoneInfo.UpdateIpAddresses;

                if (sourceZoneInfo.UpdateSecurityPolicies is not null)
                {
                    Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<DnsResourceRecordType>>> updateSecurityPolicies = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<DnsResourceRecordType>>>(sourceZoneInfo.UpdateSecurityPolicies.Count);

                    foreach (KeyValuePair<string, IReadOnlyDictionary<string, IReadOnlyList<DnsResourceRecordType>>> sourceSecurityPolicy in sourceZoneInfo.UpdateSecurityPolicies)
                    {
                        Dictionary<string, IReadOnlyList<DnsResourceRecordType>> policyMap = new Dictionary<string, IReadOnlyList<DnsResourceRecordType>>();

                        foreach (KeyValuePair<string, IReadOnlyList<DnsResourceRecordType>> sourcePolicyMap in sourceSecurityPolicy.Value)
                            policyMap.Add(string.Concat(sourcePolicyMap.Key.AsSpan(0, sourcePolicyMap.Key.Length - sourceZoneName.Length), zoneName), sourcePolicyMap.Value);

                        updateSecurityPolicies.Add(sourceSecurityPolicy.Key, policyMap);
                    }

                    zoneInfo.UpdateSecurityPolicies = updateSecurityPolicies;
                }
            }

            //copy records
            List<DnsResourceRecord> sourceRecords = new List<DnsResourceRecord>();
            ListAllZoneRecords(sourceZoneName, sourceRecords);

            List<DnsResourceRecord> newRecords = new List<DnsResourceRecord>(sourceRecords.Count);

            foreach (DnsResourceRecord sourceRecord in sourceRecords)
            {
                switch (sourceRecord.Type)
                {
                    case DnsResourceRecordType.DNSKEY:
                    case DnsResourceRecordType.RRSIG:
                    case DnsResourceRecordType.NSEC:
                    case DnsResourceRecordType.NSEC3:
                    case DnsResourceRecordType.NSEC3PARAM:
                    case DnsResourceRecordType.DS:
                        continue; //skip DNSSEC records

                    default:
                        DnsResourceRecord newRecord = new DnsResourceRecord(string.Concat(sourceRecord.Name.AsSpan(0, sourceRecord.Name.Length - sourceZoneName.Length), zoneName), sourceRecord.Type, sourceRecord.Class, sourceRecord.TTL, sourceRecord.RDATA);

                        if (sourceRecord.Tag is NSRecordInfo nsInfo)
                        {
                            NSRecordInfo nrInfo = new NSRecordInfo();

                            nrInfo.Disabled = nsInfo.Disabled;
                            nrInfo.Comments = nsInfo.Comments;
                            nrInfo.GlueRecords = nsInfo.GlueRecords;

                            newRecord.Tag = nrInfo;
                        }
                        else if (sourceRecord.Tag is SOARecordInfo soaInfo)
                        {
                            SOARecordInfo nrInfo = new SOARecordInfo();

                            nrInfo.Disabled = soaInfo.Disabled;
                            nrInfo.Comments = soaInfo.Comments;
                            nrInfo.UseSoaSerialDateScheme = soaInfo.UseSoaSerialDateScheme;

                            newRecord.Tag = nrInfo;
                        }
                        else if (sourceRecord.Tag is SVCBRecordInfo svcbInfo)
                        {
                            SVCBRecordInfo nrInfo = new SVCBRecordInfo();

                            nrInfo.Disabled = svcbInfo.Disabled;
                            nrInfo.Comments = svcbInfo.Comments;
                            nrInfo.AutoIpv4Hint = svcbInfo.AutoIpv4Hint;
                            nrInfo.AutoIpv6Hint = svcbInfo.AutoIpv6Hint;

                            newRecord.Tag = nrInfo;
                        }
                        else if (sourceRecord.Tag is GenericRecordInfo srInfo)
                        {
                            GenericRecordInfo nrInfo = new GenericRecordInfo();

                            nrInfo.Disabled = srInfo.Disabled;
                            nrInfo.Comments = srInfo.Comments;

                            newRecord.Tag = nrInfo;
                        }

                        newRecords.Add(newRecord);
                        break;
                }
            }

            LoadRecords(zoneInfo.ApexZone, newRecords);

            return zoneInfo;
        }

        public void ConvertZoneType(string zoneName, AuthZoneType type)
        {
            AuthZoneInfo currentZoneInfo = GetAuthZoneInfo(zoneName, false);
            if (currentZoneInfo is null)
                throw new DnsServerException("No such zone was found: " + (zoneName.Length == 0 ? "." : zoneName));

            if (currentZoneInfo.Type == type)
                throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type.ToString() + " zone: the zone is already of the same type.");

            switch (currentZoneInfo.Type)
            {
                case AuthZoneType.Primary:
                    switch (type)
                    {
                        case AuthZoneType.Forwarder:
                            if (currentZoneInfo.DnssecStatus != AuthZoneDnssecStatus.Unsigned)
                                throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type + " zone: converting the zone will cause lose of DNSSEC private keys.");

                            break;

                        default:
                            throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type + " zone: not supported.");
                    }

                    break;

                case AuthZoneType.Secondary:
                    switch (type)
                    {
                        case AuthZoneType.Primary:
                        case AuthZoneType.Forwarder:
                            break;

                        default:
                            throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type + " zone: not supported.");
                    }


                    break;

                case AuthZoneType.Forwarder:
                    switch (type)
                    {
                        case AuthZoneType.Primary:
                            break;

                        default:
                            throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type + " zone: not supported.");
                    }

                    break;

                default:
                    throw new DnsServerException("Cannot convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type.ToString() + " zone: not supported.");
            }

            //read all current records
            List<DnsResourceRecord> allRecords = new List<DnsResourceRecord>();
            ListAllZoneRecords(zoneName, allRecords);

            try
            {
                //delete current zone
                DeleteZone(zoneName);

                //create new zone
                AuthZoneInfo newZoneInfo;

                switch (type)
                {
                    case AuthZoneType.Primary:
                        switch (currentZoneInfo.Type)
                        {
                            case AuthZoneType.Secondary:
                                {
                                    //reset SOA metadata and remove DNSSEC records
                                    List<DnsResourceRecord> updateRecords = new List<DnsResourceRecord>(allRecords.Count);

                                    foreach (DnsResourceRecord record in allRecords)
                                    {
                                        switch (record.Type)
                                        {
                                            case DnsResourceRecordType.SOA:
                                                {
                                                    GenericRecordInfo recordInfo = record.GetAuthGenericRecordInfo();
                                                    record.Tag = null;

                                                    GenericRecordInfo newRecordInfo = record.GetAuthGenericRecordInfo();
                                                    newRecordInfo.Comments = recordInfo.Comments;
                                                }
                                                break;

                                            case DnsResourceRecordType.DNSKEY:
                                            case DnsResourceRecordType.RRSIG:
                                            case DnsResourceRecordType.NSEC:
                                            case DnsResourceRecordType.NSEC3:
                                            case DnsResourceRecordType.NSEC3PARAM:
                                                continue;
                                        }

                                        updateRecords.Add(record);
                                    }

                                    allRecords = updateRecords;
                                }
                                break;

                            case AuthZoneType.Forwarder:
                                {
                                    //remove all FWD records
                                    List<DnsResourceRecord> updateRecords = new List<DnsResourceRecord>(allRecords.Count);

                                    foreach (DnsResourceRecord record in allRecords)
                                    {
                                        if (record.Type == DnsResourceRecordType.FWD)
                                            continue;

                                        updateRecords.Add(record);
                                    }

                                    allRecords = updateRecords;
                                }
                                break;
                        }

                        newZoneInfo = CreatePrimaryZone(zoneName, _dnsServer.ServerDomain, false);
                        break;

                    case AuthZoneType.Forwarder:
                        switch (currentZoneInfo.Type)
                        {
                            case AuthZoneType.Primary:
                                {
                                    //remove SOA and NS records
                                    List<DnsResourceRecord> updateRecords = new List<DnsResourceRecord>(allRecords.Count);

                                    foreach (DnsResourceRecord record in allRecords)
                                    {
                                        switch (record.Type)
                                        {
                                            case DnsResourceRecordType.SOA:
                                            case DnsResourceRecordType.NS:
                                                continue;
                                        }

                                        updateRecords.Add(record);
                                    }

                                    allRecords = updateRecords;
                                }
                                break;

                            case AuthZoneType.Secondary:
                                {
                                    //remove SOA, NS and DNSSEC records
                                    List<DnsResourceRecord> updateRecords = new List<DnsResourceRecord>(allRecords.Count);

                                    foreach (DnsResourceRecord record in allRecords)
                                    {
                                        switch (record.Type)
                                        {
                                            case DnsResourceRecordType.SOA:
                                            case DnsResourceRecordType.NS:
                                            case DnsResourceRecordType.DNSKEY:
                                            case DnsResourceRecordType.RRSIG:
                                            case DnsResourceRecordType.NSEC:
                                            case DnsResourceRecordType.NSEC3:
                                            case DnsResourceRecordType.NSEC3PARAM:
                                            case DnsResourceRecordType.DS:
                                                continue;
                                        }

                                        updateRecords.Add(record);
                                    }

                                    allRecords = updateRecords;
                                }
                                break;
                        }

                        newZoneInfo = CreateForwarderZone(zoneName);
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                //load records
                LoadRecords(newZoneInfo.ApexZone, allRecords);
            }
            catch (Exception ex)
            {
                _dnsServer.LogManager?.Write("DNS Server failed to convert the zone '" + (zoneName.Length == 0 ? "." : zoneName) + "' from " + currentZoneInfo.Type.ToString() + " to " + type.ToString() + " zone.\r\n" + ex.ToString());

                //delete the zone if it was created
                DeleteZone(zoneName);

                //reload old zone file
                string zoneFile = Path.Combine(_dnsServer.ConfigFolder, "zones", zoneName + ".zone");

                _zoneIndexLock.EnterWriteLock();
                try
                {
                    using (FileStream fS = new FileStream(zoneFile, FileMode.Open, FileAccess.Read))
                    {
                        AuthZoneInfo zoneInfo = LoadZoneFrom(fS, File.GetLastWriteTimeUtc(fS.SafeFileHandle));
                        _zoneIndex.Add(zoneInfo);
                        _zoneIndex.Sort();
                    }

                    _dnsServer.LogManager?.Write("DNS Server successfully loaded zone file: " + zoneFile);
                }
                catch (Exception ex2)
                {
                    _dnsServer.LogManager?.Write("DNS Server failed to load zone file: " + zoneFile + "\r\n" + ex2.ToString());
                }
                finally
                {
                    _zoneIndexLock.ExitWriteLock();
                }

                throw;
            }
        }

        public void SignPrimaryZoneWithRsaNSEC(string zoneName, string hashAlgorithm, int kskKeySize, int zskKeySize, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.SignZoneWithRsaNSec(hashAlgorithm, kskKeySize, zskKeySize, dnsKeyTtl, zskRolloverDays);
        }

        public void SignPrimaryZoneWithRsaNSEC3(string zoneName, string hashAlgorithm, int kskKeySize, int zskKeySize, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.SignZoneWithRsaNSec3(hashAlgorithm, kskKeySize, zskKeySize, iterations, saltLength, dnsKeyTtl, zskRolloverDays);
        }

        public void SignPrimaryZoneWithEcdsaNSEC(string zoneName, string curve, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.SignZoneWithEcdsaNSec(curve, dnsKeyTtl, zskRolloverDays);
        }

        public void SignPrimaryZoneWithEcdsaNSEC3(string zoneName, string curve, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.SignZoneWithEcdsaNSec3(curve, iterations, saltLength, dnsKeyTtl, zskRolloverDays);
        }

        public void UnsignPrimaryZone(string zoneName)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.UnsignZone();
        }

        public void ConvertPrimaryZoneToNSEC(string zoneName)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.ConvertToNSec();
        }

        public void ConvertPrimaryZoneToNSEC3(string zoneName, ushort iterations, byte saltLength)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.ConvertToNSec3(iterations, saltLength);
        }

        public void UpdatePrimaryZoneNSEC3Parameters(string zoneName, ushort iterations, byte saltLength)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.UpdateNSec3Parameters(iterations, saltLength);
        }

        public void UpdatePrimaryZoneDnsKeyTtl(string zoneName, uint dnsKeyTtl)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.UpdateDnsKeyTtl(dnsKeyTtl);
        }

        public void GenerateAndAddPrimaryZoneDnssecRsaPrivateKey(string zoneName, DnssecPrivateKeyType keyType, string hashAlgorithm, int keySize, ushort rolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.GenerateAndAddRsaKey(keyType, hashAlgorithm, keySize, rolloverDays);
        }

        public void GenerateAndAddPrimaryZoneDnssecEcdsaPrivateKey(string zoneName, DnssecPrivateKeyType keyType, string curve, ushort rolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.GenerateAndAddEcdsaKey(keyType, curve, rolloverDays);
        }

        public void UpdatePrimaryZoneDnssecPrivateKey(string zoneName, ushort keyTag, ushort rolloverDays)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.UpdatePrivateKey(keyTag, rolloverDays);
        }

        public void DeletePrimaryZoneDnssecPrivateKey(string zoneName, ushort keyTag)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.DeletePrivateKey(keyTag);
        }

        public void PublishAllGeneratedPrimaryZoneDnssecPrivateKeys(string zoneName)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.PublishAllGeneratedKeys();
        }

        public void RolloverPrimaryZoneDnsKey(string zoneName, ushort keyTag)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.RolloverDnsKey(keyTag);
        }

        public void RetirePrimaryZoneDnsKey(string zoneName, ushort keyTag)
        {
            if (!_root.TryGet(zoneName, out ApexZone apexZone) || (apexZone is not PrimaryZone primaryZone) || primaryZone.Internal)
                throw new DnsServerException("No such primary zone was found: " + zoneName);

            primaryZone.RetireDnsKey(keyTag);
        }

        public bool DeleteZone(string zoneName)
        {
            _zoneIndexLock.EnterWriteLock();
            try
            {
                if (_root.TryRemove(zoneName, out ApexZone apexZone))
                {
                    apexZone.Dispose();

                    AuthZoneInfo zoneInfo = new AuthZoneInfo(apexZone);
                    if (!_zoneIndex.Remove(zoneInfo))
                        throw new InvalidOperationException("Zone deleted from tree but failed to remove from zone index.");

                    return true;
                }
            }
            finally
            {
                _zoneIndexLock.ExitWriteLock();
            }

            return false;
        }

        public AuthZoneInfo GetAuthZoneInfo(string zoneName, bool loadHistory = false)
        {
            if (_root.TryGet(zoneName, out AuthZoneNode authZoneNode) && (authZoneNode.ApexZone is not null))
                return new AuthZoneInfo(authZoneNode.ApexZone, loadHistory);

            return null;
        }

        public AuthZoneInfo FindAuthZoneInfo(string domain, bool loadHistory = false)
        {
            _ = _root.FindZone(domain, out _, out _, out ApexZone apexZone, out _);
            if (apexZone is null)
                return null;

            return new AuthZoneInfo(apexZone, loadHistory);
        }

        public bool NameExists(string zoneName, string domain)
        {
            ValidateZoneNameFor(zoneName, domain);

            return _root.TryGet(zoneName, domain, out _);
        }

        public void ListAllZoneRecords(string zoneName, List<DnsResourceRecord> records)
        {
            foreach (AuthZone zone in _root.GetZoneWithSubDomainZones(zoneName))
                zone.ListAllRecords(records);
        }

        public void ListAllZoneRecords(string zoneName, DnsResourceRecordType[] types, List<DnsResourceRecord> records)
        {
            foreach (AuthZone zone in _root.GetZoneWithSubDomainZones(zoneName))
            {
                foreach (DnsResourceRecordType type in types)
                    records.AddRange(zone.GetRecords(type));
            }
        }

        public void ListAllRecords(string zoneName, string domain, List<DnsResourceRecord> records)
        {
            ValidateZoneNameFor(zoneName, domain);

            if (_root.TryGet(zoneName, domain, out AuthZone authZone))
                authZone.ListAllRecords(records);
        }

        public IReadOnlyList<DnsResourceRecord> GetRecords(string zoneName, string domain, DnsResourceRecordType type)
        {
            ValidateZoneNameFor(zoneName, domain);

            if (_root.TryGet(zoneName, domain, out AuthZone authZone))
                return authZone.GetRecords(type);

            return Array.Empty<DnsResourceRecord>();
        }

        public IReadOnlyDictionary<DnsResourceRecordType, IReadOnlyList<DnsResourceRecord>> GetAllRecords(string zoneName, string domain)
        {
            ValidateZoneNameFor(zoneName, domain);

            if (_root.TryGet(zoneName, domain, out AuthZone authZone))
                return authZone.GetAllRecords();

            return new Dictionary<DnsResourceRecordType, IReadOnlyList<DnsResourceRecord>>(1);
        }

        public IReadOnlyList<DnsResourceRecord> QueryZoneTransferRecords(string zoneName)
        {
            AuthZoneInfo authZone = GetAuthZoneInfo(zoneName, false);
            if (authZone is null)
                throw new InvalidOperationException("Zone was not found: " + zoneName);

            //only primary and secondary zones support zone transfer
            IReadOnlyList<DnsResourceRecord> soaRecords = authZone.GetApexRecords(DnsResourceRecordType.SOA);
            if (soaRecords.Count != 1)
                throw new InvalidOperationException("Zone must be a primary or secondary zone.");

            DnsResourceRecord soaRecord = soaRecords[0];

            List<DnsResourceRecord> records = new List<DnsResourceRecord>();
            ListAllZoneRecords(zoneName, records);

            List<DnsResourceRecord> xfrRecords = new List<DnsResourceRecord>(records.Count + 1);

            //start message
            xfrRecords.Add(soaRecord);

            foreach (DnsResourceRecord record in records)
            {
                GenericRecordInfo authRecordInfo = record.GetAuthGenericRecordInfo();
                if (authRecordInfo.Disabled)
                    continue;

                switch (record.Type)
                {
                    case DnsResourceRecordType.SOA:
                        break; //skip record

                    case DnsResourceRecordType.NS:
                        xfrRecords.Add(record);

                        IReadOnlyList<DnsResourceRecord> glueRecords = (authRecordInfo as NSRecordInfo).GlueRecords;
                        if (glueRecords is not null)
                        {
                            foreach (DnsResourceRecord glueRecord in glueRecords)
                                xfrRecords.Add(glueRecord);
                        }
                        break;

                    default:
                        xfrRecords.Add(record);
                        break;
                }
            }

            //end message
            xfrRecords.Add(soaRecord);

            return xfrRecords;
        }

        public IReadOnlyList<DnsResourceRecord> QueryIncrementalZoneTransferRecords(string zoneName, DnsResourceRecord clientSoaRecord)
        {
            AuthZoneInfo authZone = GetAuthZoneInfo(zoneName, true);
            if (authZone is null)
                throw new InvalidOperationException("Zone was not found: " + zoneName);

            //only primary and secondary zones support zone transfer
            IReadOnlyList<DnsResourceRecord> soaRecords = authZone.GetApexRecords(DnsResourceRecordType.SOA);
            if (soaRecords.Count != 1)
                throw new InvalidOperationException("Zone must be a primary or secondary zone.");

            DnsResourceRecord currentSoaRecord = soaRecords[0];
            uint clientSerial = (clientSoaRecord.RDATA as DnsSOARecordData).Serial;

            if (clientSerial == (currentSoaRecord.RDATA as DnsSOARecordData).Serial)
            {
                //zone not modified
                return new DnsResourceRecord[] { currentSoaRecord };
            }

            //find history record start from client serial
            IReadOnlyList<DnsResourceRecord> zoneHistory = authZone.ZoneHistory;

            int index = 0;
            while (index < zoneHistory.Count)
            {
                //check difference sequence
                if ((zoneHistory[index].RDATA as DnsSOARecordData).Serial == clientSerial)
                    break; //found history for client's serial

                //skip to next difference sequence
                index++;
                int soaCount = 1;

                while (index < zoneHistory.Count)
                {
                    if (zoneHistory[index].Type == DnsResourceRecordType.SOA)
                    {
                        soaCount++;

                        if (soaCount == 3)
                            break;
                    }

                    index++;
                }
            }

            if (index == zoneHistory.Count)
            {
                //client's serial was not found in zone history
                //do full zone transfer
                return QueryZoneTransferRecords(zoneName);
            }

            List<DnsResourceRecord> xfrRecords = new List<DnsResourceRecord>();

            //start incremental message
            xfrRecords.Add(currentSoaRecord);

            //write history
            for (int i = index; i < zoneHistory.Count; i++)
                xfrRecords.Add(zoneHistory[i]);

            //end incremental message
            xfrRecords.Add(currentSoaRecord);

            //condense
            return CondenseIncrementalZoneTransferRecords(zoneName, clientSoaRecord, xfrRecords);
        }

        public void SyncZoneTransferRecords(string zoneName, IReadOnlyList<DnsResourceRecord> xfrRecords)
        {
            if ((xfrRecords.Count < 2) || (xfrRecords[0].Type != DnsResourceRecordType.SOA) || !xfrRecords[0].Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || !xfrRecords[xfrRecords.Count - 1].Equals(xfrRecords[0]))
                throw new DnsServerException("Invalid AXFR response was received.");

            List<DnsResourceRecord> latestRecords = new List<DnsResourceRecord>(xfrRecords.Count);
            List<DnsResourceRecord> allGlueRecords = new List<DnsResourceRecord>(4);

            if (zoneName.Length == 0)
            {
                //root zone case
                for (int i = 1; i < xfrRecords.Count; i++)
                {
                    DnsResourceRecord record = xfrRecords[i];

                    switch (record.Type)
                    {
                        case DnsResourceRecordType.A:
                        case DnsResourceRecordType.AAAA:
                            if (!allGlueRecords.Contains(record))
                                allGlueRecords.Add(record);

                            break;

                        default:
                            if (!latestRecords.Contains(record))
                                latestRecords.Add(record);

                            break;
                    }
                }
            }
            else
            {
                for (int i = 1; i < xfrRecords.Count; i++)
                {
                    DnsResourceRecord record = xfrRecords[i];

                    if (record.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || record.Name.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!latestRecords.Contains(record))
                            latestRecords.Add(record);
                    }
                    else if (!allGlueRecords.Contains(record))
                    {
                        allGlueRecords.Add(record);
                    }
                }
            }

            if (allGlueRecords.Count > 0)
            {
                foreach (DnsResourceRecord record in latestRecords)
                {
                    if (record.Type == DnsResourceRecordType.NS)
                        record.SyncGlueRecords(allGlueRecords);
                }
            }

            //sync records
            List<DnsResourceRecord> currentRecords = new List<DnsResourceRecord>();
            ListAllZoneRecords(zoneName, currentRecords);

            Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> currentRecordsGroupedByDomain = DnsResourceRecord.GroupRecords(currentRecords);
            Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> latestRecordsGroupedByDomain = DnsResourceRecord.GroupRecords(latestRecords);

            //remove domains that do not exists in new records
            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> currentDomain in currentRecordsGroupedByDomain)
            {
                if (!latestRecordsGroupedByDomain.ContainsKey(currentDomain.Key))
                    _root.TryRemove(currentDomain.Key, out SubDomainZone _);
            }

            //sync new records
            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> latestEntries in latestRecordsGroupedByDomain)
            {
                AuthZone zone = GetOrAddSubDomainZone(zoneName, latestEntries.Key);

                if (zone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    zone.SyncRecords(latestEntries.Value);
                else if ((zone is SubDomainZone subDomainZone) && subDomainZone.AuthoritativeZone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    zone.SyncRecords(latestEntries.Value);
            }

            if (_root.TryGet(zoneName, out ApexZone apexZone))
                apexZone.UpdateDnssecStatus();
        }

        public IReadOnlyList<DnsResourceRecord> SyncIncrementalZoneTransferRecords(string zoneName, IReadOnlyList<DnsResourceRecord> xfrRecords)
        {
            if ((xfrRecords.Count < 2) || (xfrRecords[0].Type != DnsResourceRecordType.SOA) || !xfrRecords[0].Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || !xfrRecords[xfrRecords.Count - 1].Equals(xfrRecords[0]))
                throw new DnsServerException("Invalid IXFR/AXFR response was received.");

            if ((xfrRecords.Count < 4) || (xfrRecords[1].Type != DnsResourceRecordType.SOA))
            {
                //received AXFR response
                SyncZoneTransferRecords(zoneName, xfrRecords);
                return Array.Empty<DnsResourceRecord>();
            }

            if (!_root.TryGet(zoneName, out ApexZone apexZone))
                throw new InvalidOperationException("No such zone was found: " + zoneName);

            IReadOnlyList<DnsResourceRecord> soaRecords = apexZone.GetRecords(DnsResourceRecordType.SOA);
            if (soaRecords.Count != 1)
                throw new InvalidOperationException("No authoritative zone was found: " + zoneName);

            //process IXFR response
            DnsResourceRecord currentSoaRecord = soaRecords[0];
            DnsSOARecordData currentSoa = currentSoaRecord.RDATA as DnsSOARecordData;

            IReadOnlyList<DnsResourceRecord> condensedXfrRecords = CondenseIncrementalZoneTransferRecords(zoneName, currentSoaRecord, xfrRecords);

            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedGlueRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> addedGlueRecords = new List<DnsResourceRecord>();

            //read and apply difference sequences
            int index = 1;
            int count = condensedXfrRecords.Count - 1;

            while (index < count)
            {
                //read deleted records
                DnsResourceRecord deletedSoaRecord = condensedXfrRecords[index];
                if ((deletedSoaRecord.Type != DnsResourceRecordType.SOA) || !deletedSoaRecord.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();

                index++;

                while (index < count)
                {
                    DnsResourceRecord record = condensedXfrRecords[index];
                    if (record.Type == DnsResourceRecordType.SOA)
                        break;

                    if (zoneName.Length == 0)
                    {
                        //root zone case
                        switch (record.Type)
                        {
                            case DnsResourceRecordType.A:
                            case DnsResourceRecordType.AAAA:
                                deletedGlueRecords.Add(record);
                                break;

                            default:
                                deletedRecords.Add(record);
                                break;
                        }
                    }
                    else
                    {
                        if (record.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || record.Name.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            deletedRecords.Add(record);
                        }
                        else
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.A:
                                case DnsResourceRecordType.AAAA:
                                    deletedGlueRecords.Add(record);
                                    break;
                            }
                        }
                    }

                    index++;
                }

                //read added records
                DnsResourceRecord addedSoaRecord = condensedXfrRecords[index];
                if (!addedSoaRecord.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();

                index++;

                while (index < count)
                {
                    DnsResourceRecord record = condensedXfrRecords[index];
                    if (record.Type == DnsResourceRecordType.SOA)
                        break;

                    if (zoneName.Length == 0)
                    {
                        //root zone case
                        switch (record.Type)
                        {
                            case DnsResourceRecordType.A:
                            case DnsResourceRecordType.AAAA:
                                addedGlueRecords.Add(record);
                                break;

                            default:
                                addedRecords.Add(record);
                                break;
                        }
                    }
                    else
                    {
                        if (record.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase) || record.Name.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            addedRecords.Add(record);
                        }
                        else
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.A:
                                case DnsResourceRecordType.AAAA:
                                    addedGlueRecords.Add(record);
                                    break;
                            }
                        }
                    }

                    index++;
                }

                //check sequence soa serial
                DnsSOARecordData deletedSoa = deletedSoaRecord.RDATA as DnsSOARecordData;

                if (currentSoa.Serial != deletedSoa.Serial)
                    throw new InvalidOperationException("Current SOA serial does not match with the IXFR difference sequence deleted SOA.");

                //sync difference sequence
                if (deletedRecords.Count > 0)
                {
                    foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> deletedEntry in DnsResourceRecord.GroupRecords(deletedRecords))
                    {
                        AuthZone zone = GetOrAddSubDomainZone(zoneName, deletedEntry.Key);

                        if (zone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            zone.SyncRecords(deletedEntry.Value, null);
                        }
                        else if ((zone is SubDomainZone subDomainZone) && subDomainZone.AuthoritativeZone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            zone.SyncRecords(deletedEntry.Value, null);

                            if (zone.IsEmpty)
                                _root.TryRemove(deletedEntry.Key, out SubDomainZone _); //remove empty sub zone
                        }
                    }
                }

                if (addedRecords.Count > 0)
                {
                    foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> addedEntry in DnsResourceRecord.GroupRecords(addedRecords))
                    {
                        AuthZone zone = GetOrAddSubDomainZone(zoneName, addedEntry.Key);

                        if (zone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                            zone.SyncRecords(null, addedEntry.Value);
                        else if ((zone is SubDomainZone subDomainZone) && subDomainZone.AuthoritativeZone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                            zone.SyncRecords(null, addedEntry.Value);
                    }
                }

                if ((deletedGlueRecords.Count > 0) || (addedGlueRecords.Count > 0))
                {
                    foreach (AuthZone zone in _root.GetZoneWithSubDomainZones(zoneName))
                        zone.SyncGlueRecords(deletedGlueRecords, addedGlueRecords);
                }

                {
                    AuthZone zone = GetOrAddSubDomainZone(zoneName, zoneName);

                    addedSoaRecord.CopyRecordInfoFrom(currentSoaRecord);

                    zone.LoadRecords(DnsResourceRecordType.SOA, new DnsResourceRecord[] { addedSoaRecord });
                }

                //check next difference sequence
                currentSoa = addedSoaRecord.RDATA as DnsSOARecordData;

                deletedRecords.Clear();
                deletedGlueRecords.Clear();
                addedRecords.Clear();
                addedGlueRecords.Clear();
            }

            apexZone.UpdateDnssecStatus();

            //return history
            List<DnsResourceRecord> historyRecords = new List<DnsResourceRecord>(xfrRecords.Count - 2);

            for (int i = 1; i < xfrRecords.Count - 1; i++)
                historyRecords.Add(xfrRecords[i]);

            return historyRecords;
        }

        internal void ImportRecords(string zoneName, IReadOnlyList<DnsResourceRecord> records, bool overwrite, bool overwriteSoaSerial)
        {
            _ = _root.FindZone(zoneName, out _, out _, out ApexZone apexZone, out _);
            if ((apexZone is null) || !apexZone.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                throw new DnsServerException("No such zone was found: " + zoneName);

            if ((apexZone is not PrimaryZone) && (apexZone is not ForwarderZone))
                throw new DnsServerException("Zone must be a primary or forwarder type: " + zoneName);

            List<DnsResourceRecord> soaRRSet = null;

            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> zoneEntry in DnsResourceRecord.GroupRecords(records))
            {
                if (zoneName.Equals(zoneEntry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> rrsetEntry in zoneEntry.Value)
                    {
                        switch (rrsetEntry.Key)
                        {
                            case DnsResourceRecordType.CNAME:
                            case DnsResourceRecordType.DNAME:
                                apexZone.SetRecords(rrsetEntry.Key, rrsetEntry.Value);
                                break;

                            case DnsResourceRecordType.SOA:
                                apexZone.SetRecords(rrsetEntry.Key, rrsetEntry.Value);
                                soaRRSet = rrsetEntry.Value;
                                break;

                            default:
                                if (overwrite)
                                {
                                    apexZone.SetRecords(rrsetEntry.Key, rrsetEntry.Value);
                                }
                                else
                                {
                                    foreach (DnsResourceRecord record in rrsetEntry.Value)
                                        apexZone.AddRecord(record);
                                }
                                break;
                        }
                    }
                }
                else
                {
                    ValidateZoneNameFor(zoneName, zoneEntry.Key);

                    AuthZone authZone = GetOrAddSubDomainZone(zoneName, zoneEntry.Key);

                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> rrsetEntry in zoneEntry.Value)
                    {
                        switch (rrsetEntry.Key)
                        {
                            case DnsResourceRecordType.CNAME:
                            case DnsResourceRecordType.DNAME:
                            case DnsResourceRecordType.SOA:
                                authZone.SetRecords(rrsetEntry.Key, rrsetEntry.Value);
                                break;

                            default:
                                if (overwrite)
                                {
                                    authZone.SetRecords(rrsetEntry.Key, rrsetEntry.Value);
                                }
                                else
                                {
                                    foreach (DnsResourceRecord record in rrsetEntry.Value)
                                        authZone.AddRecord(record);
                                }
                                break;
                        }
                    }

                    if (authZone is SubDomainZone subDomainZone)
                        subDomainZone.AutoUpdateState();
                }
            }

            if (overwriteSoaSerial && (soaRRSet is not null) && (apexZone is PrimaryZone primaryZone))
                primaryZone.SetSoaSerial((soaRRSet[0].RDATA as DnsSOARecordData).Serial);
        }

        internal void LoadRecords(ApexZone apexZone, IReadOnlyList<DnsResourceRecord> records)
        {
            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> zoneEntry in DnsResourceRecord.GroupRecords(records))
            {
                if (apexZone.Name.Equals(zoneEntry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> rrsetEntry in zoneEntry.Value)
                        apexZone.LoadRecords(rrsetEntry.Key, rrsetEntry.Value);
                }
                else
                {
                    ValidateZoneNameFor(apexZone.Name, zoneEntry.Key);

                    AuthZone authZone = GetOrAddSubDomainZone(apexZone.Name, zoneEntry.Key);

                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> rrsetEntry in zoneEntry.Value)
                        authZone.LoadRecords(rrsetEntry.Key, rrsetEntry.Value);

                    if (authZone is SubDomainZone subDomainZone)
                        subDomainZone.AutoUpdateState();
                }
            }

            apexZone.UpdateDnssecStatus();
        }

        public void SetRecords(string zoneName, string domain, DnsResourceRecordType type, uint ttl, DnsResourceRecordData[] records)
        {
            ValidateZoneNameFor(zoneName, domain);

            DnsResourceRecord[] resourceRecords = new DnsResourceRecord[records.Length];

            for (int i = 0; i < records.Length; i++)
                resourceRecords[i] = new DnsResourceRecord(domain, type, DnsClass.IN, ttl, records[i]);

            AuthZone authZone = GetOrAddSubDomainZone(zoneName, domain);

            authZone.SetRecords(type, resourceRecords);

            if (authZone is SubDomainZone subDomainZone)
                subDomainZone.AutoUpdateState();
        }

        public void SetRecords(string zoneName, IReadOnlyList<DnsResourceRecord> records)
        {
            for (int i = 1; i < records.Count; i++)
            {
                if (!records[i].Name.Equals(records[0].Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();

                if (records[i].Type != records[0].Type)
                    throw new InvalidOperationException();

                if (records[i].Class != records[0].Class)
                    throw new InvalidOperationException();
            }

            AuthZone authZone = GetOrAddSubDomainZone(zoneName, records[0].Name);

            authZone.SetRecords(records[0].Type, records);

            if (authZone is SubDomainZone subDomainZone)
                subDomainZone.AutoUpdateState();
        }

        public void SetRecord(string zoneName, DnsResourceRecord record)
        {
            ValidateZoneNameFor(zoneName, record.Name);

            AuthZone authZone = GetOrAddSubDomainZone(zoneName, record.Name);

            authZone.SetRecords(record.Type, new DnsResourceRecord[] { record });

            if (authZone is SubDomainZone subDomainZone)
                subDomainZone.AutoUpdateState();
        }

        public void AddRecord(string zoneName, string domain, DnsResourceRecordType type, uint ttl, DnsResourceRecordData record)
        {
            ValidateZoneNameFor(zoneName, domain);

            AuthZone authZone = GetOrAddSubDomainZone(zoneName, domain);

            authZone.AddRecord(new DnsResourceRecord(authZone.Name, type, DnsClass.IN, ttl, record));

            if (authZone is SubDomainZone subDomainZone)
                subDomainZone.AutoUpdateState();
        }

        public void AddRecord(string zoneName, DnsResourceRecord record)
        {
            ValidateZoneNameFor(zoneName, record.Name);

            AuthZone authZone = GetOrAddSubDomainZone(zoneName, record.Name);

            authZone.AddRecord(record);

            if (authZone is SubDomainZone subDomainZone)
                subDomainZone.AutoUpdateState();
        }

        public void UpdateRecord(string zoneName, DnsResourceRecord oldRecord, DnsResourceRecord newRecord)
        {
            ValidateZoneNameFor(zoneName, oldRecord.Name);
            ValidateZoneNameFor(zoneName, newRecord.Name);

            if (oldRecord.Type != newRecord.Type)
                throw new DnsServerException("Cannot update record: new record must be of same type.");

            if (oldRecord.Type == DnsResourceRecordType.SOA)
                throw new DnsServerException("Cannot update record: use SetRecords() for updating SOA record.");

            if (!_root.TryGet(zoneName, oldRecord.Name, out AuthZone authZone))
                throw new DnsServerException("Cannot update record: zone does not exists.");

            switch (oldRecord.Type)
            {
                case DnsResourceRecordType.CNAME:
                case DnsResourceRecordType.DNAME:
                case DnsResourceRecordType.APP:
                    if (oldRecord.Name.Equals(newRecord.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        authZone.SetRecords(newRecord.Type, new DnsResourceRecord[] { newRecord });

                        if (authZone is SubDomainZone subDomainZone)
                            subDomainZone.AutoUpdateState();
                    }
                    else
                    {
                        authZone.DeleteRecords(oldRecord.Type);

                        if (authZone is SubDomainZone subDomainZone)
                        {
                            if (authZone.IsEmpty)
                                _root.TryRemove(oldRecord.Name, out SubDomainZone _); //remove empty sub zone
                            else
                                subDomainZone.AutoUpdateState();
                        }

                        AuthZone newZone = GetOrAddSubDomainZone(zoneName, newRecord.Name);

                        newZone.SetRecords(newRecord.Type, new DnsResourceRecord[] { newRecord });

                        if (newZone is SubDomainZone subDomainZone1)
                            subDomainZone1.AutoUpdateState();
                    }
                    break;

                default:
                    if (oldRecord.Name.Equals(newRecord.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        authZone.UpdateRecord(oldRecord, newRecord);

                        if (authZone is SubDomainZone subDomainZone)
                            subDomainZone.AutoUpdateState();
                    }
                    else
                    {
                        if (!authZone.DeleteRecord(oldRecord.Type, oldRecord.RDATA))
                            throw new DnsWebServiceException("Cannot update record: the old record does not exists.");

                        if (authZone is SubDomainZone subDomainZone)
                        {
                            if (authZone.IsEmpty)
                                _root.TryRemove(oldRecord.Name, out SubDomainZone _); //remove empty sub zone
                            else
                                subDomainZone.AutoUpdateState();
                        }

                        AuthZone newZone = GetOrAddSubDomainZone(zoneName, newRecord.Name);

                        newZone.AddRecord(newRecord);

                        if (newZone is SubDomainZone subDomainZone1)
                            subDomainZone1.AutoUpdateState();
                    }
                    break;
            }
        }

        public bool DeleteRecord(string zoneName, string domain, DnsResourceRecordType type, DnsResourceRecordData record)
        {
            ValidateZoneNameFor(zoneName, domain);

            if (_root.TryGet(zoneName, domain, out AuthZone authZone))
            {
                if (authZone.DeleteRecord(type, record))
                {
                    if (authZone is SubDomainZone subDomainZone)
                    {
                        if (authZone.IsEmpty)
                            _root.TryRemove(domain, out SubDomainZone _); //remove empty sub zone
                        else
                            subDomainZone.AutoUpdateState();
                    }

                    return true;
                }
            }

            return false;
        }

        public bool DeleteRecords(string zoneName, string domain, DnsResourceRecordType type)
        {
            ValidateZoneNameFor(zoneName, domain);

            if (_root.TryGet(zoneName, domain, out AuthZone authZone))
            {
                if (authZone.DeleteRecords(type))
                {
                    if (authZone is SubDomainZone subDomainZone)
                    {
                        if (authZone.IsEmpty)
                            _root.TryRemove(domain, out SubDomainZone _); //remove empty sub zone
                        else
                            subDomainZone.AutoUpdateState();
                    }

                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<AuthZoneInfo> GetAllZones()
        {
            _zoneIndexLock.EnterReadLock();
            try
            {
                return _zoneIndex.ToArray();
            }
            finally
            {
                _zoneIndexLock.ExitReadLock();
            }
        }

        public ZonesPage GetZonesPage(int pageNumber, int zonesPerPage)
        {
            _zoneIndexLock.EnterReadLock();
            try
            {
                int totalZones = _zoneIndex.Count;
                if (totalZones < 1)
                    return new ZonesPage(0, 0, 0, Array.Empty<AuthZoneInfo>());

                if (pageNumber == 0)
                    pageNumber = 1;

                int totalPages = (totalZones / zonesPerPage) + (totalZones % zonesPerPage > 0 ? 1 : 0);

                if ((pageNumber > totalPages) || (pageNumber < 0))
                    pageNumber = totalPages;

                int start = (pageNumber - 1) * zonesPerPage;
                int end = Math.Min(start + zonesPerPage, totalZones);

                List<AuthZoneInfo> zones = new List<AuthZoneInfo>(end - start);

                for (int i = start; i < end; i++)
                    zones.Add(_zoneIndex[i]);

                return new ZonesPage(pageNumber, totalPages, totalZones, zones);
            }
            finally
            {
                _zoneIndexLock.ExitReadLock();
            }
        }

        public void ListSubDomains(string domain, List<string> subDomains)
        {
            _root.ListSubDomains(domain, subDomains);
        }

        public DnsDatagram QueryClosestDelegation(DnsDatagram request)
        {
            _ = _root.FindZone(request.Question[0].Name, out _, out SubDomainZone delegation, out ApexZone apexZone, out _);
            if (delegation is not null)
            {
                bool dnssecOk = request.DnssecOk && (apexZone.DnssecStatus != AuthZoneDnssecStatus.Unsigned);

                return GetReferralResponse(request, dnssecOk, delegation, apexZone);
            }

            //no delegation found
            return null;
        }

        public DnsDatagram Query(DnsDatagram request, bool isRecursionAllowed)
        {
            DnsQuestionRecord question = request.Question[0];

            AuthZone zone = _root.FindZone(question.Name, out SubDomainZone closest, out SubDomainZone delegation, out ApexZone apexZone, out bool hasSubDomains);

            if ((apexZone is null) || !apexZone.IsActive)
                return null; //no authority for requested zone

            bool dnssecOk = request.DnssecOk && (apexZone.DnssecStatus != AuthZoneDnssecStatus.Unsigned);

            if ((zone is null) || !zone.IsActive)
            {
                //zone not found
                if ((delegation is not null) && delegation.IsActive && (delegation.Name.Length > apexZone.Name.Length))
                    return GetReferralResponse(request, dnssecOk, delegation, apexZone);

                if (apexZone is StubZone)
                    return GetReferralResponse(request, false, apexZone, apexZone);

                DnsResponseCode rCode = DnsResponseCode.NoError;
                IReadOnlyList<DnsResourceRecord> answer = null;
                IReadOnlyList<DnsResourceRecord> authority = null;

                if (closest is not null)
                {
                    answer = closest.QueryRecords(DnsResourceRecordType.DNAME, dnssecOk);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        if (!DoDNAMESubstitution(question, dnssecOk, answer, out answer))
                            rCode = DnsResponseCode.YXDomain;
                    }
                    else
                    {
                        answer = null;
                        authority = closest.QueryRecords(DnsResourceRecordType.APP, false);
                    }
                }

                if (((answer is null) || (answer.Count == 0)) && ((authority is null) || (authority.Count == 0)))
                {
                    answer = apexZone.QueryRecords(DnsResourceRecordType.DNAME, dnssecOk);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        if (!DoDNAMESubstitution(question, dnssecOk, answer, out answer))
                            rCode = DnsResponseCode.YXDomain;
                    }
                    else
                    {
                        answer = null;
                        authority = apexZone.QueryRecords(DnsResourceRecordType.APP, false);
                        if (authority.Count == 0)
                        {
                            if (apexZone is ForwarderZone)
                                return GetForwarderResponse(request, null, closest, apexZone); //no DNAME or APP record available so process FWD response

                            if (!hasSubDomains)
                                rCode = DnsResponseCode.NxDomain;

                            authority = apexZone.QueryRecords(DnsResourceRecordType.SOA, dnssecOk);

                            if (dnssecOk)
                            {
                                //add proof of non existence (NXDOMAIN) to prove the qname does not exists
                                IReadOnlyList<DnsResourceRecord> nsecRecords;

                                if (apexZone.DnssecStatus == AuthZoneDnssecStatus.SignedWithNSEC3)
                                    nsecRecords = _root.FindNSec3ProofOfNonExistenceNxDomain(question.Name, false);
                                else
                                    nsecRecords = _root.FindNSecProofOfNonExistenceNxDomain(question.Name, false);

                                if (nsecRecords.Count > 0)
                                {
                                    List<DnsResourceRecord> newAuthority = new List<DnsResourceRecord>(authority.Count + nsecRecords.Count);

                                    newAuthority.AddRange(authority);
                                    newAuthority.AddRange(nsecRecords);

                                    authority = newAuthority;
                                }
                            }
                        }
                    }
                }

                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, true, false, request.RecursionDesired, isRecursionAllowed, false, false, rCode, request.Question, answer, authority);
            }
            else
            {
                //zone found
                if (question.Type == DnsResourceRecordType.DS)
                {
                    if (zone is ApexZone)
                    {
                        if ((delegation is null) || !delegation.IsActive || (delegation.Name.Length > apexZone.Name.Length))
                            return null; //no authoritative parent side delegation zone available to answer for DS

                        zone = delegation; //switch zone to parent side sub domain delegation zone for DS record
                    }
                }
                else if ((delegation is not null) && delegation.IsActive && (delegation.Name.Length > apexZone.Name.Length))
                {
                    //zone is delegation
                    return GetReferralResponse(request, dnssecOk, delegation, apexZone);
                }

                DnsResponseCode rCode = DnsResponseCode.NoError;
                IReadOnlyList<DnsResourceRecord> answer = null;
                IReadOnlyList<DnsResourceRecord> authority = null;
                IReadOnlyList<DnsResourceRecord> additional = null;

                if (closest is not null)
                {
                    answer = closest.QueryRecords(DnsResourceRecordType.DNAME, dnssecOk);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        if (!DoDNAMESubstitution(question, dnssecOk, answer, out answer))
                            rCode = DnsResponseCode.YXDomain;
                    }
                }

                if ((answer is null) || (answer.Count == 0))
                {
                    answer = apexZone.QueryRecords(DnsResourceRecordType.DNAME, dnssecOk);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        if (!DoDNAMESubstitution(question, dnssecOk, answer, out answer))
                            rCode = DnsResponseCode.YXDomain;
                    }
                    else
                    {
                        answer = zone.QueryRecords(question.Type, dnssecOk);
                        if (answer.Count == 0)
                        {
                            //record type not found
                            if (question.Type == DnsResourceRecordType.DS)
                            {
                                //check for correct auth zone
                                if (apexZone.Name.Equals(question.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    //current auth zone is child side; find parent side auth zone for DS
                                    string parentZone = GetParentZone(question.Name);
                                    if (parentZone is null)
                                        parentZone = string.Empty;

                                    _ = _root.FindZone(parentZone, out _, out _, out apexZone, out _);

                                    if ((apexZone is null) || !apexZone.IsActive)
                                        return null; //no authority for requested zone
                                }
                            }
                            else
                            {
                                //check for delegation, stub & forwarder
                                if ((delegation is not null) && delegation.IsActive && (delegation.Name.Length > apexZone.Name.Length))
                                    return GetReferralResponse(request, dnssecOk, delegation, apexZone);

                                if (apexZone is StubZone)
                                    return GetReferralResponse(request, false, apexZone, apexZone);
                            }

                            authority = zone.QueryRecords(DnsResourceRecordType.APP, false);
                            if (authority.Count == 0)
                            {
                                if (apexZone is ForwarderZone)
                                    return GetForwarderResponse(request, zone, closest, apexZone); //no APP record available so process FWD response

                                authority = apexZone.QueryRecords(DnsResourceRecordType.SOA, dnssecOk);

                                if (dnssecOk)
                                {
                                    //add proof of non existence (NODATA) to prove that no such type or record exists
                                    IReadOnlyList<DnsResourceRecord> nsecRecords;

                                    if (apexZone.DnssecStatus == AuthZoneDnssecStatus.SignedWithNSEC3)
                                        nsecRecords = _root.FindNSec3ProofOfNonExistenceNoData(zone, apexZone);
                                    else
                                        nsecRecords = AuthZoneTree.FindNSecProofOfNonExistenceNoData(zone);

                                    if (nsecRecords.Count > 0)
                                    {
                                        List<DnsResourceRecord> newAuthority = new List<DnsResourceRecord>(authority.Count + nsecRecords.Count);

                                        newAuthority.AddRange(authority);
                                        newAuthority.AddRange(nsecRecords);

                                        authority = newAuthority;
                                    }
                                }
                            }

                            additional = null;
                        }
                        else
                        {
                            //record type found
                            if (zone.Name.StartsWith('*') && !zone.Name.Equals(question.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                //wildcard zone; generate new answer records
                                DnsResourceRecord[] wildcardAnswers = new DnsResourceRecord[answer.Count];

                                for (int i = 0; i < answer.Count; i++)
                                    wildcardAnswers[i] = new DnsResourceRecord(question.Name, answer[i].Type, answer[i].Class, answer[i].TTL, answer[i].RDATA) { Tag = answer[i].Tag };

                                answer = wildcardAnswers;

                                //add proof of non existence (WILDCARD) to prove that the wildcard expansion was legit and the qname actually does not exists
                                if (dnssecOk)
                                {
                                    IReadOnlyList<DnsResourceRecord> nsecRecords;

                                    if (apexZone.DnssecStatus == AuthZoneDnssecStatus.SignedWithNSEC3)
                                        nsecRecords = _root.FindNSec3ProofOfNonExistenceNxDomain(question.Name, true);
                                    else
                                        nsecRecords = _root.FindNSecProofOfNonExistenceNxDomain(question.Name, true);

                                    if (nsecRecords.Count > 0)
                                        authority = nsecRecords;
                                }
                            }

                            DnsResourceRecord lastRR = answer[answer.Count - 1];
                            if ((lastRR.Type != question.Type) && (question.Type != DnsResourceRecordType.ANY))
                            {
                                switch (lastRR.Type)
                                {
                                    case DnsResourceRecordType.CNAME:
                                        List<DnsResourceRecord> newAnswers = new List<DnsResourceRecord>(answer.Count + 1);
                                        newAnswers.AddRange(answer);

                                        ResolveCNAME(question, dnssecOk, lastRR, newAnswers);

                                        answer = newAnswers;
                                        break;

                                    case DnsResourceRecordType.ANAME:
                                    case DnsResourceRecordType.ALIAS:
                                        authority = apexZone.GetRecords(DnsResourceRecordType.SOA); //adding SOA for use with NO DATA response
                                        break;
                                }
                            }

                            switch (question.Type)
                            {
                                case DnsResourceRecordType.NS:
                                case DnsResourceRecordType.MX:
                                case DnsResourceRecordType.SRV:
                                case DnsResourceRecordType.SVCB:
                                case DnsResourceRecordType.HTTPS:
                                    additional = GetAdditionalRecords(answer, dnssecOk);
                                    break;

                                default:
                                    additional = null;
                                    break;
                            }
                        }
                    }
                }

                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, true, false, request.RecursionDesired, isRecursionAllowed, false, false, rCode, request.Question, answer, authority, additional);
            }
        }

        public void LoadTrustAnchorsTo(DnsClient dnsClient, string domain, DnsResourceRecordType type)
        {
            if (type == DnsResourceRecordType.DS)
            {
                domain = GetParentZone(domain);
                if (domain is null)
                    domain = "";
            }

            AuthZoneInfo zoneInfo = _dnsServer.AuthZoneManager.FindAuthZoneInfo(domain, false);
            if ((zoneInfo is not null) && (zoneInfo.DnssecStatus != AuthZoneDnssecStatus.Unsigned))
            {
                IReadOnlyList<DnsResourceRecord> dnsKeyRecords = zoneInfo.GetApexRecords(DnsResourceRecordType.DNSKEY);

                foreach (DnsResourceRecord dnsKeyRecord in dnsKeyRecords)
                {
                    DnsDNSKEYRecordData dnsKey = dnsKeyRecord.RDATA as DnsDNSKEYRecordData;

                    if (dnsKey.Flags.HasFlag(DnsDnsKeyFlag.SecureEntryPoint) && !dnsKey.Flags.HasFlag(DnsDnsKeyFlag.Revoke))
                    {
                        DnsDSRecordData dsRecord = dnsKey.CreateDS(dnsKeyRecord.Name, DnssecDigestType.SHA256);
                        dnsClient.AddTrustAnchor(zoneInfo.Name, dsRecord);
                    }
                }
            }
        }

        public AuthZoneInfo LoadZoneFrom(Stream s, DateTime lastModified)
        {
            BinaryReader bR = new BinaryReader(s);

            if (Encoding.ASCII.GetString(bR.ReadBytes(2)) != "DZ")
                throw new InvalidDataException("DnsServer zone file format is invalid.");

            switch (bR.ReadByte())
            {
                case 2:
                    {
                        DnsResourceRecord[] records = new DnsResourceRecord[bR.ReadInt32()];
                        if (records.Length == 0)
                            throw new InvalidDataException("Zone does not contain SOA record.");

                        DnsResourceRecord soaRecord = null;

                        for (int i = 0; i < records.Length; i++)
                        {
                            records[i] = new DnsResourceRecord(s);

                            if (records[i].Type == DnsResourceRecordType.SOA)
                                soaRecord = records[i];
                        }

                        if (soaRecord == null)
                            throw new InvalidDataException("Zone does not contain SOA record.");

                        //make zone info
                        AuthZoneType zoneType;
                        if (_dnsServer.ServerDomain.Equals((soaRecord.RDATA as DnsSOARecordData).PrimaryNameServer, StringComparison.OrdinalIgnoreCase))
                            zoneType = AuthZoneType.Primary;
                        else
                            zoneType = AuthZoneType.Stub;

                        AuthZoneInfo zoneInfo = new AuthZoneInfo(records[0].Name, zoneType, false);

                        //create zone
                        ApexZone apexZone = CreateEmptyZone(zoneInfo);

                        try
                        {
                            //load records
                            LoadRecords(apexZone, records);
                        }
                        catch
                        {
                            DeleteZone(zoneInfo.Name);
                            throw;
                        }

                        //init zone
                        switch (zoneInfo.Type)
                        {
                            case AuthZoneType.Primary:
                                (apexZone as PrimaryZone).TriggerNotify();
                                break;
                        }

                        return new AuthZoneInfo(apexZone);
                    }

                case 3:
                    {
                        bool zoneDisabled = bR.ReadBoolean();
                        DnsResourceRecord[] records = new DnsResourceRecord[bR.ReadInt32()];
                        if (records.Length == 0)
                            throw new InvalidDataException("Zone does not contain SOA record.");

                        DnsResourceRecord soaRecord = null;

                        for (int i = 0; i < records.Length; i++)
                        {
                            records[i] = new DnsResourceRecord(s);
                            records[i].Tag = AuthRecordInfo.ReadGenericRecordInfoFrom(bR, records[i].Type);

                            if (records[i].Type == DnsResourceRecordType.SOA)
                                soaRecord = records[i];
                        }

                        if (soaRecord == null)
                            throw new InvalidDataException("Zone does not contain SOA record.");

                        //make zone info
                        AuthZoneType zoneType;
                        if (_dnsServer.ServerDomain.Equals((soaRecord.RDATA as DnsSOARecordData).PrimaryNameServer, StringComparison.OrdinalIgnoreCase))
                            zoneType = AuthZoneType.Primary;
                        else
                            zoneType = AuthZoneType.Stub;

                        AuthZoneInfo zoneInfo = new AuthZoneInfo(records[0].Name, zoneType, zoneDisabled);

                        //create zone
                        ApexZone apexZone = CreateEmptyZone(zoneInfo);

                        try
                        {
                            //load records
                            LoadRecords(apexZone, records);
                        }
                        catch
                        {
                            DeleteZone(zoneInfo.Name);
                            throw;
                        }

                        //init zone
                        switch (zoneInfo.Type)
                        {
                            case AuthZoneType.Primary:
                                (apexZone as PrimaryZone).TriggerNotify();
                                break;
                        }

                        return new AuthZoneInfo(apexZone);
                    }

                case 4:
                    {
                        //read zone info
                        AuthZoneInfo zoneInfo = new AuthZoneInfo(bR, lastModified);

                        //create zone
                        ApexZone apexZone = CreateEmptyZone(zoneInfo);

                        //read all zone records
                        DnsResourceRecord[] records = new DnsResourceRecord[bR.ReadInt32()];
                        if (records.Length > 0)
                        {
                            for (int i = 0; i < records.Length; i++)
                            {
                                records[i] = new DnsResourceRecord(s);
                                records[i].Tag = AuthRecordInfo.ReadGenericRecordInfoFrom(bR, records[i].Type);
                            }

                            try
                            {
                                //load records
                                LoadRecords(apexZone, records);
                            }
                            catch
                            {
                                DeleteZone(zoneInfo.Name);
                                throw;
                            }

                            //init zone
                            switch (zoneInfo.Type)
                            {
                                case AuthZoneType.Primary:
                                    (apexZone as PrimaryZone).TriggerNotify();
                                    break;

                                case AuthZoneType.Secondary:
                                    SecondaryZone secondary = apexZone as SecondaryZone;

                                    secondary.TriggerNotify();
                                    secondary.TriggerRefresh();
                                    break;

                                case AuthZoneType.Stub:
                                    (apexZone as StubZone).TriggerRefresh();
                                    break;
                            }
                        }

                        return new AuthZoneInfo(apexZone);
                    }

                default:
                    throw new InvalidDataException("DNS Zone file version not supported.");
            }
        }

        public void WriteZoneTo(string zoneName, Stream s)
        {
            AuthZoneInfo zoneInfo = GetAuthZoneInfo(zoneName, true);
            if (zoneInfo is null)
                return;

            //serialize zone
            BinaryWriter bW = new BinaryWriter(s);

            bW.Write(Encoding.ASCII.GetBytes("DZ")); //format
            bW.Write((byte)4); //version

            //write zone info
            if (zoneInfo.Internal)
                throw new InvalidOperationException("Cannot save zones marked as internal.");

            zoneInfo.WriteTo(bW);

            //write all zone records
            List<DnsResourceRecord> records = new List<DnsResourceRecord>();
            ListAllZoneRecords(zoneName, records);

            bW.Write(records.Count);

            foreach (DnsResourceRecord record in records)
            {
                record.WriteTo(s);
                record.GetAuthGenericRecordInfo().WriteTo(bW);
            }
        }

        public void SaveZoneFile(string zoneName)
        {
            zoneName = zoneName.ToLowerInvariant();

            lock (_saveLock)
            {
                if (!_pendingSaveZones.TryAdd(zoneName, null))
                    return;

                if (_pendingSaveZones.Count == 1)
                    _saveTimer.Change(SAVE_TIMER_INITIAL_INTERVAL, Timeout.Infinite);
            }
        }

        public void DeleteZoneFile(string zoneName)
        {
            zoneName = zoneName.ToLower();

            File.Delete(Path.Combine(_dnsServer.ConfigFolder, "zones", zoneName + ".zone"));

            _dnsServer.LogManager?.Write("Deleted zone file for domain: " + zoneName);
        }

        #endregion

        #region properties

        public bool UseSoaSerialDateScheme
        {
            get { return _useSoaSerialDateScheme; }
            set { _useSoaSerialDateScheme = value; }
        }

        public int TotalZones
        { get { return _zoneIndex.Count; } }

        #endregion

        public class ZonesPage
        {
            #region variables

            readonly long _pageNumber;
            readonly long _totalPages;
            readonly long _totalZones;
            readonly IReadOnlyList<AuthZoneInfo> _zones;

            #endregion

            #region constructor

            public ZonesPage(long pageNumber, long totalPages, long totalZones, IReadOnlyList<AuthZoneInfo> zones)
            {
                _pageNumber = pageNumber;
                _totalPages = totalPages;
                _totalZones = totalZones;
                _zones = zones;
            }

            #endregion

            #region properties

            public long PageNumber
            { get { return _pageNumber; } }

            public long TotalPages
            { get { return _totalPages; } }

            public long TotalZones
            { get { return _totalZones; } }

            public IReadOnlyList<AuthZoneInfo> Zones
            { get { return _zones; } }

            #endregion
        }
    }
}
