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
    public class HybridCacherAsyncTests : IDisposable
    {
        private readonly HybridCacher _hybridCacher;
        private readonly RedisCacher _remoteCacher;
        private readonly HttpContextCacher _localCacher;
        private readonly RedisConnection _redisConnection;

        private delegate void SubscriptionHitHandler(string key, string message);
        private event SubscriptionHitHandler SubscriptionHit;
        private void OnSubscriptionHit(string key, string message)
        {
            if (SubscriptionHit != null)
                SubscriptionHit(key, message);
        }

        public HybridCacherAsyncTests()
        {
            // have to fake an http context to use http context cache
            HttpContext.Current = new HttpContext(new HttpRequest(null, "http://tempuri.org", null), new HttpResponse(null));

            _redisConnection = RedisConnection.Create(TestSettings.RedisHost, TestSettings.RedisPort, TestSettings.RedisPassword, TestSettings.RedisDb);

            var prefix = "test.";

            var subscriber = _redisConnection.Connection.GetSubscriber();
            subscriber.Subscribe("cacheSleeve.remove." + prefix, (redisChannel, value) => OnSubscriptionHit(redisChannel, value));
            subscriber.Subscribe("cacheSleeve.flush." + prefix, (redisChannel, value) => OnSubscriptionHit(redisChannel, "flush"));

            var nullLogger = new Mock<ICacheLogger>().Object;

            _remoteCacher = new RedisCacher(_redisConnection, new JsonObjectSerializer(), nullLogger);
            _localCacher = new HttpContextCacher(nullLogger);

            var configMock = new Mock<IHybridCacherConfig>();
            configMock.Setup(c => c.KeyPrefix).Returns(prefix);

            _hybridCacher = new HybridCacher(configMock.Object, _remoteCacher, _localCacher);
        }


        public class Basics : HybridCacherAsyncTests
        {
            [Fact]
            public async void SetCachesRemote()
            {
                await _hybridCacher.SetAsync("key", "value");
                var result = await _remoteCacher.GetAsync<string>(_hybridCacher.AddPrefix("key"));
                Assert.Equal("value", result);
            }

            [Fact]
            public async void GetsFromLocalCacheFirst()
            {
                await _remoteCacher.SetAsync(_hybridCacher.AddPrefix("key"), "value1");
                _localCacher.Set(_hybridCacher.AddPrefix("key"), "value2");
                var result = await _hybridCacher.GetAsync<string>("key");
                Assert.Equal("value2", result);
            }

            [Fact]
            public async void GetsFromRemoteCacheIfNotInLocal()
            {
                await _remoteCacher.SetAsync(_hybridCacher.AddPrefix("key"), "value1");
                var result = await _hybridCacher.GetAsync<string>("key");
                Assert.Equal("value1", result);
            }

            [Fact]
            public async void SetsExpirationOfLocalByRemoteTimeToLive()
            {
                await _remoteCacher.SetAsync(_hybridCacher.AddPrefix("key"), "value1", DateTime.Now.AddSeconds(120));
                var hybridResult = await _hybridCacher.GetAsync<string>("key");
                var ttl = _localCacher.TimeToLive(_hybridCacher.AddPrefix("key"));
                Assert.InRange(ttl, 118, 122);
            }

            [Fact]
            public async void CanGetAllKeys()
            {
                await _remoteCacher.SetAsync(_hybridCacher.AddPrefix("key1"), "value");
                _localCacher.Set(_hybridCacher.AddPrefix("key2"), "value");
                var result = await _hybridCacher.GetAllKeysAsync();
                Assert.True(result.Select(k => k.KeyName).Contains("key1"));
                Assert.True(result.Select(k => k.KeyName).Contains("key2"));
            }

            [Fact]
            public async void ExpirationTransfersFromRemoteToLocal()
            {
                await _remoteCacher.SetAsync(_hybridCacher.AddPrefix("key1"), "value", DateTime.Now.AddSeconds(120));
                await _hybridCacher.GetAsync<string>("key1");
                var results = _localCacher.GetAllKeys();
                Assert.InRange(results.First().ExpirationDate.Value, DateTime.Now.AddSeconds(118), DateTime.Now.AddSeconds(122));
            }
        }

        public class PubSub : HybridCacherAsyncTests
        {
            [Fact]
            public async void SetCausesPublishRemove()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                await _hybridCacher.SetAsync("key", "value");
                Thread.Sleep(30);
                Assert.Equal(_hybridCacher.AddPrefix("key"), lastMessage);
            }

            [Fact]
            public async void RemoveCausesPublishRemove()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                await _hybridCacher.RemoveAsync("key");
                Thread.Sleep(30);
                Assert.Equal(_hybridCacher.AddPrefix("key"), lastMessage);
            }

            [Fact]
            public async void FlushCausesPublishFlush()
            {
                var lastMessage = default(string);
                SubscriptionHit += (key, message) => { lastMessage = message; };
                await _hybridCacher.FlushAllAsync();
                Thread.Sleep(30);
                Assert.Equal("flush", lastMessage);
            }
        }

        public class Dependencies : HybridCacherAsyncTests
        {
            [Fact]
            public async void GetSetsRemoteDependencyOnLocal()
            {
                await _hybridCacher.SetAsync("key1", "value1");
                await _hybridCacher.GetAsync<string>("key1");
                await _hybridCacher.SetAsync("key2", "value2", "key1");
                await _hybridCacher.GetAsync<string>("key2");
                var result = _localCacher.Get<string>(_hybridCacher.AddPrefix("key2"));
                Assert.Equal("value2", result);
                _localCacher.Remove(_hybridCacher.AddPrefix("key1"));
                result = _localCacher.Get<string>(_hybridCacher.AddPrefix("key2"));
                Assert.Equal(null, result);
            }
        }

        public void Dispose()
        {
            _hybridCacher.FlushAll();
        }
    }
}