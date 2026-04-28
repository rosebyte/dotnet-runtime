// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal abstract class ServiceProviderEngine
    {
        public abstract Func<ServiceProviderEngineScope, ValueTask<object?>> RealizeService(ServiceCallSite callSite);
    }
}
