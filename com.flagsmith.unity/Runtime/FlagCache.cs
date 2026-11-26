using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Flagsmith
{
    /// <summary>Keeps the last good server response so a game that starts offline still gets its tuning.</summary>
    public interface IFlagCache
    {
        void Write(string key, string json);
        string Read(string key);
    }

    public sealed class MemoryFlagCache : IFlagCache
    {
        readonly Dictionary<string, string> _items = new Dictionary<string, string>();
        public void Write(string key, string json) => _items[key] = json;
        public string Read(string key) => _items.TryGetValue(key, out var json) ? json : null;
    }

    public sealed class FileFlagCache : IFlagCache
    {
        readonly string _dir = Path.Combine(Application.persistentDataPath, "flagsmith");

        public void Write(string key, string json)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(PathFor(key), json);
            }
            catch (Exception e) { Debug.LogWarning($"[Flagsmith] Could not cache flags: {e.Message}"); }
        }

        public string Read(string key)
        {
            try { return File.Exists(PathFor(key)) ? File.ReadAllText(PathFor(key)) : null; }
            catch (Exception) { return null; }
        }

        // Identifiers can hold any characters, so the file name is a hash of the key.
        string PathFor(string key)
        {
            using (var sha = SHA1.Create())
                return Path.Combine(_dir, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".json");
        }
    }
}
