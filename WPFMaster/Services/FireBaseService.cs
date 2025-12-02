using FireSharp.Interfaces;
using FireSharp.Response;
using System.Collections.Generic;
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

        public async Task UpdateMachineAsync(string name, PCInfo info)
        {
            await client.SetAsync("machines/" + name, info);
        }
        public async Task SetMachineAsync(string name, PCInfo pc)
        {
            await client.SetAsync("machines/" + name, pc);
        }

        public async Task<Dictionary<string, PCInfo>> GetAllMachinesAsync()
        {
            FirebaseResponse response = await client.GetAsync("machines");
            return response.ResultAs<Dictionary<string, PCInfo>>() ?? new Dictionary<string, PCInfo>();
        }
    }
}
