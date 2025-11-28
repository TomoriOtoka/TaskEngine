using System;
using System.Diagnostics;
using System.Management;

namespace WPFMaster.Utils
{
    public static class SystemInfo
    {
        public static float GetCpuUsage()
        {
            PerformanceCounter cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpu.NextValue();
            System.Threading.Thread.Sleep(500);
            return cpu.NextValue();
        }

        public static (float total, float used, float free) GetRamInfo()
        {
            float total = 0;
            float free = 0;

            ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            foreach (var o in searcher.Get())
            {
                total = Convert.ToSingle(o["TotalVisibleMemorySize"]) / 1024;
                free = Convert.ToSingle(o["FreePhysicalMemory"]) / 1024;
            }

            float used = total - free;

            return (total, used, free);
        }
    }
}
