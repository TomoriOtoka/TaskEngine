using FireSharp.Interfaces;
using FireSharp.Response;
using Newtonsoft.Json;
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
        // Guardar información actual de la PC (sobrescribe current)
        // =============================================================
        public async Task SetMachineAsync(string pcName, PCInfo pc)
        {
            await client.SetAsync($"machines/{pcName}/current", pc);
        }

        // =============================================================
        // Obtener información actual de la PC
        // =============================================================
        public async Task<PCInfo> GetMachineAsync(string pcName)
        {
            var response = await client.GetAsync($"machines/{pcName}/current");
            if (response?.Body == "null") return null;
            return JsonConvert.DeserializeObject<PCInfo>(response.Body);
        }

        // =============================================================
        // Agregar punto al historial (no sobrescribir)
        // =============================================================
        public async Task AddHistoryPointAsync(string pcName, PCInfo info)
        {
            await client.PushAsync($"machines/{pcName}/history", info);
        }

        // =============================================================
        // Obtener historial de una PC
        // =============================================================
        public async Task<List<PCInfo>> GetMachineHistoryAsync(string pcName, int days = 14)
        {
            var response = await client.GetAsync($"machines/{pcName}/history");
            if (response?.Body == "null") return new List<PCInfo>();

            var dict = JsonConvert.DeserializeObject<Dictionary<string, PCInfo>>(response.Body);
            if (dict == null) return new List<PCInfo>();

            DateTime cutoff = DateTime.UtcNow.AddDays(-days);
            var list = dict.Values
                           .Where(pc => DateTime.TryParse(pc.LastUpdate, out var dt) && dt >= cutoff)
                           .OrderBy(pc => DateTime.Parse(pc.LastUpdate))
                           .ToList();

            return list;
        }

        // =============================================================
        // Obtener todas las PCs actuales
        // =============================================================
        public async Task<Dictionary<string, PCInfo>> GetAllMachinesAsync()
        {
            var response = await client.GetAsync("machines");
            if (response?.Body == "null") return new Dictionary<string, PCInfo>();

            var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(response.Body);
            var result = new Dictionary<string, PCInfo>();

            foreach (var kv in dict)
            {
                try
                {
                    var objDict = kv.Value as Newtonsoft.Json.Linq.JObject;
                    if (objDict != null && objDict["current"] != null)
                    {
                        var pc = objDict["current"].ToObject<PCInfo>();
                        result[kv.Key] = pc;
                    }
                }
                catch { }
            }

            return result;
        }

        // =============================================================
        // Enviar comando al cliente
        // =============================================================
        public async Task SendClientCommandAsync(string pcName, string command)
        {
            if (string.IsNullOrEmpty(command))
                await client.DeleteAsync($"commands/{pcName}");
            else
                await client.SetAsync($"commands/{pcName}", command);
        }


        // =============================================================
        // Obtener comando pendiente para el cliente
        // =============================================================
        public async Task<string> GetClientCommandAsync(string pcName)
        {
            var response = await client.GetAsync($"commands/{pcName}");
            if (response?.Body == "null") return null;
            return JsonConvert.DeserializeObject<string>(response.Body);
        }

        public async Task CleanOldHistoryAsync(string pcName, TimeSpan retention)
        {
            FirebaseResponse response = await client.GetAsync($"machines/{pcName}/history");
            if (response?.Body == "null") return;

            var historyDict = response.ResultAs<Dictionary<string, PCInfo>>();
            if (historyDict == null || historyDict.Count == 0) return;

            long cutoffTimestamp = DateTimeOffset.UtcNow.Add(-retention).ToUnixTimeSeconds();
            var keysToDelete = new List<string>();

            foreach (var (key, value) in historyDict)
            {
                if (value.Timestamp < cutoffTimestamp)
                    keysToDelete.Add(key);
            }

            foreach (var key in keysToDelete)
                await client.DeleteAsync($"machines/{pcName}/history/{key}");
        }



        // =============================================================
        // Limpiar comando
        // =============================================================
        public async Task ClearClientCommandAsync(string pcName)
        {
            await client.DeleteAsync($"commands/{pcName}");
        }
    }
}
