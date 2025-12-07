using System.Security.Permissions;

namespace TaskEngine.Models
{
    public class PCInfo
    {
        public string PCName { get; set; }
        public float CpuUsage { get; set; }          // porcentaje 0..100
        public float CpuTemperature { get; set; }    // °C
        public float RamUsagePercent { get; set; }   // porcentaje 0..100
        public float TotalRamMB { get; set; }        // MB
        public float UsedRamMB { get; set; }         // MB
        public float DiskUsagePercent { get; set; }  // porcentaje
        public bool IsOnline { get; set; }           // true si activo
        public string LastUpdate { get; set; }       // ISO timestamp
        public long Timestamp { get; set; }
        public bool ForbiddenAppOpen { get; set; } = false; // Si esta usando una aplicación prohibida
        public List<string> ForbiddenProcesses { get; set; } = new List<string>();
        public bool DataValid { get; set; } = true; // indica si se pudieron leer métricas
        public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
        public bool IsClassMode { get; set; }
        public string Group { get; set; } = "Sin grupo";
        public string Nickname { get; set; } = "";

        public bool ClockIssue { get; set; }
        public string Heartbeat { get; set; } = "";

    }
}
