using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdminPlus;

public partial class AdminPlus
{
    // Replaces the in-memory ban cache with a database snapshot (game thread only)
    // and mirrors it to the cfg files so they keep working as offline fallback.
    internal void ApplyBanSnapshot(
        Dictionary<string, (long expiry, string line, string nick, string ip)> steamBans,
        Dictionary<string, (long expiry, string line, string nick)> ipBans)
    {
        lock (_lock)
        {
            SteamBans = steamBans;
            IpBans = ipBans;

            try
            {
                File.WriteAllLines(BannedUserPath, SteamBans.Values.Select(v => v.line));
                File.WriteAllLines(BannedIpPath, IpBans.Values.Select(v => v.line));
                _lastUserBanWriteUtc = File.GetLastWriteTimeUtc(BannedUserPath);
                _lastIpBanWriteUtc = File.GetLastWriteTimeUtc(BannedIpPath);
            }
            catch (Exception ex)
            {
                LogError($"Ban snapshot cfg mirror failed: {ex.Message}");
            }
        }
    }

    internal void KickBannedOnlinePlayers()
    {
        try
        {
            var players = Utilities.GetPlayers();
            if (players == null) return;

            foreach (var p in players.Where(p => p != null && p.IsValid && !p.IsBot).ToList())
                EnforceBan(p.Slot);
        }
        catch (Exception ex)
        {
            LogError($"Post-refresh ban enforcement failed: {ex.Message}");
        }
    }

    // Called when the per-connect database lookup finds a ban that is not yet
    // in the local cache (e.g. added from the web panel between refreshes).
    internal void ApplyDatabaseBanHit(int slot, bool isIpBan, string key, string nick, string reason, long expiry)
    {
        try
        {
        lock (_lock)
        {
            if (isIpBan)
            {
                if (!IpBans.ContainsKey(key))
                {
                    var line = $"addip \"{key}\" expiry:{expiry} // {reason} (Player: {nick})";
                    IpBans[key] = (expiry, line, nick);
                    try { File.WriteAllLines(BannedIpPath, IpBans.Values.Select(x => x.line)); }
                    catch (Exception ex) { LogError($"cfg mirror write failed: {ex.Message}"); }
                }
            }
            else
            {
                if (!SteamBans.ContainsKey(key))
                {
                    var line = $"banid \"{key}\" \"{nick}\" ip:- expiry:{expiry} // {reason}";
                    SteamBans[key] = (expiry, line, nick, "-");
                    try { File.WriteAllLines(BannedUserPath, SteamBans.Values.Select(x => x.line)); }
                    catch (Exception ex) { LogError($"cfg mirror write failed: {ex.Message}"); }
                }
            }
        }

        EnforceBan(slot);
        }
        catch (Exception ex)
        {
            LogError($"ApplyDatabaseBanHit failed: {ex.Message}");
        }
    }

    // Replaces the local mute/gag list with a database snapshot (game thread only)
    // and mirrors it to communication_data.json as offline fallback.
    internal void ApplyCommSnapshot(List<CommunicationPunishment> punishments)
    {
        try
        {
            _communicationPunishments = punishments;
            _commStates.Clear();
            SyncMuteSystemsFromJson();
            SaveCommunicationData();
            EnforceMutePunishments();
        }
        catch (Exception ex)
        {
            LogError($"ApplyCommSnapshot failed: {ex.Message}");
        }
    }

    // Merges punishments found by the per-connect database lookup that are not
    // yet in the local cache (e.g. added from the web panel between refreshes).
    internal void ApplyCommHits(ulong steamId, List<CommunicationPunishment> punishments)
    {
        try
        {
        bool added = false;

        foreach (var p in punishments)
        {
            if (_communicationPunishments.Any(x => x.SteamID == steamId && x.Type == p.Type && !x.IsExpired))
                continue;

            _communicationPunishments.Add(p);
            added = true;
        }

        if (!added) return;

        SyncMuteSystemsFromJson();
        SaveCommunicationData();

        if (_communicationPunishments.Any(x => x.SteamID == steamId && x.Type == "MUTE" && !x.IsExpired))
        {
            var player = Utilities.GetPlayers()?.FirstOrDefault(pl => pl.IsValid && pl.SteamID == steamId);
            if (player != null)
                player.VoiceFlags = VoiceFlags.Muted;
        }
        }
        catch (Exception ex)
        {
            LogError($"ApplyCommHits failed: {ex.Message}");
        }
    }

