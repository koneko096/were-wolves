using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WerewolvesLib;

/// <summary>
/// All-in-one UIManager: Handles game persistence, UI updates, and scene management
/// </summary>
public class UIManager : MonoBehaviour
{
    #region Singleton Pattern
    private static UIManager _instance;
    public static UIManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<UIManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("UIManager");
                    _instance = go.AddComponent<UIManager>();
                }
            }
            return _instance;
        }
    }
    #endregion

    #region Game Handler (Persistent)
    public GameHandler Game { get; private set; }
    public bool IsGameInitialized => Game != null;
    #endregion

    #region UI References (Assign in Inspector)
    [Header("UI Panels")]
    public GameObject mainMenuPanel;
    public GameObject lobbyPanel;
    public GameObject startPanel;
    public GameObject votePanel;
    public GameObject endGamePanel;
    public GameObject nightBackground;
    public GameObject dayBackground;

    [Header("Setup Scene")]
    public InputField playerNameInput;
    public InputField portInput;
    public Button startGameButton;
    public Button quitButton;
    public Text greetingText;

    [Header("Lobby Scene")]
    public Button findPlayersButton;
    public Button readyButton;
    public Text lobbyPlayerListText;
    public Text voteCountText;

    [Header("Game Scene")]
    public Button voteButton;
    public Text phaseText;
    public Text roleText;
    public Text gameInfoText;
    public Dropdown voteTargetInput;

    [Header("End Game")]
    public Button resetButton;
    public Text gameOverText;
    #endregion

    void Awake()
    {
        // Singleton enforcement
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        ShowPanel(startPanel);
    }

    void Update()
    {
        // Keep network alive
        Game?.Update();
        
        // Update UI if in game
        UpdateUI();
    }

    #region Game Initialization
    /// <summary>
    /// Initialize the game with player name and port. Call from Setup scene.
    /// </summary>
    public void InitializeGame(string playerName, int port = 9050)
    {
        if (Game != null)
        {
            Debug.LogWarning("Game already initialized");
            return;
        }

        Game = new GameHandler(port);
        Game.SetLocalPlayerName(playerName);

        // Hook events
        Game.OnLogMessage += (msg) => Debug.Log($"[Game] {msg}");
        Game.OnGameCommandExecuted += OnGameCommand;
        Game.OnPhaseChanged += OnPhaseChanged;
        Game.OnPlayerJoined += (id, name) => UpdateUI();
        Game.OnPlayerLeft += (id) => UpdateUI();
        Game.OnVoteReceived += (voter, target, current, required) => UpdateUI();

        Debug.Log($"Game initialized for {playerName} on port {port}");
    }
    #endregion

    #region Button Handlers
    /// <summary>
    /// Initialize from UI input fields (call from button)
    /// </summary>
    public void InitializeFromInput()
    {
        if (playerNameInput == null || portInput == null)
        {
            Debug.LogError("Input fields not assigned!");
            return;
        }

        string name = playerNameInput.text;
        int port = int.Parse(portInput.text);
        
        PlayerPrefs.SetString("PlayerName", name);
        PlayerPrefs.Save();
        greetingText.text = "Hello " + name;

        InitializeGame(name, port);
    }

    public void SubmitVote()
    {
        if (voteTargetInput != null)
        {
            Game?.SubmitVote(voteTargetInput.value);
        }
    }
    public void StartDiscovery() => Game?.StartDiscovery();
    public void VoteToStart() => Game?.VoteToStart();
    public void ResetGame() => Game?.ResetGame();
    public void Shutdown() => Game?.Shutdown();
    #endregion

    #region UI Updates
    private void UpdateUI()
    {
        if (Game == null) return;

        if (Game.CurrentPhase == GamePhase.Day)
        {
            nightBackground.SetActive(false);
            dayBackground.SetActive(true);
        }
        else
        {
            dayBackground.SetActive(false);
            nightBackground.SetActive(true);
        }

        // Lobby UI
        if (lobbyPlayerListText != null)
        {
            var players = Game.GetLobbyPlayers();
            lobbyPlayerListText.text = string.Join("\n", players);
        }

        if (voteCountText != null)
        {
            voteCountText.text = $"Ready: {Game.GetStartVoteCount()}/{Game.GetTotalLobbyPlayers()}";
        }

        // Game UI
        //if (phaseText != null)
        //    phaseText.text = $"Phase: {Game.CurrentPhase}";

        //if (roleText != null)
        //    roleText.text = $"Role: {Game.MyRole}";

        //if (gameInfoText != null)
        //    gameInfoText.text = Game.GetGameInfo();

        // Button states
        //if (readyButton != null)
        //    readyButton.interactable = (Game.CurrentPhase == GamePhase.Lobby);

        //if (resetButton != null)
        //    resetButton.interactable = (Game.CurrentPhase == GamePhase.GameOver);

        if (Game.CurrentPhase == GamePhase.GameOver)
        {
            ShowPanel(endGamePanel);
        }

        if (Game.IsAlive && (Game.CurrentPhase == GamePhase.Night || Game.CurrentPhase == GamePhase.Day))
        {
            ShowPanel(votePanel);

            voteTargetInput.ClearOptions();
            voteTargetInput.AddOptions(Game?.GetLobbyPlayers());
            voteTargetInput.RefreshShownValue();
        }
    }

    private void OnGameCommand(string message)
    {
        Debug.Log($"[GameCommand] {message}");
        if (gameOverText != null)
            gameOverText.text = message;
    }

    private void OnPhaseChanged(GamePhase phase)
    {
        Debug.Log($"Phase changed to: {phase}");
        UpdateUI();
    }
    #endregion

    #region Scene Management
    public void Reload()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void LoadLevel(string level)
    {
        SceneManager.LoadScene(level);
    }

    public void Quit()
    {
        Debug.Log("🚪 Quitting game...");
        Game?.Shutdown();
        
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #else
        Application.Quit();
        #endif
    }
    public void ShowPanel(GameObject panelToShow)
    {
        // Hide everything first
        mainMenuPanel.SetActive(false);
        lobbyPanel.SetActive(false);
        startPanel.SetActive(false);
        votePanel.SetActive(false);
        endGamePanel.SetActive(false);

        // Show the one we want
        panelToShow.SetActive(true);
    }
    #endregion
}
