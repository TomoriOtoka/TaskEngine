using System;
using System.Threading.Tasks;
using WPFMaster.Models;
using WPFMaster.Utils;

namespace WPFMaster.Services
{
    public class ClientService
    {
        private readonly FirebaseService _firebase;
        private readonly string _pcName;

        public ClientService()
        {
            _firebase = new FirebaseService();
            _pcName = Environment.MachineName;
        }

        public async Task SendSnapshotAsync()
        {
            try
            {
                float cpu = SystemInfo.GetCpuUsagePercent();
                float temp = SystemInfo.GetCpuTemperature();
                var ram = SystemInfo.GetRamInfo();
                float disk = SystemInfo.GetDiskUsagePercent("C");

                PCInfo info = new PCInfo
                {
                    PCName = _pcName,
                    CpuUsage = cpu,
                    CpuTemperature = temp,
                    RamUsagePercent = ram.percentUsed,
                    TotalRamMB = ram.totalMB,
                    UsedRamMB = ram.usedMB,
                    DiskUsagePercent = disk,
                    IsOnline = true,
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };

                // 🔥 Este sí sobreescribe LastUpdate correctamente
                await _firebase.SetMachineAsync(_pcName, info);
            }
            catch
            {
                // silencioso
            }
        }
    }
}
