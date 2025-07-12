# Live MMR Monitor

A Dota 2 tool that retrieves the lobby average MMR of matches you play or live-spectate, using the Game Coordinator API.

## Prerequisites

- Secondary Steam account with Steam Guard disabled. Needed because the tool requires a Game Coordinator connection, and Valve only allows one connection per account, so you can't use your main acc at the same time. (Steam Guards needs to be disabled because I'm too lazy to implement Steam Guard authentification, patches welcome.)
- For compilation: .NET 8.0

## Setup

### 1. Enable Dota 2 Console Logging

Add the following launch options to Dota 2 in Steam:
```
-console -consolelog -conclearlog
```

This enables console logging to the `console.log` file.

### 2. Optionally, configure friends list (requires recompilation)

If you want the output to display which friends are in a given lobby (helpful when you're for looking some specific past game in the log)
edit the `mainFriends` dictionary in `Program.cs` to add your friends' Steam account IDs:

```csharp
static Dictionary<uint, string> mainFriends = new Dictionary<uint, string>
{
    { 123456789, "Friend Name 1" },
    { 987654321, "Friend Name 2" },
    // Add more friends here
};
```

## Running

The tool requires three command line arguments: username, password, and log file path.

```bash
cd path/to/average_mmr_monitor
dotnet run <username> <password> <log_file_path> # or: average_mmr_monitor.exe <username> <password> <log_file_path>
```
The log file path will typically be something like ``C:\Steam\steamapps\common\dota 2 beta\game\dota\console.log``, depending on your installation location.

**Note**: If your log file path contains spaces, make sure to wrap it in quotes.

## How It Works

1. **Steam Connection**: The tool connects to Steam and the Dota 2 Game Coordinator using the provided credentials
2. **Log Monitoring**: Monitors the Dota 2 console log file for lobby state changes
3. **Lobby Detection**: When a new lobby is detected, it requests detailed information, including the mmr average, from the Game Coordinator using [this endpoint](https://github.com/SteamDatabase/GameTracking-Dota2/blob/fef468d2909fa1841c04cee7d235ec36d1e5d26d/Protobufs/dota_gcmessages_client_watch.proto#L3-L65)

The endpoint only works for currently live games, so you can't look up the average mmr of past games.

Technically, you can use that endpoint to retrieve the average mmr of any ranked lobby if you have its lobby ID. However, I only know of two ways to find a match's lobby ID:
1. Joining it (either by playing or by live-spectating with dota plus) writes the lobby ID to your console
2. You can monitor Steam's Rich Presence data to find the lobby IDs of matches your friends play (see for example https://github.com/daveknippers/AghanimsWager/blob/main/DotaBet_GC.py)
(This means you could technically extend this tool to track all of your friends's games if they add your secondary acc)

## Output Example

```
Connected to Steam! Logging in ...
Logging in as username...
Logged in successfully! Launching Dota 2 GC...
Dota 2 GC Welcome Received. Version: 1234
Monitoring console log: C:\Steam\steamapps\common\dota 2 beta\game\dota\console.log
Detected new lobby ID: 29388152423586440 (timestamp: 12/25 14:30:45)
Requesting details for lobby ID: 29388152423586440

--- Lobby Found ---
Match ID: 7891234567 (Lobby ID: 29388152423586440)
Game Start Time: 12/25/2023 2:30:45 PM
Average MMR: 4250
Player in match: Friend Name 1 (ID: 123456789)
-------------------
```
