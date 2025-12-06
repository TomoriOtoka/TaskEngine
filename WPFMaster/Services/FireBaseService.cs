using FireSharp.Interfaces;
using FireSharp.Response;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaskEngine.Models;

namespace TaskEngine.Services
{
    public class FirebaseService
    {
        private readonly IFirebaseClient client;

        public FirebaseService()
        {
            client = new FireSharp.FirebaseClient(FirebaseConfigData.Config);
        }

        // =============================================================
        // GUARDAR INFORMACIÓN DE PC
        // =============================================================
        public async Task SetMachineAsync(string name, PCInfo pc)
        {
            await client.SetAsync("machines/" + name, pc);
        }

        // =============================================================
        // ELIMINAR UN NODO DE FIREBASE
        // =============================================================
        public async Task DeleteAtPathAsync(string path)
        {
            try
            {
                await client.DeleteAsync(path);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error eliminando {path}: {ex.Message}", ex);
            }
        }


        public async Task UpdateMachineAsync(string name, PCInfo info)
        {
            await client.SetAsync("machines/" + name, info);
        }

        /// <summary>
        /// Guarda un objeto PCInfo en una ruta arbitraria (por ejemplo "history/PCNAME/key").
        /// </summary>
        public async Task SetMachineAtPathAsync(string path, PCInfo info)
        {
            try
            {
                await client.SetAsync(path, info);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al guardar en {path}: {ex.Message}", ex);
            }
        }

        // =============================================================
        // OBTENER INFORMACIÓN DE UNA PC (CONSULTA DIRECTA A SU NODO)
        // =============================================================
        public async Task<PCInfo> GetMachineAsync(string pcName)
        {
            try
            {
                FirebaseResponse response = await client.GetAsync("machines/" + pcName);

                if (response == null || response.Body == "null")
                    return null;

                return response.ResultAs<PCInfo>();
            }
            catch
            {
                return null;
            }
        }

        // =============================================================
        // OBTENER TODAS LAS PCS
        // =============================================================
        public async Task<Dictionary<string, PCInfo>> GetAllMachinesAsync()
        {
            FirebaseResponse response = await client.GetAsync("machines");
            return response.ResultAs<Dictionary<string, PCInfo>>() ?? new Dictionary<string, PCInfo>();
        }

        // =============================================================
        // COMANDOS PARA EL CLIENTE
        // =============================================================
        public async Task SendClientCommandAsync(string pcName, string command)
        {
            if (command == null)
            {
                await client.DeleteAsync("commands/" + pcName);
                return;
            }

            await client.SetAsync("commands/" + pcName, command);
        }

        public async Task<string> GetClientCommandAsync(string pcName)
        {
            FirebaseResponse response = await client.GetAsync("commands/" + pcName);

            if (response == null || response.Body == "null")
                return null;

            try
            {
                return response.ResultAs<string>();
            }
            catch
            {
                return null;
            }
        }

        public async Task ClearClientCommandAsync(string pcName)
        {
            await client.DeleteAsync("commands/" + pcName);
        }

        // =============================================================
        // HISTORIAL DE PC
        // =============================================================
        public async Task AddHistoryPointAsync(string pcName, PCInfo info)
        {
            // Usamos LastUpdateTime como referencia
            string key = info.LastUpdateTime.ToString("yyyy-MM-dd-HH-mm"); // Guardamos con minuto
            await client.SetAsync($"machines/{pcName}/history/{key}", info);
        }

        public async Task<List<PCInfo>> GetMachineHistoryAsync(string pcName)
        {
            FirebaseResponse response = await client.GetAsync($"machines/{pcName}/history");

            if (response == null || response.Body == "null")
                return new List<PCInfo>();

            var dict = response.ResultAs<Dictionary<string, PCInfo>>();
            if (dict == null || dict.Count == 0)
                return new List<PCInfo>();

            DateTime minDate = DateTime.UtcNow.AddDays(-14);
            var list = new List<PCInfo>();

            foreach (var kv in dict)
            {
                // clave: yyyy-MM-dd-HH-mm
                if (DateTime.TryParseExact(kv.Key, "yyyy-MM-dd-HH-mm", null, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime timestamp))
                {
                    if (timestamp >= minDate)
                    {
                        kv.Value.LastUpdateTime = timestamp;
                        list.Add(kv.Value);
                    }
                }
            }

            return list.OrderBy(x => x.LastUpdateTime).ToList();
        }
    }
}
