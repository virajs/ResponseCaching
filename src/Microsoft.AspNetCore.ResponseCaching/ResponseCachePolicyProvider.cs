// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching
{
    public class ResponseCachePolicyProvider : IResponseCachePolicyProvider
    {
        private static readonly CacheControlHeaderValue EmptyCacheControl = new CacheControlHeaderValue();

        public virtual bool IsRequestCacheable(ResponseCacheContext context)
        {
            // Verify the method
            var request = context.HttpContext.Request;
            if (!string.Equals("GET", request.Method, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals("HEAD", request.Method, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Verify existence of authorization headers
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.Authorization]))
            {
                return false;
            }

            // Verify request cache-control parameters
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.CacheControl]))
            {
                foreach (var c in request.Headers[HeaderNames.CacheControl])
                {
                    if (c.Equals("no-cache"))
                    {
                        return false;
                    }
                }
                // if (context.RequestCacheControlHeaderValue.NoCache)
                // {
                //     return false;
                // }
            }
            else
            {
                // Support for legacy HTTP 1.0 cache directive
                var pragmaHeaderValues = request.Headers[HeaderNames.Pragma];
                foreach (var directive in pragmaHeaderValues)
                {
                    if (string.Equals("no-cache", directive, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public virtual bool IsResponseCacheable(ResponseCacheContext context)
        {
            // Only cache pages explicitly marked with public
            if (!context.ResponseCacheControlHeaderValue.Public)
            {
                return false;
            }

            // Check no-store
            foreach (var c in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
            {
                if (c.Equals("no-store"))
                {
                    return false;
                }
            }
            if (/*context.RequestCacheControlHeaderValue.NoStore ||*/ context.ResponseCacheControlHeaderValue.NoStore)
            {
                return false;
            }

            // Check no-cache
            if (context.ResponseCacheControlHeaderValue.NoCache)
            {
                return false;
            }

            var response = context.HttpContext.Response;

            // Do not cache responses with Set-Cookie headers
            if (!StringValues.IsNullOrEmpty(response.Headers[HeaderNames.SetCookie]))
            {
                return false;
            }

            // Do not cache responses varying by *
            var varyHeader = response.Headers[HeaderNames.Vary];
            if (varyHeader.Count == 1 && string.Equals(varyHeader, "*", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check private
            if (context.ResponseCacheControlHeaderValue.Private)
            {
                return false;
            }

            // Check response code
            if (response.StatusCode != StatusCodes.Status200OK)
            {
                return false;
            }

            // Check response freshness
            if (!context.ResponseDate.HasValue)
            {
                if (!context.ResponseCacheControlHeaderValue.SharedMaxAge.HasValue &&
                    !context.ResponseCacheControlHeaderValue.MaxAge.HasValue &&
                    context.ResponseTime >= context.ResponseExpires)
                {
                    return false;
                }
            }
            else
            {
                var age = context.ResponseTime - context.ResponseDate.Value;

                // Validate shared max age
                if (age >= context.ResponseCacheControlHeaderValue.SharedMaxAge)
                {
                    return false;
                }
                else if (!context.ResponseCacheControlHeaderValue.SharedMaxAge.HasValue)
                {
                    // Validate max age
                    if (age >= context.ResponseCacheControlHeaderValue.MaxAge)
                    {
                        return false;
                    }
                    else if (!context.ResponseCacheControlHeaderValue.MaxAge.HasValue)
                    {
                        // Validate expiration
                        if (context.ResponseTime >= context.ResponseExpires)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public virtual bool IsCachedEntryFresh(ResponseCacheContext context)
        {
            var age = context.CachedEntryAge;
            var cachedControlHeaders = context.CachedResponseHeaders.CacheControl ?? EmptyCacheControl;

            // Add min-fresh requirements
            //TimeSpan? minFresh = null;
            foreach (var c in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
            {
                var index = c.IndexOf("min-fresh");
                if (index != -1)
                {
                    ++index;
                    int seconds;
                    if (TryParseValue(index, c, out seconds))
                    {
                        age += new TimeSpan(0, 0, seconds);
                    }
                    break;
                }
            }

            // Validate shared max age, this overrides any max age settings for shared caches
            if (age >= cachedControlHeaders.SharedMaxAge)
            {
                // shared max age implies must revalidate
                return false;
            }
            else if (!cachedControlHeaders.SharedMaxAge.HasValue)
            {
                TimeSpan? maxAge = null;
                foreach (var c in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
                {
                    var index = c.IndexOf("max-age");
                    if (index != -1)
                    {
                        ++index;
                        int seconds;
                        if (TryParseValue(index, c, out seconds))
                        {
                            maxAge = new TimeSpan(0, 0, seconds);
                        }
                        break;
                    }
                }
                // Validate max age
                if (age >= cachedControlHeaders.MaxAge || age >= maxAge)
                {
                    // Must revalidate
                    if (cachedControlHeaders.MustRevalidate)
                    {
                        return false;
                    }

                    // Request allows stale values
                    TimeSpan? maxStale = null;
                    foreach (var c in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
                    {
                        var index = c.IndexOf("max-stale");
                        if (index != -1)
                        {
                            ++index;
                            int seconds;
                            if (TryParseValue(index, c, out seconds))
                            {
                                maxStale = new TimeSpan(0, 0, seconds);
                            }
                            break;
                        }
                    }
                    if (age < maxStale)
                    {
                        return true;
                    }

                    return false;
                }

                if (!cachedControlHeaders.MaxAge.HasValue && !maxAge.HasValue)
                {
                    // Validate expiration
                    if (context.ResponseTime >= context.CachedResponseHeaders.Expires)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryParseValue(int startIndex, string header, out int seconds)
        {
            while (startIndex != header.Length)
            {
                if (header[startIndex] == '=')
                {
                    break;
                }
                ++startIndex;
            }
            if (startIndex != header.Length)
            {
                var endIndex = startIndex + 1;
                while (endIndex < header.Length)
                {
                    var cc = header[endIndex];
                    if ((cc >= '0') && (cc <= '9'))
                    {
                        endIndex++;
                    }
                    else
                    {
                        break;
                    }
                }
                seconds = int.Parse(header.Substring(startIndex + 1, endIndex - (startIndex + 1)));
                return true;
            }
            seconds = 0;
            return false;
        }
    }
}
