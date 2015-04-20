using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CacheSleeve.Models;

namespace CacheSleeve
{
    public partial class HybridCacher : IAsyncCacher
    {
        public async Task<T> GetAsync<T>(string key)
        {
            var cacheKey = AddPrefix(key);
            var result = _localCacher.Get<T>(cacheKey);
            if (result != null)
                return result;
            result = await _remoteCacher.GetAsync<T>(cacheKey);
            if (result != null)
            {
                var ttl = (int)(await _remoteCacher.TimeToLiveAsync(cacheKey));
                var parentKey = _remoteCacher.Get<string>(cacheKey + ".parent");

                if (ttl > -1)
                    _localCacher.Set(key, result, TimeSpan.FromSeconds(ttl), parentKey);
                else
                    _localCacher.Set(key, result, parentKey);

                result = _localCacher.Get<T>(cacheKey);
            }
            return result;
        }

        public async Task<bool> SetAsync<T>(string key, T value, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            bool isSet;
            try
            {
                isSet = await _remoteCacher.SetAsync(cacheKey, value, AddPrefix(parentKey));
            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey); // this might be a really bad idea
                return false;
            }
            if (isSet)
                _remoteCacher.PublishToChannel(_removeChannel, cacheKey);
            return true;
        }

        public async Task<bool> SetAsync<T>(string key, T value, DateTime expiresAt, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            bool isSet;
            try
            {
                isSet = await _remoteCacher.SetAsync(cacheKey, value, expiresAt, AddPrefix(parentKey));
            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey); // this might be a really bad idea
                return false;
            }
            if (isSet)
                _remoteCacher.PublishToChannel(_removeChannel, cacheKey);
            return true;
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan expiresIn, string parentKey = null)
        {
            var cacheKey = AddPrefix(key);
            bool isSet;
            try
            {
                isSet = await _remoteCacher.SetAsync(key, value, expiresIn, AddPrefix(parentKey));
            }
            catch (Exception)
            {
                _localCacher.Remove(cacheKey);
                _remoteCacher.Remove(cacheKey); // this might be a really bad idea
                return false;
            }
            if (isSet)
                _remoteCacher.PublishToChannel(_removeChannel, cacheKey);
            return true;
        }

        public async Task<bool> RemoveAsync(string key)
        {
            var cacheKey = AddPrefix(key);
            bool isRemoved;
            try
            {
                isRemoved = await _remoteCacher.RemoveAsync(cacheKey);
            }
            catch (Exception)
            {
                return false;
            }
            if (isRemoved)
            {
                _remoteCacher.PublishToChannel(_removeChannel, cacheKey);
            }
            return isRemoved;
        }

        public async Task FlushAllAsync()
        {
            await _remoteCacher.FlushAllAsync();
            _remoteCacher.PublishToChannel(_flushChannel, "");
        }

        public async Task<IEnumerable<Key>> GetAllKeysAsync()
        {
            var keys = await _remoteCacher.GetAllKeysAsync();
            var result = keys
                .Union(_localCacher.GetAllKeys())
                .Distinct()
                .Select(k => new Key(k.KeyName.Substring(KeyPrefix.Length), k.ExpirationDate));
            return result;
        }
    }
}