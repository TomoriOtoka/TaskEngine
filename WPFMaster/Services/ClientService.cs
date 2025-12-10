using Newtonsoft.Json;
using OpenTK.Compute.OpenCL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TaskEngine.Models;
using TaskEngine.Utils;
using Timer = System.Timers.Timer;

namespace TaskEngine.Services
{
    /// <summary>
    /// Servicio que corre en el cliente: envía estado 'current', agrega puntos de historial
    /// y chequea mensajes. Diseñado para ser robusto y minimizar operaciones innecesarias.
    /// - Guarda un punto de historial cada 10 minutos (configurable).
    /// - Limpia historia más vieja que 8 días.
    /// - Persiste último mensaje / grupo localmente.
    /// </summary>
    public class ClientService : IDisposable
    {
        private readonly FirebaseService _firebase;
        private readonly string _pcName;
        private readonly Timer _metricsTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _messageTimer;

        public string CurrentNickname { get; private set; }
        private bool _disposed;

        // Persistencia local
        private readonly string _stateFilePath;
        private readonly string _groupConfigPath;

        // configuración (fáciles de ajustar)
        private const int METRICS_INTERVAL_MS = 3000;                      // cada 3s se recopilan métricas (pero historiales se guardan cada X)
        private const int HISTORY_SAVE_INTERVAL_SECONDS = 60 * 10;         // guardar historial cada 10 minutos
        private const int CLEANUP_INTERVAL_MINUTES = 60;                   // ejecutar limpieza cada 60 minutos
        private const int HISTORY_RETENTION_DAYS_FOR_CLEANUP = 8;          // borrar > 8 días
        private const int MESSAGE_CHECK_INTERVAL_MS = 10_000;             // chequear mensajes cada 10s

        private DateTime _lastHistorySaveUtc = DateTime.MinValue;
        private string _pcGroup = "Sin grupo";

        // Mensajes persistidos para no repetir
        private string _lastGlobalMessageId;
        private string _lastLabMessageId;

        public event Action<string> OnLog;

        public ClientService(double metricsIntervalMs = METRICS_INTERVAL_MS)
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;

            // directorio de appdata para persistencia
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskEngine");
            Directory.CreateDirectory(appData);
            _stateFilePath = Path.Combine(appData, "client_state.json");
            _groupConfigPath = Path.Combine(appData, "group_config.txt");

            // timers
            _metricsTimer = new Timer(metricsIntervalMs) { AutoReset = true };
            _metricsTimer.Elapsed += async (s, e) => await MetricsTickAsync();

            _cleanupTimer = new Timer(TimeSpan.FromMinutes(CLEANUP_INTERVAL_MINUTES).TotalMilliseconds) { AutoReset = true };
            _cleanupTimer.Elapsed += async (s, e) => await CleanupHistoryAsync();


            _messageTimer = new Timer(MESSAGE_CHECK_INTERVAL_MS) { AutoReset = true };
            _messageTimer.Elapsed += async (s, e) =>
            {   
                await CheckGlobalMessagesAsync();
                await CheckLabMessagesAsync();
            };
        }
        /// <summary>
        /// Inicia timers y tareas.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (_metricsTimer.Enabled) return;

            // ✅ ACTIVAR AUTOINICIO DESDE LA PRIMERA VEZ QUE SE INICIA EL CLIENTE
            if (!IsAutoStartEnabled())
            {
                SetAutoStart(true);
                Log("Inicio automático activado para el cliente.");
            }

            // Asegurar que la PC exista en Firebase si nunca antes
            _ = RegisterIfFirstTimeAsync();
            _ = InitializeGroupAsync();

