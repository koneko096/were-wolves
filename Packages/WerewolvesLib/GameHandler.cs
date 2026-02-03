using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WerewolvesLib
{
    public enum GamePhase
    {
        Lobby,
        Night,
        Day,
        GameOver
    }

    public enum PlayerRole
    {
        Villager,
        Werewolf,
        Dead
    }

    public enum VoteType
    {
        WolfKill,
        VillagerLynch
    }

    public class GameHandler
    {
        private NetworkManager _network;
        private Paxos _paxos;

        private int _nextOpenSlot = 1;
        private string _myPendingVote = null;

        // GAME STATE (simplified inline)
        private GamePhase _currentPhase = GamePhase.Lobby;
        private Dictionary<int, PlayerRole> _playerRoles = new Dictionary<int, PlayerRole>();
        private HashSet<int> _alivePlayers = new HashSet<int>();
        private HashSet<int> _startVotes = new HashSet<int>();

        // VOTE SESSION (simplified inline)
        private VoteType? _currentVoteType = null;
        private Dictionary<int, int> _currentVotes = new Dictionary<int, int>();
        private HashSet<int> _eligibleVoters = new HashSet<int>();

        // IDENTITY
        public string MyName { get; private set; } = "Unknown";
        private Dictionary<int, string> _playerNames = new Dictionary<int, string>();

        // Events for Unity to update UI
        public event Action<string> OnLogMessage;
        public event Action<string> OnGameCommandExecuted;
        public event Action<GamePhase> OnPhaseChanged;
        public event Action<int, string> OnPlayerJoined;  // nodeID, name
        public event Action<int> OnPlayerLeft;  // nodeID
        public event Action<int, int, int, int> OnVoteReceived;  // voterID, targetID, voteCount, requiredVotes

        public GameHandler(int port = 9050)
        {
            _network = new NetworkManager(port);
            _network.OnLog = (msg) => OnLogMessage?.Invoke(msg);

            _paxos = new Paxos(_network);
            _paxos.OnLog = (msg) => OnLogMessage?.Invoke(msg);
            _paxos.OnConsensusReached += HandleConsensus;

            _network.PacketProcessor.SubscribeNetSerializable<PlayerInfoPacket, NetPeer>(
                (packet, peer) => OnPlayerInfoReceived(packet)
            );
            _network.OnValidateConnection = HandleConnectionValidation;
            _network.OnPeerConnected += SendMyName;
            _network.OnPeerDisconnected += OnPeerDisconnected;

            // Add myself to the game state
            _alivePlayers.Add(_network.MyNodeID);
        }

        // --- IDENTITY METHODS ---

        public void SetLocalPlayerName(string name)
        {
            MyName = name;
            _playerNames[_network.MyNodeID] = name + " (Me)";
            _network.LocalName = name;
        }

        private void SendMyName(NetPeer peer)
        {
            var packet = new PlayerInfoPacket
            {
                NodeID = _network.MyNodeID,
                Name = MyName
            };

            NetDataWriter writer = new NetDataWriter();
            _network.PacketProcessor.WriteNetSerializable(writer, ref packet);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        private void OnPlayerInfoReceived(PlayerInfoPacket packet)
        {
            if (!_playerNames.ContainsKey(packet.NodeID))
            {
                _playerNames[packet.NodeID] = packet.Name;
                _alivePlayers.Add(packet.NodeID);
                OnLogMessage?.Invoke($"[Lobby] Registered Player: {packet.Name} (ID: {packet.NodeID})");
                OnPlayerJoined?.Invoke(packet.NodeID, packet.Name);
            }
        }

        private void OnPeerDisconnected(int nodeID)
        {
            _alivePlayers.Remove(nodeID);
            _playerRoles.Remove(nodeID);
            _startVotes.Remove(nodeID);
            _playerNames.Remove(nodeID);
            OnLogMessage?.Invoke($"[Network] Player {nodeID} disconnected");
            OnPlayerLeft?.Invoke(nodeID);
        }

        public string GetName(int id)
        {
            if (_playerNames.ContainsKey(id)) return _playerNames[id];
            return $"Player {id}";
        }

        // --- LOBBY & GAME START ---

        public List<string> GetLobbyPlayers()
        {
            return _playerNames.Values.ToList();
        }

        public int GetStartVoteCount()
        {
            return _startVotes.Count;
        }

        public int GetTotalLobbyPlayers()
        {
            return _playerNames.Count;
        }

        public void VoteToStart()
        {
            if (_currentPhase != GamePhase.Lobby)
            {
                OnLogMessage?.Invoke("[Error] Can only vote to start in Lobby phase");
                return;
            }

            int myID = _network.MyNodeID;
            _myPendingVote = $"VOTE_START:{myID}";
            AttemptToPropose(_nextOpenSlot);
        }

        private void CheckStartConsensus()
        {
            int totalPlayers = _playerNames.Count;
            if (_startVotes.Count >= totalPlayers && totalPlayers > 0)
            {
                // Only the player with the lowest NodeID proposes START_GAME to avoid conflicts
                int lowestNodeID = _playerNames.Keys.Min();
                
                if (_network.MyNodeID == lowestNodeID)
                {
                    OnLogMessage?.Invoke($"[Lobby] All {totalPlayers} players ready! Starting game...");
                    _myPendingVote = "START_GAME";
                    AttemptToPropose(_nextOpenSlot);
                }
                else
                {
                    OnLogMessage?.Invoke($"[Lobby] All {totalPlayers} players ready! Waiting for game start...");
                }
            }
        }

        private void StartGame()
        {
            if (_currentPhase != GamePhase.Lobby)
                return;

            AssignRoles();
            TransitionToPhase(GamePhase.Night);
            
            // Start night voting session
            StartNightVoting();
        }

        private void AssignRoles()
        {
            // CRITICAL: Sort player IDs to ensure deterministic order across all clients
            List<int> playerIDs = _alivePlayers.OrderBy(x => x).ToList();
            int totalPlayers = playerIDs.Count;
            
            // Simple rule: 1/3 of players are wolves (minimum 1)
            int wolfCount = Math.Max(1, totalPlayers / 3);

            // Shuffle players using a deterministic seed based on sorted IDs
            int seed = playerIDs.Sum();
            Random rng = new Random(seed);
            playerIDs = playerIDs.OrderBy(x => rng.Next()).ToList();

            // Assign roles
            for (int i = 0; i < playerIDs.Count; i++)
            {
                PlayerRole role = i < wolfCount ? PlayerRole.Werewolf : PlayerRole.Villager;
                _playerRoles[playerIDs[i]] = role;
            }

            // Log my role
            PlayerRole myRole = GetPlayerRole(_network.MyNodeID);
            OnLogMessage?.Invoke($"[Game] You are a {myRole}!");
            
            if (myRole == PlayerRole.Werewolf)
            {
                var wolves = GetAliveWerewolves();
                var wolfNames = wolves.Select(id => GetName(id)).ToList();
                OnLogMessage?.Invoke($"[Game] Fellow wolves: {string.Join(", ", wolfNames)}");
            }
        }

        // --- PHASE MANAGEMENT ---

        private void TransitionToPhase(GamePhase newPhase)
        {
            _currentPhase = newPhase;
            OnPhaseChanged?.Invoke(newPhase);
            OnLogMessage?.Invoke($"[Game] === Phase: {newPhase} ===");
        }

        private void StartNightVoting()
        {
            var wolves = GetAliveWerewolves();
            if (wolves.Count == 0)
            {
                // No wolves left, villagers win
                TransitionToPhase(GamePhase.GameOver);
                OnGameCommandExecuted?.Invoke("VILLAGERS WIN! All werewolves eliminated!");
                return;
            }

            // Initialize vote session
            _currentVoteType = VoteType.WolfKill;
            _currentVotes.Clear();
            _eligibleVoters = new HashSet<int>(wolves);
            
            OnLogMessage?.Invoke($"[Night] {wolves.Count} werewolves must vote to kill a villager");
        }

        private void StartDayVoting()
        {
            var alivePlayers = _alivePlayers.ToList();
            if (alivePlayers.Count == 0)
            {
                TransitionToPhase(GamePhase.GameOver);
                OnGameCommandExecuted?.Invoke("GAME OVER! No survivors!");
                return;
            }

            // Initialize vote session
            _currentVoteType = VoteType.VillagerLynch;
            _currentVotes.Clear();
            _eligibleVoters = new HashSet<int>(alivePlayers);
            
            OnLogMessage?.Invoke($"[Day] {alivePlayers.Count} players must vote to lynch a suspect");
        }

        // --- VOTING ---

        public void SubmitVote(int targetID)
        {
            if (_currentPhase == GamePhase.Lobby || _currentPhase == GamePhase.GameOver)
            {
                OnLogMessage?.Invoke("[Error] Cannot vote in current phase");
                return;
            }

            if (_currentVoteType == null)
            {
                OnLogMessage?.Invoke("[Error] No active voting session");
                return;
            }

            int myID = _network.MyNodeID;
            if (!_eligibleVoters.Contains(myID))
            {
                OnLogMessage?.Invoke("[Error] You are not eligible to vote in this round");
                return;
            }

            string voteType = _currentVoteType.ToString();
            _myPendingVote = $"VOTE:{myID}:{targetID}:{voteType}";
            AttemptToPropose(_nextOpenSlot);
        }

        public void SubmitVoteByName(string targetName)
        {
            int targetID = -1;
            foreach (var kvp in _playerNames)
            {
                string cleanName = kvp.Value.Replace(" (Me)", "");
                if (cleanName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    targetID = kvp.Key;
                    break;
                }
            }

            if (targetID != -1)
            {
                SubmitVote(targetID);
            }
            else
            {
                OnLogMessage?.Invoke($"[Error] Could not find player named '{targetName}'");
            }
        }

        // --- PAXOS INTERACTION ---

        private void AttemptToPropose(int slot)
        {
            OnLogMessage?.Invoke($"[RSM] Trying to write to Slot {slot}...");
            _paxos.Propose(slot, _myPendingVote);
        }

        private void HandleConsensus(int slotID, string decidedValue)
        {
            if (slotID >= _nextOpenSlot)
            {
                _nextOpenSlot = slotID + 1;
            }

            ProcessGameLogic(decidedValue);

            if (_myPendingVote != null)
            {
                if (decidedValue == _myPendingVote)
                {
                    OnLogMessage?.Invoke("[RSM] Success! My vote is in the log.");
                    _myPendingVote = null;
                }
                else
                {
                    OnLogMessage?.Invoke($"[RSM] Slot {slotID} was taken. Retrying in {_nextOpenSlot}...");
                    AttemptToPropose(_nextOpenSlot);
                }
            }
        }

        // --- GAME LOGIC ---

        private void ProcessGameLogic(string command)
        {
            if (command.StartsWith("VOTE_START:"))
            {
                try
                {
                    string[] parts = command.Split(':');
                    int playerID = int.Parse(parts[1]);
                    
                    _startVotes.Add(playerID);
                    string playerName = GetName(playerID);
                    OnLogMessage?.Invoke($"[Lobby] {playerName} is ready to start ({_startVotes.Count}/{_playerNames.Count})");
                    
                    CheckStartConsensus();
                }
                catch (Exception e)
                {
                    OnLogMessage?.Invoke($"[Error] Failed to parse VOTE_START: {e.Message}");
                }
            }
            else if (command == "START_GAME")
            {
                StartGame();
            }
            else if (command.StartsWith("VOTE:"))
            {
                try
                {
                    string[] parts = command.Split(':');
                    int voterID = int.Parse(parts[1]);
                    int targetID = int.Parse(parts[2]);
                    string voteTypeStr = parts[3];

                    if (_currentVoteType == null)
                        return;

                    // Check if this voter has already voted to avoid double-processing
                    if (_currentVotes.ContainsKey(voterID))
                        return;

                    _currentVotes[voterID] = targetID;

                    string voterName = GetName(voterID);
                    string targetName = GetName(targetID);
                    OnLogMessage?.Invoke($"[Vote] {voterName} voted for {targetName} ({_currentVotes.Count}/{_eligibleVoters.Count})");
                    OnVoteReceived?.Invoke(voterID, targetID, _currentVotes.Count, _eligibleVoters.Count);

                    // Only process result when voting is complete
                    if (_currentVotes.Count >= _eligibleVoters.Count)
                    {
                        ProcessVoteResult();
                    }
                }
                catch (Exception e)
                {
                    OnLogMessage?.Invoke($"[Error] Failed to parse VOTE: {e.Message}");
                }
            }
        }

        private void ProcessVoteResult()
        {
            if (_currentVoteType == null)
                return;

            // Prevent processing the same vote result multiple times
            VoteType completedVoteType = _currentVoteType.Value;
            
            int victimID = GetVictim();
            if (victimID == -1)
            {
                OnLogMessage?.Invoke("[Vote] No one was eliminated (tie or no votes)");
            }
            else
            {
                string victimName = GetName(victimID);
                PlayerRole victimRole = GetPlayerRole(victimID);
                KillPlayer(victimID);
                
                OnGameCommandExecuted?.Invoke($"{victimName} ({victimRole}) has been eliminated!");
            }

            // Clear the vote session BEFORE checking win conditions
            _currentVoteType = null;
            _currentVotes.Clear();
            _eligibleVoters.Clear();

            // Check win conditions AFTER killing the player
            int aliveWolves = GetAliveWerewolfCount();
            int aliveVillagers = GetAliveVillagerCount();

            if (aliveWolves == 0)
            {
                TransitionToPhase(GamePhase.GameOver);
                OnGameCommandExecuted?.Invoke("VILLAGERS WIN! All werewolves eliminated!");
                return;
            }

            if (aliveWolves >= aliveVillagers)
            {
                TransitionToPhase(GamePhase.GameOver);
                OnGameCommandExecuted?.Invoke("WEREWOLVES WIN! They outnumber the villagers!");
                return;
            }

            // Continue to next phase
            if (completedVoteType == VoteType.WolfKill)
            {
                TransitionToPhase(GamePhase.Day);
                StartDayVoting();
            }
            else
            {
                TransitionToPhase(GamePhase.Night);
                StartNightVoting();
            }
        }

        private int GetVictim()
        {
            // Tally votes
            Dictionary<int, int> tally = new Dictionary<int, int>();
            foreach (var targetID in _currentVotes.Values)
            {
                if (!tally.ContainsKey(targetID))
                    tally[targetID] = 0;
                tally[targetID]++;
            }

            // Find player with most votes
            int victim = -1;
            int maxVotes = 0;
            foreach (var kvp in tally)
            {
                if (kvp.Value > maxVotes)
                {
                    maxVotes = kvp.Value;
                    victim = kvp.Key;
                }
            }

            return victim;
        }

        private bool HandleConnectionValidation(string incomingName)
        {
            // Reject if game has already started
            if (_currentPhase != GamePhase.Lobby)
            {
                OnLogMessage?.Invoke($"[Network] Rejected {incomingName}: Game already started");
                return false;
            }

            if (incomingName == MyName)
                return false;

            foreach (var existingName in _playerNames.Values)
            {
                string cleanExisting = existingName.Replace(" (Me)", "");
                if (cleanExisting == incomingName)
                    return false;
            }

            return true;
        }

        // --- HELPER METHODS ---

        private PlayerRole GetPlayerRole(int playerID)
        {
            return _playerRoles.ContainsKey(playerID) ? _playerRoles[playerID] : PlayerRole.Villager;
        }

        private void KillPlayer(int playerID)
        {
            if (_alivePlayers.Contains(playerID))
            {
                _alivePlayers.Remove(playerID);
                _playerRoles[playerID] = PlayerRole.Dead;
            }
        }

        private int GetAliveWerewolfCount()
        {
            return _alivePlayers.Count(id => _playerRoles.ContainsKey(id) && _playerRoles[id] == PlayerRole.Werewolf);
        }

        private int GetAliveVillagerCount()
        {
            return _alivePlayers.Count(id => _playerRoles.ContainsKey(id) && _playerRoles[id] == PlayerRole.Villager);
        }

        private List<int> GetAliveWerewolves()
        {
            return _alivePlayers.Where(id => _playerRoles.ContainsKey(id) && _playerRoles[id] == PlayerRole.Werewolf).ToList();
        }

        // --- RESET AND LIFECYCLE ---

        public void ResetGame()
        {
            OnLogMessage?.Invoke("[System] Resetting game to Lobby...");
            
            // Reset game state but keep player connections
            _currentPhase = GamePhase.Lobby;
            _playerRoles.Clear();
            _startVotes.Clear();
            _currentVoteType = null;
            _currentVotes.Clear();
            _eligibleVoters.Clear();
            
            // Reset alive players to all connected players
            _alivePlayers.Clear();
            foreach (var playerID in _playerNames.Keys)
            {
                _alivePlayers.Add(playerID);
            }
            
            OnPhaseChanged?.Invoke(GamePhase.Lobby);
            OnLogMessage?.Invoke("[System] Game reset complete. Ready to start new round.");
        }

        // --- PUBLIC API ---

        public void Update()
        {
            _network.Update();
        }

        public void StartDiscovery()
        {
            _network.SendBroadcastDiscovery();
        }

        public void ManualConnect(string ip, int port)
        {
            _network.ConnectTo(ip, port);
        }

        public void Shutdown()
        {
            OnLogMessage?.Invoke("[System] Shutting down...");
            _network.Stop();
        }

        public int PeerCount => _network.ConnectedPeers.Count;
        public GamePhase CurrentPhase => _currentPhase;
        public PlayerRole MyRole => GetPlayerRole(_network.MyNodeID);
        public bool IsAlive => _alivePlayers.Contains(_network.MyNodeID);

        public string GetGameInfo()
        {
            var info = new System.Text.StringBuilder();
            info.AppendLine($"Phase: {_currentPhase}");
            info.AppendLine($"Your Role: {MyRole}");
            info.AppendLine($"Alive: {IsAlive}");
            info.AppendLine($"Alive Players: {_alivePlayers.Count}");
            info.AppendLine($"Alive Wolves: {GetAliveWerewolfCount()}");
            info.AppendLine($"Alive Villagers: {GetAliveVillagerCount()}");
            
            if (_currentVoteType != null)
            {
                info.AppendLine($"Vote Progress: {_currentVotes.Count}/{_eligibleVoters.Count}");
            }
            
            return info.ToString();
        }
    }
}
