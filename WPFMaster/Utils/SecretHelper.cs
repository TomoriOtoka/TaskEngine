using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskEngine.Models;

namespace TaskEngine.Utils
{
    public static class SecretHelper
    {
        // Ruta al lado del EXE
        public static string FilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "master_config.json");

        // ------------------------------
        // Generar salt
        // ------------------------------
        public static string GenerateSalt()
        {
            byte[] saltBytes = new byte[16];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(saltBytes);
            return Convert.ToBase64String(saltBytes);
        }

        // ------------------------------
        // Hash de la contraseña
        // ------------------------------
        public static string HashPassword(string password, string saltBase64)
        {
            if (password == null || saltBase64 == null)
                return null;

            byte[] saltBytes = Convert.FromBase64String(saltBase64);
            byte[] passBytes = Encoding.UTF8.GetBytes(password);

            byte[] combined = new byte[saltBytes.Length + passBytes.Length];
            Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
            Buffer.BlockCopy(passBytes, 0, combined, saltBytes.Length, passBytes.Length);

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(combined);
            return Convert.ToBase64String(hash);
        }

        // ------------------------------
        // Guardar config
        // ------------------------------
        public static void SaveConfig(MasterConfig cfg)
        {
            string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        // ------------------------------
        // Cargar config
        // ------------------------------
        public static MasterConfig LoadConfig()
        {
            if (!File.Exists(FilePath))
                return null;

            try
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<MasterConfig>(json);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------
        // Verificar contraseña
        // ------------------------------
        public static bool VerifyPassword(string inputPassword)
        {
            var cfg = LoadConfig();
            if (cfg == null)
                return false;

            string hash = HashPassword(inputPassword, cfg.Salt);
            return hash == cfg.PasswordHash;
        }
    }
}
