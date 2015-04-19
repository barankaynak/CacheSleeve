﻿using System;
using System.Web;
using Moq;
using Xunit;
using System.Linq;

namespace CacheSleeve.Tests
{
    public class OverviewGeneratorTests : IDisposable
    {
        private readonly HybridCacher _hybridCacher;
        private readonly RedisCacher _remoteCacher;
        private readonly HttpContextCacher _localCacher;
        private readonly RedisConnection _redisConnection;

        private delegate void SubscriptionHitHandler(string key, string message);

        public OverviewGeneratorTests()
        {
            // have to fake an http context to use http context cache
            HttpContext.Current = new HttpContext(new HttpRequest(null, "http://tempuri.org", null), new HttpResponse(null));

            _redisConnection = RedisConnection.Create(TestSettings.RedisHost, TestSettings.RedisPort, TestSettings.RedisPassword, TestSettings.RedisDb);

            var nullLogger = new Mock<ICacheLogger>().Object;

            _remoteCacher = new RedisCacher(_redisConnection, new JsonObjectSerializer(), nullLogger);
            _localCacher = new HttpContextCacher(nullLogger);

            var configMock = new Mock<IHybridCacherConfig>();
            configMock.Setup(c => c.KeyPrefix).Returns("test.");

            _hybridCacher = new HybridCacher(configMock.Object, _remoteCacher, _localCacher);
        }


        [Fact]
        public void GeneratesOverview()
        {
            var result = Overview.Overview.Generate(_hybridCacher);
            Assert.False(string.IsNullOrWhiteSpace(result));
        }

        [Fact]
        public void OverviewContainsKeys()
        {
            _remoteCacher.Set("key1", "value1", DateTime.Now.AddSeconds(30));
            _localCacher.Set("key2", "value2", DateTime.Now.AddMinutes(5));
            var result = Overview.Overview.Generate(_hybridCacher);
            Assert.Equal(1, result.Select((c, i) => result.Substring(i)).Count(sub => sub.StartsWith("key1")));
            Assert.Equal(1, result.Select((c, i) => result.Substring(i)).Count(sub => sub.StartsWith("key2")));
        }


        public void Dispose()
        {
            _hybridCacher.FlushAll();
            _redisConnection.Connection.Dispose();
        }
    }
}