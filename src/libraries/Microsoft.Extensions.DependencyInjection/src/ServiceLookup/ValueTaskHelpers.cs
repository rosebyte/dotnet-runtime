// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal static class ValueTaskHelpers
    {
        // Unwraps a ValueTask<T> that is expected to be already completed.
        // If the task faulted or was canceled, the original exception is propagated.
        // If the task has not completed (i.e. a real async source produced an
        // incomplete result), throws InvalidOperationException because today's
        // sync public API contract cannot represent an asynchronous result.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetSynchronousResult<T>(ValueTask<T> valueTask)
        {
            if (valueTask.IsCompletedSuccessfully)
            {
                return valueTask.Result;
            }

            if (valueTask.IsCompleted)
            {
                // Faulted or canceled - propagate the original exception.
                return valueTask.GetAwaiter().GetResult();
            }

            ThrowAsynchronousResolution();
            return default!;
        }

        private static void ThrowAsynchronousResolution() =>
            throw new InvalidOperationException(SR.AsynchronousResolutionNotSupported);
    }
}
