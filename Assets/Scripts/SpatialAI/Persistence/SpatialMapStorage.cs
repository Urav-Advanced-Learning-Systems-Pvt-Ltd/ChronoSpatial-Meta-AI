using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public static class SpatialMapStorage
    {
        static string DirectoryPath => Path.Combine(Application.persistentDataPath, "SpatialAI", "Maps");
        public sealed class Entry
        {
            public string id, name, destination, failure, createdAt;
            public string Label => string.IsNullOrEmpty(destination) ? name + " — Map only" : name + " — Map & Path to " + destination;
            public string ChoiceLabel => Label+"\n"+(DateTime.TryParse(createdAt,System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,out var time)?"Created "+time.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss")
                :"Copy "+(id==null?"":id.Substring(Math.Max(0,id.Length-6))));
        }
        [Serializable] sealed class CatalogEnvelope { public string schema; public int version; public CatalogMap map; }
        [Serializable] sealed class CatalogMap { public string mapId, displayName, createdAt; public int version; public List<CatalogRoute> routes; }
        [Serializable] sealed class CatalogRoute { public int version; public string destination; }
        public static string DisplayName(SpatialMapData map) => string.IsNullOrWhiteSpace(map.displayName)
            ? map.mapId == "local-map" ? "Previously saved map" : map.mapId : map.displayName;
        public static List<Entry> ListMaps()
        {
            var entries = new List<Entry>();
            if (!Directory.Exists(DirectoryPath)) return entries;
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!ValidId(id)) continue;
                var entry = new Entry { id=id, name=id };
                try
                {
                    // Listing reads only metadata. Full geometry is validated when
                    // selected or searched, avoiding four full map validations just
                    // to open a dialog on an older phone.
                    if(new FileInfo(path).Length>SpatialMapSerializer.MaximumCharacters) throw new InvalidDataException("Map exceeds resource limits");
                    var envelope=JsonUtility.FromJson<CatalogEnvelope>(File.ReadAllText(path));var map=envelope?.map;
                    if(map==null || map.mapId!=id || map.version!=SpatialMapSerializer.CurrentVersion
                        || envelope.version!=SpatialMapSerializer.CurrentVersion || envelope.schema!="SpatialAI-native-pixels-ydown")
                        throw new InvalidDataException("Unsupported map or mismatched identity");
                    if(!string.IsNullOrEmpty(map.displayName) && !SpatialNavigationRoute.ValidName(map.displayName)) throw new InvalidDataException("Invalid map name");
                    entry.name=string.IsNullOrWhiteSpace(map.displayName)?id=="local-map"?"Previously saved map":id:map.displayName;
                    entry.createdAt=map.createdAt;
                    if(map.routes!=null && map.routes.Count>0)
                    {
                        if(map.routes.Count!=1 || map.routes[0]==null || map.routes[0].version!=1 || !SpatialNavigationRoute.ValidName(map.routes[0].destination))
                            throw new InvalidDataException("Invalid path metadata");
                        entry.destination=map.routes[0].destination;
                    }
                }
                catch (Exception e) { entry.failure=e.Message; }
                entries.Add(entry);
            }
            entries.Sort((a,b)=>{int name=string.Compare(a.name,b.name,StringComparison.CurrentCultureIgnoreCase);return name!=0?name:string.CompareOrdinal(b.createdAt,a.createdAt);});
            return entries;
        }
        public static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 80) return false;
            foreach (var character in id) if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z')
                && !(character >= '0' && character <= '9') && character != '-' && character != '_') return false;
            return true;
        }
        static string PathFor(string id)
        {
            if (!ValidId(id)) throw new ArgumentException("Invalid map ID");
            return Path.Combine(DirectoryPath, id + ".json");
        }
        public static void Save(SpatialMapData map)
        {
            var json = SpatialMapSerializer.Serialize(map);
            var path = PathFor(map.mapId);
            Directory.CreateDirectory(DirectoryPath);
            // Preserve incompatible originals even after later Save operations rotate .backup.
            if (File.Exists(path) && (new FileInfo(path).Length > SpatialMapSerializer.MaximumCharacters
                || SpatialMapSerializer.Deserialize(File.ReadAllText(path)) == null)) PreserveOriginal(path);
            var temporary = path + ".pending";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) { writer.Write(json); writer.Flush(); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".backup");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static string PreserveOriginal(string path)
        {
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path)) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            var preserved = path + ".incompatible-" + hash + ".preserved";
            if (!File.Exists(preserved)) File.Copy(path, preserved, false);
            return preserved;
        }
        public static SpatialMapData Load(string id) => Load(id, out _);
        public static SpatialMapData Load(string id, out string reason)
        {
            var path = PathFor(id);
            reason = "Map is missing or over resource limits";
            if (!File.Exists(path) || new FileInfo(path).Length > SpatialMapSerializer.MaximumCharacters) return null;
            var loaded = SpatialMapSerializer.Deserialize(File.ReadAllText(path), out reason);
            if (loaded != null && loaded.mapId != id) { reason="Map identity does not match its file"; return null; }
            return loaded;
        }
        public static bool Delete(string id)
        {
            var path = PathFor(id); if (!File.Exists(path)) return false;
            // Remove only this validated ID's sidecars; leave primary last so a
            // cleanup failure cannot report failure with the catalog entry gone.
            foreach(var sidecar in Directory.EnumerateFiles(DirectoryPath,id+".json.incompatible-*.preserved"))File.Delete(sidecar);
            if(File.Exists(path+".backup"))File.Delete(path+".backup");
            if(File.Exists(path+".pending"))File.Delete(path+".pending");
            File.Delete(path); return true;
        }
        public static bool DeletePath(string id)
        {
            var map=Load(id,out var reason);
            if(map==null)throw new InvalidDataException(reason??"Saved map is unavailable");
            if(map.route==null)return false;
            map.route=null;map.transientRevision++;Save(map);return true;
        }
    }
}
