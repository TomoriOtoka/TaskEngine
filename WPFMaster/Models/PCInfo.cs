namespace WPFMaster.Models
{
    public class PCInfo
    {
        public string PCName { get; set; }
        
        public float CpuUsage { get; set; }
        public float TotalRam { get; set; }
        public float UsedRam { get; set; }
        public float FreeRam { get; set; }
        public string LastUpdate { get; set; }
    }
}
