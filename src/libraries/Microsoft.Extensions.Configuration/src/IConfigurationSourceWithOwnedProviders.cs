// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Extensions.Configuration;

// Only exact built-in source types opt in; derived sources may return shared providers.
internal interface IConfigurationSourceWithOwnedProviders
{
    bool OwnsProviders { get; }
}
