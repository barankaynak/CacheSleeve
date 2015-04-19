using System;
using System.Collections.Generic;
using System.Linq;
using CacheSleeve.Models;

namespace CacheSleeve
{
    public partial class HybridCacher : ICacher
    {
        private readonly RedisCacher _remoteCacher;
        private readonly HttpContextCacher _localCacher;

        public RedisCacher RemoteCacher { get { return _remoteCacher; } }
        public HttpContextCacher LocalCacher { get { return _localCacher; } }
        public string KeyPrefix { get; private set; }

        public HybridCacher(
            RedisCacher redisCacher,
            HttpContextCacher httpContextCacher
            )
        {
            _remoteCacher = redisCacher;
            _localCacher = httpContextCacher;

            _remoteCacher.SubscribeToChannel("cacheSleeve.remove", (redisChannel, value) => _localCacher.Remove(value));
            _remoteCacher.SubscribeToChannel("cacheSleeve.flush", (redisChannel, value) => _localCacher.FlushAll());
        }


        /// <summary>
        /// Adds the prefix to the key.
        /// </summary>
        /// <param name="key">The specified key value.</param>
        /// <returns>The specified key with the prefix attached.</returns>
        public string AddPrefix(string key)
        {
            // much faster than string format, we assume here key will never be null
            if (!string.IsNullOrEmpty(KeyPrefix))
                return KeyPrefix + key;
            return key;
        }

        public T Get<T>(string key)
        {
            var cacheKey = AddPrefix(key);
            var result = _localCacher.Get<T>(cacheKey);
            if (result != null)
                return result;
            result = _remoteCacher.Get<T>(cacheKey);
            if (result != null)
            {
                var ttl = _remoteCacher.TimeToLive(cacheKey);
                var parentKey = _remoteCacher.Get<string>(cacheKey + ".parent");
                if (ttl > -1)
                    _localCacher.Set(cacheKey, result, TimeSpan.FromSeconds(ttl), parentKey);
                else
                    _localCacher.Set(cacheKey, result, parentKey);
                result = _localCacher.Get<T>(cacheKey);
            }
            return result;
        }

        public T GetOrSet<T>(string key, Func<string, T> valueFactory, DateTime expiresAt, string parentKey = null)
        {
            var value = Get<T>(key);
            if (value == null)
            {
                value = valueFactory(key);
                if (value != null && !value.Equals(default(T)))
                    Set(key, value, expiresAt, parentKey);
            }
            return value;
        }

        public bool Set<T>(string key, T value, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            try
            {
                _remoteCacher.Set(cacheKey, value, AddPrefix(parentKey));
            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey);
                return false;
            }
            _remoteCacher.PublishToChannel("cacheSleeve.remove", cacheKey);
            return true;
        }

        public bool Set<T>(string key, T value, DateTime expiresAt, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            try
            {
                _remoteCacher.Set(cacheKey, value, expiresAt, AddPrefix(parentKey));

            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey);
                return false;
            }
            _remoteCacher.PublishToChannel("cacheSleeve.remove", cacheKey);
            return true;
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            try
            {
                _remoteCacher.Set(cacheKey, value, expiresIn, AddPrefix(parentKey));

            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey);
                return false;
            }
            _remoteCacher.PublishToChannel("cacheSleeve.remove", cacheKey);
            return true;
        }

        public bool Remove(string key)
        {
            var cacheKey = AddPrefix(key);
            try
            {
                _remoteCacher.Remove(cacheKey);
                _remoteCacher.PublishToChannel("cacheSleeve.remove", cacheKey);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void FlushAll()
        {
            _remoteCacher.FlushAll();
            _remoteCacher.PublishToChannel("cacheSleeve.flush", "");
        }

        public IEnumerable<Key> GetAllKeys()
        {
            var keys = 
                _remoteCacher.GetAllKeys()
                    .Union(_localCacher.GetAllKeys())
                    .Distinct();
            if (!string.IsNullOrEmpty(KeyPrefix))
                keys = keys.Select(k => new Key(k.KeyName.Substring(this.KeyPrefix.Length), k.ExpirationDate));
            return keys;
        }
    }
}