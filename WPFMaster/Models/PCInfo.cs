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
        public bool ForbiddenAppOpen { get; set; } = false; // Si esta usando una aplicación prohibida
        public List<string> ForbiddenProcesses { get; set; } = new List<string>();



    }
}
