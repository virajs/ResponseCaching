﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    public class ResponseCachePolicyProvider : IResponseCachePolicyProvider
    {
        public virtual bool IsRequestCacheable(ResponseCacheContext context)
        {
            // Verify the method
            var request = context.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            {
                context.Logger.LogRequestMethodNotCacheable(request.Method);
                return false;
            }

            // Verify existence of authorization headers
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.Authorization]))
            {
                context.Logger.LogRequestWithAuthorizationNotCacheable();
                return false;
            }

            // Verify request cache-control parameters
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.CacheControl]))
            {
                foreach (var header in request.Headers[HeaderNames.CacheControl])
                {
                    if (header.IndexOf(CacheControlValues.NoCacheString, StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        context.Logger.LogRequestWithNoCacheNotCacheable();
                        return false;
                    }
                }
            }
            else
            {
                // Support for legacy HTTP 1.0 cache directive
                var pragmaHeaderValues = request.Headers[HeaderNames.Pragma];
                foreach (var directive in pragmaHeaderValues)
                {
                    if (string.Equals(CacheControlValues.NoCacheString, directive, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Logger.LogRequestWithPragmaNoCacheNotCacheable();
                        return false;
                    }
                }
            }

            return true;
        }

        public virtual bool IsResponseCacheable(ResponseCacheContext context)
        {
            var responseCacheControlHeader = context.HttpContext.Response.Headers[HeaderNames.CacheControl];

            // Only cache pages explicitly marked with public
            var isPublic = false;
            foreach (var header in responseCacheControlHeader)
            {
                if (header.IndexOf(CacheControlValues.PublicString, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    isPublic = true;
                    break;
                }
            }
            if (!isPublic)
            {
                context.Logger.LogResponseWithoutPublicNotCacheable();
                return false;
            }

            // Check no-store
            foreach (var header in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
            {
                if (header.IndexOf(CacheControlValues.NoStoreString, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    context.Logger.LogResponseWithNoStoreNotCacheable();
                    return false;
                }
            }

            foreach (var header in responseCacheControlHeader)
            {
                if (header.IndexOf(CacheControlValues.NoStoreString, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    context.Logger.LogResponseWithNoStoreNotCacheable();
                    return false;
                }
            }

            // Check no-cache
            foreach (var header in responseCacheControlHeader)
            {
                if (header.IndexOf(CacheControlValues.NoCacheString, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    context.Logger.LogResponseWithNoCacheNotCacheable();
                    return false;
                }
            }

            var response = context.HttpContext.Response;

            // Do not cache responses with Set-Cookie headers
            if (!StringValues.IsNullOrEmpty(response.Headers[HeaderNames.SetCookie]))
            {
                context.Logger.LogResponseWithSetCookieNotCacheable();
                return false;
            }

            // Do not cache responses varying by *
            var varyHeader = response.Headers[HeaderNames.Vary];
            if (varyHeader.Count == 1 && string.Equals(varyHeader, "*", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogResponseWithVaryStarNotCacheable();
                return false;
            }

            // Check private
            foreach (var header in responseCacheControlHeader)
            {
                if (header.IndexOf(CacheControlValues.PrivateString, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    context.Logger.LogResponseWithPrivateNotCacheable();
                    return false;
                }
            }

            // Check response code
            if (response.StatusCode != StatusCodes.Status200OK)
            {
                context.Logger.LogResponseWithUnsuccessfulStatusCodeNotCacheable(response.StatusCode);
                return false;
            }

            // Check response freshness
            if (!context.ResponseDate.HasValue)
            {
                if (!context.ResponseSharedMaxAge.HasValue &&
                    !context.ResponseMaxAge.HasValue &&
                    context.ResponseTime.Value >= context.ResponseExpires)
                {
                    context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                    return false;
                }
            }
            else
            {
                var age = context.ResponseTime.Value - context.ResponseDate.Value;

                // Validate shared max age
                if (age >= context.ResponseSharedMaxAge)
                {
                    context.Logger.LogExpirationSharedMaxAgeExceeded(age, context.ResponseSharedMaxAge.Value);
                    return false;
                }
                else if (!context.ResponseSharedMaxAge.HasValue)
                {
                    // Validate max age
                    if (age >= context.ResponseMaxAge)
                    {
                        context.Logger.LogExpirationMaxAgeExceeded(age, context.ResponseMaxAge.Value);
                        return false;
                    }
                    else if (!context.ResponseMaxAge.HasValue)
                    {
                        // Validate expiration
                        if (context.ResponseTime.Value >= context.ResponseExpires)
                        {
                            context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public virtual bool IsCachedEntryFresh(ResponseCacheContext context)
        {
            var age = context.CachedEntryAge.Value;
            var cachedControlHeaders = context.CachedResponseHeaders[HeaderNames.CacheControl];
            var requestCacheControlHeaders = context.HttpContext.Request.Headers[HeaderNames.CacheControl];

            // Add min-fresh requirements
            foreach (var header in requestCacheControlHeaders)
            {
                var index = header.IndexOf(CacheControlValues.MinFreshString, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    index += CacheControlValues.MinFreshString.Length;
                    int seconds;
                    if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                    {
                        var minFresh = TimeSpan.FromSeconds(seconds);
                        age += minFresh;
                        context.Logger.LogExpirationMinFreshAdded(minFresh);
                    }
                    break;
                }
            }

            // Validate shared max age, this overrides any max age settings for shared caches
            TimeSpan? cachedSharedMaxAge = null;
            foreach (var header in cachedControlHeaders)
            {
                var index = header.IndexOf(CacheControlValues.SharedMaxAgeString, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    index += CacheControlValues.SharedMaxAgeString.Length;
                    int seconds;
                    if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                    {
                        cachedSharedMaxAge = TimeSpan.FromSeconds(seconds);
                    }
                    break;
                }
            }

            if (age >= cachedSharedMaxAge)
            {
                // shared max age implies must revalidate
                context.Logger.LogExpirationSharedMaxAgeExceeded(age, cachedSharedMaxAge.Value);
                return false;
            }
            else if (!cachedSharedMaxAge.HasValue)
            {
                TimeSpan? requestMaxAge = null;
                foreach (var header in requestCacheControlHeaders)
                {
                    var index = header.IndexOf(CacheControlValues.MaxAgeString, StringComparison.OrdinalIgnoreCase);
                    if (index != -1)
                    {
                        index += CacheControlValues.MaxAgeString.Length;
                        int seconds;
                        if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                        {
                            requestMaxAge = TimeSpan.FromSeconds(seconds);
                        }
                        break;
                    }
                }

                TimeSpan? cachedMaxAge = null;
                foreach (var header in cachedControlHeaders)
                {
                    var index = header.IndexOf(CacheControlValues.MaxAgeString, StringComparison.OrdinalIgnoreCase);
                    if (index != -1)
                    {
                        index += CacheControlValues.MaxAgeString.Length;
                        int seconds;
                        if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                        {
                            cachedMaxAge = TimeSpan.FromSeconds(seconds);
                        }
                        break;
                    }
                }

                var lowestMaxAge = cachedMaxAge < requestMaxAge ? cachedMaxAge : requestMaxAge ?? cachedMaxAge;
                // Validate max age
                if (age >= lowestMaxAge)
                {
                    // Must revalidate
                    foreach (var header in cachedControlHeaders)
                    {
                        if (header.IndexOf(CacheControlValues.MustRevalidateString, StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            context.Logger.LogExpirationMustRevalidate(age, lowestMaxAge.Value);
                            return false;
                        }
                    }

                    TimeSpan? requestMaxStale = null;
                    foreach (var header in requestCacheControlHeaders)
                    {
                        var index = header.IndexOf(CacheControlValues.MaxStaleString, StringComparison.OrdinalIgnoreCase);
                        if (index != -1)
                        {
                            index += CacheControlValues.MaxStaleString.Length;
                            int seconds;
                            if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                            {
                                requestMaxStale = TimeSpan.FromSeconds(seconds);
                            }
                            break;
                        }
                    }

                    // Request allows stale values
                    if (requestMaxStale.HasValue && age - lowestMaxAge < requestMaxStale)
                    {
                        context.Logger.LogExpirationMaxStaleSatisfied(age, lowestMaxAge.Value, requestMaxStale.Value);
                        return true;
                    }

                    context.Logger.LogExpirationMaxAgeExceeded(age, lowestMaxAge.Value);
                    return false;
                }
                else if (!cachedMaxAge.HasValue && !requestMaxAge.HasValue)
                {
                    // Validate expiration
                    DateTimeOffset expires;
                    if (context.CachedResponseHeaders[HeaderNames.Expires].Count > 0)
                    {
                        if (ParsingHelpers.TryStringToDate(context.CachedResponseHeaders[HeaderNames.Expires], out expires))
                        {
                            if (context.ResponseTime.Value >= expires)
                            {
                                context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, expires);
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }
    }
}
