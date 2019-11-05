﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace PrivateWin10
{
    [Serializable()]
    public class Program
    {
        //public Guid guid;

        public ProgramID ID;
        public string Description;

        [NonSerialized()] // Note: BinaryFormatter can handle circular references
        public ProgramSet ProgSet = null;

        [NonSerialized()]
        public Dictionary<string, FirewallRuleEx> Rules = new Dictionary<string, FirewallRuleEx>();

        [NonSerialized()]
        public Dictionary<Guid, NetworkSocket> Sockets = new Dictionary<Guid, NetworkSocket>();

        [NonSerialized()]
        public List<LogEntry> Log = new List<LogEntry>();

        [NonSerialized()]
        public Dictionary<string, DnsEntry> DnsLog = new Dictionary<string, DnsEntry>();

        public DateTime lastAllowed = DateTime.MinValue;
        public int countAllowed = 0;
        public DateTime lastBlocked = DateTime.MinValue;
        public int countBlocked = 0;

        public int SocketCount = 0;
        public UInt64 UploadRate = 0;
        public UInt64 DownloadRate = 0;

        public void AssignSet(ProgramSet progSet)
        {
            // unlink old config
            if (ProgSet != null)
                ProgSet.Programs.Remove(ID);

            // link program with its config
            ProgSet = progSet;
            ProgSet.Programs.Add(ID, this);
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c)
        {
            /*Rules = new Dictionary<Guid, FirewallRule>();
            Sockets = new Dictionary<Guid, NetworkSocket>();
            Log = new List<LogEntry>();*/
        }

        public Program()
        {
            //guid = Guid.NewGuid();
        }

        public Program(ProgramID progID)
        {
            //guid = Guid.NewGuid();

            ID = progID.Duplicate();

            string Name = "";
            string Info = null;

            switch (ID.Type)
            {
                case ProgramID.Types.System:
                    Name = Translate.fmt("name_system");
                    break;
                case ProgramID.Types.Global:
                    Name = Translate.fmt("name_global");
                    break;
                case ProgramID.Types.Program:
                    Name = System.IO.Path.GetFileName(ID.Path);
                    Info = MiscFunc.GetExeDescription(ID.Path);
                    break;
                case ProgramID.Types.Service:
                    Name = ID.GetServiceId();
                    Info = ID.GetServiceName();
                    break;
                case ProgramID.Types.App:
                    Name = ID.GetPackageName();
                    Info = App.PkgMgr?.GetAppInfoBySid(ID.GetPackageSID())?.Name;
                    break;
            }

            if (Info != null && Info.Length > 0)
                Description = Info + " (" + Name + ")";
            else
                Description = Name;
        }

        public bool Update()
        {
            UInt64 uploadRate = 0;
            UInt64 downloadRate = 0;
            foreach (NetworkSocket Socket in Sockets.Values)
            {
                uploadRate += Socket.Stats.UploadRate.ByteRate;
                downloadRate += Socket.Stats.DownloadRate.ByteRate;
            }

            if (UploadRate != uploadRate || DownloadRate != downloadRate || SocketCount != Sockets.Count)
            {
                SocketCount = Sockets.Count;

                UploadRate = uploadRate;
                DownloadRate = downloadRate;
                return true;
            }

            return false;
        }

        public bool IsSpecial()
        {
            if (ID.Type == ProgramID.Types.System || ID.Type == ProgramID.Types.Global)
                return true;
            return false;
        }

        public void AddLogEntry(LogEntry logEntry)
        {
            switch (logEntry.FwEvent.Action)
            {
                case FirewallRule.Actions.Allow:
                    countBlocked++;
                    lastAllowed = logEntry.FwEvent.TimeStamp;
                    break;
                case FirewallRule.Actions.Block:
                    countAllowed++;
                    lastBlocked = logEntry.FwEvent.TimeStamp;
                    break;
            }

            // add to log
            Log.Add(logEntry);
            while (Log.Count > ProgramList.MaxLogLength)
                Log.RemoveAt(0);
        }

        public FirewallRule.Actions LookupRuleAction(FirewallEvent FwEvent, int FwProfile)
        {
            int BlockRules = 0;
            int AllowRules = 0;
            foreach (FirewallRule rule in Rules.Values)
            {
                if (!rule.Enabled)
                    continue;
                if (rule.Direction != FwEvent.Direction)
                    continue;
                if (rule.Protocol != (int)NetFunc.KnownProtocols.Any && FwEvent.Protocol != rule.Protocol)
                    continue;
                if ((FwProfile & rule.Profile) == 0)
                    continue;
                if (!rule.MatchRemoteEndpoint(FwEvent.RemoteAddress, FwEvent.RemotePort))
                    continue;
                if (!rule.MatchLocalEndpoint(FwEvent.LocalAddress, FwEvent.LocalPort))
                    continue;

                if (rule.Action == FirewallRule.Actions.Allow)
                    AllowRules++;
                else if (rule.Action == FirewallRule.Actions.Block)
                    BlockRules++;
            }

            // Note: block rules take precedence
            if (BlockRules > 0)
                return FirewallRule.Actions.Block;
            if (AllowRules > 0)
                return FirewallRule.Actions.Allow;
            return FirewallRule.Actions.Undefined;
        }

        [Serializable()]
        public class LogEntry : DnsInspector.WithHost
        {
            public Guid guid;

            public ProgramID ProgID;
            public FirewallEvent FwEvent;

            public enum States
            {
                Undefined = 0,
                FromLog,
                UnRuled, // there was no rule found for this connection
                RuleAllowed,
                RuleBlocked,
                RuleError, // a rule was found but it appears it was not obeyed (!)
            }
            public States State = States.Undefined;

            public void CheckAction(FirewallRule.Actions action)
            {
                switch (action)
                {
                    case FirewallRule.Actions.Undefined:
                        State = States.UnRuled;
                        break;
                    case FirewallRule.Actions.Allow:
                        if (FwEvent.Action == FirewallRule.Actions.Allow)
                            State = LogEntry.States.RuleAllowed;
                        else
                            State = LogEntry.States.RuleError;
                        break;
                    case FirewallRule.Actions.Block:
                        if (FwEvent.Action == FirewallRule.Actions.Block)
                            State = LogEntry.States.RuleBlocked;
                        else
                            State = LogEntry.States.RuleError;
                        break;
                }
            }

            public LogEntry(FirewallEvent Event, ProgramID progID)
            {
                guid = Guid.NewGuid();
                FwEvent = Event;
                ProgID = progID;
            }
        }

        public void AddSocket(NetworkSocket socket)
        {
            socket.Assigned = true;
            Sockets.Add(socket.guid, socket);
        }

        public void RemoveSocket(NetworkSocket socket)
        {
            Sockets.Remove(socket.guid);
        }


        [Serializable()]
        public class DnsEntry
        {
            public Guid guid;
            public ProgramID ProgID;
            public string HostName;
            //public IPAddress LastSeenIP;
            public DateTime LastSeen;
            public int SeenCounter;

            public DnsEntry(ProgramID progID)
            {
                guid = Guid.NewGuid();
                ProgID = progID;
                LastSeen = DateTime.Now;
                SeenCounter = 0;
            }

            public void Store(XmlWriter writer)
            {
                writer.WriteStartElement("Entry");

                writer.WriteElementString("HostName", HostName);
                writer.WriteElementString("LastSeen", LastSeen.ToString());
                writer.WriteElementString("SeenCounter", SeenCounter.ToString());

                writer.WriteEndElement();
            }

            public bool Load(XmlNode entryNode)
            {
                foreach (XmlNode node in entryNode.ChildNodes)
                {
                    if (node.Name == "HostName")
                        HostName = node.InnerText;
                    else if (node.Name == "LastSeenUTC")
                        DateTime.TryParse(node.InnerText, out LastSeen);
                    else if (node.Name == "SeenCounter")
                        int.TryParse(node.InnerText, out SeenCounter);
                }
                return HostName != null;
            }
        }

        public void LogDomain(string HostName)
        {
            DnsEntry Entry = null;
            if (!DnsLog.TryGetValue(HostName, out Entry))
            {
                Entry = new DnsEntry(ID);
                Entry.HostName = HostName;
                DnsLog.Add(HostName, Entry);
            }
            else
                Entry.LastSeen = DateTime.Now;
            //Entry.LastSeenIP = IP;
            Entry.SeenCounter++;
        }


        public void Store(XmlWriter writer)
        {
            writer.WriteStartElement("Program");

            // Note: ID must be first!!!
            ID.Store(writer);

            writer.WriteElementString("Description", Description);

            writer.WriteStartElement("FwRules");
            foreach (FirewallRuleEx rule in Rules.Values)
                rule.Store(writer);
            writer.WriteEndElement();

            writer.WriteStartElement("DnsLog");
            foreach (DnsEntry Entry in DnsLog.Values)
                Entry.Store(writer);
            writer.WriteEndElement();

            writer.WriteEndElement();
        }

        public bool Load(XmlNode entryNode)
        {
            foreach (XmlNode node in entryNode.ChildNodes)
            {
                if (node.Name == "ID")
                {
                    ProgramID id = new ProgramID();
                    if (id.Load(node))
                        ID = id;
                }
                else if (node.Name == "Description")
                    Description = node.InnerText;
                else if (node.Name == "FwRules")
                {
                    foreach (XmlNode childNode in node.ChildNodes)
                    {
                        FirewallRuleEx rule = new FirewallRuleEx();
                        rule.ProgID = ID;
                        if (rule.Load(childNode) && !Rules.ContainsKey(rule.guid))
                            Rules.Add(rule.guid, rule);
                        else
                            App.LogError("Failed to load Firewall RuleEx {0} in {1}", rule.Name != null ? rule.Name : "[un named]", this.Description);
                    }
                }
                else if (node.Name == "DnsLog")
                {
                    foreach (XmlNode childNode in node.ChildNodes)
                    {
                        DnsEntry Entry = new DnsEntry(ID);
                        if (Entry.Load(childNode) && !DnsLog.ContainsKey(Entry.HostName))
                            DnsLog.Add(Entry.HostName, Entry);
                        else
                            App.LogError("Failed to load DnsLog Entry in {0}", this.Description);
                    }
                }
                else
                    AppLog.Debug("Unknown Program Value, '{0}':{1}", node.Name, node.InnerText);
            }
            return ID != null;
        }
    }
}