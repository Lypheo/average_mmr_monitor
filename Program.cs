// Create new file: TestGC.cs
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Diagnostics;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;
using SteamKit2.Internal;

public class TestGC
{
    static SteamClient steamClient;
    static CallbackManager manager;
    static SteamUser steamUser;
    static SteamGameCoordinator coordinator;
    static bool isRunning;
    static bool isGCReady = false;
    static string logFilePath = @"E:\Programs\Games\Steam\steamapps\common\dota 2 beta\game\dota\console.log"; // Make sure this path is correct
    static FileSystemWatcher logWatcher;
    static long lastLogSize = 0;
    static ulong currentLobbyId = 0;
    static ulong lastRequestedLobbyId = 0;
    static HashSet<ulong> processedLobbyIds = new HashSet<ulong>();

    static Dictionary<uint, string> mainFriends = new Dictionary<uint, string>
    {
    };

    static Dictionary<ulong, DateTime> pendingLobbyRequests = new Dictionary<ulong, DateTime>();
    static int maxRetries = 2; // Allow 1 retry before restarting
    static Dictionary<ulong, int> lobbyRetryCount = new Dictionary<ulong, int>();

    static void Main(string[] args)
    {
        // Initialize SteamKit
        steamClient = new SteamClient();
        manager = new CallbackManager(steamClient);
        steamUser = steamClient.GetHandler<SteamUser>();
        coordinator = steamClient.GetHandler<SteamGameCoordinator>();

        // Register callbacks
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

        // Setup console log watcher
        SetupLogWatcher();

        Console.CancelKeyPress += (sender, e) => {
            Console.WriteLine("Ctrl+C detected! Cleaning up...");
            Disconnect();
            e.Cancel = true;
            Environment.Exit(0);
        };

        Console.WriteLine("Connecting to Steam...");
        steamClient.Connect();
        isRunning = true;

        // Main loop
        while (isRunning)
        {
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));

            // Check console log periodically if watcher fails
            CheckConsoleLog();

            // Request lobby details if GC is ready and a new lobby is found
            if (isGCReady && currentLobbyId != 0 && currentLobbyId != lastRequestedLobbyId)
            {
                RequestLobbyDetails(currentLobbyId);
                lastRequestedLobbyId = currentLobbyId;
            }

