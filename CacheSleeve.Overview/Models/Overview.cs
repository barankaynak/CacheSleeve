using System.Collections.Generic;
using CacheSleeve.Models;

namespace CacheSleeve.Overview.Models
{
    public class Overview
    {
        public IEnumerable<Key> RemoteKeys { get; set; }
        public IEnumerable<Key> LocalKeys { get; set; }
    }
}