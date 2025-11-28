using System;
using System.Threading.Tasks;
using WPFMaster.Models;
using WPFMaster.Utils;

namespace WPFMaster.Services
{
    public class ClientService
    {
        private readonly FirebaseService firebase;
        private readonly string pcName;

        public ClientService()
        {
            firebase = new FirebaseService();
            pcName = Environment.MachineName;
        }

        public async Task SendStatusAsync()
        {
            float cpu = SystemInfo.GetCpuUsage();
            var ram = SystemInfo.GetRamInfo();

            PCInfo info = new PCInfo
            {
                PCName = pcName,
                CpuUsage = cpu,
                TotalRam = ram.total,
                UsedRam = ram.used,
                FreeRam = ram.free,
                LastUpdate = DateTime.Now.ToString("HH:mm:ss")
            };

            await firebase.UpdateMachineAsync(pcName, info);
        }
    }
}
    