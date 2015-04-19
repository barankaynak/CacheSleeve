using StackExchange.Redis;

namespace CacheSleeve
{
    public interface IRedisConnection
    {
        ConnectionMultiplexer Connection { get; }
        int RedisDb { get; }
    }
}