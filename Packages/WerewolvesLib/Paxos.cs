using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;

namespace WerewolvesLib
{
    // Define the steps of the algorithm
    public enum PaxosPhase
    {
        Prepare,
        Promise,
        Accept,
        Accepted,
        Decide // Optional: Tells everyone "Consensus reached, run the game logic"
    }

    public class PlayerInfoPacket : INetSerializable
    {
        public int NodeID;
        public string Name;

        // Writing logic
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NodeID);
            writer.Put(Name);
        }

        // Reading logic
        public void Deserialize(NetDataReader reader)
        {
            NodeID = reader.GetInt();
            Name = reader.GetString();
        }
    }

    public class PaxosPacket : INetSerializable
    {
        public PaxosPhase Phase;
        public int SlotID;
        public int NodeID;
        public long ProposalID;

        // Nullable fields need handling
        public string Value;
        public long LastAcceptedID;
        public string LastAcceptedValue;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((int)Phase); // Cast Enum to int
            writer.Put(SlotID);
            writer.Put(NodeID);
            writer.Put(ProposalID);

            // Handle potentially null strings
            writer.Put(Value ?? "");
            writer.Put(LastAcceptedID);
            writer.Put(LastAcceptedValue ?? "");
        }

        public void Deserialize(NetDataReader reader)
        {
            Phase = (PaxosPhase)reader.GetInt();
            SlotID = reader.GetInt();
            NodeID = reader.GetInt();
            ProposalID = reader.GetLong();

            Value = reader.GetString();
            if (Value == "") Value = null;

            LastAcceptedID = reader.GetLong();

            LastAcceptedValue = reader.GetString();
            if (LastAcceptedValue == "") LastAcceptedValue = null;
        }
    }

    public class PaxosSlotState
    {
        // Phase 1 Variables (Promise Phase)
        public long HighestPromisedID = -1;
        public int PromiseCount = 0;

        // Phase 2 Variables (Accept Phase)
        public long AcceptedID = -1;
        public string AcceptedValue = null;

        // Phase 3 Variables (Learner Phase)
        public int AcceptedCount = 0;
        public bool ConsensusReached = false;

        // Proposer Logic
        public bool Phase2Started = false;
        public string MyProposedValue = null;
    }

    public class Paxos
    {
        private NetworkManager _netManager;
        private Dictionary<int, PaxosSlotState> _paxosLog = new Dictionary<int, PaxosSlotState>();

        // Event to tell Unity that consensus was reached
        public Action<int, string> OnConsensusReached;
        public Action<string> OnLog;

        public Paxos(NetworkManager net)
        {
            _netManager = net;
            _netManager.PacketProcessor.SubscribeNetSerializable<PaxosPacket, NetPeer>(
                (packet, peer) => OnPaxosMessageReceived(packet)
            );
        }

        public void Propose(int slotID, string value)
        {
            // (Use the Logic we wrote earlier, adjusted for Pure C#)
            if (!_paxosLog.ContainsKey(slotID)) _paxosLog[slotID] = new PaxosSlotState();

            var state = _paxosLog[slotID];
            state.PromiseCount = 0;
            state.Phase2Started = false;
            state.MyProposedValue = value;

            long proposalID = DateTime.Now.Ticks + _netManager.MyNodeID;

            var packet = new PaxosPacket
            {
                Phase = PaxosPhase.Prepare,
                SlotID = slotID,
                ProposalID = proposalID,
                NodeID = _netManager.MyNodeID
            };

            SendToAll(packet);
            OnLog?.Invoke($"[Paxos] Proposing {value} for Slot {slotID}");
        }

        // --- NETWORK RECEIVER ---
        private void OnPaxosMessageReceived(PaxosPacket packet)
        {
            // Ensure we have memory for this specific slot
            if (!_paxosLog.ContainsKey(packet.SlotID))
                _paxosLog[packet.SlotID] = new PaxosSlotState();

            switch (packet.Phase)
            {
                case PaxosPhase.Prepare:
                    HandlePrepare(packet);
                    break;
                case PaxosPhase.Promise:
                    HandlePromise(packet);
                    break;
                case PaxosPhase.Accept:
                    HandleAccept(packet);
                    break;
                case PaxosPhase.Accepted:
                    HandleAccepted(packet);
                    break;
            }
        }

        // --- HANDLERS ---

        // 1. Acceptor receives PREPARE
        private void HandlePrepare(PaxosPacket p)
        {
            var state = _paxosLog[p.SlotID];

            // Check against the highest ID we have ever promised
            if (p.ProposalID > state.HighestPromisedID)
            {
                // Update our promise
                state.HighestPromisedID = p.ProposalID;

                // Reply with PROMISE
                var reply = new PaxosPacket
                {
                    Phase = PaxosPhase.Promise,
                    SlotID = p.SlotID,
                    ProposalID = p.ProposalID,
                    NodeID = _netManager.MyNodeID,

                    // IMPORTANT: Tell them if we already accepted something previously
                    LastAcceptedID = state.AcceptedID,
                    LastAcceptedValue = state.AcceptedValue
                };

                // In a pro implementation, you send this ONLY to the Proposer.
                // For this assignment, broadcasting is fine and easier.
                SendToAll(reply);
            }
        }

        // 2. Proposer receives PROMISE (Not implemented fully here, depends on your Logic)
        private void HandlePromise(PaxosPacket p)
        {
            var state = _paxosLog[p.SlotID];

            if (!string.IsNullOrEmpty(p.LastAcceptedValue))
            {
                // Log this important event
                OnLog?.Invoke($"[Paxos] Conflict! Switching my proposal from {state.MyProposedValue} to {p.LastAcceptedValue}");
                state.MyProposedValue = p.LastAcceptedValue;
            }

            state.PromiseCount++;

            state.PromiseCount++;

            int totalNodes = _netManager.ConnectedPeers.Count + 1; // Changed _netManager to _net for consistency
            int quorum = (totalNodes / 2) + 1;

            if (state.PromiseCount >= quorum && !state.Phase2Started)
            {
                state.Phase2Started = true;

                // 2. Use the value we stored in state (which might have been updated above)
                string valueToPropose = state.MyProposedValue;

                // 3. FINAL SAFETY CHECK (The NPE Fix)
                if (string.IsNullOrEmpty(valueToPropose))
                {
                    OnLog?.Invoke("[Error] Paxos attempted to Accept a NULL value. Aborting Phase 2.");
                    return; // STOP! Do not send a null packet.
                }

                // --- START PHASE 2 (ACCEPT) ---
                var acceptPacket = new PaxosPacket
                {
                    Phase = PaxosPhase.Accept,
                    SlotID = p.SlotID,
                    ProposalID = p.ProposalID,
                    NodeID = _netManager.MyNodeID,
                    Value = valueToPropose
                };

                SendToAll(acceptPacket); // Changed SendToAll to SendPacket to include Loopback logic
            }
        }

        // 3. Acceptor receives ACCEPT
        private void HandleAccept(PaxosPacket p)
        {
            var state = _paxosLog[p.SlotID];
            state.AcceptedCount++;

            if (p.ProposalID >= state.HighestPromisedID)
            {
                state.HighestPromisedID = p.ProposalID;
                state.AcceptedID = p.ProposalID;
                state.AcceptedValue = p.Value;

                // Broadcast ACCEPTED
                var reply = new PaxosPacket
                {
                    Phase = PaxosPhase.Accepted,
                    SlotID = p.SlotID,
                    ProposalID = p.ProposalID,
                    NodeID = _netManager.MyNodeID,
                    Value = p.Value
                };
                SendToAll(reply);
            }
        }

        // 4. Learner receives ACCEPTED
        private void HandleAccepted(PaxosPacket p)
        {
            var state = _paxosLog[p.SlotID];
            state.AcceptedCount++;

            int totalNodes = _netManager.ConnectedPeers.Count + 1;
            int quorum = (totalNodes / 2) + 1;

            if (state.AcceptedCount >= quorum && !state.ConsensusReached)
            {
                state.ConsensusReached = true;

                // JUST REPORT THE RESULT. Do not retry here.
                OnConsensusReached?.Invoke(p.SlotID, p.Value); // Note: Passing SlotID back is helpful
                OnLog?.Invoke($"[Paxos] Slot {p.SlotID} Consensus: {p.Value}");
            }
        }

        // --- THE SEND METHOD ---

        private void SendToAll(PaxosPacket packet)
        {
            if (_netManager.PacketProcessor == null) return;

            NetDataWriter writer = new NetDataWriter();
            _netManager.PacketProcessor.WriteNetSerializable(writer, ref packet);
            _netManager.SendToAll(writer);
            OnPaxosMessageReceived(packet);
        }
    }
}