            // Check for pending requests that have timed out
            CheckPendingRequests();
        }

        Console.WriteLine("Exited main loop.");
    }

    static void Disconnect()
    {
        isRunning = false;
        isGCReady = false;
        logWatcher?.Dispose();
        steamUser?.LogOff();
        steamClient?.Disconnect();
        Console.WriteLine("Disconnected.");
    }

    static void OnConnected(SteamClient.ConnectedCallback callback)
    {
        Console.WriteLine("Connected to Steam! Logging in ...");
        try
        {
            var username = Environment.GetEnvironmentVariable("STEAM_USERNAME");
            var password = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                Console.WriteLine("Error: STEAM_USERNAME or STEAM_PASSWORD environment variables are not set.");
                isRunning = false;
                return;
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password
            });
        }
        catch
        {
            Console.WriteLine("Error during login");
            steamClient.Disconnect();
        }
    }

    static void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        Console.WriteLine("Disconnected from Steam. Reconnecting in 5 seconds...");
        isGCReady = false;
        Thread.Sleep(TimeSpan.FromSeconds(5));
        steamClient.Connect(); // Attempt to reconnect
    }

    static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            Console.WriteLine($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");
            if (callback.Result == EResult.AccountLogonDenied || callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                Console.WriteLine("Steam Guard/2FA required.");
                // Handle Auth/2FA code input here if necessary
            }
            else if (callback.Result == EResult.InvalidPassword)
            {
                 Console.WriteLine("Invalid password.");
            }
            Disconnect();
            return;
        }

        Console.WriteLine("Logged in successfully! Launching Dota 2 GC...");

        // Indicate we're playing Dota 2 (AppID 570)
        var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(570) });
        steamClient.Send(playGame);

        // Wait a second before sending the GC hello
        Thread.Sleep(1000);

        // Send GC hello
        var clientHello = new ClientGCMsgProtobuf<SteamKit2.GC.Dota.Internal.CMsgClientHello>(
            (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
        clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
        coordinator.Send(clientHello, 570);
    }

     static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Console.WriteLine($"Logged off Steam: {callback.Result}");
        isGCReady = false;
    }

    static void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
    {
        // Console.WriteLine($"GC Message: {callback.EMsg}"); // Debug: Print all GC messages

        switch (callback.EMsg)
        {
            case (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome:
                HandleWelcome(callback.Message);
                break;
            case (uint)EDOTAGCMsg.k_EMsgGCToClientFindTopSourceTVGamesResponse:
                HandleLobbyResponse(callback.Message);
                break;
                // Add other message handlers if needed
        }
    }

    static void HandleWelcome(IPacketGCMsg msg)
    {
        var welcomeMsg = new ClientGCMsgProtobuf<CMsgClientWelcome>(msg);
        Console.WriteLine($"Dota 2 GC Welcome Received. Version: {welcomeMsg.Body.version}");
        isGCReady = true;
        // Initial check of log file on welcome
        CheckConsoleLog(true);
    }

    static void HandleLobbyResponse(IPacketGCMsg msg)
    {
        var response = new ClientGCMsgProtobuf<CMsgGCToClientFindTopSourceTVGamesResponse>(msg);

        if (response.Body.specific_games && response.Body.game_list.Count > 0)
        {
            foreach (var game in response.Body.game_list)
            {
                if (processedLobbyIds.Contains(game.lobby_id)) continue; // Skip if already processed

                Console.WriteLine($"\n--- Lobby Found ---");
                Console.WriteLine($"Match ID: {game.match_id} (Lobby ID: {game.lobby_id})");
                DateTime gameStartTime = DateTime.UtcNow.AddSeconds(-game.game_time);
                Console.WriteLine($"Game Start Time: {gameStartTime.ToLocalTime()}");
                Console.WriteLine($"Average MMR: {game.average_mmr}");


                bool friendFound = false;
                foreach (var player in game.players)
                {
                    if (mainFriends.TryGetValue(player.account_id, out string playerName))
                    {
                        Console.WriteLine($"Player in match: {playerName} (ID: {player.account_id})");
                        friendFound = true;
                    }
                }
                if (!friendFound) {
                     Console.WriteLine("No known friends found in this lobby.");
                }
                 Console.WriteLine($"-------------------");

                processedLobbyIds.Add(game.lobby_id); // Mark as processed
                
                // Clean up tracking for this lobby
                pendingLobbyRequests.Remove(game.lobby_id);
                lobbyRetryCount.Remove(game.lobby_id);
            }
        } else {
            Console.WriteLine($"Received non-specific or empty game list response for lobby request.");
        }
    }

    static void CheckPendingRequests()
    {
        if (!isGCReady || pendingLobbyRequests.Count == 0)
            return;

        List<ulong> timeoutLobbies = new List<ulong>();
        DateTime now = DateTime.UtcNow;

        foreach (var kvp in pendingLobbyRequests)
        {
            ulong lobbyId = kvp.Key;
            DateTime requestTime = kvp.Value;

            // Check if request timed out (3 seconds)
            if ((now - requestTime).TotalSeconds > 3)
            {
                timeoutLobbies.Add(lobbyId);
            }
        }

        foreach (ulong lobbyId in timeoutLobbies)
        {
            // Remove from pending requests
            pendingLobbyRequests.Remove(lobbyId);

            if (processedLobbyIds.Contains(lobbyId))
            {
                // Request was actually processed, just clean up tracking
                if (lobbyRetryCount.ContainsKey(lobbyId))
                    lobbyRetryCount.Remove(lobbyId);
                continue;
            }

            // Get current retry count
            if (!lobbyRetryCount.TryGetValue(lobbyId, out int retryCount))
            {
                retryCount = 0;
            }

            if (retryCount < maxRetries)
            {
                // Try again
                Console.WriteLine($"Lobby request for {lobbyId} timed out. Retrying... (Attempt {retryCount + 1}/{maxRetries})");
                lobbyRetryCount[lobbyId] = retryCount + 1;
                RequestLobbyDetails(lobbyId);
            }
            else
            {
                // Max retries exceeded, restart the application
                Console.WriteLine($"Max retries ({maxRetries}) exceeded for lobby {lobbyId}. Restarting application...");
                RestartApplication();
            }
        }
    }

    static void RestartApplication()
    {
        Console.WriteLine("Restarting application due to persistent lobby request failures...");
        
        // Clean up
        Disconnect();
        // Wait for 3 seconds before restarting
        Thread.Sleep(TimeSpan.FromSeconds(3));

        Main([]); // Restart the main method
    }

    static void RequestLobbyDetails(ulong lobbyId)
    {
        if (!isGCReady)
        {
            Console.WriteLine("Cannot request lobby details: GC not ready.");
            return;
        }
        if (processedLobbyIds.Contains(lobbyId))
        {
             Console.WriteLine($"Skipping request for already processed lobby ID: {lobbyId}");
             return;
        }

        Console.WriteLine($"Requesting details for lobby ID: {lobbyId}");
        var request = new ClientGCMsgProtobuf<CMsgClientToGCFindTopSourceTVGames>((uint)EDOTAGCMsg.k_EMsgClientToGCFindTopSourceTVGames);
        request.Body.lobby_ids.Add(lobbyId);
        coordinator.Send(request, 570);
        
        // Track this request
        pendingLobbyRequests[lobbyId] = DateTime.UtcNow;
    }

    // --- Console Log Monitoring ---

    static void SetupLogWatcher()
    {
        try
        {
            if (!File.Exists(logFilePath))
            {
                Console.WriteLine($"Warning: Log file not found at {logFilePath}. Monitoring will rely on periodic checks.");
                return;
            }

            string? logDirectory = Path.GetDirectoryName(logFilePath);
            string logFileName = Path.GetFileName(logFilePath);

            if (logDirectory == null) {
                 Console.WriteLine($"Warning: Could not determine directory for log file {logFilePath}. Monitoring will rely on periodic checks.");
                 return;
            }

            logWatcher = new FileSystemWatcher(logDirectory)
            {
                Path = logDirectory,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = logFileName,
                EnableRaisingEvents = true
            };

            logWatcher.Changed += OnConsoleLogChanged;
            logWatcher.Error += OnWatcherError; // Handle potential watcher errors

            // Get initial size
            lastLogSize = new FileInfo(logFilePath).Length;
            Console.WriteLine($"Monitoring console log: {logFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting up FileSystemWatcher: {ex.Message}");
            logWatcher?.Dispose(); // Ensure disposal if setup fails partially
            logWatcher = null; // Indicate watcher is not active
        }
    }

     static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Console.WriteLine($"FileSystemWatcher Error: {e.GetException().Message}. Falling back to periodic checks.");
        // Optionally disable the watcher here if errors persist
        // logWatcher.EnableRaisingEvents = false;
    }

    static void OnConsoleLogChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Changed)
        {
            CheckConsoleLog();
        }
    }

    static readonly Regex lobbyRegex = new Regex(@"LOBBY STATE RUN: lobby (\d+)", RegexOptions.Compiled);
    static void CheckConsoleLog(bool forceRead = false)
    {
        try
        {
            if (!File.Exists(logFilePath)) return; // Skip if file doesn't exist

            var fileInfo = new FileInfo(logFilePath);
            long currentSize = fileInfo.Length;

            if (currentSize > lastLogSize || forceRead)
            {
                using (var fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    if (!forceRead) {
                        fs.Seek(lastLogSize, SeekOrigin.Begin); // Seek to where we left off
                    } else {
                         Console.WriteLine("Force reading console log...");
                    }

                    string newContent = sr.ReadToEnd();
                    var matches = lobbyRegex.Matches(newContent);

                    if (matches.Count > 0)
                    {
                        // Get the last lobby ID mentioned in the new content
                        string lobbyIdStr = matches[matches.Count - 1].Groups[1].Value;
                        if (ulong.TryParse(lobbyIdStr, out ulong newLobbyId))
                        {
                            if (newLobbyId != currentLobbyId)
                            {
                                Console.WriteLine($"Detected new lobby ID: {newLobbyId}");
                                currentLobbyId = newLobbyId;
                                // Request will be sent in the main loop
                            }
                        }
                    }
                }
                lastLogSize = currentSize;
            }
             else if (currentSize < lastLogSize) {
                // Log file might have been truncated or replaced
                Console.WriteLine("Log file size decreased, resetting position.");
                lastLogSize = currentSize;
            }
        }
        catch (IOException ex)
        {
            // Handle potential file access issues gracefully
            Console.WriteLine($"Warning: Could not read log file: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking console log: {ex.Message}");
        }
    }
}