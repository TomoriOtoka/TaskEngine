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
        public string CurrentNickname { get; set; }

        private bool _disposed;
        private string _nickname;


        
        public event Action<string> OnLog;

        /// <summary>
        /// Intervalo en ms por defecto 3000 ms (3s). 
        /// </summary>
        public ClientService(double intervalMs = 3000)
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;
            _timer = new Timer(intervalMs) { AutoReset = true };
            _timer.Elapsed += async (s, e) => await TimerTickAsync();
        }

        public async Task InitializeAsync()
        {
            var existing = await _firebase.GetMachineAsync(_pcName);
            if (existing == null)
            {
                CurrentNickname = _pcName; // por defecto

                var info = new PCInfo
                {
                    PCName = _pcName,
                    Nickname = CurrentNickname,
                    IsOnline = true
                };

                await _firebase.SetMachineAsync(_pcName, info);
            }
            else
            {
                // si ya existe, conserva el nickname guardado
                CurrentNickname = existing.Nickname ?? _pcName;
            }
        }

        /// <summary>
        /// Inicia el envío periódico. Idempotente.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (_timer.Enabled) return;

            _ = RegisterIfFirstTimeAsync();
            _ = InitializeNicknameAsync();

            Log($"ClientService starting (interval {_timer.Interval}ms) for {_pcName}");

            _ = TimerTickAsync(); // una sola actualización inmediata
            _timer.Start();      
        }

        private async Task RegisterIfFirstTimeAsync()
        {
            var existing = await _firebase.GetMachineAsync(_pcName);
            if (existing == null)
            {
                _nickname = _pcName; // por defecto
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
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };

                await _firebase.SetMachineAsync(_pcName, info);
            }
            else
            {
                _nickname = existing.Nickname;
            }

        }
        /// <summary>
        /// Para el envío periódico.
        /// </summary>
        public void Stop()
        {
            if (_disposed) return;
            if (!_timer.Enabled) return;
            _timer.Stop();
            Log("ClientService stopped");
        }

        /// <summary>
        /// Método público para forzar un envío (por ejemplo desde el UI).
        /// </summary>
        public Task SendSnapshotAsync() => TimerTickAsync();

        /// <summary>
        /// Mata procesos de la lista negra que estén corriendo).
        /// Público para que el Master pueda pedir esto remotamente.
        /// </summary>
        public void KillForbiddenProcesses()
        {
            string[] forbidden = GetForbiddenList();

            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        var name = p.ProcessName.ToLowerInvariant();
                        if (forbidden.Any(f => name == f || name.StartsWith(f)))
                        {
                            try
                            {
                                Log($"Killing process {name} (pid {p.Id})");
                                p.Kill(true);
                            }
                            catch (Exception exKill)
                            {
                                Log($"Failed to kill {name}: {exKill.Message}");
                            }
                        }
                    }
                    catch { /* ignore individual process access errors */ }
                }
            }
            catch (Exception ex)
            {
                Log($"KillForbiddenProcesses failed: {ex.Message}");
            }
        }

        private async Task InitializeNicknameAsync()
        {
            try
            {
                var existing = await _firebase.GetMachineAsync(_pcName); // intenta leer de Firebase

                if (existing != null && !string.IsNullOrWhiteSpace(existing.Nickname))
                {
                    CurrentNickname = existing.Nickname; // conserva nickname existente
                }
                else
                {
                    CurrentNickname = _pcName; // por defecto
                                               // crea registro inicial
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
                        Nickname = CurrentNickname
                    };
                    await _firebase.SetMachineAsync(_pcName, info);
                }
            }
            catch (Exception ex)
            {
                Log("Error inicializando nickname: " + ex.Message);
                CurrentNickname = _pcName;
            }
        }


        /// <summary>
        /// Lógica que se ejecuta en cada tick. Maneja fallos parciales y siempre intenta enviar lo que pueda.
        /// </summary>
        /// 
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

        private DateTime _lastHistorySave = DateTime.MinValue;

        private async Task TimerTickAsync()
        {
            List<string> openForbiddenApps = new List<string>();
            var now = DateTime.UtcNow;

            // --- Leer registro actual de Firebase (incluye IsClassMode) ---
            PCInfo existing = null;
            try
            {
                existing = await _firebase.GetMachineAsync(_pcName);
            }
            catch
            {
                Log("Failed to read existing PC info from Firebase");
            }

            string currentGroup = existing?.Group;
            bool isClassMode = existing?.IsClassMode == true;

            // --- Detectar apps prohibidas ---
            string[] forbidden = GetForbiddenList();
            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        if (forbidden.Any(f => name == f || name.StartsWith(f)))
                            openForbiddenApps.Add(name);
                    }
                    catch { }
                }
                openForbiddenApps = openForbiddenApps.Distinct().ToList();
            }
            catch { }

            // --- MODO CLASE: Bloquear apps en tiempo real ---
            if (isClassMode)
            {
                KillForbiddenProcesses();
            }

            // --- Métricas del sistema ---
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

            // --- Construir paquete principal ---
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
                ForbiddenAppOpen = openForbiddenApps.Count > 0,
                ForbiddenProcesses = openForbiddenApps,
                DataValid = (cpu >= 0 && ramPercent >= 0 && disk >= 0),
                Nickname = CurrentNickname,
                IsClassMode = isClassMode,
                Group = currentGroup,
            };

            // --- Enviar a Firebase en la ruta principal (tiempo real) ---
            try
            {
                await _firebase.SetMachineAsync(_pcName, info);
            }
            catch (Exception ex)
            {
                Log($"Firebase error: {ex.Message}");
            }

            // --- GUARDAR HISTORIAL solo cada hora ---
            if ((now - _lastHistorySave).TotalHours >= 1)
            {
                _lastHistorySave = now;

                try
                {
                    var historyEntry = new PCInfo
                    {
                        PCName = _pcName,
                        CpuUsage = cpu,
                        CpuTemperature = temp,
                        RamUsagePercent = ramPercent,
                        UsedRamMB = usedMB,
                        TotalRamMB = totalMB,
                        DiskUsagePercent = disk,
                        LastUpdate = DateTime.UtcNow.ToString("o"),
                        Group = currentGroup,
                        IsClassMode = isClassMode
                    };

                    string timestampKey = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    string historyPath = $"machines/{_pcName}/history/{timestampKey}";
                    await _firebase.SetMachineAtPathAsync(historyPath, historyEntry);

                    // === LIMPIAR HISTORIAL ANTIGUO (>14 días) ===
                    DateTime cutoff = DateTime.UtcNow.AddDays(-14);
                    var allHistory = await _firebase.GetMachineHistoryAsync(_pcName);
                    foreach (var h in allHistory)
                    {
                        if (DateTime.TryParse(h.LastUpdate, out DateTime hTime) && hTime < cutoff)
                        {
                            string oldKey = $"machines/{_pcName}/history/{hTime:yyyyMMdd_HHmmss}";
                            await _firebase.DeleteAtPathAsync(oldKey);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to save/clean history: {ex.Message}");
                }
            }

            // --- Revisar comandos remotos ---
            try
            {
                var cmd = await _firebase.GetClientCommandAsync(_pcName);
                if (!string.IsNullOrEmpty(cmd))
                {
                    Log($"Received command: {cmd}");
                    if (cmd == "KILL_FORBIDDEN")
                    {
                        KillForbiddenProcesses();
                        await _firebase.SendClientCommandAsync(_pcName, null);
                    }
                }
            }
            catch { }
        }

        private void Log(string text)
        {
            try
            {
                var msg = $"[ClientService {DateTime.Now:HH:mm:ss}] {text}";
                Console.WriteLine(msg);
                OnLog?.Invoke(msg);
            }
            catch { /* no hagas fallar el servicio por logging */ }
        }

        /// <summary>
        /// Lista negra centralizada. Si luego quieres cargarla desde el servidor, cambia aquí para leer la configuración remota.
        /// </summary>
        /// <returns>nombres en minúscula sin extensión (.exe no incluido)</returns>
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
                    "leagueoflegends",
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
            }
            catch { }
            _disposed = true;
        }
        #endregion
    }
}
