using FireSharp.Config;
using FireSharp.Interfaces;

namespace TaskEngine.Services
{
    public static class FirebaseConfigData
    {
        public static IFirebaseConfig Config = new FirebaseConfig
        {
            BasePath = "https://miproyecto-ce57d-default-rtdb.firebaseio.com/",
            AuthSecret = "CIOwjGpGr8sjCLwFkqr6mMTTXvRhZ7hBTHQkWeEr"
        };
    }
}
