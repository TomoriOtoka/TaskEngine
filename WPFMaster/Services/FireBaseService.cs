using FireSharp.Interfaces;
using FireSharp.Response;
using FireSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WPFMaster.Models;

namespace WPFMaster.Services
{
    public class FirebaseService
    {
        private readonly IFirebaseClient client;

        public FirebaseService()
        {
            client = new FirebaseClient(FirebaseConfigData.Config);
        }

        public bool IsConnected => client != null;

        public async Task<bool> RegisterMachineAsync(string name)
        {
            try
            {
                var empty = new PCStatus
                {
                    PCName = name,
                    Role = "client",
                    CpuUsage = 0,
                    TotalRam = 0,
                    UsedRam = 0,
                    FreeRam = 0,
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };
                await client.SetAsync($"machines/{name}", empty);
                return true;
            }
            catch { return false; }
        }

        public async Task UpdateMachineAsync(string name, PCStatus status)
        {
            try
            {
                await client.SetAsync($"machines/{name}", status);
            }
            catch { /* ignore for now */ }
        }

        public async Task<Dictionary<string, PCStatus>> GetAllMachinesAsync()
        {
            try
            {
                FirebaseResponse res = await client.GetAsync("machines");
                if (res.Body == "null" || string.IsNullOrEmpty(res.Body)) return new Dictionary<string, PCStatus>();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, PCStatus>>(res.Body);
                return dict ?? new Dictionary<string, PCStatus>();
            }
            catch { return new Dictionary<string, PCStatus>(); }
        }

        // Optional: subscribe (FireSharp OnAsync)
        public void OnMachinesChanged(Action<Dictionary<string, PCStatus>> onChanged)
        {
            client.OnAsync("machines", (sender, args, context) =>
            {
                if (args.Data == "null") { onChanged(new Dictionary<string, PCStatus>()); return; }
                var dict = JsonConvert.DeserializeObject<Dictionary<string, PCStatus>>(args.Data);
                onChanged(dict ?? new Dictionary<string, PCStatus>());
            });
        }
    }
}
