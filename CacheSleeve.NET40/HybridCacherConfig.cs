namespace CacheSleeve
{
    public class HybridCacherConfig : IHybridCacherConfig
    {
        public HybridCacherConfig()
        {
            KeyPrefix = "cs.";
        }

        public string KeyPrefix { get; set; }
    }
}