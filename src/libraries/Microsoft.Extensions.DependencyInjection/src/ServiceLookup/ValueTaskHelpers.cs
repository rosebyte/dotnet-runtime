// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

        // Slow-path async composition helpers called by expression-tree and
        // IL-emit generated code when at least one child resolution produces
        // an incomplete ValueTask. On the all-sync fast path these are never invoked.

        internal static async ValueTask<object?> AwaitConstructor(
            ConstructorInfo constructor, ValueTask<object?>[] parameterTasks)
        {
            object?[] args = new object?[parameterTasks.Length];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = await parameterTasks[i].ConfigureAwait(false);
            }

#if NETFRAMEWORK || NETSTANDARD2_0
            try
            {
                return constructor.Invoke(args);
            }
            catch (Exception ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
#else
            return constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: args, culture: null);
#endif
        }

        [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
            Justification = "VerifyAotCompatibility ensures elementType is not a ValueType")]
        internal static async ValueTask<object?> AwaitArrayElements(
            Type elementType, ValueTask<object?>[] elementTasks)
        {
            Array array = Array.CreateInstance(elementType, elementTasks.Length);
            for (int i = 0; i < elementTasks.Length; i++)
            {
                array.SetValue(await elementTasks[i].ConfigureAwait(false), i);
            }

            return array;
        }

        internal static async ValueTask<object?> AwaitAndCaptureDisposable(
            ValueTask<object?> task, ServiceProviderEngineScope scope)
        {
            object? result = await task.ConfigureAwait(false);
            return scope.CaptureDisposable(result);
        }
    }
}
