﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.AspNetCore.ResponseCaching.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ResponseCacheServiceCollectionExtensions
    {
        public static IServiceCollection AddMemoryResponseCacheStore(this IServiceCollection services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddMemoryCache();
            services.TryAdd(ServiceDescriptor.Singleton<IResponseCachePolicyProvider, ResponseCachePolicyProvider>());
            services.TryAdd(ServiceDescriptor.Singleton<IResponseCacheKeyProvider, ResponseCacheKeyProvider>());
            services.TryAdd(ServiceDescriptor.Singleton<IResponseCacheStore, MemoryResponseCacheStore>());

            return services;
        }
    }
}
