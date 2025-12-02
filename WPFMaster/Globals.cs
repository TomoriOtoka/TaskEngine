using System;
using System.IO;

namespace TaskEngine
{
    public static class Globals
    {
        public static string ConfigFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskEngine");

        public static string MasterConfigFile =
            Path.Combine(ConfigFolder, "master_config.json");

        static Globals()
        {
            // Crear carpeta si no existe
            if (!Directory.Exists(ConfigFolder))
                Directory.CreateDirectory(ConfigFolder);
        }
    }
}
