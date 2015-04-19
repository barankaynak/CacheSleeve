﻿using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CacheSleeve.Tests
{
    public class HybridCacherTests : IDisposable
    {
        private HybridCacher _hybridCacher;
        private RedisCacher _remoteCacher;
        private HttpContextCacher _localCacher;
        private readonly RedisConnection _redisConnection;

        private delegate void SubscriptionHitHandler(string key, string message);
        private event SubscriptionHitHandler SubscriptionHit;
        private void OnSubscriptionHit(string key, string message)
        {
            if (SubscriptionHit != null)
                SubscriptionHit(key, message);
        }

        public HybridCacherTests()
        {
            // have to fake an http context to use http context cache
            HttpContext.Current = new HttpContext(new HttpRequest(null, "http://tempuri.org", null), new HttpResponse(null));

            _redisConnection = RedisConnection.Create(TestSettings.RedisHost, TestSettings.RedisPort, TestSettings.RedisPassword, TestSettings.RedisDb);

            var subscriber = _redisConnection.Connection.GetSubscriber();
            subscriber.Subscribe("cacheSleeve.remove", (redisChannel, value) => OnSubscriptionHit(redisChannel, value));
            subscriber.Subscribe("cacheSleeve.flush", (redisChannel, value) => OnSubscriptionHit(redisChannel, "flush"));

            var nullLogger = new Mock<ICacheLogger>().Object;

            _remoteCacher = new RedisCacher(_redisConnection, new JsonObjectSerializer(), nullLogger);
            _localCacher = new HttpContextCacher(nullLogger);
            _hybridCacher = new HybridCacher(_remoteCacher, _localCacher);
        }


        public class Basics : HybridCacherTests
        {
            [Fact]
            public void SetCachesRemote()
            {
                _hybridCacher.Set("key", "value");
                var result = _remoteCacher.Get<string>("key");
                Assert.Equal("value", result);
            }

            [Fact]
            public void GetsFromLocalCacheFirst()
            {
                _remoteCacher.Set("key", "value1");
                _localCacher.Set("key", "value2");
                var result = _hybridCacher.Get<string>("key");
                Assert.Equal("value2", result);
            }

            [Fact]
            public void GetsFromRemoteCacheIfNotInLocal()
            {
                _remoteCacher.Set("key", "value1");
                var result = _hybridCacher.Get<string>("key");
                Assert.Equal("value1", result);
            }

            [Fact]
            public void SetsExpirationOfLocalByRemoteTimeToLive()
            {
                _remoteCacher.Set("key", "value1", DateTime.Now.AddSeconds(120));
                var hybridResult = _hybridCacher.Get<string>("key");
                var ttl = _localCacher.TimeToLive("key");
                Assert.InRange(ttl, 118, 122);
            }

            [Fact]
            public void CanGetAllKeys()
            {
                _remoteCacher.Set("key1", "value");
                _localCacher.Set("key2", "value");
                var result = _hybridCacher.GetAllKeys();
                Assert.True(result.Select(k => k.KeyName).Contains(_hybridCacher.AddPrefix("key1")));
                Assert.True(result.Select(k => k.KeyName).Contains(_hybridCacher.AddPrefix("key2")));
            }

            [Fact]
            public void ExpirationTransfersFromRemoteToLocal()
            {
                _remoteCacher.Set("key1", "value", DateTime.Now.AddSeconds(120));
                _hybridCacher.Get<string>("key1");
                var results = _localCacher.GetAllKeys();
                Assert.InRange(results.First().ExpirationDate.Value, DateTime.Now.AddSeconds(118), DateTime.Now.AddSeconds(122));
            }
        }

        public class PubSub : HybridCacherTests
        {
            [Fact]
            public void SetCausesPublishRemove()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                _hybridCacher.Set("key", "value");
                Thread.Sleep(30);
                Assert.Equal("key", lastMessage);
            }

            [Fact]
            public void RemoveCausesPublishRemove()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                _hybridCacher.Remove("key");
                Thread.Sleep(30);
                Assert.Equal("key", lastMessage);
            }

            [Fact]
            public void FlushCausesPublishFlush()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                _hybridCacher.FlushAll();
                Thread.Sleep(30);
                Assert.Equal("flush", lastMessage);
            }
        }

        public class Dependencies : HybridCacherTests
        {
            [Fact]
            public void GetSetsRemoteDependencyOnLocal()
            {
                _hybridCacher.Set("key1", "value1");
                _hybridCacher.Get<string>("key1");
                _hybridCacher.Set("key2", "value2", "key1");
                _hybridCacher.Get<string>("key2");
                var result = _localCacher.Get<string>("key2");
                Assert.Equal("value2", result);
                _localCacher.Remove("key1");
                result = _localCacher.Get<string>("key2");
                Assert.Equal(null, result);
            }
        }

        public void Dispose()
        {
            _hybridCacher.FlushAll();
            _redisConnection.Connection.Dispose();
        }
    }
}