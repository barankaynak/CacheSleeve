﻿using StackExchange.Redis;
using System.Collections.Generic;

namespace CacheSleeve
{
    public interface ICacheManager
    {
        string AddPrefix(string key);

        bool Debug { get; set; }
        
        IEnumerable<RedisKey> GetAllKeys(string pattern = null);

        IDatabase GetDatebase();

        string KeyPrefix { get; }

        HttpContextCacher LocalCacher { get; }

        int RedisDb { get; }

        ConfigurationOptions RedisConfiguration { get; }

        RedisCacher RemoteCacher { get; }
    }
}