using System;
using System.Threading.Tasks;
using System.Timers;
using WPFMaster.Models;
using WPFMaster.Utils;
using Timer = System.Timers.Timer;

namespace WPFMaster.Services
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
        /// Lógica que se ejecuta en cada tick. Maneja fallos parciales y siempre intenta enviar lo que pueda.
        /// </summary>
        private async Task TimerTickAsync()
        {
            try
            {
                float cpu = 0f;
                float temp = 0f;
                float disk = 0f;
                int usedMB = 0, totalMB = 0;
                float ramPercent = 0f;

                // Cada lectura en su try para que un fallo en una no cancele las demás
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
                    usedMB =  Convert.ToInt32(r.usedMB);
                    totalMB = Convert.ToInt32(r.totalMB);
                }
                catch (Exception ex)
                {
                    Log($"GetRamInfo error: {ex.Message}");
                }

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
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };

                try
                {
                    await _firebase.SetMachineAsync(_pcName, info);
                    Log($"Sent snapshot — CPU:{cpu:0.0}% RAM:{ramPercent:0.0}% Disk:{disk:0.0}%");
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
