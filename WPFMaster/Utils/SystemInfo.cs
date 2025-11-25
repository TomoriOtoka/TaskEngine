using System;
using System.Diagnostics;
using System.Threading;
using System.Management;

namespace WPFMaster.Utils
{
    public static class SystemInfo
    {
        public static string MachineName => Environment.MachineName;

        public static float GetCpuUsage()
        {
            PerformanceCounter cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpu.NextValue();
            Thread.Sleep(500);
            return cpu.NextValue();
        }

        public static (float total, float used, float free) GetRamInfo()
        {
            float total = 0;
            float free = 0;

            var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                total = Convert.ToSingle(obj["TotalVisibleMemorySize"]) / 1024; // MB
                free = Convert.ToSingle(obj["FreePhysicalMemory"]) / 1024;     // MB
            }

            float used = total - free;

            return (total, used, free);
        }
    }
}
