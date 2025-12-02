using FireSharp.Interfaces;
using FireSharp.Response;
using System;
using System.Threading.Tasks;

namespace TaskEngine.Services
{
    public class MasterService
    {
        private readonly IFirebaseClient client;

        public MasterService()
        {
            client = new FireSharp.FirebaseClient(FirebaseConfigData.Config);
        }

        public async Task<bool> IsThisMachineMasterAsync()
        {
            string pc = Environment.MachineName;

            FirebaseResponse res = await client.GetAsync("masters/" + pc);

            if (res.Body == "null" || res.Body == null)
                return false;

            bool val = res.ResultAs<bool>();
            return val;
        }
    }
}
