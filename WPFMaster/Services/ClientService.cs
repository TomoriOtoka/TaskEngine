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
        private bool _disposed;

        /// <summary>
        /// Evento opcional para que la UI reciba logs (no obligatorio).
        /// </summary>
        public event Action<string> OnLog;

        /// <summary>
        /// Intervalo en ms por defecto 3000 ms (3s). Puedes pasar otro valor en el constructor.
        /// </summary>
        public ClientService(double intervalMs = 3000)
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;
            _timer = new Timer(intervalMs) { AutoReset = true };
            _timer.Elapsed += async (s, e) => await TimerTickAsync();
        }

        /// <summary>
        /// Inicia el envío periódico. Idempotente.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (_timer.Enabled) return;

            Log($"ClientService starting (interval {_timer.Interval}ms) for {_pcName}");
            _timer.Start();
            _ = TimerTickAsync(); // envía una primera actualización inmediata (fire-and-forget)
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
        /// Mata procesos de la lista negra que estén corriendo (intenta con Kill(true)).
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

        /// <summary>
        /// Lógica que se ejecuta en cada tick. Maneja fallos parciales y siempre intenta enviar lo que pueda.
        /// </summary>
        private async Task TimerTickAsync()
        {
            // -------------------------
            // 1) Detectar procesos prohibidos
            // -------------------------
            bool forbiddenAppOpen = false;
            List<string> openForbiddenApps = new List<string>();

            string[] forbidden = GetForbiddenList();

            try
            {
                var processes = Process.GetProcesses();

                foreach (var p in processes)
                {
                    try
                    {
                        // ProcessName no incluye sufijo .exe, así que trabajamos con nombres sin extension
                        string name = p.ProcessName.ToLowerInvariant();

                        foreach (var f in forbidden)
                        {
                            // Coincidencia exacta o inicio del nombre evita la mayoría de falsos positivos.
                            if (name == f || name.StartsWith(f))
                            {
                                openForbiddenApps.Add(name);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignorar procesos que no se puedan leer
                    }
                }

                // Eliminamos duplicados y normalizamos
                openForbiddenApps = openForbiddenApps.Distinct().ToList();
                forbiddenAppOpen = openForbiddenApps.Count > 0;
            }
            catch (Exception ex)
            {
                Log($"Process scan failed: {ex.Message}");
            }

            // -------------------------
            // 2) Leer métricas (CPU, temp, RAM, disco) — cada lectura en su try
            // -------------------------
            try
            {
                float cpu = 0f;
                float temp = 0f;
                float disk = 0f;
                int usedMB = 0, totalMB = 0;
                float ramPercent = 0f;

                try
                {
                    cpu = SystemInfo.GetCpuUsagePercent();
                }
                catch (Exception ex)
                {
                    Log($"GetCpuUsagePercent error: {ex.Message}");
                }

                try
                {
                    temp = SystemInfo.GetCpuTemperature();
                }
                catch (Exception ex)
                {
                    Log($"GetCpuTemperature error: {ex.Message}");
                }

                try
                {
                    disk = SystemInfo.GetDiskUsagePercent("C");
                }
                catch (Exception ex)
                {
                    Log($"GetDiskUsagePercent error: {ex.Message}");
                }

                try
                {
                    var r = SystemInfo.GetRamInfo();
                    ramPercent = r.percentUsed;
                    usedMB = Convert.ToInt32(r.usedMB);
                    totalMB = Convert.ToInt32(r.totalMB);
                }
                catch (Exception ex)
                {
                    Log($"GetRamInfo error: {ex.Message}");
                }

                // -------------------------
                // 3) Preparar objeto a enviar
                // -------------------------
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
                    ForbiddenAppOpen = forbiddenAppOpen,
                    // Aquí incluimos la lista concreta de procesos prohibidos abiertos
                    ForbiddenProcesses = openForbiddenApps
                };

                // -------------------------
                // 4) Enviar a Firebase (o tu backend)
                // -------------------------
                try
                {
                    await _firebase.SetMachineAsync(_pcName, info);
                    Log($"Sent snapshot — CPU:{cpu:0.0}% RAM:{ramPercent:0.0}% Disk:{disk:0.0}% Forbidden:{(forbiddenAppOpen ? string.Join(",", openForbiddenApps) : "none")}");
                }
                catch (Exception ex)
                {
                    Log($"Firebase.SetMachineAsync error: {ex.Message}");
                }
            }
            catch (Exception exOuter)
            {
                // Nunca permitir que una excepción no controlada detenga el timer
                Log($"Unhandled error in TimerTickAsync: {exOuter}");
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