    internal void ScheduleDatabaseBanCheck(int slot)
    {
        if (!BanDatabase.Enabled) return;

        // Small delay so SteamID/IP are populated before querying.
        AddTimer(1.0f, () =>
        {
            try
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid || player.IsBot) return;

                var steamId = player.SteamID.ToString();
                var ip = player.IpAddress ?? "";
                if (ip.Contains(':')) ip = ip.Split(':')[0];

                BanDatabase.CheckPlayerOnConnect(this, slot, steamId, ip);
            }
            catch (Exception ex)
            {
                LogError($"Database ban check scheduling failed: {ex.Message}");
            }
        });
    }
}

internal sealed class BanDatabaseSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("host")] public string Host { get; set; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; set; } = 3306;
    [JsonPropertyName("user")] public string User { get; set; } = "root";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "adminplus";
    [JsonPropertyName("tablePrefix")] public string TablePrefix { get; set; } = "adminplus_";
    [JsonPropertyName("serverId")] public string ServerId { get; set; } = "default";
}

internal sealed class BanDatabaseConfigFile
{
    [JsonPropertyName("database")] public BanDatabaseSettings Database { get; set; } = new();
}

internal static class BanDatabase
{
    public const string ConfigFileName = "adminplus-database.json";

    public static bool Enabled { get; private set; }

    private static string _connectionString = "";
    private static string _table = "adminplus_bans";
    private static string _commTable = "adminplus_comms";
    private static string _serverId = "default";

    // Failed writes stay queued and are retried before the next write/refresh,
    // so a short database outage does not lose bans (cfg files keep them meanwhile).
    private static readonly ConcurrentQueue<Func<MySqlConnection, Task>> _pendingWrites = new();
    private static readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly SemaphoreSlim _initGate = new(1, 1);

    // Circuit breaker: after a connection failure all database work is skipped
    // for a short window so a down/misconfigured database never floods the
    // server with connection attempts on every player connect.
    private const int OfflinePauseSeconds = 30;
    private static long _offlineUntilTicks;
    private static int _ready;
    private static List<AdminPlus.CommunicationPunishment>? _commImportSnapshot;

    private static bool IsOffline => DateTime.UtcNow.Ticks < Interlocked.Read(ref _offlineUntilTicks);

    private static void MarkOffline(string context, Exception ex)
    {
        Interlocked.Exchange(ref _offlineUntilTicks, DateTime.UtcNow.AddSeconds(OfflinePauseSeconds).Ticks);
        AdminPlus.LogError($"Database unavailable ({context}), pausing database work for {OfflinePauseSeconds}s: {ex.Message}");
    }

