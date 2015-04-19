using System;
using System.Collections.Generic;
using System.Linq;
using CacheSleeve.Models;
using StackExchange.Redis;

namespace CacheSleeve
{
    public partial class RedisCacher : ICacher
    {
        private ConnectionMultiplexer _redisConnection;
        private int _redisDb;
        private readonly IObjectSerializer _objectSerializer;
        private readonly ICacheLogger _logger;

        public RedisCacher(
            IRedisConnection redisConnection,
            IObjectSerializer serializer,
            ICacheLogger logger)
        {
            _redisConnection = redisConnection.Connection;
            _redisDb = redisConnection.RedisDb;
            _objectSerializer = serializer;
            _logger = logger;
        }


        public T Get<T>(string key)
        {
            var conn = _redisConnection.GetDatabase(_redisDb);
            if (typeof(T) == typeof(string) || typeof(T) == typeof(byte[]))
                return (T)(dynamic)conn.StringGet(key);
            string result;
            try
            {
                result = conn.StringGet(key);
            }
            catch (Exception)
            {
                return default(T);
            }
            if (result != null)
                return _objectSerializer.DeserializeObject<T>(result);
            return default(T);
        }

        public bool Set<T>(string key, T value, string parentKey = null)
        {
            if (InternalSet(key, value))
            {
                RemoveDependencies(key);
                SetDependencies(key, parentKey);
            }
            return true;
        }

        public bool Set<T>(string key, T value, DateTime expiresAt, string parentKey = null)
        {
            return Set(key, value, expiresAt - DateTime.Now, parentKey);
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn, string parentKey = null)
        {
            var result = InternalSet(key, value);
            if (result)
            {
                var conn = _redisConnection.GetDatabase(_redisDb);
                result = conn.KeyExpire(key, expiresIn);
                RemoveDependencies(key);
                SetDependencies(key, parentKey);
            }
            return result;
        }

        public bool Remove(string key)
        {
            var conn = _redisConnection.GetDatabase(_redisDb);
            if (conn.KeyDelete(key))
            {
                RemoveDependencies(key);
                conn.KeyDelete(key + ".parent");

                if (_logger.DebugEnabled)
                    _logger.Debug(String.Format("CS Redis: Removed cache item with key {0}", key));

                return true;
            }
            return false;
        }

        public void FlushAll()
        {
            foreach (var endpoint in _redisConnection.GetEndPoints())
            {
                var server = _redisConnection.GetServer(endpoint);
                server.FlushDatabase(_redisDb);
            }
        }

        public IEnumerable<Key> GetAllKeys()
        {
            var conn = _redisConnection.GetDatabase(_redisDb);
            var keys = new List<RedisKey>();
            foreach (var endpoint in _redisConnection.GetEndPoints())
            {
                var server = _redisConnection.GetServer(endpoint);
                if (!server.IsSlave)
                    keys.AddRange(server.Keys(_redisDb, "*"));
            }
            var listOfKeys = new List<Key>(keys.Count);
            foreach (var keyString in keys)
            {
                var ttl = conn.KeyTimeToLive(keyString);
                var expiration = default(DateTime?);
                if (ttl != null)
                    expiration = DateTime.Now.AddSeconds(ttl.Value.TotalSeconds);
                listOfKeys.Add(new Key(keyString, expiration));
            }
            return listOfKeys;
        }

        /// <summary>
        /// Gets the amount of time left before the item expires.
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <returns>The amount of time in seconds.</returns>
        public long TimeToLive(string key)
        {
            var conn = _redisConnection.GetDatabase(_redisDb);
            var ttl = conn.KeyTimeToLive(key);
            if (ttl == null)
                return -1;
            return (long)ttl.Value.TotalSeconds;
        }

        /// <summary>
        /// Publishes a message with a specified key.
        /// Any clients connected to the Redis server and subscribed to the key will recieve the message.
        /// </summary>
        /// <param name="channel">The channel that other clients subscribe to.</param>
        /// <param name="message">The message to send to subscribed clients.</param>
        public void PublishToChannel(string channel, string message)
        {
            var subscriber = _redisConnection.GetSubscriber();
            subscriber.Publish(channel, message, CommandFlags.FireAndForget);
        }

        /// <summary>
        /// Subscribes client to a channel.
        /// Client will recieve any message published to channel.
        /// </summary>
        /// <param name="channel">The channel to subscribe. You can subscribe to multiple channels using wildcard(*)</param>
        /// <param name="handler">Handler that will process received messages.</param>
        public void SubscribeToChannel(string channel, Action<string, string> handler)
        {
            var subscriber = _redisConnection.GetSubscriber();
            subscriber.Subscribe(channel, (ch, v) => handler(ch, v));
        }


        /// <summary>
        /// Shared insert for public wrappers.
        /// </summary>
        /// <typeparam name="T">The type of the item to insert.</typeparam>
        /// <param name="key">The key of the item to insert.</param>
        /// <param name="value">The value of the item to insert.</param>
        /// <returns></returns>
        private bool InternalSet<T>(string key, T value)
        {
            var conn = _redisConnection.GetDatabase(_redisDb);
            try
            {
                if (typeof(T) == typeof(byte[]))
                {
                    conn.StringSet(key, value as byte[]);
                }
                else if (typeof(T) == typeof(string))
                {
                    conn.StringSet(key, value as string);
                }
                else
                {
                    var serializedValue = _objectSerializer.SerializeObject<T>(value);
                    conn.StringSet(key, serializedValue);
                }

                if (_logger.DebugEnabled)
                    _logger.Debug(String.Format("CS Redis: Set cache item with key {0}", key));
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Adds a child key as a dependency of a parent key.
        /// When the parent is invalidated by remove, overwrite, or expiration the child will be removed.
        /// </summary>
        /// <param name="childKey">The key of the child item.</param>
        /// <param name="parentKey">The key of the parent item.</param>
        private void SetDependencies(string childKey, string parentKey)
        {
            if (String.IsNullOrEmpty(childKey) || String.IsNullOrEmpty(parentKey))
                return;

            var conn = _redisConnection.GetDatabase(_redisDb);
            var parentDepKey = parentKey + ".children";
            var childDepKey = childKey + ".parent";

            conn.ListRightPush(parentDepKey, childKey);
            conn.StringSet(childDepKey, parentKey);
            var ttl = conn.KeyTimeToLive(parentKey);
            if (ttl != null && ttl.Value.TotalSeconds > -1)
            {
                var children = conn.ListRange(parentDepKey, 0, -1).ToList();
                conn.KeyExpire(parentDepKey, ttl);
                conn.KeyExpire(childDepKey, ttl);
                foreach (var child in children)
                    conn.KeyExpire(child.ToString(), ttl);
            }
        }

        /// <summary>
        /// Removes all of the dependencies of the key from the cache.
        /// </summary>
        /// <param name="key">The key of the item to remove children for.</param>
        private void RemoveDependencies(string key)
        {
            if (String.IsNullOrEmpty(key))
                return;

            var conn = _redisConnection.GetDatabase(_redisDb);
            var depKey = key + ".children";
            var children = conn.ListRange(depKey, 0, -1).ToList();
            if (children.Count > 0)
            {
                var keys = new List<RedisKey>(children.Count * 2 + 1);
                keys.Add(depKey);
                foreach (var child in children)
                {
                    keys.Add(child.ToString());
                    keys.Add(child + ".parent");
                }
                conn.KeyDelete(keys.ToArray());
            }

        }
    }
}