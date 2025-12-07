using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        public string CurrentNickname { get; set; }
        private bool _disposed;

        public event Action<string> OnLog;

        private const int HISTORY_SAVE_INTERVAL_SECONDS = 60 * 30;
        private const int CLEANUP_INTERVAL_MINUTES = 60;
        private const int HISTORY_RETENTION_MINUTES = 1440 * 7;

        private DateTime _lastHistorySave = DateTime.MinValue;

        public ClientService(double intervalMs = 1800000) // 30 min
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;

            // Temporizador principal (métricas)
            _timer = new Timer(intervalMs) { AutoReset = true };
            _timer.Elapsed += async (s, e) => await TimerTickAsync();

            // Temporizador de limpieza
            _cleanupTimer = new Timer(TimeSpan.FromMinutes(CLEANUP_INTERVAL_MINUTES).TotalMilliseconds)
            {
                AutoReset = true
            };
            _cleanupTimer.Elapsed += async (s, e) => await CleanupHistoryAsync();
        }

        public async Task InitializeAsync()
        {
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
                await _firebase.SetMachineAsync(_pcName, info); // ✅ Solo "current"
            }
            else
            {
                CurrentNickname = existing.Nickname ?? _pcName;
            }
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (_timer.Enabled) return;

            _ = RegisterIfFirstTimeAsync();
            _ = InitializeNicknameAsync();

            Log($"ClientService starting (interval {_timer.Interval}ms) for {_pcName}");
            _ = TimerTickAsync();
            _timer.Start();
            _cleanupTimer.Start();
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_timer.Enabled) return;
            _timer.Stop();
            _cleanupTimer.Stop();
            Log("ClientService stopped");
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

        private async Task TimerTickAsync()
        {
            var now = DateTime.UtcNow;

            // --- Recoger métricas ---
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
                LastUpdate = now.ToString("o"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Nickname = CurrentNickname
            };

            // --- Guardar estado actual ---
            try { await _firebase.SetMachineAsync(_pcName, info); }
            catch (Exception ex) { Log($"Firebase error (main): {ex.Message}"); }

            // --- Guardar historial ---
            if ((now - _lastHistorySave).TotalSeconds >= HISTORY_SAVE_INTERVAL_SECONDS)
            {
                _lastHistorySave = now;
                try { await _firebase.AddHistoryPointAsync(_pcName, info);
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

        public Task SendSnapshotAsync()
        {
            // Llama al método que ya actualiza los datos y guarda en Firebase
            return TimerTickAsync();
        }

        public async Task UpdateNicknameAsync(string newNickname)
        {
            CurrentNickname = newNickname;

            // Obtener información existente de la PC
            var info = await _firebase.GetMachineAsync(_pcName);
            if (info != null)
            {
                info.Nickname = newNickname;
                await _firebase.SetMachineAsync(_pcName, info);
            }
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
            }
            catch { }
            _disposed = true;
        }
        #endregion
    }
}
