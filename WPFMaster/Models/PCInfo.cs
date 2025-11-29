namespace WPFMaster.Models
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
    }
}