    // Creates the schema and runs the one-time imports; retried automatically
    // (via the periodic refreshes) until it succeeds once.
    private static async Task EnsureReadyAsync()
    {
        if (Volatile.Read(ref _ready) == 1) return;

        await _initGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _ready) == 1) return;

            await CreateSchemaAsync();
            await ImportCfgBansIfEmptyAsync();

            var commSnapshot = _commImportSnapshot;
            if (commSnapshot != null)
                await ImportCommsIfEmptyAsync(commSnapshot);

            Volatile.Write(ref _ready, 1);
            AdminPlus.LogWarn($"Database ready — tables `{_table}` and `{_commTable}` are in use.");
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static void LoadConfig()
    {
        Enabled = false;

        try
        {
            var path = Path.Combine(AdminPlus._instance?.ModuleDirectory ?? "", ConfigFileName);

            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new BanDatabaseConfigFile(), JsonOptions));
                AdminPlus.LogWarn($"Created {ConfigFileName} — set enabled=true and fill in MySQL credentials to store bans in a database.");
                return;
            }

            var config = JsonSerializer.Deserialize<BanDatabaseConfigFile>(File.ReadAllText(path), JsonOptions)?.Database;
            if (config == null || !config.Enabled)
                return;

            if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.Name))
            {
                AdminPlus.LogError("Database config is enabled but host/name are empty; staying on cfg files.");
                return;
            }

            var prefix = Regex.Replace(config.TablePrefix ?? "", @"[^A-Za-z0-9_]", "");
            _table = prefix + "bans";
            _commTable = prefix + "comms";
            _serverId = string.IsNullOrWhiteSpace(config.ServerId) ? "default" : config.ServerId.Trim();

            _connectionString = new MySqlConnectionStringBuilder
            {
                Server = config.Host,
                Port = (uint)config.Port,
                UserID = config.User,
                Password = config.Password,
                Database = config.Name,
                Pooling = true,
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 10
            }.ConnectionString;

            Enabled = true;
        }
        catch (Exception ex)
        {
            AdminPlus.LogError($"Database config load failed: {ex.Message}");
        }
    }

    public static void Initialize(AdminPlus plugin)
    {
        if (!Enabled) return;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();
                await RefreshCacheAsync(plugin, kickOnline: true);
                AdminPlus.LogWarn($"Database connected — bans are now backed by MySQL table `{_table}`.");
            }
            catch (Exception ex)
            {
                MarkOffline("init", ex);
            }
        });
    }

    public static void RefreshCache(AdminPlus plugin, bool kickOnline)
    {
        if (!Enabled || IsOffline) return;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();
                await RefreshCacheAsync(plugin, kickOnline);
            }
            catch (Exception ex)
            {
                MarkOffline("ban refresh", ex);
            }
        });
    }

    public static void SaveSteamBan(string steamId, string nick, string ip, long expiry, int minutes, string reason, string adminName, string adminSteamId)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(steamId)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        EnqueueWrite(async conn =>
        {
            using (var old = new MySqlCommand($"UPDATE `{_table}` SET status='unbanned', unbanned_by='Rebanned', unbanned_at=@now WHERE type='steam' AND steamid=@sid AND status='active'", conn))
            {
                old.Parameters.AddWithValue("@now", now);
                old.Parameters.AddWithValue("@sid", steamId);
                await old.ExecuteNonQueryAsync();
            }

            using var cmd = new MySqlCommand($@"INSERT INTO `{_table}`
                (type, steamid, ip, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at, server_id)
                VALUES ('steam', @sid, @ip, @nick, @admin, @adminSid, @reason, @minutes, @now, @expiry, @server)", conn);
            cmd.Parameters.AddWithValue("@sid", steamId);
            cmd.Parameters.AddWithValue("@ip", string.IsNullOrWhiteSpace(ip) ? "-" : Truncate(ip, 45));
            cmd.Parameters.AddWithValue("@nick", Truncate(nick, 128));
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@adminSid", Truncate(adminSteamId, 32));
            cmd.Parameters.AddWithValue("@reason", Truncate(reason, 255));
            cmd.Parameters.AddWithValue("@minutes", minutes);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@expiry", expiry);
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public static void SaveIpBan(string ip, string nick, string reason, string adminName, string adminSteamId)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ip)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        EnqueueWrite(async conn =>
        {
            using (var old = new MySqlCommand($"UPDATE `{_table}` SET status='unbanned', unbanned_by='Rebanned', unbanned_at=@now WHERE type='ip' AND ip=@ip AND status='active'", conn))
            {
                old.Parameters.AddWithValue("@now", now);
                old.Parameters.AddWithValue("@ip", ip);
                await old.ExecuteNonQueryAsync();
            }

            using var cmd = new MySqlCommand($@"INSERT INTO `{_table}`
                (type, steamid, ip, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at, server_id)
                VALUES ('ip', NULL, @ip, @nick, @admin, @adminSid, @reason, 0, @now, 0, @server)", conn);
            cmd.Parameters.AddWithValue("@ip", Truncate(ip, 45));
            cmd.Parameters.AddWithValue("@nick", Truncate(nick, 128));
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@adminSid", Truncate(adminSteamId, 32));
            cmd.Parameters.AddWithValue("@reason", Truncate(reason, 255));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public static void SaveUnban(string key, string adminName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(key)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        EnqueueWrite(async conn =>
        {
            using var cmd = new MySqlCommand($"UPDATE `{_table}` SET status='unbanned', unbanned_by=@admin, unbanned_at=@now WHERE status='active' AND (steamid=@key OR ip=@key)", conn);
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@key", key);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    // banType: "all", "steam" or "ip"
    public static void SaveClear(string banType, string adminName)
    {
        if (!Enabled) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string typeFilter = banType switch
        {
            "steam" => " AND type='steam'",
            "ip" => " AND type='ip'",
            _ => ""
        };

        EnqueueWrite(async conn =>
        {
            using var cmd = new MySqlCommand($"UPDATE `{_table}` SET status='unbanned', unbanned_by=@admin, unbanned_at=@now WHERE status='active'{typeFilter}", conn);
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public static void CheckPlayerOnConnect(AdminPlus plugin, int slot, string steamId, string ip)
    {
        if (!Enabled || IsOffline) return;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = new MySqlCommand($@"SELECT type, steamid, ip, player_name, reason, expires_at FROM `{_table}`
                    WHERE status='active' AND (expires_at = 0 OR expires_at > @now)
                      AND (steamid = @sid OR (@ip <> '' AND @ip <> '-' AND ip = @ip))
                    LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@sid", steamId);
                cmd.Parameters.AddWithValue("@ip", ip);

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return;

                var type = reader.GetString(0);
                bool isIpBan = type == "ip";
                var key = isIpBan
                    ? (reader.IsDBNull(2) ? "" : reader.GetString(2))
                    : (reader.IsDBNull(1) ? "" : reader.GetString(1));
                var nick = CleanForCfg(reader.IsDBNull(3) ? "Unknown" : reader.GetString(3));
                var reason = CleanForCfg(reader.IsDBNull(4) ? "" : reader.GetString(4));
                var expiry = reader.GetInt64(5);

                if (string.IsNullOrWhiteSpace(key)) return;

                Server.NextFrame(() => plugin.ApplyDatabaseBanHit(slot, isIpBan, key, nick, reason, expiry));
            }
            catch (Exception ex)
            {
                MarkOffline("connect ban check", ex);
            }
        });
    }

    private static async Task RefreshCacheAsync(AdminPlus plugin, bool kickOnline)
    {
        await DrainWritesAsync();
        if (!_pendingWrites.IsEmpty)
        {
            AdminPlus.LogWarn("Skipping database ban refresh: pending writes are not synced yet.");
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var steam = new Dictionary<string, (long expiry, string line, string nick, string ip)>();
        var ips = new Dictionary<string, (long expiry, string line, string nick)>();

        using (var conn = new MySqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            using (var expire = new MySqlCommand($"UPDATE `{_table}` SET status='expired' WHERE status='active' AND expires_at > 0 AND expires_at <= @now", conn))
            {
                expire.Parameters.AddWithValue("@now", now);
                await expire.ExecuteNonQueryAsync();
            }

            using var cmd = new MySqlCommand($"SELECT type, steamid, ip, player_name, reason, expires_at FROM `{_table}` WHERE status='active'", conn);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var type = reader.GetString(0);
                var steamId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var ip = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var nick = CleanForCfg(reader.IsDBNull(3) ? "Unknown" : reader.GetString(3));
                var reason = CleanForCfg(reader.IsDBNull(4) ? "" : reader.GetString(4));
                var expiry = reader.GetInt64(5);

                if (type == "steam" && !string.IsNullOrWhiteSpace(steamId))
                {
                    var ipToken = string.IsNullOrWhiteSpace(ip) ? "-" : ip.Replace(" ", "");
                    var line = $"banid \"{steamId}\" \"{nick}\" ip:{ipToken} expiry:{expiry} // {reason}";
                    steam[steamId] = (expiry, line, nick, ipToken);
                }
                else if (type == "ip" && !string.IsNullOrWhiteSpace(ip))
                {
                    var line = $"addip \"{ip}\" expiry:{expiry} // {reason} (Player: {nick})";
                    ips[ip] = (expiry, line, nick);
                }
            }
        }

        Server.NextFrame(() =>
        {
            plugin.ApplyBanSnapshot(steam, ips);
            if (kickOnline)
                plugin.KickBannedOnlinePlayers();
        });
    }

    private static async Task CreateSchemaAsync()
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = $@"CREATE TABLE IF NOT EXISTS `{_table}` (
            `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
            `type` ENUM('steam','ip') NOT NULL,
            `steamid` VARCHAR(32) NULL,
            `ip` VARCHAR(45) NULL,
            `player_name` VARCHAR(128) NOT NULL DEFAULT '',
            `admin_name` VARCHAR(128) NOT NULL DEFAULT 'Console',
            `admin_steamid` VARCHAR(32) NOT NULL DEFAULT '',
            `reason` VARCHAR(255) NOT NULL DEFAULT '',
            `duration_minutes` INT NOT NULL DEFAULT 0,
            `created_at` BIGINT NOT NULL,
            `expires_at` BIGINT NOT NULL DEFAULT 0,
            `status` ENUM('active','expired','unbanned') NOT NULL DEFAULT 'active',
            `unbanned_by` VARCHAR(128) NULL,
            `unbanned_at` BIGINT NULL,
            `server_id` VARCHAR(64) NOT NULL DEFAULT 'default',
            PRIMARY KEY (`id`),
            KEY `idx_steamid_status` (`steamid`,`status`),
            KEY `idx_ip_status` (`ip`,`status`),
            KEY `idx_status_expires` (`status`,`expires_at`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

        using (var cmd = new MySqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync();

        var commSql = $@"CREATE TABLE IF NOT EXISTS `{_commTable}` (
            `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
            `type` ENUM('MUTE','GAG') NOT NULL,
            `steamid` VARCHAR(32) NOT NULL,
            `player_name` VARCHAR(128) NOT NULL DEFAULT '',
            `admin_name` VARCHAR(128) NOT NULL DEFAULT 'Console',
            `admin_steamid` VARCHAR(32) NOT NULL DEFAULT '',
            `reason` VARCHAR(255) NOT NULL DEFAULT '',
            `duration_minutes` INT NOT NULL DEFAULT 0,
            `created_at` BIGINT NOT NULL,
            `expires_at` BIGINT NOT NULL DEFAULT 0,
            `status` ENUM('active','expired','removed') NOT NULL DEFAULT 'active',
            `removed_by` VARCHAR(128) NULL,
            `removed_at` BIGINT NULL,
            `server_id` VARCHAR(64) NOT NULL DEFAULT 'default',
            PRIMARY KEY (`id`),
            KEY `idx_steamid_status` (`steamid`,`status`),
            KEY `idx_status_expires` (`status`,`expires_at`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

        using (var cmd = new MySqlCommand(commSql, conn))
            await cmd.ExecuteNonQueryAsync();
    }

    // One-time migration: if the table is empty, seed it with whatever the
    // cfg files currently hold (already loaded into the in-memory cache).
    private static async Task ImportCfgBansIfEmptyAsync()
    {
        List<(string steamId, long expiry, string nick, string ip, string reason)> steam;
        List<(string ip, long expiry, string nick, string reason)> ips;

        lock (AdminPlus._lock)
        {
            steam = AdminPlus.SteamBans
                .Select(kv => (kv.Key, kv.Value.expiry, kv.Value.nick, kv.Value.ip, ExtractReason(kv.Value.line)))
                .ToList();
            ips = AdminPlus.IpBans
                .Select(kv => (kv.Key, kv.Value.expiry, kv.Value.nick, ExtractReason(kv.Value.line)))
                .ToList();
        }

        if (steam.Count == 0 && ips.Count == 0) return;

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        using (var count = new MySqlCommand($"SELECT COUNT(*) FROM `{_table}`", conn))
        {
            var existing = Convert.ToInt64(await count.ExecuteScalarAsync());
            if (existing > 0) return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var b in steam)
        {
            using var cmd = new MySqlCommand($@"INSERT INTO `{_table}`
                (type, steamid, ip, player_name, admin_name, reason, created_at, expires_at, server_id)
                VALUES ('steam', @sid, @ip, @nick, 'CfgImport', @reason, @now, @expiry, @server)", conn);
            cmd.Parameters.AddWithValue("@sid", b.steamId);
            cmd.Parameters.AddWithValue("@ip", string.IsNullOrWhiteSpace(b.ip) ? "-" : Truncate(b.ip, 45));
            cmd.Parameters.AddWithValue("@nick", Truncate(b.nick, 128));
            cmd.Parameters.AddWithValue("@reason", Truncate(b.reason, 255));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@expiry", b.expiry);
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var b in ips)
        {
            using var cmd = new MySqlCommand($@"INSERT INTO `{_table}`
                (type, steamid, ip, player_name, admin_name, reason, created_at, expires_at, server_id)
                VALUES ('ip', NULL, @ip, @nick, 'CfgImport', @reason, @now, @expiry, @server)", conn);
            cmd.Parameters.AddWithValue("@ip", Truncate(b.ip, 45));
            cmd.Parameters.AddWithValue("@nick", Truncate(b.nick, 128));
            cmd.Parameters.AddWithValue("@reason", Truncate(b.reason, 255));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@expiry", b.expiry);
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        }

        AdminPlus.LogWarn($"Imported {steam.Count} SteamID bans and {ips.Count} IP bans from cfg files into the database.");
    }

    // ===== Communication punishments (mute/gag) =====

    public static void InitializeComms(AdminPlus plugin, List<AdminPlus.CommunicationPunishment> localSnapshot)
    {
        if (!Enabled) return;

        _commImportSnapshot = localSnapshot;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();
                await RefreshCommsAsync(plugin);
                AdminPlus.LogWarn($"Communication punishments are now backed by MySQL table `{_commTable}`.");
            }
            catch (Exception ex)
            {
                MarkOffline("comms init", ex);
            }
        });
    }

    public static void RefreshComms(AdminPlus plugin)
    {
        if (!Enabled || IsOffline) return;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();
                await RefreshCommsAsync(plugin);
            }
            catch (Exception ex)
            {
                MarkOffline("comms refresh", ex);
            }
        });
    }

    public static void SaveComm(ulong steamId, string nick, string type, int duration, string reason, string adminName, ulong adminSteamId, DateTime created, DateTime endTime)
    {
        if (!Enabled || steamId == 0) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long createdUnix = ToUnix(created);
        long expiresUnix = duration == 0 ? 0 : ToUnix(endTime);
        string sid = steamId.ToString();

        EnqueueWrite(async conn =>
        {
            using (var old = new MySqlCommand($"UPDATE `{_commTable}` SET status='removed', removed_by='Replaced', removed_at=@now WHERE steamid=@sid AND type=@type AND status='active'", conn))
            {
                old.Parameters.AddWithValue("@now", now);
                old.Parameters.AddWithValue("@sid", sid);
                old.Parameters.AddWithValue("@type", type);
                await old.ExecuteNonQueryAsync();
            }

            using var cmd = new MySqlCommand($@"INSERT INTO `{_commTable}`
                (type, steamid, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at, server_id)
                VALUES (@type, @sid, @nick, @admin, @adminSid, @reason, @minutes, @created, @expiry, @server)", conn);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@nick", Truncate(nick, 128));
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@adminSid", adminSteamId == 0 ? "" : adminSteamId.ToString());
            cmd.Parameters.AddWithValue("@reason", Truncate(reason, 255));
            cmd.Parameters.AddWithValue("@minutes", duration);
            cmd.Parameters.AddWithValue("@created", createdUnix);
            cmd.Parameters.AddWithValue("@expiry", expiresUnix);
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public static void SaveCommRemove(ulong steamId, string type, string adminName)
    {
        if (!Enabled || steamId == 0) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sid = steamId.ToString();

        EnqueueWrite(async conn =>
        {
            using var cmd = new MySqlCommand($"UPDATE `{_commTable}` SET status='removed', removed_by=@admin, removed_at=@now WHERE steamid=@sid AND type=@type AND status='active'", conn);
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@type", type);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    // commType: "all", "MUTE" or "GAG"
    public static void SaveCommClear(string commType, string adminName)
    {
        if (!Enabled) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string typeFilter = commType switch
        {
            "MUTE" => " AND type='MUTE'",
            "GAG" => " AND type='GAG'",
            _ => ""
        };

        EnqueueWrite(async conn =>
        {
            using var cmd = new MySqlCommand($"UPDATE `{_commTable}` SET status='removed', removed_by=@admin, removed_at=@now WHERE status='active'{typeFilter}", conn);
            cmd.Parameters.AddWithValue("@admin", Truncate(adminName, 128));
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public static void CheckCommsOnConnect(AdminPlus plugin, ulong steamId)
    {
        if (!Enabled || steamId == 0 || IsOffline) return;

        Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = new MySqlCommand($@"SELECT type, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at FROM `{_commTable}`
                    WHERE status='active' AND steamid=@sid AND (expires_at = 0 OR expires_at > @now)", conn);
                cmd.Parameters.AddWithValue("@sid", steamId.ToString());
                cmd.Parameters.AddWithValue("@now", now);

                var hits = new List<AdminPlus.CommunicationPunishment>();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                        hits.Add(ReadCommPunishment(reader, steamId));
                }

                if (hits.Count == 0) return;

                Server.NextFrame(() => plugin.ApplyCommHits(steamId, hits));
            }
            catch (Exception ex)
            {
                MarkOffline("connect comms check", ex);
            }
        });
    }

    private static async Task RefreshCommsAsync(AdminPlus plugin)
    {
        await DrainWritesAsync();
        if (!_pendingWrites.IsEmpty)
        {
            AdminPlus.LogWarn("Skipping comms database refresh: pending writes are not synced yet.");
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var punishments = new List<AdminPlus.CommunicationPunishment>();

        using (var conn = new MySqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            using (var expire = new MySqlCommand($"UPDATE `{_commTable}` SET status='expired' WHERE status='active' AND expires_at > 0 AND expires_at <= @now", conn))
            {
                expire.Parameters.AddWithValue("@now", now);
                await expire.ExecuteNonQueryAsync();
            }

            using var cmd = new MySqlCommand($"SELECT type, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at, steamid FROM `{_commTable}` WHERE status='active'", conn);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (!ulong.TryParse(reader.IsDBNull(8) ? "" : reader.GetString(8), out var sid) || sid == 0)
                    continue;

                punishments.Add(ReadCommPunishment(reader, sid));
            }
        }

        Server.NextFrame(() => plugin.ApplyCommSnapshot(punishments));
    }

    private static AdminPlus.CommunicationPunishment ReadCommPunishment(MySqlDataReader reader, ulong steamId)
    {
        var duration = reader.GetInt32(5);
        var createdUnix = reader.GetInt64(6);
        var expiresUnix = reader.GetInt64(7);

        // Rows inserted from outside the plugin may set expires_at without
        // duration_minutes; derive it so IsPermanent/IsExpired behave correctly.
        if (duration == 0 && expiresUnix > 0)
            duration = (int)Math.Max(1, (expiresUnix - createdUnix) / 60);

        ulong.TryParse(reader.IsDBNull(3) ? "" : reader.GetString(3), out var adminSid);

        return new AdminPlus.CommunicationPunishment
        {
            SteamID = steamId,
            PlayerName = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
            Type = reader.GetString(0),
            Duration = duration,
            Reason = reader.IsDBNull(4) ? "" : reader.GetString(4),
            AdminName = reader.IsDBNull(2) ? "Console" : reader.GetString(2),
            AdminSteamID = adminSid,
            Created = FromUnix(createdUnix),
            EndTime = expiresUnix == 0 ? DateTime.MaxValue : FromUnix(expiresUnix)
        };
    }

    private static async Task ImportCommsIfEmptyAsync(List<AdminPlus.CommunicationPunishment> snapshot)
    {
        var active = snapshot.Where(p => !p.IsExpired).ToList();
        if (active.Count == 0) return;

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        using (var count = new MySqlCommand($"SELECT COUNT(*) FROM `{_commTable}`", conn))
        {
            var existing = Convert.ToInt64(await count.ExecuteScalarAsync());
            if (existing > 0) return;
        }

        foreach (var p in active)
        {
            using var cmd = new MySqlCommand($@"INSERT INTO `{_commTable}`
                (type, steamid, player_name, admin_name, admin_steamid, reason, duration_minutes, created_at, expires_at, server_id)
                VALUES (@type, @sid, @nick, @admin, @adminSid, @reason, @minutes, @created, @expiry, @server)", conn);
            cmd.Parameters.AddWithValue("@type", p.Type);
            cmd.Parameters.AddWithValue("@sid", p.SteamID.ToString());
            cmd.Parameters.AddWithValue("@nick", Truncate(p.PlayerName, 128));
            cmd.Parameters.AddWithValue("@admin", Truncate(p.AdminName, 128));
            cmd.Parameters.AddWithValue("@adminSid", p.AdminSteamID == 0 ? "" : p.AdminSteamID.ToString());
            cmd.Parameters.AddWithValue("@reason", Truncate(p.Reason, 255));
            cmd.Parameters.AddWithValue("@minutes", p.Duration);
            cmd.Parameters.AddWithValue("@created", ToUnix(p.Created));
            cmd.Parameters.AddWithValue("@expiry", p.IsPermanent ? 0 : ToUnix(p.EndTime));
            cmd.Parameters.AddWithValue("@server", _serverId);
            await cmd.ExecuteNonQueryAsync();
        }

        AdminPlus.LogWarn($"Imported {active.Count} communication punishments from communication_data.json into the database.");
    }

    private static long ToUnix(DateTime dt)
    {
        if (dt == DateTime.MaxValue || dt == default) return 0;

        try
        {
            return new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime FromUnix(long unix)
        => unix <= 0 ? DateTime.Now : DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

    private static void EnqueueWrite(Func<MySqlConnection, Task> op)
    {
        _pendingWrites.Enqueue(op);
        _ = Task.Run(DrainWritesAsync);
    }

    private static async Task DrainWritesAsync()
    {
        if (!await _writeGate.WaitAsync(0)) return;

        try
        {
            if (_pendingWrites.IsEmpty || IsOffline) return;

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            while (_pendingWrites.TryPeek(out var op))
            {
                await op(conn);
                _pendingWrites.TryDequeue(out _);
            }
        }
        catch (Exception ex)
        {
            MarkOffline("write queue (will retry)", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string ExtractReason(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[(idx + 2)..].Trim() : "";
    }

    private static string CleanForCfg(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var cleaned = Regex.Replace(input, @"[""\\\n\r\t;]", "");
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Length <= max ? value : value[..max];
    }
}
