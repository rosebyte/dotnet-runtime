// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal sealed class RuntimeServiceProviderEngine : ServiceProviderEngine
    {
        public static RuntimeServiceProviderEngine Instance { get; } = new RuntimeServiceProviderEngine();

        private RuntimeServiceProviderEngine() { }

        public override Func<ServiceProviderEngineScope, ValueTask<object?>> RealizeService(ServiceCallSite callSite)
        {
            return scope => CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope);
        }
    }
}
