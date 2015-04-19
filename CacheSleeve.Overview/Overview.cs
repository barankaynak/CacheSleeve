using RazorEngine;
using System.IO;
using System.Reflection;

namespace CacheSleeve.Overview
{
    public class Overview
    {
        public static string Generate(ICacheManager cacheManager)
        {
            const string resourceName = "CacheSleeve.Overview.Razor.Overview.cshtml";
            var model = new Models.Overview()
            {
                RemoteKeys = cacheManager.RemoteCacher.GetAllKeys(),
                LocalKeys = cacheManager.LocalCacher.GetAllKeys()
            };
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return "";
                using (var reader = new StreamReader(stream))
                    return Razor.Parse(reader.ReadToEnd(), model);
            }
        }
    }
}