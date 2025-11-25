using FireSharp.Config;
using FireSharp.Interfaces;

namespace WPFMaster.Services
{
    public static class FirebaseConfigData
    {
        // REEMPLAZA por los datos reales de tu Realtime DB
        // Si usas FireSharp.Core u otra variante, el tipo puede variar.
        public static IFirebaseConfig Config = new FirebaseConfig
        {
            AuthSecret = "CIOwjGpGr8sjCLwFkqr6mMTTXvRhZ7hBTHQkWeEr", // si tu DB no usa secret, deja vacío (pero FireSharp lo requiere)
            BasePath = "https://TU-PROYECTO-default-rtdb.firebaseio.com/"
        };
    }
}