            Log($"ClientService starting for {_pcName} (metrics every {_metricsTimer.Interval}ms)");
            _metricsTimer.Start();
            _cleanupTimer.Start();
            _messageTimer.Start();
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_metricsTimer.Enabled) return;
            _metricsTimer.Stop();
            _cleanupTimer.Stop();
            _messageTimer.Stop();
            Log("ClientService stopped");
        }

        public static class AutoCloseMessageBox
        {
            private const int MB_OK = 0x00000000;
            private const int MB_ICONINFORMATION = 0x00000040;
            private const int MB_SYSTEMMODAL = 0x00001000;
            private const int MB_TOPMOST = 0x00040000;

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern int MessageBox(IntPtr hWnd, string text, string caption, int type);

            public static void Show(string text, string title, int timeoutMs = 10000)
            {
                var thread = new Thread(() =>
                {
                    // TOPMOST + SYSTEMMODAL = encima de todo, incluso fullscreen
                    int flags = MB_OK | MB_ICONINFORMATION | MB_SYSTEMMODAL | MB_TOPMOST;

                    MessageBox(IntPtr.Zero, text, title, flags);
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }



        public async Task InitializeAsync()
        {
            await LoadPersistedStateAsync();

            

            // Si no existe registro "current" en Firebase, lo creamos (esto evita nulls al master)
            try
            {
                var existing = await _firebase.GetMachineAsync(_pcName);
                if (existing == null)
                {
                    // ✅ Crear con datos básicos, pero SIN sobrescribir Nickname si ya existía
                    var info = NewBaseInfo();
                    await _firebase.SetMachineAsync(_pcName, info);
                }

                // ✅ Ahora, actualizar solo Nickname y Group si existen en Firebase
                var currentFirebaseInfo = await _firebase.GetMachineAsync(_pcName);
                if (currentFirebaseInfo != null)
                {
                    CurrentNickname = string.IsNullOrWhiteSpace(currentFirebaseInfo.Nickname) ? _pcName : currentFirebaseInfo.Nickname;
                    if (!string.IsNullOrWhiteSpace(currentFirebaseInfo.Group))
                    {
                        _pcGroup = currentFirebaseInfo.Group;
                        await File.WriteAllTextAsync(_groupConfigPath, _pcGroup);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"InitializeAsync Firebase error: {ex.Message}");
            }

        }

        private async Task RegisterIfFirstTimeAsync()
        {
            try
            {
                var existing = await _firebase.GetMachineAsync(_pcName);
                if (existing == null)
                {
                    var info = NewBaseInfo();
                    info.Nickname = _pcName;
                    info.Group = _pcGroup;
                    await _firebase.SetMachineAsync(_pcName, info);
                }
            }
            catch (Exception ex)
            {
                Log($"RegisterIfFirstTimeAsync error: {ex.Message}");
            }
        }

        private PCInfo NewBaseInfo()
        {
            var nowUtc = DateTime.UtcNow;
            return new PCInfo
            {
                PCName = _pcName,
                CpuUsage = 0f,
                CpuTemperature = 0f,
                RamUsagePercent = 0f,
                TotalRamMB = 0,
                UsedRamMB = 0,
                DiskUsagePercent = 0f,
                IsOnline = true,
                LastUpdate = nowUtc.ToString("o"), // ISO UTC
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Nickname = _pcName,
                Group = _pcGroup
            };
        }

        /// <summary>
        /// Carga estado local (últimos ids de mensajes y grupo guardado).
        /// </summary>
        private async Task LoadPersistedStateAsync()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = await File.ReadAllTextAsync(_stateFilePath);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

                    dict.TryGetValue("LastGlobalMessageId", out _lastGlobalMessageId);
                    dict.TryGetValue("LastLabMessageId", out _lastLabMessageId);

                    if (dict.TryGetValue("LastLabTimestamp", out var ts) && DateTime.TryParse(ts, out var parsedTs))
                    {
                        _lastLabMessageTimestamp = parsedTs;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"LoadPersistedStateAsync error: {ex.Message}");
            }

            try
            {
                if (File.Exists(_groupConfigPath))
                {
                    _pcGroup = (await File.ReadAllTextAsync(_groupConfigPath)).Trim();
                    if (string.IsNullOrEmpty(_pcGroup)) _pcGroup = "Sin grupo";
                }
            }
            catch (Exception ex)
            {
                Log($"LoadPersistedStateAsync (group) error: {ex.Message}");
            }
        }

        /// <summary>
        /// Guarda en disco ids de mensajes para no repetir notificaciones.
        /// </summary>
        private async Task SaveMessageStateAsync()
        {
            try
            {
                var dict = new Dictionary<string, string>
                {
                    ["LastGlobalMessageId"] = _lastGlobalMessageId ?? "",
                    ["LastLabMessageId"] = _lastLabMessageId ?? "",
                    ["LastLabTimestamp"] = _lastLabMessageTimestamp == DateTime.MinValue ? "" : _lastLabMessageTimestamp.ToString("o")
                };
                await File.WriteAllTextAsync(_stateFilePath, JsonConvert.SerializeObject(dict, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log($"SaveMessageStateAsync error: {ex.Message}");
            }
        }


        private async Task InitializeGroupAsync()
        {
            try
            {
                var pc = await _firebase.GetMachineAsync(_pcName);
                if (pc != null && !string.IsNullOrWhiteSpace(pc.Group))
                {
                    _pcGroup = pc.Group;
                    await File.WriteAllTextAsync(_groupConfigPath, _pcGroup);
                }
            }
            catch (Exception ex)
            {
                Log($"InitializeGroupAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Timer principal: recopila métricas y decide si escribir historial.
        /// </summary>
        /// 

        private DateTime _lastAutoKillUtc = DateTime.MinValue;
        private bool _isClassMode = false;  // almacena el estado local de modo clase

        private async Task MetricsTickAsync()
        {
            var nowUtc = DateTime.UtcNow;

            // ---------- RECOPILAR MÉTRICAS ----------
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

            // ---------- DETECTAR APPS PROHIBIDAS ----------
            // ---------- DETECTAR APPS PROHIBIDAS ----------
            var forbidden = new List<string>();
            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();

                        // Detectar por nombre
                        if (GetForbiddenList().Any(f => name == f || name.StartsWith(f)))
                        {
                            // Si es java o javaw, verificar si está relacionado con Minecraft
                            if (name == "javaw" || name == "java")
                            {
                                if (IsJavaProcessMinecraft(p))
                                    forbidden.Add(name);
                            }
                            else
                            {
                                // Otros procesos prohibidos (como steam, discord, etc.)
                                forbidden.Add(name);
                            }
                        }
                    }
                    catch { }
                }
                forbidden = forbidden.Distinct().ToList();
            }
            catch { }

            // ---------- LEER ESTADO DEL GRUPO (MODO CLASE) ----------
            // === MODO CLASE AUTOMÁTICO: cerrar apps prohibidas si IsClassMode es true ===
            bool isClassMode = false;
            try
            {
                var groupClassMode = await _firebase.GetGroupClassModeAsync(_pcGroup);
                if (groupClassMode.HasValue)
                    isClassMode = groupClassMode.Value;
            }
            catch { }

            if (isClassMode && forbidden.Count > 0)
            {
                if ((DateTime.UtcNow - _lastAutoKillUtc).TotalSeconds >= 10)
                {
                    _lastAutoKillUtc = DateTime.UtcNow;
                    KillForbiddenProcesses();
                }
            }

            // ---------- CERRAR APPS PROHIBIDAS AUTOMÁTICAMENTE ----------
            if (_isClassMode && forbidden.Count > 0)
            {
                if ((DateTime.UtcNow - _lastAutoKillUtc).TotalSeconds >= 10)
                {
                    _lastAutoKillUtc = DateTime.UtcNow;
                    KillForbiddenProcesses();
                }
            }

            // ✅ === LEER EL ESTADO ACTUAL DE FIREBASE ANTES DE ENVIAR ===
            // Esto asegura que Nickname y Group no se sobrescriban accidentalmente
            PCInfo firebaseState = null;
            try
            {
                firebaseState = await _firebase.GetMachineAsync(_pcName);
            }
            catch (Exception ex)
            {
                Log($"MetricsTickAsync: Error al leer estado de Firebase: {ex.Message}");
                // Si no se puede leer, no enviamos nada (para evitar sobrescribir con datos vacíos)
                return;
            }

            // ✅ Extraer Nickname y Group desde el estado actual de Firebase (si existen)
            string currentNickname = firebaseState?.Nickname ?? _pcName; // Valor por defecto si no existe
            string currentGroup = firebaseState?.Group ?? _pcGroup;     // Valor por defecto si no existe

            // ✅ Actualizar variables locales solo si cambió el valor en Firebase
            if (currentNickname != CurrentNickname)
            {
                CurrentNickname = currentNickname;
                Log($"Nickname actualizado localmente: {CurrentNickname}");
            }
            if (currentGroup != _pcGroup)
            {
                _pcGroup = currentGroup;
                await File.WriteAllTextAsync(_groupConfigPath, _pcGroup); // Persistir localmente
                Log($"Grupo actualizado localmente: {_pcGroup}");
            }

            // ---------- CONSTRUIR PAQUETE PARA FIREBASE ----------
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
                LastUpdate = nowUtc.ToString("o"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                // ✅ NO usar CurrentNickname o _pcGroup directamente, sino los valores leídos de Firebase
                Nickname = currentNickname,
                Group = currentGroup,
                ForbiddenProcesses = forbidden,
                ForbiddenAppOpen = forbidden.Count > 0,
                // NO escribir _isClassMode para no sobrescribir el master
            };

            // ---------- DECIDIR SI HACER WRITE-BACK ----------
            // Solo si hay cambios en los datos que el cliente debe actualizar
            bool shouldWriteBack = firebaseState == null
                                   || firebaseState.CpuUsage != info.CpuUsage
                                   || firebaseState.RamUsagePercent != info.RamUsagePercent
                                   || firebaseState.DiskUsagePercent != info.DiskUsagePercent
                                   || firebaseState.IsOnline != info.IsOnline
                                   || firebaseState.ForbiddenAppOpen != info.ForbiddenAppOpen
                                   || Math.Abs(firebaseState.CpuTemperature - info.CpuTemperature) > 0.1f;

            if (shouldWriteBack)
            {
                try
                {
                    await _firebase.SetMachineAsync(_pcName, info);
                }
                catch (Exception ex)
                {
                    Log($"MetricsTickAsync: SetMachineAsync error: {ex.Message}");
                }
            }

            // ---------- GUARDAR HISTORIAL ----------
            var elapsed = (nowUtc - _lastHistorySaveUtc).TotalSeconds;
            if (elapsed >= HISTORY_SAVE_INTERVAL_SECONDS)
            {
                _lastHistorySaveUtc = nowUtc;
                try
                {
                    await _firebase.AddHistoryPointAsync(_pcName, info);
                    await CleanupHistoryAsync();
                }
                catch (Exception ex)
                {
                    Log($"Error al guardar historial: {ex.Message}");
                }
            }

            // ---------- PROCESAR COMANDO KILL_FORBIDDEN (Master) ----------
            try
            {
                var cmd = await _firebase.GetClientCommandAsync(_pcName);
                if (!string.IsNullOrEmpty(cmd) && cmd == "KILL_FORBIDDEN")
                {
                    KillForbiddenProcesses();
                    await _firebase.SendClientCommandAsync(_pcName, null);
                }
            }
            catch (Exception ex)
            {
                Log($"Error al procesar comando: {ex.Message}");
            }
        }



        /// <summary>
        /// Limpia history entries más viejos que HISTORY_RETENTION_DAYS_FOR_CLEANUP días.
        /// Implementa batching y manejo de excepción.
        /// </summary>
        private async Task CleanupHistoryAsync()
        {
            try
            {
                // pedimos al servicio que borre todo más viejo que N días
                var retention = TimeSpan.FromDays(HISTORY_RETENTION_DAYS_FOR_CLEANUP);
                await _firebase.CleanOldHistoryAsync(_pcName, retention);
            }
            catch (Exception ex)
            {
                Log($"CleanupHistoryAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Revisa mensajes globales y de laboratorio y evita procesar el mismo mensaje dos veces.
        /// </summary>
        private async Task CheckGlobalMessagesAsync()
        {
            try
            {
                var global = await _firebase.GetGlobalMessageAsync();
                if (global == null || string.IsNullOrWhiteSpace(global.Id)) return;

                // validar timestamp opcional (ejemplo: ignorar si > 30 minutos)
                if (DateTime.TryParse(global.Timestamp, out DateTime gTime))
                {
                    if ((DateTime.UtcNow - gTime).TotalMinutes > 30) return;
                }

                if (global.Id == _lastGlobalMessageId) return;

                _lastGlobalMessageId = global.Id;
                Log($"[GLOBAL MESSAGE] {global.Sender}: {global.Message}");
                await SaveMessageStateAsync();

                // Si quieres mostrar algo al usuario en el cliente, aquí puedes añadir MessageBox.Show(...)
            }
            catch (Exception ex)
            {
                Log($"CheckGlobalMessagesAsync error: {ex.Message}");
            }
        }



        private DateTime _lastLabMessageTimestamp = DateTime.MinValue;


        private async Task CheckLabMessagesAsync()
        {
            Log($"CheckLabMessagesAsync: Grupo actual = '{_pcGroup}'");

            try
            {
                if (string.IsNullOrEmpty(_pcGroup))
                {
                    Log("CheckLabMessagesAsync: Grupo vacío, omitiendo.");
                    return;
                }

                var lab = await _firebase.GetLabMessageAsync(_pcGroup);
                Log($"CheckLabMessagesAsync: Obtenido mensaje: {lab?.Message ?? "null"}");

                if (lab == null || string.IsNullOrWhiteSpace(lab.Id)) return;

                // Si ya procesamos este ID, ignorar
                if (lab.Id == _lastLabMessageId)
                {
                    Log($"CheckLabMessagesAsync: Mensaje con ID {lab.Id} ya procesado, ignorando.");
                    return;
                }

                // Si el timestamp existe y es anterior o igual al último guardado, ignorar (mensaje viejo)
                DateTime msgTime = DateTime.MinValue;
                if (DateTime.TryParse(lab.Timestamp, out var parsed))
                {
                    msgTime = parsed;
                    if (msgTime <= _lastLabMessageTimestamp)
                    {
                        Log($"CheckLabMessagesAsync: Mensaje con timestamp {msgTime} es viejo, ignorando.");
                        return;
                    }
                }

                // Es un mensaje nuevo -> actualizar estado
                _lastLabMessageId = lab.Id;
                if (msgTime != DateTime.MinValue) _lastLabMessageTimestamp = msgTime;

                // Mostrar solo el texto del mensaje (sin nombre PC)
                AutoCloseMessageBox.Show(lab.Message, "Mensaje del Laboratorio", 10000);

                Log($"[LAB MESSAGE {_pcGroup}] {lab.Sender}: {lab.Message}");
                await SaveMessageStateAsync();
            }
            catch (Exception ex)
            {
                Log($"CheckLabMessagesAsync error: {ex.Message}");
            }
        }

        private bool IsJavaProcessMinecraft(Process p)
        {
            try
            {
                // Intentamos obtener el nombre de la ventana o el comando de inicio
                var processName = p.ProcessName.ToLowerInvariant();

                // Si no es java, no nos interesa
                if (processName != "javaw" && processName != "java") return false;

                // Intentamos leer el MainModule (esto puede fallar si no tenemos permisos)
                string fileName = "";
                try
                {
                    fileName = p.MainModule?.FileName?.ToLowerInvariant() ?? "";
                }
                catch
                {
                    // Si no podemos leerlo, intentamos con otro método
                    return false;
                }

                // Si el nombre del archivo contiene "minecraft", es probablemente Minecraft
                if (fileName.Contains("minecraft")) return true;

                // Opcional: también podrías leer los argumentos del proceso si tienes acceso
                // Esto es más complejo y puede requerir WMI o lectura de CommandLine, que es más lento

                return false;
            }
            catch
            {
                return false;
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
            catch { /* no falls */ }
        }

        public Task SendSnapshotAsync() => MetricsTickAsync();

        public async Task UpdateNicknameAsync(string newNickname)
        {
            if (string.IsNullOrWhiteSpace(newNickname)) return;
            CurrentNickname = newNickname;
            try
            {
                var info = await _firebase.GetMachineAsync(_pcName) ?? NewBaseInfo();
                info.Nickname = newNickname;
                await _firebase.SetMachineAsync(_pcName, info);
            }
            catch (Exception ex)
            {
                Log($"UpdateNicknameAsync error: {ex.Message}");
            }
        }


        public class LabMessage
        {
            public string text { get; set; }
            public string timestamp { get; set; }
        }

        private void KillForbiddenProcesses()
        {
            try
            {
                var forbiddenList = GetForbiddenList();
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        if (forbiddenList.Any(f => name == f || name.StartsWith(f)))
                        {
                            Log($"Matando proceso prohibido: {name} (PID: {p.Id})");
                            p.Kill(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error al matar {p.ProcessName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error en KillForbiddenProcesses: {ex.Message}");
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
                "tlauncher",
                "sklauncher",
                "OPENJDK Platform binary",
                "leagueclient",
                "discord"
            }.Select(s => s.ToLowerInvariant()).ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Stop();
                _metricsTimer.Dispose();
                _cleanupTimer.Dispose();
                _messageTimer.Dispose();
            }
            catch { }
            _disposed = true;
        }

        // --- AUTO STARTUP FUNCTIONS ---

        private const string AUTO_STARTUP_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        // Método para activar/desactivar el inicio automático
        private void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTO_STARTUP_KEY, true))
                {
                    if (enable)
                    {
                        // Ruta del ejecutable actual
                        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue("TaskEngineClient", exePath); // Cambié el nombre para distinguirlo del master
                    }
                    else
                    {
                        key.DeleteValue("TaskEngineClient", false); // 'false' evita excepción si no existe
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error al configurar inicio automático: {ex.Message}");
            }
        }

        // Método para verificar si está activado el inicio automático
        private bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTO_STARTUP_KEY, false)) // Solo lectura
                {
                    var value = key?.GetValue("TaskEngineClient");
                    if (value != null)
                    {
                        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        return value.ToString() == exePath;
                    }
                }
            }
            catch
            {
                // Si hay un error de acceso, asumimos que no está activado
            }
            return false;
        }
    }
}
